using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
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
    private readonly PeerSyncStatusService? _statusService;

    public PeerSyncCoordinator(
        FamilyDeviceIdentity identity,
        IPeerDiscovery discovery,
        IPeerTransport transport,
        PairingCoordinator pairing,
        ChangeLogEngine changeLog,
        IPeerListener? listener = null,
        PeerSyncStatusService? statusService = null)
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
        _statusService = statusService;
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
                var pairingCompleted = incoming.Span.StartsWith("familyapp://pair/v1/"u8);
                Guid? pairedDeviceId = null;
                if (pairingCompleted)
                {
                    if (incoming.Length > 8192) throw new InvalidDataException("Pairing response is too large.");
                    var response = new UTF8Encoding(false, true).GetString(incoming.Span);
                    var summary = PairingCoordinator.InspectQrPayload(response);
                    if (summary.Kind != PairingQrPayloadKind.Response)
                        throw new InvalidDataException("A pairing response is required.");
                    await _pairing.CompletePairingAsync(_identity, response, cancellationToken);
                    pairedDeviceId = summary.DeviceId;
                    incoming = await connection.ReceiveAsync(cancellationToken);
                }
                var peerDeviceId = EncryptedPeerSyncSession.ReadSenderDeviceId(
                    incoming.Span, _identity.FamilyId, _identity.DeviceId);
                if (pairedDeviceId is Guid expectedPeer && expectedPeer != peerDeviceId)
                    throw new InvalidDataException("Pairing and sync device identities do not match.");
                var trustedSecret = await _pairing.ReadTrustedSecretAsync(_identity, peerDeviceId, cancellationToken);
                if (trustedSecret is null)
                {
                    await ReportAsync(peerDeviceId, PeerSyncState.Unpaired, "Peer is not paired.", cancellationToken);
                    return;
                }

                try
                {
                    var remoteChanges = EncryptedPeerSyncSession.Decrypt(
                        incoming.Span, _identity.FamilyId, peerDeviceId, _identity.DeviceId, trustedSecret);
                    _changeLog.Merge(remoteChanges);
                    await connection.AcknowledgeAsync(cancellationToken);
                    var response = EncryptedPeerSyncSession.Encrypt(
                        _changeLog.Changes.ToArray(), _identity.FamilyId, _identity.DeviceId, peerDeviceId, trustedSecret);
                    await connection.SendAsync(response, cancellationToken);
                    if (pairingCompleted)
                    {
                        var receipt = await connection.ReceiveAsync(cancellationToken);
                        var expected = EncryptedPeerSyncSession.CreatePairingReceipt(trustedSecret, response);
                        if (!CryptographicOperations.FixedTimeEquals(receipt.Span, expected))
                            throw new CryptographicException("Pairing confirmation was not authenticated.");
                        await connection.SendAsync(EncryptedPeerSyncSession.CreatePairingConfirmation(trustedSecret, response), cancellationToken);
                    }
                    await ReportAsync(peerDeviceId, PeerSyncState.Synchronized, cancellationToken: cancellationToken,
                        pairingCompleted: pairingCompleted);
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
                await ReportAsync(Guid.Empty, PeerSyncState.Rejected, exception.GetType().Name, cancellationToken);
            }
            catch (Exception exception)
            {
                await ReportAsync(Guid.Empty, PeerSyncState.Retrying, exception.GetType().Name, cancellationToken);
            }
        }
    }

    public async Task PairAndSynchronizePeerAsync(
        PeerServiceEndpoint peer, string response, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peer);
        var peerDeviceId = peer.DeviceId;
        var summary = PairingCoordinator.InspectQrPayload(response);
        if (peerDeviceId is null || peerDeviceId == _identity.DeviceId ||
            summary.Kind != PairingQrPayloadKind.Response || summary.FamilyId != _identity.FamilyId ||
            summary.DeviceId != _identity.DeviceId)
            throw new InvalidOperationException("The pairing response is not valid for this device.");
        var message = Encoding.UTF8.GetBytes(response);
        if (message.Length > 8192) throw new InvalidDataException("Pairing response is too large.");
        var secret = await _pairing.ReadTrustedSecretAsync(_identity, peerDeviceId.Value, cancellationToken)
            ?? throw new InvalidOperationException("Scan a new pairing offer to join this device.");
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(12));
                try
                {
                    await using var connection = await _transport.ConnectAsync(peer, timeout.Token);
                    await connection.SendAsync(message, timeout.Token);
                    await new EncryptedPeerSyncSession().ExchangeAsync(connection, _changeLog,
                        _identity.FamilyId, _identity.DeviceId, peerDeviceId.Value, secret, timeout.Token, confirmPairing: true);
                    break;
                }
                catch (Exception exception) when (attempt == 0 && !cancellationToken.IsCancellationRequested &&
                    (exception is IOException or TimeoutException ||
                     exception is OperationCanceledException && timeout.IsCancellationRequested))
                {
                    // Reconnect with the same proof; the host recognizes its accepted response.
                    // Neither side announces completion until the fresh encrypted exchange finishes.
                }
            }
            await ReportAsync(peerDeviceId.Value, PeerSyncState.Synchronized,
                cancellationToken: cancellationToken, pairingCompleted: true);
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
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
            await ReportAsync(Guid.Empty, PeerSyncState.Rejected, "Peer identity is invalid.", cancellationToken);
            return;
        }

        var trustedSecret = await _pairing.ReadTrustedSecretAsync(_identity, peerDeviceId.Value, cancellationToken);
        if (trustedSecret is null)
        {
            await ReportAsync(peerDeviceId.Value, PeerSyncState.Unpaired, "Peer is not paired.", cancellationToken);
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
            await ReportAsync(peerDeviceId.Value, PeerSyncState.Synchronized, cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await ReportAsync(peerDeviceId.Value, PeerSyncState.Retrying, exception.GetType().Name, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(trustedSecret);
        }
    }

    private async Task ReportAsync(
        Guid peerDeviceId,
        PeerSyncState state,
        string? detail = null,
        CancellationToken cancellationToken = default,
        bool pairingCompleted = false)
    {
        var status = new PeerSyncStatus(peerDeviceId, state, detail, DateTimeOffset.UtcNow)
        { PairingCompleted = pairingCompleted };
        StatusChanged?.Invoke(status);
        if (_statusService is not null)
            await _statusService.RecordAsync(status, cancellationToken);
    }
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
        CancellationToken cancellationToken = default,
        bool confirmPairing = false)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(changeLog);
        ValidateIdentity(familyId, localDeviceId, peerDeviceId);
        if (trustedSecret.Length != 32) throw new ArgumentException("A 256-bit pairing secret is required.", nameof(trustedSecret));

        var outbound = Encrypt(changeLog.Changes.ToArray(), familyId, localDeviceId, peerDeviceId, trustedSecret.Span);
        await connection.SendAsync(outbound, cancellationToken);
        var inbound = await connection.ReceiveAsync(cancellationToken);
        var remoteChanges = Decrypt(inbound.Span, familyId, peerDeviceId, localDeviceId, trustedSecret.Span);
        var applied = changeLog.Merge(remoteChanges);
        await connection.AcknowledgeAsync(cancellationToken);
        if (confirmPairing)
        {
            await connection.SendAsync(CreatePairingReceipt(trustedSecret.Span, inbound.Span), cancellationToken);
            var confirmation = await connection.ReceiveAsync(cancellationToken);
            var expected = CreatePairingConfirmation(trustedSecret.Span, inbound.Span);
            if (!CryptographicOperations.FixedTimeEquals(confirmation.Span, expected))
                throw new CryptographicException("The offering device did not authenticate pairing completion.");
        }
        return applied;
    }

    internal static byte[] CreatePairingReceipt(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> response)
        => CreatePairingProof(secret, response, "familyapp-pairing-receipt-v1"u8);

    internal static byte[] CreatePairingConfirmation(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> response)
        => CreatePairingProof(secret, response, "familyapp-pairing-confirmed-v1"u8);

    private static byte[] CreatePairingProof(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> response, ReadOnlySpan<byte> context)
    {
        var data = new byte[context.Length + 32];
        context.CopyTo(data);
        SHA256.HashData(response).CopyTo(data, context.Length);
        return HMACSHA256.HashData(secret, data);
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
