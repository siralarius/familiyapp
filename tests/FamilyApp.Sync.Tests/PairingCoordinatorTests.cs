using System.Security.Cryptography;
using FamilyApp.Sync.Pairing;
using Xunit;

namespace FamilyApp.Sync.Tests;

public class PairingCoordinatorTests
{
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