using FamilyApp.Sync.Discovery;

namespace FamilyApp.Sync.Transport;

public interface IPeerConnection : IAsyncDisposable
{
    Task SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default);
    Task<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken = default);
    Task AcknowledgeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public interface IPeerTransport
{
    Task<IPeerConnection> ConnectAsync(PeerServiceEndpoint peer, CancellationToken cancellationToken = default);
}

public interface IPeerListener
{
    IAsyncEnumerable<IPeerConnection> AcceptAsync(CancellationToken cancellationToken = default);
}

public enum PeerSyncState
{
    Rejected,
    Synchronized,
    Retrying
}

public sealed record PeerSyncStatus(
    Guid PeerDeviceId,
    PeerSyncState State,
    string? Detail,
    DateTimeOffset TimestampUtc);