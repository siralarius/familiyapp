using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using FamilyApp.Sync.ChangeLog;
using FamilyApp.Sync.Discovery;
using FamilyApp.Sync.Pairing;
using FamilyApp.Sync.Transport;
using Xunit;

namespace FamilyApp.Sync.Tests;

public class PeerSyncCoordinatorTests
{
    [Fact]
    public async Task Paired_devices_exchange_encrypted_changes_and_converge()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (identityA, identityB, pairingA, pairingB) = await PairedDevicesAsync(cancellationToken);
        var changeA = Change(identityA.DeviceId, "A");
        var changeB = Change(identityB.DeviceId, "B");
        var logA = new ChangeLogEngine();
        var logB = new ChangeLogEngine();
        logA.Apply(changeA);
        logB.Apply(changeB);
        var (connectionA, connectionB) = InMemoryPeerConnection.CreatePair();
        var coordinatorA = Coordinator(identityA, pairingA, logA, new FakePeerTransport(connectionA));
        var coordinatorB = Coordinator(identityB, pairingB, logB, new FakePeerTransport(connectionB));

        await Task.WhenAll(
            coordinatorA.SynchronizePeerAsync(Endpoint(identityB.DeviceId), cancellationToken),
            coordinatorB.SynchronizePeerAsync(Endpoint(identityA.DeviceId), cancellationToken));

