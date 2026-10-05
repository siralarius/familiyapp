using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FamilyApp.Sync.Pairing;

public sealed record FamilyDeviceIdentity
{
    public FamilyDeviceIdentity(Guid familyId, Guid deviceId, string displayName)
    {
        if (familyId == Guid.Empty) throw new ArgumentException("Family ID is required.", nameof(familyId));
        if (deviceId == Guid.Empty) throw new ArgumentException("Device ID is required.", nameof(deviceId));
        if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("Display name is required.", nameof(displayName));

        FamilyId = familyId;
        DeviceId = deviceId;
        DisplayName = displayName;
    }

    public Guid FamilyId { get; }
    public Guid DeviceId { get; }
    public string DisplayName { get; }
}

public sealed record PairingKeyPair(byte[] PublicKey, byte[] PrivateKey);

public interface ISecurePairingStore
{
    Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default);
    Task WriteAsync(string key, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default);
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}

public interface IPairingKeyAgreement
{
    PairingKeyPair CreateKeyPair();
    byte[] ExportPublicKey(ReadOnlySpan<byte> privateKey);
    byte[] DeriveKey(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> remotePublicKey, ReadOnlySpan<byte> context);
}

public sealed class EcdhPairingKeyAgreement : IPairingKeyAgreement
{
    public PairingKeyPair CreateKeyPair()
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return new PairingKeyPair(key.ExportSubjectPublicKeyInfo(), key.ExportPkcs8PrivateKey());
    }

    public byte[] ExportPublicKey(ReadOnlySpan<byte> privateKey)
    {
        using var key = ECDiffieHellman.Create();
        key.ImportPkcs8PrivateKey(privateKey, out _);
        return key.ExportSubjectPublicKeyInfo();
    }

    public byte[] DeriveKey(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> remotePublicKey, ReadOnlySpan<byte> context)
    {
        using var key = ECDiffieHellman.Create();
        key.ImportPkcs8PrivateKey(privateKey, out _);
        using var remoteKey = ECDiffieHellman.Create();
        remoteKey.ImportSubjectPublicKeyInfo(remotePublicKey, out _);
        var contextBytes = context.ToArray();
        return key.DeriveKeyFromHash(remoteKey.PublicKey, HashAlgorithmName.SHA256, contextBytes, contextBytes);
    }
}

public sealed class PairingCoordinator
{
    private const int ProtocolVersion = 1;
    private const string QrPrefix = "familyapp://pair/v1/";
    private static readonly TimeSpan OfferLifetime = TimeSpan.FromMinutes(5);

    private readonly ISecurePairingStore _secureStore;
    private readonly IPairingKeyAgreement _keyAgreement;
    private readonly TimeProvider _timeProvider;

    public PairingCoordinator(
        ISecurePairingStore secureStore,
        IPairingKeyAgreement keyAgreement,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(secureStore);
        ArgumentNullException.ThrowIfNull(keyAgreement);
        _secureStore = secureStore;
        _keyAgreement = keyAgreement;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<string> CreateOfferAsync(
        FamilyDeviceIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var keyPair = _keyAgreement.CreateKeyPair();
        var nonce = RandomNumberGenerator.GetBytes(32);
        var expiresAtUtc = _timeProvider.GetUtcNow().Add(OfferLifetime);
        try
        {
            await _secureStore.WriteAsync(PendingKey(identity, nonce), keyPair.PrivateKey, cancellationToken);
            return Encode(new OfferPayload(
                ProtocolVersion,
                identity.FamilyId,
                identity.DeviceId,
                identity.DisplayName,
                Convert.ToBase64String(keyPair.PublicKey),
                Convert.ToBase64String(nonce),
                expiresAtUtc));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyPair.PrivateKey);
        }
    }

