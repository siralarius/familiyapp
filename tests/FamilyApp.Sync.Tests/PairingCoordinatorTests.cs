using System.Security.Cryptography;
using System.Text.Json;
using FamilyApp.Sync.Pairing;
using Xunit;

namespace FamilyApp.Sync.Tests;

public class PairingCoordinatorTests
{
    [Fact]
    public async Task Only_the_exact_accepted_response_can_resume_pairing_and_forgetting_revokes_it()
    {
        var token = TestContext.Current.CancellationToken;
        var family = Guid.NewGuid();
        var host = new FamilyDeviceIdentity(family, Guid.NewGuid(), "Host");
        var guest = new FamilyDeviceIdentity(family, Guid.NewGuid(), "Guest");
        var store = new FakeSecurePairingStore();
        var pairing = new PairingCoordinator(store, new EcdhPairingKeyAgreement());
        var guestPairing = new PairingCoordinator(new FakeSecurePairingStore(), new EcdhPairingKeyAgreement());
        var offer = await pairing.CreateOfferAsync(host, token);
        var response = await guestPairing.AcceptOfferAsync(guest, offer, token);
        var otherResponse = await guestPairing.AcceptOfferAsync(guest, offer, token);
        await pairing.CompletePairingAsync(host, response, token);
        var secret = await pairing.ReadTrustedSecretAsync(host, guest.DeviceId, token);
        var restarted = new PairingCoordinator(store, new EcdhPairingKeyAgreement());

        await restarted.CompletePairingAsync(host, response, token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.CompletePairingAsync(host, otherResponse, token));
        Assert.Equal(secret, await restarted.ReadTrustedSecretAsync(host, guest.DeviceId, token));
        await restarted.ForgetTrustedDeviceAsync(host, guest.DeviceId, token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.CompletePairingAsync(host, response, token));
        Assert.Null(await restarted.ReadTrustedSecretAsync(host, guest.DeviceId, token));
    }

    [Fact]
    public async Task Devices_pair_using_qr_payloads_and_store_shared_secret_only_in_secure_store()
    {
        var familyId = Guid.NewGuid();
        var deviceA = new FamilyDeviceIdentity(familyId, Guid.NewGuid(), "Phone A");
        var deviceB = new FamilyDeviceIdentity(familyId, Guid.NewGuid(), "Phone B");
        var secureStoreA = new FakeSecurePairingStore();
        var secureStoreB = new FakeSecurePairingStore();
        var pairingA = new PairingCoordinator(secureStoreA, new EcdhPairingKeyAgreement());
        var pairingB = new PairingCoordinator(secureStoreB, new EcdhPairingKeyAgreement());

        var cancellationToken = TestContext.Current.CancellationToken;
        var offerQr = await pairingA.CreateOfferAsync(deviceA, cancellationToken);
        var pendingPrivateKey = Assert.Single(secureStoreA.Values).Value;
        Assert.DoesNotContain(Convert.ToBase64String(pendingPrivateKey), offerQr, StringComparison.Ordinal);

        var responseQr = await pairingB.AcceptOfferAsync(deviceB, offerQr, cancellationToken);
        var secretOnB = await pairingB.ReadTrustedSecretAsync(deviceB, deviceA.DeviceId, cancellationToken);
        Assert.NotNull(secretOnB);
        Assert.DoesNotContain(Convert.ToBase64String(secretOnB), responseQr, StringComparison.Ordinal);

        await pairingA.CompletePairingAsync(deviceA, responseQr, cancellationToken);
        var secretOnA = await pairingA.ReadTrustedSecretAsync(deviceA, deviceB.DeviceId, cancellationToken);

        Assert.NotNull(secretOnA);
        Assert.Equal(secretOnA, secretOnB);
        Assert.DoesNotContain(secureStoreA.Values.Keys, key => key.StartsWith("pairing:pending:", StringComparison.Ordinal));

        await pairingA.ForgetTrustedDeviceAsync(deviceA, deviceB.DeviceId, cancellationToken);
        Assert.Null(await pairingA.ReadTrustedSecretAsync(deviceA, deviceB.DeviceId, cancellationToken));
    }

