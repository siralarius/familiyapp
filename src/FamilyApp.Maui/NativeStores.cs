using System.Globalization;
using System.Text.Json;
using FamilyApp.Sync.Pairing;
using FamilyApp.Sync.Transport;

namespace FamilyApp.Maui;

public sealed class NativeSecurePairingStore : ISecurePairingStore
{
    public async Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = await SecureStorage.Default.GetAsync(key);
        return value is null ? null : Convert.FromBase64String(value);
    }

    public Task WriteAsync(string key, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return SecureStorage.Default.SetAsync(key, Convert.ToBase64String(value.Span));
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SecureStorage.Default.Remove(key);
        return Task.CompletedTask;
    }
}

public sealed class NativeSyncStatusStore : IPeerSyncStatusStore
{
    private static string Key(string suffix) => $"sync:{Preferences.Default.Get("family-id", "")}:v2:{suffix}";

    public NativeSyncStatusStore()
    {
        // This singleton is constructed before any family-switch operation can run.
        if (!Preferences.Default.Get("sync-status-v2-migrated", false) &&
            Guid.TryParse(Preferences.Default.Get("family-id", ""), out _))
        {
            var legacy = Preferences.Default.Get("last-sync-utc", "");
            if (!Preferences.Default.ContainsKey(Key("last-success")) && !string.IsNullOrEmpty(legacy))
                Preferences.Default.Set(Key("last-success"), legacy);
            Preferences.Default.Set("sync-status-v2-migrated", true);
        }
    }
    public Task<DateTimeOffset?> ReadLastSuccessfulSyncAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = Preferences.Default.Get(Key("last-success"), "");
        return Task.FromResult<DateTimeOffset?>(DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var timestamp) ? timestamp : null);
    }

    public Task WriteLastSuccessfulSyncAsync(DateTimeOffset timestampUtc, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Preferences.Default.Set(Key("last-success"), timestampUtc.ToString("O", CultureInfo.InvariantCulture));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyCollection<Guid>> ReadKnownPeersAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyCollection<Guid>>(JsonSerializer.Deserialize<Guid[]>(Preferences.Default.Get(Key("known-peers"), "[]")) ?? []);
    }

    public Task WriteKnownPeersAsync(IReadOnlyCollection<Guid> peerIds, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Preferences.Default.Set(Key("known-peers"), JsonSerializer.Serialize(peerIds));
        return Task.CompletedTask;
    }
}