    public async Task<string> AcceptOfferAsync(
        FamilyDeviceIdentity identity,
        string offerQr,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var offer = Decode<OfferPayload>(offerQr);
        ValidateOffer(identity, offer);

        var offerNonce = DecodeBytes(offer.Nonce, 32);
        var offerPublicKey = DecodeBytes(offer.PublicKey, 1);
        var keyPair = _keyAgreement.CreateKeyPair();
        var responseNonce = RandomNumberGenerator.GetBytes(32);
        var context = CreateContext(identity.FamilyId, offer.DeviceId, identity.DeviceId, offerNonce, responseNonce);
        byte[]? sharedKey = null;
        try
        {
            sharedKey = _keyAgreement.DeriveKey(keyPair.PrivateKey, offerPublicKey, context);
            var proof = CreateProof(sharedKey, context, offerPublicKey, keyPair.PublicKey);
            await _secureStore.WriteAsync(TrustedKey(identity.FamilyId, offer.DeviceId), sharedKey, cancellationToken);
            return Encode(new ResponsePayload(
                ProtocolVersion,
                identity.FamilyId,
                offer.DeviceId,
                identity.DeviceId,
                identity.DisplayName,
                Convert.ToBase64String(offerPublicKey),
                Convert.ToBase64String(keyPair.PublicKey),
                offer.Nonce,
                Convert.ToBase64String(responseNonce),
                offer.ExpiresAtUtc,
                Convert.ToBase64String(proof)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyPair.PrivateKey);
            if (sharedKey is not null) CryptographicOperations.ZeroMemory(sharedKey);
        }
    }

    public async Task CompletePairingAsync(
        FamilyDeviceIdentity identity,
        string responseQr,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var response = Decode<ResponsePayload>(responseQr);
        ValidateResponse(identity, response);

        var offerNonce = DecodeBytes(response.OfferNonce, 32);
        var privateKey = await _secureStore.ReadAsync(PendingKey(identity, offerNonce), cancellationToken)
            ?? throw new InvalidOperationException("The pairing offer is no longer available.");
        byte[]? sharedKey = null;
        try
        {
            var offerPublicKey = DecodeBytes(response.OfferPublicKey, 1);
            var responsePublicKey = DecodeBytes(response.PublicKey, 1);
            var responseNonce = DecodeBytes(response.Nonce, 32);
            var proof = DecodeBytes(response.Proof, 32);
            var expectedOfferPublicKey = _keyAgreement.ExportPublicKey(privateKey);
            if (!CryptographicOperations.FixedTimeEquals(offerPublicKey, expectedOfferPublicKey))
                throw new InvalidOperationException("The response does not match the pairing offer.");

            var context = CreateContext(identity.FamilyId, identity.DeviceId, response.DeviceId, offerNonce, responseNonce);
            sharedKey = _keyAgreement.DeriveKey(privateKey, responsePublicKey, context);
            var expectedProof = CreateProof(sharedKey, context, offerPublicKey, responsePublicKey);
            if (!CryptographicOperations.FixedTimeEquals(proof, expectedProof))
                throw new InvalidOperationException("The pairing response could not be authenticated.");

            await _secureStore.WriteAsync(TrustedKey(identity.FamilyId, response.DeviceId), sharedKey, cancellationToken);
            await _secureStore.DeleteAsync(PendingKey(identity, offerNonce), cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
            if (sharedKey is not null) CryptographicOperations.ZeroMemory(sharedKey);
        }
    }

    public Task<byte[]?> ReadTrustedSecretAsync(
        FamilyDeviceIdentity identity,
        Guid peerDeviceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (peerDeviceId == Guid.Empty) throw new ArgumentException("Peer device ID is required.", nameof(peerDeviceId));
        return _secureStore.ReadAsync(TrustedKey(identity.FamilyId, peerDeviceId), cancellationToken);
    }

    public Task ForgetTrustedDeviceAsync(
        FamilyDeviceIdentity identity,
        Guid peerDeviceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (peerDeviceId == Guid.Empty) throw new ArgumentException("Peer device ID is required.", nameof(peerDeviceId));
        return _secureStore.DeleteAsync(TrustedKey(identity.FamilyId, peerDeviceId), cancellationToken);
    }

    private void ValidateOffer(FamilyDeviceIdentity identity, OfferPayload offer)
    {
        ValidateVersionAndExpiry(offer.Version, offer.ExpiresAtUtc);
        if (offer.FamilyId != identity.FamilyId || offer.DeviceId == identity.DeviceId || string.IsNullOrWhiteSpace(offer.DisplayName))
            throw new InvalidOperationException("The pairing offer is not valid for this device.");
        _ = DecodeBytes(offer.PublicKey, 1);
        _ = DecodeBytes(offer.Nonce, 32);
    }

    private void ValidateResponse(FamilyDeviceIdentity identity, ResponsePayload response)
    {
        ValidateVersionAndExpiry(response.Version, response.ExpiresAtUtc);
        if (response.FamilyId != identity.FamilyId || response.InitiatorDeviceId != identity.DeviceId ||
            response.DeviceId == identity.DeviceId || string.IsNullOrWhiteSpace(response.DisplayName))
            throw new InvalidOperationException("The pairing response is not valid for this device.");
    }

    private void ValidateVersionAndExpiry(int version, DateTimeOffset expiresAtUtc)
    {
        if (version != ProtocolVersion) throw new InvalidOperationException("The pairing protocol version is unsupported.");
        if (expiresAtUtc <= _timeProvider.GetUtcNow()) throw new InvalidOperationException("The pairing code has expired.");
    }

    private static byte[] CreateContext(Guid familyId, Guid initiatorId, Guid responderId, byte[] offerNonce, byte[] responseNonce)
        => Encoding.UTF8.GetBytes($"{familyId:N}|{initiatorId:N}|{responderId:N}|{Convert.ToBase64String(offerNonce)}|{Convert.ToBase64String(responseNonce)}");

    private static byte[] CreateProof(byte[] sharedKey, byte[] context, byte[] offerPublicKey, byte[] responsePublicKey)
    {
        var proofData = new byte[context.Length + offerPublicKey.Length + responsePublicKey.Length];
        context.CopyTo(proofData, 0);
        offerPublicKey.CopyTo(proofData, context.Length);
        responsePublicKey.CopyTo(proofData, context.Length + offerPublicKey.Length);
        return HMACSHA256.HashData(sharedKey, proofData);
    }

    private static string PendingKey(FamilyDeviceIdentity identity, byte[] nonce)
        => $"pairing:pending:{identity.FamilyId:N}:{identity.DeviceId:N}:{Convert.ToBase64String(nonce)}";

    private static string TrustedKey(Guid familyId, Guid peerDeviceId)
        => $"pairing:trusted:{familyId:N}:{peerDeviceId:N}";

    private static string Encode<TPayload>(TPayload payload)
    {
        var encoded = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(payload))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return QrPrefix + encoded;
    }

    private static TPayload Decode<TPayload>(string qr)
    {
        if (string.IsNullOrWhiteSpace(qr) || !qr.StartsWith(QrPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException("The pairing QR code is invalid.");

        try
        {
            var encoded = qr[QrPrefix.Length..].Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + (4 - encoded.Length % 4) % 4, '=');
            return JsonSerializer.Deserialize<TPayload>(Convert.FromBase64String(encoded))
                ?? throw new InvalidOperationException("The pairing QR code is invalid.");
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("The pairing QR code is invalid.", exception);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The pairing QR code is invalid.", exception);
        }
    }

    private static byte[] DecodeBytes(string value, int minimumLength)
    {
        var bytes = Convert.FromBase64String(value);
        if (bytes.Length < minimumLength) throw new InvalidOperationException("The pairing QR code is invalid.");
        return bytes;
    }

    private sealed record OfferPayload(
        int Version,
        Guid FamilyId,
        Guid DeviceId,
        string DisplayName,
        string PublicKey,
        string Nonce,
        DateTimeOffset ExpiresAtUtc);

    private sealed record ResponsePayload(
        int Version,
        Guid FamilyId,
        Guid InitiatorDeviceId,
        Guid DeviceId,
        string DisplayName,
        string OfferPublicKey,
        string PublicKey,
        string OfferNonce,
        string Nonce,
        DateTimeOffset ExpiresAtUtc,
        string Proof);
}