    [Fact]
    public async Task Pairing_response_with_invalid_proof_is_not_trusted()
    {
        var familyId = Guid.NewGuid();
        var deviceA = new FamilyDeviceIdentity(familyId, Guid.NewGuid(), "Phone A");
        var deviceB = new FamilyDeviceIdentity(familyId, Guid.NewGuid(), "Phone B");
        var storeA = new FakeSecurePairingStore();
        var storeB = new FakeSecurePairingStore();
        var pairingA = new PairingCoordinator(storeA, new EcdhPairingKeyAgreement());
        var pairingB = new PairingCoordinator(storeB, new EcdhPairingKeyAgreement());
        var cancellationToken = TestContext.Current.CancellationToken;
        var offerQr = await pairingA.CreateOfferAsync(deviceA, cancellationToken);
        var responseQr = await pairingB.AcceptOfferAsync(deviceB, offerQr, cancellationToken);
        var tamperedResponse = responseQr[..^1] + (responseQr[^1] == 'A' ? 'B' : 'A');

        await Assert.ThrowsAsync<InvalidOperationException>(() => pairingA.CompletePairingAsync(deviceA, tamperedResponse, cancellationToken));
        Assert.Null(await pairingA.ReadTrustedSecretAsync(deviceA, deviceB.DeviceId, cancellationToken));
    }

    [Fact]
    public async Task Inspect_qr_payload_identifies_offer_and_response()
    {
        var familyId = Guid.NewGuid();
        var deviceA = new FamilyDeviceIdentity(familyId, Guid.NewGuid(), "Phone A");
        var deviceB = new FamilyDeviceIdentity(familyId, Guid.NewGuid(), "Phone B");
        var pairingA = new PairingCoordinator(new FakeSecurePairingStore(), new EcdhPairingKeyAgreement());
        var pairingB = new PairingCoordinator(new FakeSecurePairingStore(), new EcdhPairingKeyAgreement());
        var cancellationToken = TestContext.Current.CancellationToken;

        var offer = await pairingA.CreateOfferAsync(deviceA, cancellationToken);
        var offerSummary = PairingCoordinator.InspectQrPayload(offer);
        Assert.Equal(PairingQrPayloadKind.Offer, offerSummary.Kind);
        Assert.Equal(familyId, offerSummary.FamilyId);

        var response = await pairingB.AcceptOfferAsync(deviceB, offer, cancellationToken);
        var responseSummary = PairingCoordinator.InspectQrPayload(response);
        Assert.Equal(PairingQrPayloadKind.Response, responseSummary.Kind);
        Assert.Equal(familyId, responseSummary.FamilyId);
    }

    [Fact]
    public void Inspect_qr_payload_rejects_unsupported_payload()
    {
        var unsupportedPayload = ToPairingQr(new { FamilyId = Guid.NewGuid(), ExpiresAtUtc = DateTimeOffset.UtcNow });
        var invalidPayload = "familyapp://pair/v1/%%%";

        var unsupported = Assert.Throws<InvalidOperationException>(() => PairingCoordinator.InspectQrPayload(unsupportedPayload));
        Assert.Equal("The pairing QR code format is unsupported.", unsupported.Message);
        var invalid = Assert.Throws<InvalidOperationException>(() => PairingCoordinator.InspectQrPayload(invalidPayload));
        Assert.Equal("The pairing QR code is invalid.", invalid.Message);
    }

