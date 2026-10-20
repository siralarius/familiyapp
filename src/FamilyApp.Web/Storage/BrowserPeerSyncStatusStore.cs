using System.Globalization;
using System.Text.Json;
using FamilyApp.Sync.Transport;
using Microsoft.JSInterop;

namespace FamilyApp.Web.Storage;

public sealed class BrowserPeerSyncStatusStore(IJSRuntime javascript) : IPeerSyncStatusStore
{
    private const string StorageKey = "familyapp.sync.last-success.v1";
    private const string PeersStorageKey = "familyapp.sync.known-peers.v1";

    public async Task<IReadOnlyCollection<Guid>> ReadKnownPeersAsync(CancellationToken cancellationToken = default)
    {
        var value = await javascript.InvokeAsync<string?>("localStorage.getItem", cancellationToken, PeersStorageKey);
        return value is null ? [] : JsonSerializer.Deserialize<Guid[]>(value) ?? [];
    }

    public Task WriteKnownPeersAsync(IReadOnlyCollection<Guid> peerIds, CancellationToken cancellationToken = default)
        => javascript.InvokeVoidAsync("localStorage.setItem", cancellationToken, PeersStorageKey, JsonSerializer.Serialize(peerIds)).AsTask();

    public async Task<DateTimeOffset?> ReadLastSuccessfulSyncAsync(CancellationToken cancellationToken = default)
    {
        var value = await javascript.InvokeAsync<string?>("localStorage.getItem", cancellationToken, StorageKey);
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp)
            ? timestamp
            : null;
    }

    public Task WriteLastSuccessfulSyncAsync(DateTimeOffset timestampUtc, CancellationToken cancellationToken = default)
        => javascript.InvokeVoidAsync(
            "localStorage.setItem",
            cancellationToken,
            StorageKey,
            timestampUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)).AsTask();
}
