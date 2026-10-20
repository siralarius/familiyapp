using System.Collections.Concurrent;

namespace FamilyApp.Sync.Transport;

public enum PeerSyncOverviewState { Waiting, Synchronized, Retrying, PairingRequired, PartiallySynchronized }

public interface IPeerSyncStatusStore
{
    Task<DateTimeOffset?> ReadLastSuccessfulSyncAsync(CancellationToken cancellationToken = default);
    Task WriteLastSuccessfulSyncAsync(DateTimeOffset timestampUtc, CancellationToken cancellationToken = default);
}

public sealed class PeerSyncStatusService
{
    private readonly IPeerSyncStatusStore _store;
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private bool _initialized;
    private readonly ConcurrentDictionary<Guid, PeerSyncStatus> _peerStatuses = new();

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
            if (LatestStatus is { PeerDeviceId: var peerId, State: var latestState } && peerId == Guid.Empty && latestState != PeerSyncState.Unpaired)
                states.Add(latestState);
            if (states.Contains(PeerSyncState.Synchronized))
                return states.Any(state => state != PeerSyncState.Synchronized)
                    ? PeerSyncOverviewState.PartiallySynchronized : PeerSyncOverviewState.Synchronized;
            if (states.Contains(PeerSyncState.Retrying)) return PeerSyncOverviewState.Retrying;
            return states.Contains(PeerSyncState.Rejected) || states.Contains(PeerSyncState.Unpaired) || LatestStatus?.State == PeerSyncState.Unpaired
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
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                PersistenceWarning = "Last-sync time could not be loaded.";
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
        LatestStatus = status;
        // Discovery of an unrelated device is not a failure of an existing paired connection.
        // Keep LatestStatus as the individual event for invitation completion subscribers.
        if (status.PeerDeviceId != Guid.Empty &&
            (status.State != PeerSyncState.Unpaired || _peerStatuses.ContainsKey(status.PeerDeviceId)))
            _peerStatuses[status.PeerDeviceId] = status;
        if (status.State == PeerSyncState.Synchronized)
        {
            LastSuccessfulSyncAtUtc = status.TimestampUtc;
            PersistenceWarning = null;
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
}