    [Fact]
    public async Task Expired_offer_is_rejected_during_acceptance()
    {
        var fakeTime = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var familyId = Guid.NewGuid();
        var deviceA = new FamilyDeviceIdentity(familyId, Guid.NewGuid(), "Phone A");
        var deviceB = new FamilyDeviceIdentity(familyId, Guid.NewGuid(), "Phone B");
        var pairingA = new PairingCoordinator(new FakeSecurePairingStore(), new EcdhPairingKeyAgreement(), fakeTime);
        var pairingB = new PairingCoordinator(new FakeSecurePairingStore(), new EcdhPairingKeyAgreement(), fakeTime);
        var cancellationToken = TestContext.Current.CancellationToken;

        var offer = await pairingA.CreateOfferAsync(deviceA, cancellationToken);
        fakeTime.Advance(TimeSpan.FromMinutes(6));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => pairingB.AcceptOfferAsync(deviceB, offer, cancellationToken));
        Assert.Equal("The pairing code has expired.", exception.Message);
    }

    [Theory]
    [InlineData("DeviceId")]
    [InlineData("FamilyId")]
    [InlineData("Version")]
    [InlineData("PublicKey")]
    [InlineData("Nonce")]
    [InlineData("DisplayName")]
    [InlineData("ExpiresAtUtc")]
    public void Offer_is_fully_validated_without_writing_trust_before_joining(string invalidField)
    {
        var familyId = Guid.NewGuid();
        var identity = new FamilyDeviceIdentity(familyId, Guid.NewGuid(), "Joining phone");
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var payload = new Dictionary<string, object>
        {
            ["Version"] = 1, ["FamilyId"] = familyId, ["DeviceId"] = Guid.NewGuid(),
            ["DisplayName"] = "Offering phone", ["PublicKey"] = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
            ["Nonce"] = Convert.ToBase64String(new byte[32]), ["ExpiresAtUtc"] = DateTimeOffset.UtcNow.AddMinutes(5)
        };
        var store = new FakeSecurePairingStore();
        var pairing = new PairingCoordinator(store, new EcdhPairingKeyAgreement());
        pairing.ValidateOfferForJoining(identity, ToPairingQr(payload));
        Assert.Empty(store.Values);
        payload[invalidField] = invalidField switch
        {
            "DeviceId" => Guid.Empty, "FamilyId" => Guid.Empty, "Version" => 999, "PublicKey" => "AQ==", "Nonce" => "AQ==",
            "DisplayName" => "", _ => DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        Assert.ThrowsAny<Exception>(() => pairing.ValidateOfferForJoining(identity, ToPairingQr(payload)));
        Assert.Empty(store.Values);
        Assert.Equal(familyId, identity.FamilyId);
    }

    [Fact]
    public async Task Expiry_after_preflight_does_not_select_or_persist_a_family()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var host = new FamilyDeviceIdentity(Guid.NewGuid(), Guid.NewGuid(), "Host");
        var local = new FamilyDeviceIdentity(Guid.NewGuid(), Guid.NewGuid(), "Joiner");
        var offer = await new PairingCoordinator(new FakeSecurePairingStore(), new EcdhPairingKeyAgreement(), time)
            .CreateOfferAsync(host, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(4));
        var store = new FakeSecurePairingStore();
        var pairing = new PairingCoordinator(store, new ExpiringKeyAgreement(time), time);
        var selected = false;
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            pairing.AcceptOfferForJoiningAsync(local, offer, _ => { selected = true; return Task.CompletedTask; }, TestContext.Current.CancellationToken));
        Assert.Contains("expired", exception.Message);
        Assert.False(selected); Assert.Empty(store.Values);
    }

    [Fact]
    public async Task Family_selection_follows_successful_proof_and_secure_storage()
    {
        var host = new FamilyDeviceIdentity(Guid.NewGuid(), Guid.NewGuid(), "Host");
        var local = new FamilyDeviceIdentity(Guid.NewGuid(), Guid.NewGuid(), "Joiner");
        var offer = await new PairingCoordinator(new FakeSecurePairingStore(), new EcdhPairingKeyAgreement()).CreateOfferAsync(host, TestContext.Current.CancellationToken);
        var store = new FakeSecurePairingStore();
        var pairing = new PairingCoordinator(store, new EcdhPairingKeyAgreement());
        var selected = Guid.Empty;
        var response = await pairing.AcceptOfferForJoiningAsync(local, offer, familyId =>
        {
            Assert.Single(store.Values); selected = familyId; return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);
        Assert.Equal(host.FamilyId, selected);
        Assert.Equal(host.FamilyId, PairingCoordinator.InspectQrPayload(response).FamilyId);
    }

    private sealed class ExpiringKeyAgreement(MutableTimeProvider time) : IPairingKeyAgreement
    {
        private readonly EcdhPairingKeyAgreement inner = new();
        public byte[] ExportPublicKey(ReadOnlySpan<byte> privateKey) => inner.ExportPublicKey(privateKey);
        public PairingKeyPair CreateKeyPair() { time.Advance(TimeSpan.FromMinutes(2)); return inner.CreateKeyPair(); }
        public byte[] DeriveKey(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> remotePublicKey, ReadOnlySpan<byte> context)
            => inner.DeriveKey(privateKey, remotePublicKey, context);
    }

    private static string ToPairingQr(object payload)
    {
        var encoded = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(payload))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return $"familyapp://pair/v1/{encoded}";
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }

    private sealed class FakeSecurePairingStore : ISecurePairingStore
    {
        public Dictionary<string, byte[]> Values { get; } = new(StringComparer.Ordinal);

        public Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult(Values.TryGetValue(key, out var value) ? value.ToArray() : null);

        public Task WriteAsync(string key, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default)
        {
            Values[key] = value.ToArray();
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            if (Values.Remove(key, out var value)) CryptographicOperations.ZeroMemory(value);
            return Task.CompletedTask;
        }
    }
}
