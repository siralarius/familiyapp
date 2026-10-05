using FamilyApp.Sync.ChangeLog;

namespace FamilyApp.Sync.Simulation;

public sealed record SyncMessage(
    string SourceDeviceId,
    string DestinationDeviceId,
    IReadOnlyList<ChangeRecord> Changes);

public sealed class TwoDeviceSyncSimulator
{
    public const string DeviceAId = "device-a";
    public const string DeviceBId = "device-b";

    public ChangeLogEngine DeviceA { get; } = new();
    public ChangeLogEngine DeviceB { get; } = new();

    public SyncMessage CreateMessage(string sourceDeviceId, string destinationDeviceId)
    {
        var source = GetDevice(sourceDeviceId);
        var destination = GetDevice(destinationDeviceId);
        var knownChangeIds = destination.Changes.Select(change => change.ChangeId).ToHashSet();
        var missingChanges = source.Changes
            .Where(change => !knownChangeIds.Contains(change.ChangeId))
            .ToArray();

        return new SyncMessage(sourceDeviceId, destinationDeviceId, Array.AsReadOnly(missingChanges));
    }

    public int Deliver(SyncMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return GetDevice(message.DestinationDeviceId).Merge(message.Changes);
    }

    private ChangeLogEngine GetDevice(string deviceId) => deviceId switch
    {
        DeviceAId => DeviceA,
        DeviceBId => DeviceB,
        _ => throw new ArgumentException($"Unknown simulated device '{deviceId}'.", nameof(deviceId))
    };
}