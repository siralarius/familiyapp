using System.Collections.Concurrent;

namespace FamilyApp.Sync.Transport;

public enum PeerSyncOverviewState { Waiting, Synchronized, Retrying, PairingRequired, PartiallySynchronized }

public interface IPeerSyncStatusStore
{
    Task<DateTimeOffset?> ReadLastSuccessfulSyncAsync(CancellationToken cancellationToken = default);
    Task WriteLastSuccessfulSyncAsync(DateTimeOffset timestampUtc, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<Guid>> ReadKnownPeersAsync(CancellationToken cancellationToken = default);
    Task WriteKnownPeersAsync(IReadOnlyCollection<Guid> peerIds, CancellationToken cancellationToken = default);
}

public sealed class PeerSyncStatusService
{
    private readonly IPeerSyncStatusStore _store;
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private readonly SemaphoreSlim _recordLock = new(1, 1);
    private bool _initialized;
    private readonly ConcurrentDictionary<Guid, PeerSyncStatus> _peerStatuses = new();
    private PeerSyncStatus? _globalStatus;
    private readonly HashSet<Guid> _knownPeers = [];
    private bool _knownPeersDirty;

    public PeerSyncStatusService(IPeerSyncStatusStore store)
        => _store = store ?? throw new ArgumentNullException(nameof(store));

    public PeerSyncStatus? LatestStatus { get; private set; }
    public DateTimeOffset? LastSuccessfulSyncAtUtc { get; private set; }
    public string? PersistenceWarning { get; private set; }
    public event Action? Changed;

    public PeerSyncOverviewState OverviewState
    {
        get
        {
            var states = _peerStatuses.Values.Select(status => status.State).ToList();
            if (Volatile.Read(ref _globalStatus) is { } globalStatus)
                states.Add(globalStatus.State);
            if (states.Contains(PeerSyncState.Synchronized))
                return states.Any(state => state != PeerSyncState.Synchronized)
                    ? PeerSyncOverviewState.PartiallySynchronized : PeerSyncOverviewState.Synchronized;
            if (states.Contains(PeerSyncState.Retrying)) return PeerSyncOverviewState.Retrying;
            return states.Contains(PeerSyncState.Rejected) || states.Contains(PeerSyncState.Unpaired)
                ? PeerSyncOverviewState.PairingRequired : PeerSyncOverviewState.Waiting;
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;
        await _initializeLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;
            try
            {
                LastSuccessfulSyncAtUtc = await _store.ReadLastSuccessfulSyncAsync(cancellationToken);
                foreach (var peer in await _store.ReadKnownPeersAsync(cancellationToken))
                {
                    if (peer == Guid.Empty) continue;
                    _knownPeers.Add(peer);
                    _peerStatuses.TryAdd(peer, new PeerSyncStatus(peer, PeerSyncState.Retrying,
                        "Waiting for a known device.", DateTimeOffset.MinValue));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                PersistenceWarning = "Sync status could not be loaded.";
            }

            _initialized = true;
        }
        finally
        {
            _initializeLock.Release();
        }
    }

    public async Task RecordAsync(PeerSyncStatus status, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(status);
        await _recordLock.WaitAsync(cancellationToken);
        try
        {
            await InitializeAsync(cancellationToken);
            LatestStatus = status;
            // Discovery of an unrelated device is not a failure of an existing paired connection.
            // Keep LatestStatus as the individual event for invitation completion subscribers.
            if (status.PeerDeviceId != Guid.Empty &&
                (status.State != PeerSyncState.Unpaired || _peerStatuses.ContainsKey(status.PeerDeviceId)))
                _peerStatuses[status.PeerDeviceId] = status;
            if (status.PeerDeviceId == Guid.Empty && status.State != PeerSyncState.Unpaired)
                Volatile.Write(ref _globalStatus, status);
            if (status.PeerDeviceId != Guid.Empty && status.State != PeerSyncState.Unpaired && _knownPeers.Add(status.PeerDeviceId))
                _knownPeersDirty = true;
            if (_knownPeersDirty)
            {
                try { await _store.WriteKnownPeersAsync(_knownPeers.ToArray(), cancellationToken); _knownPeersDirty = false; }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch { PersistenceWarning = "Known devices could not be saved."; }
            }
            if (status.State == PeerSyncState.Synchronized)
            {
                var globalStatus = Volatile.Read(ref _globalStatus);
                if (globalStatus is not null && globalStatus.TimestampUtc <= status.TimestampUtc)
                    Interlocked.CompareExchange(ref _globalStatus, null, globalStatus);
                LastSuccessfulSyncAtUtc = status.TimestampUtc;
                if (!_knownPeersDirty) PersistenceWarning = null;
                try
                {
                    await _store.WriteLastSuccessfulSyncAsync(status.TimestampUtc, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    PersistenceWarning = "Last-sync time could not be saved.";
                }
            }

            Changed?.Invoke();
        }
        finally { _recordLock.Release(); }
    }

    // The caller switches the store's family scope after stopping old-family exchanges.
    public async Task ReloadForFamilyAsync(CancellationToken cancellationToken = default)
    {
        await _recordLock.WaitAsync(cancellationToken);
        try
        {
            await _initializeLock.WaitAsync(cancellationToken);
            try
            {
                _initialized = false;
                _knownPeers.Clear();
                _knownPeersDirty = false;
                _peerStatuses.Clear();
                Volatile.Write(ref _globalStatus, null);
                LatestStatus = null;
                LastSuccessfulSyncAtUtc = null;
                PersistenceWarning = null;
            }
            finally { _initializeLock.Release(); }
            await InitializeAsync(cancellationToken);
            Changed?.Invoke();
        }
        finally { _recordLock.Release(); }
    }
}
