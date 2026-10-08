using System.Globalization;
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
    public Task<DateTimeOffset?> ReadLastSuccessfulSyncAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = Preferences.Default.Get("last-sync-utc", "");
        return Task.FromResult<DateTimeOffset?>(DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var timestamp) ? timestamp : null);
    }

    public Task WriteLastSuccessfulSyncAsync(DateTimeOffset timestampUtc, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Preferences.Default.Set("last-sync-utc", timestampUtc.ToString("O", CultureInfo.InvariantCulture));
        return Task.CompletedTask;
    }
}
