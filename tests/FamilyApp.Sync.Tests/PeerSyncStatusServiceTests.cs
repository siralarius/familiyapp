using FamilyApp.Sync.Transport;
using Xunit;

namespace FamilyApp.Sync.Tests;

public class PeerSyncStatusServiceTests
{
    [Fact]
    public async Task Last_successful_sync_survives_service_restart()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new InMemoryStatusStore();
        var timestamp = new DateTimeOffset(2026, 10, 5, 12, 30, 0, TimeSpan.Zero);
        var firstRun = new PeerSyncStatusService(store);

        await firstRun.RecordAsync(
            new PeerSyncStatus(Guid.NewGuid(), PeerSyncState.Synchronized, null, timestamp), cancellationToken);

        var restarted = new PeerSyncStatusService(store);
        await restarted.InitializeAsync(cancellationToken);

        Assert.Equal(timestamp, restarted.LastSuccessfulSyncAtUtc);
        Assert.Null(restarted.LatestStatus);
    }

    [Fact]
    public async Task Non_success_status_does_not_replace_last_success_time()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new InMemoryStatusStore();
        var timestamp = new DateTimeOffset(2026, 10, 5, 12, 30, 0, TimeSpan.Zero);
        var service = new PeerSyncStatusService(store);
        await service.RecordAsync(new PeerSyncStatus(Guid.NewGuid(), PeerSyncState.Synchronized, null, timestamp), cancellationToken);

        await service.RecordAsync(new PeerSyncStatus(Guid.NewGuid(), PeerSyncState.Retrying, "Offline", timestamp.AddMinutes(1)), cancellationToken);

        Assert.Equal(timestamp, service.LastSuccessfulSyncAtUtc);
        Assert.Equal(PeerSyncState.Retrying, service.LatestStatus?.State);
    }

    private sealed class InMemoryStatusStore : IPeerSyncStatusStore
    {
        public DateTimeOffset? Value { get; private set; }

        public Task<DateTimeOffset?> ReadLastSuccessfulSyncAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Value);
        }

        public Task WriteLastSuccessfulSyncAsync(DateTimeOffset timestampUtc, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Value = timestampUtc;
            return Task.CompletedTask;
        }
    }
}