using System.Security.Cryptography;
using System.Text.Json;
using FamilyApp.Sync.ChangeLog;
using FamilyApp.Sync.Discovery;
using FamilyApp.Sync.Pairing;

namespace FamilyApp.Sync.Transport;

public sealed class PeerSyncCoordinator
{
    private readonly FamilyDeviceIdentity _identity;
    private readonly IPeerDiscovery _discovery;
    private readonly IPeerTransport _transport;
    private readonly PairingCoordinator _pairing;
    private readonly ChangeLogEngine _changeLog;
    private readonly IPeerListener? _listener;

    public PeerSyncCoordinator(
        FamilyDeviceIdentity identity,
        IPeerDiscovery discovery,
        IPeerTransport transport,
        PairingCoordinator pairing,
        ChangeLogEngine changeLog,
        IPeerListener? listener = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(pairing);
        ArgumentNullException.ThrowIfNull(changeLog);
        _identity = identity;
        _discovery = discovery;
        _transport = transport;
        _pairing = pairing;
        _changeLog = changeLog;
        _listener = listener;
    }

    public event Action<PeerSyncStatus>? StatusChanged;

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var discoveryTask = WatchForPeersAsync(cancellationToken);
        if (_listener is null)
        {
            await discoveryTask;
            return;
        }

        await Task.WhenAll(discoveryTask, ListenForPeersAsync(cancellationToken));
    }

    public async Task HandleIncomingPeerAsync(IPeerConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using (connection)
        {
            try
            {
                var incoming = await connection.ReceiveAsync(cancellationToken);
                var peerDeviceId = EncryptedPeerSyncSession.ReadSenderDeviceId(
                    incoming.Span, _identity.FamilyId, _identity.DeviceId);
                var trustedSecret = await _pairing.ReadTrustedSecretAsync(_identity, peerDeviceId, cancellationToken);
                if (trustedSecret is null)
                {
                    Report(peerDeviceId, PeerSyncState.Rejected, "Peer is not paired.");
                    return;
                }

                try
                {
                    var remoteChanges = EncryptedPeerSyncSession.Decrypt(
                        incoming.Span, _identity.FamilyId, peerDeviceId, _identity.DeviceId, trustedSecret);
                    _changeLog.Merge(remoteChanges);
                    var response = EncryptedPeerSyncSession.Encrypt(
                        _changeLog.Changes.ToArray(), _identity.FamilyId, _identity.DeviceId, peerDeviceId, trustedSecret);
                    await connection.SendAsync(response, cancellationToken);
                    Report(peerDeviceId, PeerSyncState.Synchronized);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(trustedSecret);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (CryptographicException exception)
            {
                Report(Guid.Empty, PeerSyncState.Rejected, exception.GetType().Name);
            }
            catch (Exception exception)
            {
                Report(Guid.Empty, PeerSyncState.Retrying, exception.GetType().Name);
            }
        }
    }

    private async Task WatchForPeersAsync(CancellationToken cancellationToken)
    {
        await foreach (var peerEvent in _discovery.WatchAsync(cancellationToken))
        {
            if (peerEvent.Kind == PeerDiscoveryEventKind.Available && peerEvent.Endpoint is not null)
                await SynchronizePeerAsync(peerEvent.Endpoint, cancellationToken);
        }
    }

    private async Task ListenForPeersAsync(CancellationToken cancellationToken)
    {
        await foreach (var connection in _listener!.AcceptAsync(cancellationToken))
            await HandleIncomingPeerAsync(connection, cancellationToken);
    }

    public async Task SynchronizePeerAsync(
        PeerServiceEndpoint peer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peer);
        var peerDeviceId = peer.DeviceId;
        if (peerDeviceId is null || peerDeviceId == _identity.DeviceId)
        {
            Report(Guid.Empty, PeerSyncState.Rejected, "Peer identity is invalid.");
            return;
        }

        var trustedSecret = await _pairing.ReadTrustedSecretAsync(_identity, peerDeviceId.Value, cancellationToken);
        if (trustedSecret is null)
        {
            Report(peerDeviceId.Value, PeerSyncState.Rejected, "Peer is not paired.");
            return;
        }

        try
        {
            await using var connection = await _transport.ConnectAsync(peer, cancellationToken);
            var session = new EncryptedPeerSyncSession();
            await session.ExchangeAsync(
                connection,
                _changeLog,
                _identity.FamilyId,
                _identity.DeviceId,
                peerDeviceId.Value,
                trustedSecret,
                cancellationToken);
            Report(peerDeviceId.Value, PeerSyncState.Synchronized);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Report(peerDeviceId.Value, PeerSyncState.Retrying, exception.GetType().Name);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(trustedSecret);
        }
    }

    private void Report(Guid peerDeviceId, PeerSyncState state, string? detail = null)
        => StatusChanged?.Invoke(new PeerSyncStatus(peerDeviceId, state, detail));
}

public sealed class EncryptedPeerSyncSession
{
    private const int ProtocolVersion = 1;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int MaximumMessageLength = 16 * 1024 * 1024;

