namespace FamilyApp.Sync.ChangeLog;

public sealed record ChangeRecord
{
    public ChangeRecord(
        Guid changeId,
        string deviceId,
        string entityType,
        Guid entityId,
        long version,
        DateTimeOffset timestamp,
        bool isTombstone,
        string payload,
        bool isPermanentTombstone = false)
    {
        if (changeId == Guid.Empty) throw new ArgumentException("Change ID is required.", nameof(changeId));
        if (string.IsNullOrWhiteSpace(deviceId)) throw new ArgumentException("Device ID is required.", nameof(deviceId));
        if (string.IsNullOrWhiteSpace(entityType)) throw new ArgumentException("Entity type is required.", nameof(entityType));
        if (entityId == Guid.Empty) throw new ArgumentException("Entity ID is required.", nameof(entityId));
        if (version < 1) throw new ArgumentOutOfRangeException(nameof(version));

        if (isPermanentTombstone && !isTombstone)
            throw new ArgumentException("A permanent deletion must be a tombstone.", nameof(isPermanentTombstone));

        ChangeId = changeId;
        DeviceId = deviceId;
        EntityType = entityType;
        EntityId = entityId;
        Version = version;
        Timestamp = timestamp;
        IsTombstone = isTombstone;
        IsPermanentTombstone = isPermanentTombstone;
        Payload = payload ?? string.Empty;
    }

    public Guid ChangeId { get; }
    public string DeviceId { get; }
    public string EntityType { get; }
    public Guid EntityId { get; }
    public long Version { get; }
    public DateTimeOffset Timestamp { get; }
    public bool IsTombstone { get; }
    public string Payload { get; }
    public bool IsPermanentTombstone { get; }
}
