using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using FamilyApp.Sync.ChangeLog;
using FamilyApp.Sync.Discovery;
using FamilyApp.Sync.Pairing;
using FamilyApp.Sync.Transport;
using Xunit;

namespace FamilyApp.Sync.Tests;

public class RemoteRelayPeerTransportTests
{
    [Fact]
    public async Task Paired_devices_converge_through_opaque_relay_and_acknowledge_after_merge()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var familyId = Guid.NewGuid();
        var deviceA = new FamilyDeviceIdentity(familyId, Guid.NewGuid(), "A");
        var deviceB = new FamilyDeviceIdentity(familyId, Guid.NewGuid(), "B");
        var pairingA = CreatePairing(new FakeSecurePairingStore());
        var pairingB = CreatePairing(new FakeSecurePairingStore());
        var offer = await pairingA.CreateOfferAsync(deviceA, timeout.Token);
        var response = await pairingB.AcceptOfferAsync(deviceB, offer, timeout.Token);
        await pairingA.CompletePairingAsync(deviceA, response, timeout.Token);

        var changeA = Change(deviceA.DeviceId, "private-A");
        var changeB = Change(deviceB.DeviceId, "private-B");
        var logA = new ChangeLogEngine();
        var logB = new ChangeLogEngine();
        logA.Apply(changeA);
        logB.Apply(changeB);
        var relay = new FakeRelayClient();
        var transportA = new RemoteRelayPeerTransport(familyId, deviceA.DeviceId, relay);
        var transportB = new RemoteRelayPeerTransport(familyId, deviceB.DeviceId, relay);
        var coordinatorA = new PeerSyncCoordinator(deviceA, new FakeDiscovery(), transportA, pairingA, logA);
        var coordinatorB = new PeerSyncCoordinator(deviceB, new FakeDiscovery(), transportB, pairingB, logB);

        await Task.WhenAll(
            coordinatorA.SynchronizePeerAsync(Endpoint(deviceB.DeviceId), timeout.Token),
            coordinatorB.SynchronizePeerAsync(Endpoint(deviceA.DeviceId), timeout.Token));

        Assert.Equal(logA.Changes, logB.Changes);
        Assert.Equal(2, logA.Changes.Count);
        Assert.Empty(relay.Messages);
        Assert.DoesNotContain("private-A", System.Text.Encoding.UTF8.GetString(relay.LastStoredCiphertext));
        Assert.DoesNotContain("private-B", System.Text.Encoding.UTF8.GetString(relay.LastStoredCiphertext));
    }

    [Fact]
    public async Task Retrying_identical_ciphertext_uses_same_relay_message_id()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var familyId = Guid.NewGuid();
        var sender = Guid.NewGuid();
        var recipient = Guid.NewGuid();
        var relay = new FakeRelayClient();
        var transport = new RemoteRelayPeerTransport(familyId, sender, relay);
        await using var connection = await transport.ConnectAsync(Endpoint(recipient), cancellationToken);
        var encryptedMessage = RandomNumberGenerator.GetBytes(48);

        await connection.SendAsync(encryptedMessage, cancellationToken);
        await connection.SendAsync(encryptedMessage, cancellationToken);

        Assert.Single(relay.Messages);
        Assert.DoesNotContain("household plaintext", System.Text.Encoding.UTF8.GetString(relay.LastStoredCiphertext));
    }

    [Fact]
    public async Task Failed_local_connect_falls_back_to_remote_transport()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var remoteConnection = new NoOpPeerConnection();
        var transport = new FallbackPeerTransport(
            new FailingPeerTransport(),
            new SuccessfulPeerTransport(remoteConnection));

        var connection = await transport.ConnectAsync(Endpoint(Guid.NewGuid()), cancellationToken);

        Assert.Same(remoteConnection, connection);
    }

    private static PeerServiceEndpoint Endpoint(Guid deviceId)
        => new(PeerServiceEndpoint.CreateServiceName(deviceId), PeerDiscoveryDefaults.BonjourServiceType, "local.");

    private static ChangeRecord Change(Guid deviceId, string value)
        => new(Guid.NewGuid(), deviceId.ToString("N"), "shopping", Guid.NewGuid(), 1,
            new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero), false, $"{{\"value\":\"{value}\"}}");

    private static PairingCoordinator CreatePairing(FakeSecurePairingStore store)
        => new(store, new EcdhPairingKeyAgreement());

    private sealed class FakeDiscovery : IPeerDiscovery
    {
        public async IAsyncEnumerable<PeerDiscoveryEvent> WatchAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            yield break;
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

    private sealed class FakeRelayClient : IRemoteRelayClient
    {
        private readonly ConcurrentDictionary<Guid, EncryptedRelayMessage> _messages = new();
        private byte[] _lastStoredCiphertext = [];

        public IReadOnlyCollection<EncryptedRelayMessage> Messages => _messages.Values.ToArray();
        public byte[] LastStoredCiphertext => _lastStoredCiphertext;

        public Task EnqueueAsync(EncryptedRelayMessage message, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _lastStoredCiphertext = message.Ciphertext.ToArray();
            if (_messages.TryGetValue(message.MessageId, out var existing))
            {
                if (existing != message && !existing.Ciphertext.AsSpan().SequenceEqual(message.Ciphertext))
                    throw new InvalidOperationException("Conflicting duplicate message ID.");
            }
            else if (!_messages.TryAdd(message.MessageId, message))
            {
                return EnqueueAsync(message, cancellationToken);
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<EncryptedRelayMessage>> GetPendingAsync(
            Guid familyId, Guid recipientDeviceId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<EncryptedRelayMessage> pending = _messages.Values
                .Where(message => message.FamilyId == familyId && message.RecipientDeviceId == recipientDeviceId)
                .OrderBy(message => message.CreatedAtUtc)
                .ToArray();
            return Task.FromResult(pending);
        }

        public Task AcknowledgeAsync(
            Guid familyId, Guid recipientDeviceId, Guid messageId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_messages.TryGetValue(messageId, out var message) &&
                message.FamilyId == familyId && message.RecipientDeviceId == recipientDeviceId)
                _messages.TryRemove(messageId, out _);
            return Task.CompletedTask;
        }
    }

    private sealed class FailingPeerTransport : IPeerTransport
    {
        public Task<IPeerConnection> ConnectAsync(PeerServiceEndpoint peer, CancellationToken cancellationToken = default)
            => Task.FromException<IPeerConnection>(new IOException("Local peer unavailable."));
    }

    private sealed class SuccessfulPeerTransport(IPeerConnection connection) : IPeerTransport
    {
        public Task<IPeerConnection> ConnectAsync(PeerServiceEndpoint peer, CancellationToken cancellationToken = default)
            => Task.FromResult(connection);
    }

    private sealed class NoOpPeerConnection : IPeerConnection
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<ReadOnlyMemory<byte>>(Array.Empty<byte>());
    }
}