        Assert.Equal(logA.Changes, logB.Changes);
        Assert.Equal(2, logA.Changes.Count);
    }

    [Fact]
    public async Task Unpaired_discovered_peer_is_rejected_before_transport_connects()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var identity = Identity(Guid.NewGuid());
        var unpairedDeviceId = Guid.NewGuid();
        var transport = new FakePeerTransport(null);
        var coordinator = Coordinator(identity, Pairing(new FakeSecurePairingStore()), new ChangeLogEngine(), transport);
        PeerSyncStatus? reportedStatus = null;
        coordinator.StatusChanged += status => reportedStatus = status;

        await coordinator.SynchronizePeerAsync(Endpoint(unpairedDeviceId), cancellationToken);

        Assert.Equal(0, transport.ConnectCount);
        Assert.Equal(PeerSyncState.Rejected, reportedStatus?.State);
        Assert.Equal(unpairedDeviceId, reportedStatus?.PeerDeviceId);
    }

    [Fact]
    public async Task Trusted_incoming_connection_merges_changes_and_returns_an_encrypted_snapshot()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (identityA, identityB, pairingA, pairingB) = await PairedDevicesAsync(cancellationToken);
        var changeA = Change(identityA.DeviceId, "A");
        var changeB = Change(identityB.DeviceId, "B");
        var logA = new ChangeLogEngine();
        var logB = new ChangeLogEngine();
        logA.Apply(changeA);
        logB.Apply(changeB);
        var (connectionA, connectionB) = InMemoryPeerConnection.CreatePair();
        var coordinatorA = Coordinator(identityA, pairingA, logA, new FakePeerTransport(connectionA));
        var coordinatorB = Coordinator(identityB, pairingB, logB, new FakePeerTransport(null));

        await Task.WhenAll(
            coordinatorA.SynchronizePeerAsync(Endpoint(identityB.DeviceId), cancellationToken),
            coordinatorB.HandleIncomingPeerAsync(connectionB, cancellationToken));

        Assert.Equal(logA.Changes, logB.Changes);
        Assert.Equal(2, logA.Changes.Count);
    }

    [Fact]
    public async Task Tampered_change_envelope_is_not_merged()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (identityA, identityB, pairingA, pairingB) = await PairedDevicesAsync(cancellationToken);
        var changeA = Change(identityA.DeviceId, "A");
        var changeB = Change(identityB.DeviceId, "B");
        var logA = new ChangeLogEngine();
        var logB = new ChangeLogEngine();
        logA.Apply(changeA);
        logB.Apply(changeB);
        var (connectionA, connectionB) = InMemoryPeerConnection.CreatePair();
        var coordinatorA = Coordinator(identityA, pairingA, logA, new FakePeerTransport(connectionA));
        var coordinatorB = Coordinator(identityB, pairingB, logB, new FakePeerTransport(new TamperingPeerConnection(connectionB)));
        PeerSyncStatus? statusB = null;
        coordinatorB.StatusChanged += status => statusB = status;

        await Task.WhenAll(
            coordinatorA.SynchronizePeerAsync(Endpoint(identityB.DeviceId), cancellationToken),
            coordinatorB.SynchronizePeerAsync(Endpoint(identityA.DeviceId), cancellationToken));

        Assert.DoesNotContain(logB.Changes, change => change.ChangeId == changeA.ChangeId);
        Assert.Equal(PeerSyncState.Retrying, statusB?.State);
    }

    [Fact]
    public async Task Discovery_loop_reconnects_after_a_failed_connection()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var (identityA, identityB, pairingA, pairingB) = await PairedDevicesAsync(cancellationToken);
        var changeA = Change(identityA.DeviceId, "A");
        var changeB = Change(identityB.DeviceId, "B");
        var logA = new ChangeLogEngine();
        var logB = new ChangeLogEngine();
        logA.Apply(changeA);
        logB.Apply(changeB);
        var (connectionA, connectionB) = InMemoryPeerConnection.CreatePair();
        var discovery = new FakePeerDiscovery();
        var transportA = new RetryOncePeerTransport(connectionA);
        var coordinatorA = Coordinator(identityA, pairingA, logA, transportA, discovery);
        var coordinatorB = Coordinator(identityB, pairingB, logB, new FakePeerTransport(null));
        var statuses = Channel.CreateUnbounded<PeerSyncStatus>();
        coordinatorA.StatusChanged += status => statuses.Writer.TryWrite(status);
        var coordinatorTask = coordinatorA.RunAsync(stopping.Token);

        discovery.Publish(new PeerDiscoveryEvent(PeerDiscoveryEventKind.Available, Endpoint(identityB.DeviceId)));
        Assert.Equal(PeerSyncState.Retrying, (await statuses.Reader.ReadAsync(cancellationToken)).State);

        var listenerTask = coordinatorB.HandleIncomingPeerAsync(connectionB, cancellationToken);
        discovery.Publish(new PeerDiscoveryEvent(PeerDiscoveryEventKind.Available, Endpoint(identityB.DeviceId)));
        Assert.Equal(PeerSyncState.Synchronized, (await statuses.Reader.ReadAsync(cancellationToken)).State);
        await listenerTask;
        Assert.Equal(logA.Changes, logB.Changes);

        stopping.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await coordinatorTask);
    }

    private static PeerSyncCoordinator Coordinator(
        FamilyDeviceIdentity identity,
        PairingCoordinator pairing,
        ChangeLogEngine changeLog,
        IPeerTransport transport,
        IPeerDiscovery? discovery = null)
        => new(identity, discovery ?? new FakePeerDiscovery(), transport, pairing, changeLog);

    private static PeerServiceEndpoint Endpoint(Guid deviceId)
        => new(PeerServiceEndpoint.CreateServiceName(deviceId), PeerDiscoveryDefaults.BonjourServiceType, "local.");

    private static ChangeRecord Change(Guid deviceId, string value)
        => new(Guid.NewGuid(), deviceId.ToString("N"), "shopping", Guid.NewGuid(), 1,
            new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero), false, $"{{\"value\":\"{value}\"}}");

    private static FamilyDeviceIdentity Identity(Guid familyId)
        => new(familyId, Guid.NewGuid(), "Test device");

    private static PairingCoordinator Pairing(FakeSecurePairingStore store)
        => new(store, new EcdhPairingKeyAgreement());

    private static async Task<(FamilyDeviceIdentity DeviceA, FamilyDeviceIdentity DeviceB, PairingCoordinator PairingA, PairingCoordinator PairingB)> PairedDevicesAsync(CancellationToken cancellationToken)
    {
        var familyId = Guid.NewGuid();
        var deviceA = Identity(familyId);
        var deviceB = Identity(familyId);
        var pairingA = Pairing(new FakeSecurePairingStore());
        var pairingB = Pairing(new FakeSecurePairingStore());
        var offer = await pairingA.CreateOfferAsync(deviceA, cancellationToken);
        var response = await pairingB.AcceptOfferAsync(deviceB, offer, cancellationToken);
        await pairingA.CompletePairingAsync(deviceA, response, cancellationToken);
        return (deviceA, deviceB, pairingA, pairingB);
    }

    private sealed class FakePeerDiscovery : IPeerDiscovery
    {
        private readonly Channel<PeerDiscoveryEvent> _events = Channel.CreateUnbounded<PeerDiscoveryEvent>();

        public void Publish(PeerDiscoveryEvent peerEvent) => _events.Writer.TryWrite(peerEvent);

        public async IAsyncEnumerable<PeerDiscoveryEvent> WatchAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var peerEvent in _events.Reader.ReadAllAsync(cancellationToken))
                yield return peerEvent;
        }
    }

    private sealed class FakePeerTransport(IPeerConnection? connection) : IPeerTransport
    {
        public int ConnectCount { get; private set; }

        public Task<IPeerConnection> ConnectAsync(PeerServiceEndpoint peer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConnectCount++;
            return connection is null
                ? Task.FromException<IPeerConnection>(new InvalidOperationException("No connection configured."))
                : Task.FromResult(connection);
        }
    }

    private sealed class RetryOncePeerTransport(IPeerConnection connection) : IPeerTransport
    {
        private int _attemptCount;

        public Task<IPeerConnection> ConnectAsync(PeerServiceEndpoint peer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _attemptCount) == 1)
                return Task.FromException<IPeerConnection>(new IOException("Simulated temporary network loss."));
            return Task.FromResult(connection);
        }
    }

    private sealed class FakeSecurePairingStore : ISecurePairingStore
    {
        private readonly Dictionary<string, byte[]> _values = new(StringComparer.Ordinal);

        public Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult(_values.TryGetValue(key, out var value) ? value.ToArray() : null);

        public Task WriteAsync(string key, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default)
        {
            _values[key] = value.ToArray();
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            if (_values.Remove(key, out var value)) CryptographicOperations.ZeroMemory(value);
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryPeerConnection(
        ChannelWriter<byte[]> outgoing,
        ChannelReader<byte[]> incoming) : IPeerConnection
    {
        public static (IPeerConnection A, IPeerConnection B) CreatePair()
        {
            var aToB = Channel.CreateUnbounded<byte[]>();
            var bToA = Channel.CreateUnbounded<byte[]>();
            return (
                new InMemoryPeerConnection(aToB.Writer, bToA.Reader),
                new InMemoryPeerConnection(bToA.Writer, aToB.Reader));
        }

        public ValueTask DisposeAsync()
        {
            outgoing.TryComplete();
            return ValueTask.CompletedTask;
        }

        public Task SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
            => outgoing.WriteAsync(message.ToArray(), cancellationToken).AsTask();

        public async Task<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken = default)
            => await incoming.ReadAsync(cancellationToken);
    }

    private sealed class TamperingPeerConnection(IPeerConnection inner) : IPeerConnection
    {
        public ValueTask DisposeAsync() => inner.DisposeAsync();

        public Task SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
            => inner.SendAsync(message, cancellationToken);

        public async Task<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken = default)
        {
            var json = await inner.ReceiveAsync(cancellationToken);
            var envelope = JsonNode.Parse(json.Span)?.AsObject()
                ?? throw new InvalidDataException("The test envelope is invalid.");
            var ciphertext = Convert.FromBase64String(envelope["Ciphertext"]!.GetValue<string>());
            ciphertext[0] ^= 0x80;
            envelope["Ciphertext"] = Convert.ToBase64String(ciphertext);
            return JsonSerializer.SerializeToUtf8Bytes(envelope);
        }
    }
}