using FamilyApp.Relay.Services;
using FamilyApp.Sync.Transport;
using Microsoft.Data.Sqlite;

namespace FamilyApp.Relay.Storage;

public sealed class SqliteRelayMessageStore(string connectionString) : IRelayMessageStore
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS relay_messages (
                message_id TEXT PRIMARY KEY,
                family_id TEXT NOT NULL,
                sender_device_id TEXT NOT NULL,
                recipient_device_id TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                expires_at_utc TEXT NOT NULL,
                ciphertext BLOB NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_relay_recipient_expiry
                ON relay_messages(family_id, recipient_device_id, expires_at_utc);
            CREATE TRIGGER IF NOT EXISTS limit_relay_mailbox_size
            BEFORE INSERT ON relay_messages
            WHEN (SELECT COUNT(*) FROM relay_messages
                  WHERE family_id = NEW.family_id AND recipient_device_id = NEW.recipient_device_id) >= 100
            BEGIN
                SELECT RAISE(IGNORE);
            END;
            CREATE TABLE IF NOT EXISTS revoked_devices (
                family_id TEXT NOT NULL,
                device_id TEXT NOT NULL,
                revoked_at_utc TEXT NOT NULL,
                PRIMARY KEY (family_id, device_id)
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<RelayEnqueueResult> EnqueueAsync(
        EncryptedRelayMessage message,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await DeleteExpiredAsync(connection, cancellationToken);

        var revoked = connection.CreateCommand();
        revoked.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM revoked_devices
                WHERE family_id = $familyId AND device_id IN ($senderDeviceId, $recipientDeviceId));
            """;
        AddIdentityParameters(revoked, message.FamilyId, message.SenderDeviceId, message.RecipientDeviceId);
        if ((long)(await revoked.ExecuteScalarAsync(cancellationToken) ?? 0L) != 0)
            return RelayEnqueueResult.Conflict;

        var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT OR IGNORE INTO relay_messages (
                message_id, family_id, sender_device_id, recipient_device_id,
                created_at_utc, expires_at_utc, ciphertext)
            VALUES (
                $messageId, $familyId, $senderDeviceId, $recipientDeviceId,
                $createdAtUtc, $expiresAtUtc, $ciphertext);
            """;
        insert.Parameters.AddWithValue("$messageId", message.MessageId.ToString("N"));
        insert.Parameters.AddWithValue("$familyId", message.FamilyId.ToString("N"));
        insert.Parameters.AddWithValue("$senderDeviceId", message.SenderDeviceId.ToString("N"));
        insert.Parameters.AddWithValue("$recipientDeviceId", message.RecipientDeviceId.ToString("N"));
        insert.Parameters.AddWithValue("$createdAtUtc", message.CreatedAtUtc.ToUniversalTime().ToString("O"));
        insert.Parameters.AddWithValue("$expiresAtUtc", message.ExpiresAtUtc.ToUniversalTime().ToString("O"));
        insert.Parameters.Add("$ciphertext", SqliteType.Blob).Value = message.Ciphertext;
        if (await insert.ExecuteNonQueryAsync(cancellationToken) == 1)
            return RelayEnqueueResult.Created;

        var existing = connection.CreateCommand();
        existing.CommandText = """
            SELECT family_id, sender_device_id, recipient_device_id, ciphertext
            FROM relay_messages WHERE message_id = $messageId;
            """;
        existing.Parameters.AddWithValue("$messageId", message.MessageId.ToString("N"));
        await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return RelayEnqueueResult.Conflict;
        var isDuplicate = string.Equals(reader.GetString(0), message.FamilyId.ToString("N"), StringComparison.Ordinal) &&
            string.Equals(reader.GetString(1), message.SenderDeviceId.ToString("N"), StringComparison.Ordinal) &&
            string.Equals(reader.GetString(2), message.RecipientDeviceId.ToString("N"), StringComparison.Ordinal) &&
            ((byte[])reader[3]).AsSpan().SequenceEqual(message.Ciphertext);
        return isDuplicate ? RelayEnqueueResult.Duplicate : RelayEnqueueResult.Conflict;
    }

    public async Task<IReadOnlyList<EncryptedRelayMessage>> GetPendingAsync(
        Guid familyId,
        Guid recipientDeviceId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await DeleteExpiredAsync(connection, cancellationToken);

        var query = connection.CreateCommand();
        query.CommandText = """
            SELECT message_id, family_id, sender_device_id, recipient_device_id,
                   created_at_utc, expires_at_utc, ciphertext
            FROM relay_messages AS messages
            WHERE family_id = $familyId AND recipient_device_id = $recipientDeviceId
              AND NOT EXISTS (
                  SELECT 1 FROM revoked_devices
                  WHERE family_id = messages.family_id
                    AND device_id = messages.sender_device_id)
            ORDER BY created_at_utc, message_id
            LIMIT $limit;
            """;
        query.Parameters.AddWithValue("$familyId", familyId.ToString("N"));
        query.Parameters.AddWithValue("$recipientDeviceId", recipientDeviceId.ToString("N"));
        query.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100));

        var messages = new List<EncryptedRelayMessage>();
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            messages.Add(new EncryptedRelayMessage(
                Guid.ParseExact(reader.GetString(0), "N"),
                Guid.ParseExact(reader.GetString(1), "N"),
                Guid.ParseExact(reader.GetString(2), "N"),
                Guid.ParseExact(reader.GetString(3), "N"),
                DateTimeOffset.Parse(reader.GetString(4), System.Globalization.CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture),
                (byte[])reader[6]));
        }

        return messages;
    }

    public async Task<EncryptedRelayMessage?> GetByIdAsync(
        Guid familyId,
        Guid recipientDeviceId,
        Guid messageId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await DeleteExpiredAsync(connection, cancellationToken);

        var query = connection.CreateCommand();
        query.CommandText = """
            SELECT message_id, family_id, sender_device_id, recipient_device_id,
                   created_at_utc, expires_at_utc, ciphertext
            FROM relay_messages
            WHERE message_id = $messageId AND family_id = $familyId
              AND recipient_device_id = $recipientDeviceId;
            """;
        query.Parameters.AddWithValue("$messageId", messageId.ToString("N"));
        query.Parameters.AddWithValue("$familyId", familyId.ToString("N"));
        query.Parameters.AddWithValue("$recipientDeviceId", recipientDeviceId.ToString("N"));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        return new EncryptedRelayMessage(
            Guid.ParseExact(reader.GetString(0), "N"),
            Guid.ParseExact(reader.GetString(1), "N"),
            Guid.ParseExact(reader.GetString(2), "N"),
            Guid.ParseExact(reader.GetString(3), "N"),
            DateTimeOffset.Parse(reader.GetString(4), System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture),
            (byte[])reader[6]);
    }

    public async Task AcknowledgeAsync(
        Guid familyId,
        Guid recipientDeviceId,
        Guid messageId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var delete = connection.CreateCommand();
        delete.CommandText = """
            DELETE FROM relay_messages
            WHERE message_id = $messageId AND family_id = $familyId AND recipient_device_id = $recipientDeviceId;
            """;
        delete.Parameters.AddWithValue("$messageId", messageId.ToString("N"));
        delete.Parameters.AddWithValue("$familyId", familyId.ToString("N"));
        delete.Parameters.AddWithValue("$recipientDeviceId", recipientDeviceId.ToString("N"));
        await delete.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RevokeDeviceAsync(
        Guid familyId,
        Guid deviceId,
        DateTimeOffset revokedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var revoke = connection.CreateCommand();
        revoke.Transaction = (SqliteTransaction)transaction;
        revoke.CommandText = """
            INSERT INTO revoked_devices (family_id, device_id, revoked_at_utc)
            VALUES ($familyId, $deviceId, $revokedAtUtc)
            ON CONFLICT(family_id, device_id) DO UPDATE SET revoked_at_utc = excluded.revoked_at_utc;
            """;
        revoke.Parameters.AddWithValue("$familyId", familyId.ToString("N"));
        revoke.Parameters.AddWithValue("$deviceId", deviceId.ToString("N"));
        revoke.Parameters.AddWithValue("$revokedAtUtc", revokedAtUtc.ToUniversalTime().ToString("O"));
        await revoke.ExecuteNonQueryAsync(cancellationToken);

        var cleanup = connection.CreateCommand();
        cleanup.Transaction = (SqliteTransaction)transaction;
        cleanup.CommandText = """
            DELETE FROM relay_messages
            WHERE family_id = $familyId AND (sender_device_id = $deviceId OR recipient_device_id = $deviceId);
            """;
        cleanup.Parameters.AddWithValue("$familyId", familyId.ToString("N"));
        cleanup.Parameters.AddWithValue("$deviceId", deviceId.ToString("N"));
        await cleanup.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<bool> IsDeviceRevokedAsync(
        Guid familyId,
        Guid deviceId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM revoked_devices WHERE family_id = $familyId AND device_id = $deviceId);";
        command.Parameters.AddWithValue("$familyId", familyId.ToString("N"));
        command.Parameters.AddWithValue("$deviceId", deviceId.ToString("N"));
        return (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L) != 0;
    }

    private static async Task DeleteExpiredAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM relay_messages WHERE expires_at_utc <= $now;";
        delete.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await delete.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddIdentityParameters(SqliteCommand command, Guid familyId, Guid senderDeviceId, Guid recipientDeviceId)
    {
        command.Parameters.AddWithValue("$familyId", familyId.ToString("N"));
        command.Parameters.AddWithValue("$senderDeviceId", senderDeviceId.ToString("N"));
        command.Parameters.AddWithValue("$recipientDeviceId", recipientDeviceId.ToString("N"));
    }
}