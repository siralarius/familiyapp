namespace FamilyApp.Sync.Pairing;

public interface IDevicePairingService
{
    FamilyDeviceIdentity Identity { get; }
    Task JoinFamilyAsync(Guid familyId);
    Task JoinFromOfferAsync(string offer);
    Task<string> CreateOfferAsync();
    Task<string> AcceptOfferAsync(string offer);
    Task CompletePairingAsync(string response);
}
