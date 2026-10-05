using System.Collections.Concurrent;
using FamilyApp.Maui.Platforms.iOS;
using FamilyApp.Sync.ChangeLog;
using FamilyApp.Sync.Discovery;
using FamilyApp.Sync.Pairing;
using FamilyApp.Sync.Transport;

namespace FamilyApp.Maui;

public sealed class NativeDeviceSync : IDevicePairingService
{
    private readonly ChangeLogEngine _engine;
    private readonly PairingCoordinator _pairing;
    private readonly PeerSyncStatusService _status;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;
    private bool _active;

    public NativeDeviceSync(ChangeLogEngine engine, PairingCoordinator pairing, PeerSyncStatusService status)
    {
        _engine = engine;
        _pairing = pairing;
        _status = status;
        var stored = Preferences.Default.Get("family-id", "");
        var familyId = Guid.TryParse(stored, out var id) ? id : Guid.NewGuid();
        Preferences.Default.Set("family-id", familyId.ToString());
        Identity = new FamilyDeviceIdentity(familyId,
            Guid.Parse(Preferences.Default.Get("device-id", "")), DeviceInfo.Name);
    }

    public FamilyDeviceIdentity Identity { get; private set; }

    public void Resume() { _active = true; _ = ReconcileAsync(); }
    public void Suspend() { _active = false; _runCancellation?.Cancel(); _ = ReconcileAsync(); }

    public async Task JoinFamilyAsync(Guid familyId)
    {
        if (familyId == Guid.Empty) throw new ArgumentException("Enter a valid family code.");
        await _lifecycle.WaitAsync();
        try
        {
            if (familyId == Identity.FamilyId) return;
            if (_engine.Changes.Count != 0)
                throw new InvalidOperationException("Join your family before adding household data on this phone.");
            await StopAsync();
            if (_engine.Changes.Count != 0)
            {
                if (_active) Start();
                throw new InvalidOperationException("Join your family before adding household data on this phone.");
            }
            Identity = new FamilyDeviceIdentity(familyId, Identity.DeviceId, Identity.DisplayName);
            Preferences.Default.Set("family-id", familyId.ToString());
            if (_active) Start();
        }
        finally { _lifecycle.Release(); }
    }

    public Task<string> CreateOfferAsync() => _pairing.CreateOfferAsync(Identity);
    public Task<string> AcceptOfferAsync(string offer) => _pairing.AcceptOfferAsync(Identity, offer);
    public Task CompletePairingAsync(string response) => _pairing.CompletePairingAsync(Identity, response);

    private async Task ReconcileAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (!_active) await StopAsync();
            else if (_runTask is null || _runTask.IsCompleted) Start();
        }
        finally { _lifecycle.Release(); }
    }

    private void Start()
    {
        _runCancellation?.Dispose();
        _runCancellation = new CancellationTokenSource();
        _runTask = RunAsync(_runCancellation.Token);
    }

    private async Task StopAsync()
    {
        _runCancellation?.Cancel();
        if (_runTask is not null) await _runTask;
        _runCancellation?.Dispose();
        _runCancellation = null;
        _runTask = null;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                await using var listener = new BonjourPeerListener(Identity.DeviceId);
                var discovery = new RememberingDiscovery(new BonjourPeerDiscovery(), Identity.DeviceId);
                var coordinator = new PeerSyncCoordinator(Identity, discovery, new BonjourPeerTransport(),
                    _pairing, _engine, listener, _status);
                var watch = coordinator.RunAsync(session.Token);
                var retry = RetryKnownPeersAsync(coordinator, discovery, session.Token);
                await Task.WhenAny(watch, retry);
                session.Cancel();
                await Task.WhenAll(watch, retry);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch
            {
                session.Cancel();
                await _status.RecordAsync(new PeerSyncStatus(Guid.Empty, PeerSyncState.Retrying,
                    "Local sync is reconnecting.", DateTimeOffset.UtcNow));
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        }
    }

    private static async Task RetryKnownPeersAsync(PeerSyncCoordinator coordinator,
        RememberingDiscovery discovery, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (await timer.WaitForNextTickAsync(cancellationToken))
            foreach (var peer in discovery.Peers.Values)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                try { await coordinator.SynchronizePeerAsync(peer, timeout.Token); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            }
    }

    private sealed class RememberingDiscovery(IPeerDiscovery inner, Guid localDeviceId) : IPeerDiscovery
    {
        public ConcurrentDictionary<string, PeerServiceEndpoint> Peers { get; } = new();
        public async IAsyncEnumerable<PeerDiscoveryEvent> WatchAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var change in inner.WatchAsync(cancellationToken))
            {
                if (change.Endpoint is { } endpoint && endpoint.DeviceId != localDeviceId)
                {
                    if (change.Kind == PeerDiscoveryEventKind.Lost) Peers.TryRemove(endpoint.Id, out _);
                    else if (change.Kind == PeerDiscoveryEventKind.Available && Peers.Count < 32)
                        Peers[endpoint.Id] = endpoint;
                }
                // The periodic loop serializes outbound exchanges and retries after edits and pairing.
                if (change.Kind == PeerDiscoveryEventKind.StateChanged) yield return change;
            }
        }
    }
}
