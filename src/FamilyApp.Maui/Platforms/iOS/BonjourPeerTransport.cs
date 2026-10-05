using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using FamilyApp.Sync.Discovery;
using FamilyApp.Sync.Transport;
using Network;

namespace FamilyApp.Maui.Platforms.iOS;

public sealed class BonjourPeerTransport : IPeerTransport
{
    public async Task<IPeerConnection> ConnectAsync(
        PeerServiceEndpoint peer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peer);
        if (!string.Equals(peer.Type, PeerDiscoveryDefaults.BonjourServiceType, StringComparison.Ordinal))
            throw new ArgumentException("Unsupported Bonjour service type.", nameof(peer));

        var endpoint = NWEndpoint.CreateBonjourService(peer.Name, peer.Type, peer.Domain)
            ?? throw new InvalidOperationException("The Bonjour endpoint could not be created.");
        var connection = new BonjourPeerConnection(new NWConnection(endpoint, new NWParameters()));
        try
        {
            await connection.StartAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}

public sealed class BonjourPeerListener : IPeerListener, IAsyncDisposable
{
    private readonly NWListener _listener;
    private readonly Channel<IPeerConnection> _incoming = Channel.CreateUnbounded<IPeerConnection>();
    private readonly CancellationTokenSource _shutdown = new();

    public BonjourPeerListener(Guid deviceId)
    {
        _listener = NWListener.Create(new NWParameters());
        var descriptor = NWAdvertiseDescriptor.CreateBonjourService(
            PeerServiceEndpoint.CreateServiceName(deviceId),
            PeerDiscoveryDefaults.BonjourServiceType,
            "local.") ?? throw new InvalidOperationException("The Bonjour service could not be advertised.");

        _listener.SetAdvertiseDescriptor(descriptor);
        _listener.SetNewConnectionHandler(connection => _ = QueueConnectionAsync(connection));
        _listener.SetStateChangedHandler((state, error) =>
        {
            if (string.Equals(state.ToString(), "Failed", StringComparison.Ordinal))
                _incoming.Writer.TryComplete(new IOException(error?.LocalizedDescription ?? "The Bonjour listener failed."));
        });
        _listener.Start();
    }

    public async IAsyncEnumerable<IPeerConnection> AcceptAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var connection in _incoming.Reader.ReadAllAsync(cancellationToken))
            yield return connection;
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _listener.Cancel();
        _listener.Dispose();
        _incoming.Writer.TryComplete();
        while (_incoming.Reader.TryRead(out var connection))
            await connection.DisposeAsync();
        _shutdown.Dispose();
    }

    private async Task QueueConnectionAsync(NWConnection nativeConnection)
    {
        var connection = new BonjourPeerConnection(nativeConnection);
        try
        {
            await connection.StartAsync(_shutdown.Token);
            await _incoming.Writer.WriteAsync(connection, _shutdown.Token);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            await connection.DisposeAsync();
        }
        catch (Exception exception)
        {
            await connection.DisposeAsync();
            _incoming.Writer.TryWrite(new FailedPeerConnection(exception));
        }
    }
}

internal sealed class BonjourPeerConnection(NWConnection connection) : IPeerConnection
{
    private const int MaximumMessageLength = 16 * 1024 * 1024;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.SetStateChangeHandler((state, error) =>
        {
            switch (state.ToString())
            {
                case "Ready":
                    started.TrySetResult();
                    break;
                case "Failed":
                case "Cancelled":
                    started.TrySetException(new IOException(error?.LocalizedDescription ?? $"Peer connection {state}.") );
                    break;
            }
        });
        connection.Start();
        await started.Task.WaitAsync(cancellationToken);
    }

    public async Task SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        if (message.Length > MaximumMessageLength) throw new InvalidDataException("The sync message is too large.");
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Send(message.ToArray(), NWContentContext.DefaultMessage, true, error =>
        {
            if (error is null)
                sent.TrySetResult();
            else
                sent.TrySetException(new IOException(error.LocalizedDescription));
        });
        await sent.Task.WaitAsync(cancellationToken);
    }

    public async Task<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        var received = new TaskCompletionSource<ReadOnlyMemory<byte>>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.ReceiveMessage((data, dataSize, _, isComplete, error) =>
        {
            if (error is not null)
            {
                received.TrySetException(new IOException(error.LocalizedDescription));
                return;
            }

            var length = dataSize.ToUInt64();
            if (!isComplete || data == IntPtr.Zero || length == 0 || length > MaximumMessageLength)
            {
                received.TrySetException(new InvalidDataException("The received sync message is invalid."));
                return;
            }

            var message = new byte[checked((int)length)];
            Marshal.Copy(data, message, 0, message.Length);
            received.TrySetResult(message);
        });
        return await received.Task.WaitAsync(cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        connection.Cancel();
        connection.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed class FailedPeerConnection(Exception exception) : IPeerConnection
{
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public Task SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
        => Task.FromException(exception);

    public Task<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken = default)
        => Task.FromException<ReadOnlyMemory<byte>>(exception);
}