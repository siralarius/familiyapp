using FamilyApp.Sync.Transport;
using Xunit;

namespace FamilyApp.Sync.Tests;

public class PeerSyncStatusServiceTests
{
    [Fact]
    public async Task Unpaired_third_device_does_not_replace_a_healthy_paired_connection()
    {
        var token = TestContext.Current.CancellationToken;
        var service = new PeerSyncStatusService(new InMemoryStatusStore());
        var paired = Guid.NewGuid(); var unrelated = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await service.RecordAsync(new PeerSyncStatus(paired, PeerSyncState.Synchronized, null, now)
            { PairingCompleted = true }, token);
        await service.RecordAsync(new PeerSyncStatus(unrelated, PeerSyncState.Unpaired, "Peer is not paired.", now.AddSeconds(1)), token);

        Assert.Equal(PeerSyncOverviewState.Synchronized, service.OverviewState);
        Assert.Equal(unrelated, service.LatestStatus?.PeerDeviceId);
        Assert.False(service.LatestStatus?.PairingCompleted);
        Assert.Equal(now, service.LastSuccessfulSyncAtUtc);
    }

    [Fact]
    public async Task Failures_of_another_paired_peer_remain_visible_until_that_peer_recovers()
    {
        var token = TestContext.Current.CancellationToken;
        var service = new PeerSyncStatusService(new InMemoryStatusStore());
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await service.RecordAsync(new PeerSyncStatus(first, PeerSyncState.Retrying, "Offline", now), token);
        Assert.Equal(PeerSyncOverviewState.Retrying, service.OverviewState);
        await service.RecordAsync(new PeerSyncStatus(second, PeerSyncState.Synchronized, null, now.AddSeconds(1)), token);
        Assert.Equal(PeerSyncOverviewState.PartiallySynchronized, service.OverviewState);
        await service.RecordAsync(new PeerSyncStatus(second, PeerSyncState.Synchronized, null, now.AddSeconds(2)), token);
        Assert.Equal(PeerSyncOverviewState.PartiallySynchronized, service.OverviewState);
        await service.RecordAsync(new PeerSyncStatus(first, PeerSyncState.Synchronized, null, now.AddSeconds(3)), token);
        Assert.Equal(PeerSyncOverviewState.Synchronized, service.OverviewState);
    }

    [Fact]
    public async Task Missing_trust_is_actionable_without_other_paired_connections()
    {
        var token = TestContext.Current.CancellationToken;
        var service = new PeerSyncStatusService(new InMemoryStatusStore());
        var peer = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        Assert.Equal(PeerSyncOverviewState.Waiting, service.OverviewState);
        await service.RecordAsync(new PeerSyncStatus(peer, PeerSyncState.Unpaired, null, now), token);
        Assert.Equal(PeerSyncOverviewState.PairingRequired, service.OverviewState);
        await service.RecordAsync(new PeerSyncStatus(peer, PeerSyncState.Synchronized, null, now.AddSeconds(1)), token);
        await service.RecordAsync(new PeerSyncStatus(peer, PeerSyncState.Unpaired, null, now.AddSeconds(2)), token);
        Assert.Equal(PeerSyncOverviewState.PairingRequired, service.OverviewState);
    }

    [Fact]
    public async Task A_global_reconnect_error_clears_after_a_successful_exchange()
    {
        var token = TestContext.Current.CancellationToken;
        var service = new PeerSyncStatusService(new InMemoryStatusStore());
        var peer = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        await service.RecordAsync(new PeerSyncStatus(peer, PeerSyncState.Synchronized, null, now), token);
        await service.RecordAsync(new PeerSyncStatus(Guid.Empty, PeerSyncState.Retrying, "Listener restarting", now.AddSeconds(1)), token);
        Assert.Equal(PeerSyncOverviewState.PartiallySynchronized, service.OverviewState);
        await service.RecordAsync(new PeerSyncStatus(peer, PeerSyncState.Synchronized, null, now.AddSeconds(2)), token);
        Assert.Equal(PeerSyncOverviewState.Synchronized, service.OverviewState);
    }

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
