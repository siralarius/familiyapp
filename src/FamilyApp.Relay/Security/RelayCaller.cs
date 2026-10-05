using System.Security.Claims;

namespace FamilyApp.Relay.Security;

public sealed record RelayCaller(Guid FamilyId, Guid DeviceId)
{
    public const string FamilyIdClaim = "family_id";
    public const string DeviceIdClaim = "device_id";
    public const string FamilyRoleClaim = "family_role";

    public static bool TryCreate(ClaimsPrincipal principal, out RelayCaller caller)
    {
        caller = default!;
        if (!Guid.TryParse(principal.FindFirstValue(FamilyIdClaim), out var familyId) || familyId == Guid.Empty ||
            !Guid.TryParse(principal.FindFirstValue(DeviceIdClaim), out var deviceId) || deviceId == Guid.Empty)
            return false;

        caller = new RelayCaller(familyId, deviceId);
        return true;
    }
}