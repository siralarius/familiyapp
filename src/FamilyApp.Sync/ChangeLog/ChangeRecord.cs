namespace FamilyApp.Sync.ChangeLog;

public sealed record ChangeRecord(
    Guid ChangeId,
    string DeviceId,
    string EntityType,
    Guid EntityId,
    long Version,
    DateTimeOffset Timestamp,
    bool IsTombstone,
    string Payload)
{
    public ChangeRecord
    {
        if (ChangeId == Guid.Empty) throw new ArgumentException("Change ID is required.", nameof(ChangeId));
        if (string.IsNullOrWhiteSpace(DeviceId)) throw new ArgumentException("Device ID is required.", nameof(DeviceId));
        if (string.IsNullOrWhiteSpace(EntityType)) throw new ArgumentException("Entity type is required.", nameof(EntityType));
        if (EntityId == Guid.Empty) throw new ArgumentException("Entity ID is required.", nameof(EntityId));
        if (Version < 1) throw new ArgumentOutOfRangeException(nameof(Version));
        Payload ??= string.Empty;
    }
}
