using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using CoreFoundation;
using FamilyApp.Sync.Discovery;
using FamilyApp.Sync.Transport;
using Network;

namespace FamilyApp.Maui.Platforms.iOS;

public sealed class BonjourPeerTransport : IPeerTransport
{
    public async Task<IPeerConnection> ConnectAsync(PeerServiceEndpoint peer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peer);
        if (peer.Type != PeerDiscoveryDefaults.BonjourServiceType)
            throw new ArgumentException("Unsupported Bonjour service type.", nameof(peer));
        using var endpoint = NWEndpoint.CreateBonjourService(peer.Name, peer.Type, peer.Domain)
            ?? throw new InvalidOperationException("The Bonjour endpoint could not be created.");
        using var parameters = NWParameters.CreateTcp();
        var connection = new BonjourPeerConnection(new NWConnection(endpoint, parameters));
        try { await connection.StartAsync(cancellationToken); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }
}

public sealed class BonjourPeerListener : IPeerListener, IAsyncDisposable
{
    private readonly NWListener _listener;
    private readonly DispatchQueue _queue = new("familyapp.listener");
    private readonly Channel<IPeerConnection> _incoming = Channel.CreateBounded<IPeerConnection>(16);
    private readonly CancellationTokenSource _shutdown = new();

    public BonjourPeerListener(Guid deviceId)
    {
        using var parameters = NWParameters.CreateTcp();
        _listener = NWListener.Create(parameters)
            ?? throw new InvalidOperationException("The Bonjour listener could not be created.");
        using var descriptor = NWAdvertiseDescriptor.CreateBonjourService(
            PeerServiceEndpoint.CreateServiceName(deviceId), PeerDiscoveryDefaults.BonjourServiceType, "local.")
            ?? throw new InvalidOperationException("The Bonjour service could not be advertised.");
        _listener.SetAdvertiseDescriptor(descriptor);
        _listener.SetNewConnectionHandler(connection => _ = QueueConnectionAsync(connection));
        _listener.SetStateChangedHandler((state, _) =>
        {
            if (state == NWListenerState.Failed)
                _incoming.Writer.TryComplete(new IOException("The Bonjour listener failed."));
        });
        _listener.SetQueue(_queue);
        _listener.Start();
    }

    public async IAsyncEnumerable<IPeerConnection> AcceptAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var connection in _incoming.Reader.ReadAllAsync(cancellationToken)) yield return connection;
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _listener.Cancel();
        _listener.Dispose();
        _queue.Dispose();
        _incoming.Writer.TryComplete();
        while (_incoming.Reader.TryRead(out var connection)) await connection.DisposeAsync();
    }

    private async Task QueueConnectionAsync(NWConnection nativeConnection)
    {
        var connection = new BonjourPeerConnection(nativeConnection);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await connection.StartAsync(timeout.Token);
            if (!_incoming.Writer.TryWrite(connection)) await connection.DisposeAsync();
        }
        catch { await connection.DisposeAsync(); }
    }
}

internal sealed class BonjourPeerConnection(NWConnection connection) : IPeerConnection
{
    private const int MaximumMessageLength = 16 * 1024 * 1024;
    private readonly DispatchQueue _queue = new("familyapp.connection");

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.SetStateChangeHandler((state, _) =>
        {
            if (state == NWConnectionState.Ready) started.TrySetResult();
            else if (state is NWConnectionState.Failed or NWConnectionState.Cancelled)
                started.TrySetException(new IOException($"Peer connection {state}."));
        });
        connection.SetQueue(_queue);
        connection.Start();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
    }

    public async Task SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        if (message.Length is 0 or > MaximumMessageLength) throw new InvalidDataException("Invalid sync message length.");
        // TCP is a byte stream, so frame each message explicitly.
        var frame = new byte[sizeof(int) + message.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, message.Length);
        message.Span.CopyTo(frame.AsSpan(sizeof(int)));
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Send(frame, NWContentContext.DefaultMessage, true, error =>
        {
            if (error is null) sent.TrySetResult();
            else sent.TrySetException(new IOException("Peer send failed."));
        });
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
    }

    public async Task<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        var header = await ReadExactlyAsync(sizeof(int), cancellationToken);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is <= 0 or > MaximumMessageLength) throw new InvalidDataException("Invalid sync message length.");
        return await ReadExactlyAsync(length, cancellationToken);
    }

    private async Task<byte[]> ReadExactlyAsync(int length, CancellationToken cancellationToken)
    {
        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var received = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var destinationOffset = offset;
            var remaining = length - offset;
            connection.Receive(1, (uint)remaining, (data, dataSize, _, _, error) =>
            {
                var count = checked((int)dataSize);
                if (error is not null || data == IntPtr.Zero || count <= 0 || count > remaining)
                {
                    received.TrySetException(new IOException("Peer receive failed or connection closed."));
                    return;
                }
                Marshal.Copy(data, buffer, destinationOffset, count);
                received.TrySetResult(count);
            });
            offset += await received.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        }
        return buffer;
    }

    public ValueTask DisposeAsync()
    {
        connection.Cancel();
        connection.Dispose();
        _queue.Dispose();
        return ValueTask.CompletedTask;
    }
}
