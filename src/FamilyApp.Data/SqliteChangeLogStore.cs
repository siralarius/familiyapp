using System.Text.Json;
using FamilyApp.Sync.ChangeLog;
using Microsoft.Data.Sqlite;

namespace FamilyApp.Data;

public sealed class SqliteChangeLogStore : IChangeLogStore
{
    private readonly string _connectionString;

    public SqliteChangeLogStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS changes (id TEXT PRIMARY KEY, record TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
    }

    public IReadOnlyCollection<ChangeRecord> Load()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT record FROM changes ORDER BY rowid";
        using var reader = command.ExecuteReader();
        var result = new List<ChangeRecord>();
        while (reader.Read())
            result.Add(JsonSerializer.Deserialize<ChangeRecord>(reader.GetString(0))
                ?? throw new InvalidDataException("A stored change is invalid."));
        return result;
    }

    public void Append(IReadOnlyCollection<ChangeRecord> changes)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO changes (id, record) VALUES ($id, $record)";
        var id = command.Parameters.Add("$id", SqliteType.Text);
        var record = command.Parameters.Add("$record", SqliteType.Text);
        foreach (var change in changes)
        {
            id.Value = change.ChangeId.ToString("N");
            record.Value = JsonSerializer.Serialize(change);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }
}