    public async Task<int> ExchangeAsync(
        IPeerConnection connection,
        ChangeLogEngine changeLog,
        Guid familyId,
        Guid localDeviceId,
        Guid peerDeviceId,
        ReadOnlyMemory<byte> trustedSecret,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(changeLog);
        ValidateIdentity(familyId, localDeviceId, peerDeviceId);
        if (trustedSecret.Length != 32) throw new ArgumentException("A 256-bit pairing secret is required.", nameof(trustedSecret));

        var outbound = Encrypt(changeLog.Changes.ToArray(), familyId, localDeviceId, peerDeviceId, trustedSecret.Span);
        await connection.SendAsync(outbound, cancellationToken);
        var inbound = await connection.ReceiveAsync(cancellationToken);
        var remoteChanges = Decrypt(inbound.Span, familyId, peerDeviceId, localDeviceId, trustedSecret.Span);
        return changeLog.Merge(remoteChanges);
    }

    internal static byte[] Encrypt(
        IReadOnlyCollection<ChangeRecord> changes,
        Guid familyId,
        Guid senderDeviceId,
        Guid recipientDeviceId,
        ReadOnlySpan<byte> trustedSecret)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(changes);
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagLength];
        var associatedData = CreateAssociatedData(familyId, senderDeviceId, recipientDeviceId);
        try
        {
            using (var aes = new AesGcm(trustedSecret, TagLength))
                aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);

            return JsonSerializer.SerializeToUtf8Bytes(new EncryptedChangeEnvelope(
                ProtocolVersion,
                familyId,
                senderDeviceId,
                recipientDeviceId,
                nonce,
                ciphertext,
                tag));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    internal static IReadOnlyCollection<ChangeRecord> Decrypt(
        ReadOnlySpan<byte> message,
        Guid familyId,
        Guid expectedSender,
        Guid expectedRecipient,
        ReadOnlySpan<byte> trustedSecret)
    {
        if (message.Length > MaximumMessageLength) throw new InvalidDataException("The sync message is too large.");
        var envelope = JsonSerializer.Deserialize<EncryptedChangeEnvelope>(message)
            ?? throw new InvalidDataException("The sync message is invalid.");
        if (envelope.Version != ProtocolVersion || envelope.FamilyId != familyId ||
            envelope.SenderDeviceId != expectedSender || envelope.RecipientDeviceId != expectedRecipient ||
            envelope.Nonce?.Length != NonceLength || envelope.Tag?.Length != TagLength || envelope.Ciphertext is null)
            throw new InvalidDataException("The sync message is not addressed to this trusted device.");

        var plaintext = new byte[envelope.Ciphertext.Length];
        var associatedData = CreateAssociatedData(familyId, expectedSender, expectedRecipient);
        try
        {
            using (var aes = new AesGcm(trustedSecret, TagLength))
                aes.Decrypt(envelope.Nonce, envelope.Ciphertext, envelope.Tag, plaintext, associatedData);

            return JsonSerializer.Deserialize<ChangeRecord[]>(plaintext)
                ?? throw new InvalidDataException("The sync message payload is invalid.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    internal static Guid ReadSenderDeviceId(ReadOnlySpan<byte> message, Guid expectedFamilyId, Guid expectedRecipient)
    {
        if (message.Length > MaximumMessageLength) throw new InvalidDataException("The sync message is too large.");
        var envelope = JsonSerializer.Deserialize<EncryptedChangeEnvelope>(message)
            ?? throw new InvalidDataException("The sync message is invalid.");
        if (envelope.Version != ProtocolVersion || envelope.FamilyId != expectedFamilyId ||
            envelope.RecipientDeviceId != expectedRecipient || envelope.SenderDeviceId == Guid.Empty)
            throw new InvalidDataException("The sync message is not addressed to this family device.");
        return envelope.SenderDeviceId;
    }

    private static byte[] CreateAssociatedData(Guid familyId, Guid senderDeviceId, Guid recipientDeviceId)
    {
        var data = new byte[1 + 16 + 16 + 16];
        data[0] = ProtocolVersion;
        familyId.TryWriteBytes(data.AsSpan(1, 16));
        senderDeviceId.TryWriteBytes(data.AsSpan(17, 16));
        recipientDeviceId.TryWriteBytes(data.AsSpan(33, 16));
        return data;
    }

    private static void ValidateIdentity(Guid familyId, Guid localDeviceId, Guid peerDeviceId)
    {
        if (familyId == Guid.Empty) throw new ArgumentException("Family ID is required.", nameof(familyId));
        if (localDeviceId == Guid.Empty) throw new ArgumentException("Local device ID is required.", nameof(localDeviceId));
        if (peerDeviceId == Guid.Empty || peerDeviceId == localDeviceId)
            throw new ArgumentException("A different peer device ID is required.", nameof(peerDeviceId));
    }

    private sealed record EncryptedChangeEnvelope(
        int Version,
        Guid FamilyId,
        Guid SenderDeviceId,
        Guid RecipientDeviceId,
        byte[] Nonce,
        byte[] Ciphertext,
        byte[] Tag);
}