namespace FamilyApp.Sync.Transport;

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

    public PeerSyncStatusService(IPeerSyncStatusStore store)
        => _store = store ?? throw new ArgumentNullException(nameof(store));

    public PeerSyncStatus? LatestStatus { get; private set; }
    public DateTimeOffset? LastSuccessfulSyncAtUtc { get; private set; }
    public string? PersistenceWarning { get; private set; }
    public event Action? Changed;

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