using FamilyApp.Sync.Transport;
using Microsoft.Extensions.Logging;

namespace FamilyApp.Relay.Services;

public enum RelayMailboxResult
{
    Accepted,
    Duplicate,
    Conflict,
    Invalid
}

public interface IRelayMailboxService
{
    Task<RelayMailboxResult> EnqueueAsync(EncryptedRelayMessage message, CancellationToken cancellationToken = default);
    Task<EncryptedRelayMessage?> GetByIdAsync(Guid familyId, Guid recipientDeviceId, Guid messageId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EncryptedRelayMessage>> GetPendingAsync(Guid familyId, Guid recipientDeviceId, CancellationToken cancellationToken = default);
    Task AcknowledgeAsync(Guid familyId, Guid recipientDeviceId, Guid messageId, CancellationToken cancellationToken = default);
    Task RevokeDeviceAsync(Guid familyId, Guid deviceId, CancellationToken cancellationToken = default);
    Task<bool> IsDeviceRevokedAsync(Guid familyId, Guid deviceId, CancellationToken cancellationToken = default);
}

public sealed class RelayMailboxService(
    IRelayMessageStore store,
    ILogger<RelayMailboxService> logger,
    TimeProvider? timeProvider = null) : IRelayMailboxService
{
    private static readonly TimeSpan MaximumMessageLifetime = TimeSpan.FromDays(7);
    private const int MaximumCiphertextLength = 16 * 1024 * 1024;
    private const int MaximumPendingMessages = 50;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<RelayMailboxResult> EnqueueAsync(
        EncryptedRelayMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var now = _timeProvider.GetUtcNow();
        if (message.MessageId == Guid.Empty || message.FamilyId == Guid.Empty ||
            message.SenderDeviceId == Guid.Empty || message.RecipientDeviceId == Guid.Empty ||
            message.SenderDeviceId == message.RecipientDeviceId || message.Ciphertext is null ||
            message.Ciphertext.Length is 0 or > MaximumCiphertextLength ||
            message.ExpiresAtUtc <= now || message.ExpiresAtUtc > now.Add(MaximumMessageLifetime))
            return RelayMailboxResult.Invalid;

        if (await store.IsDeviceRevokedAsync(message.FamilyId, message.SenderDeviceId, cancellationToken) ||
            await store.IsDeviceRevokedAsync(message.FamilyId, message.RecipientDeviceId, cancellationToken))
            return RelayMailboxResult.Conflict;

        var result = await store.EnqueueAsync(message, cancellationToken);
        logger.LogDebug("Encrypted relay enqueue completed with {RelayResult}.", result);
        return result switch
        {
            RelayEnqueueResult.Created => RelayMailboxResult.Accepted,
            RelayEnqueueResult.Duplicate => RelayMailboxResult.Duplicate,
            _ => RelayMailboxResult.Conflict
        };
    }

    public async Task<IReadOnlyList<EncryptedRelayMessage>> GetPendingAsync(
        Guid familyId, Guid recipientDeviceId, CancellationToken cancellationToken = default)
    {
        if (familyId == Guid.Empty || recipientDeviceId == Guid.Empty)
            throw new ArgumentException("Family and recipient device IDs are required.");
        var messages = await store.GetPendingAsync(familyId, recipientDeviceId, MaximumPendingMessages, cancellationToken);
        logger.LogDebug("Relay inbox returned {MessageCount} encrypted messages.", messages.Count);
        return messages;
    }

    public Task<EncryptedRelayMessage?> GetByIdAsync(
        Guid familyId, Guid recipientDeviceId, Guid messageId, CancellationToken cancellationToken = default)
        => store.GetByIdAsync(familyId, recipientDeviceId, messageId, cancellationToken);

    public async Task AcknowledgeAsync(
        Guid familyId, Guid recipientDeviceId, Guid messageId, CancellationToken cancellationToken = default)
    {
        await store.AcknowledgeAsync(familyId, recipientDeviceId, messageId, cancellationToken);
        logger.LogDebug("Encrypted relay message acknowledged.");
    }

    public async Task RevokeDeviceAsync(Guid familyId, Guid deviceId, CancellationToken cancellationToken = default)
    {
        if (familyId == Guid.Empty || deviceId == Guid.Empty)
            throw new ArgumentException("Family and device IDs are required.");
        await store.RevokeDeviceAsync(familyId, deviceId, _timeProvider.GetUtcNow(), cancellationToken);
        logger.LogInformation("Family device was revoked from relay access.");
    }

    public Task<bool> IsDeviceRevokedAsync(Guid familyId, Guid deviceId, CancellationToken cancellationToken = default)
        => store.IsDeviceRevokedAsync(familyId, deviceId, cancellationToken);
}