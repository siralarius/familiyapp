using FamilyApp.Sync.ChangeLog;
using FamilyApp.Sync.Simulation;
using Xunit;

namespace FamilyApp.Sync.Tests;

public class TwoDeviceSyncSimulatorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Divergent_devices_converge_in_either_exchange_order(bool deviceBFirst)
    {
        var simulator = new TwoDeviceSyncSimulator();
        var entityId = Guid.NewGuid();
        var timestamp = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        var changeA = Change(entityId, TwoDeviceSyncSimulator.DeviceAId, timestamp, "A");
        var changeB = Change(entityId, TwoDeviceSyncSimulator.DeviceBId, timestamp, "B");

        Assert.NotSame(simulator.DeviceA, simulator.DeviceB);
        simulator.DeviceA.Apply(changeA);
        simulator.DeviceB.Apply(changeB);

        var firstSource = deviceBFirst ? TwoDeviceSyncSimulator.DeviceBId : TwoDeviceSyncSimulator.DeviceAId;
        var firstDestination = deviceBFirst ? TwoDeviceSyncSimulator.DeviceAId : TwoDeviceSyncSimulator.DeviceBId;
        var secondSource = deviceBFirst ? TwoDeviceSyncSimulator.DeviceAId : TwoDeviceSyncSimulator.DeviceBId;
        var secondDestination = deviceBFirst ? TwoDeviceSyncSimulator.DeviceBId : TwoDeviceSyncSimulator.DeviceAId;

        simulator.Deliver(simulator.CreateMessage(firstSource, firstDestination));
        simulator.Deliver(simulator.CreateMessage(secondSource, secondDestination));

        Assert.Equal(simulator.DeviceA.Current(), simulator.DeviceB.Current());
        Assert.Equal(changeB, simulator.DeviceA.GetCurrent("shopping", entityId));
        Assert.Equal(2, simulator.DeviceA.Changes.Count);
        Assert.Equal(2, simulator.DeviceB.Changes.Count);
    }

    [Fact]
    public void Retrying_a_message_is_idempotent_and_only_missing_changes_are_sent()
    {
        var simulator = new TwoDeviceSyncSimulator();
        var timestamp = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        simulator.DeviceA.Apply(Change(Guid.NewGuid(), TwoDeviceSyncSimulator.DeviceAId, timestamp, "A"));
        simulator.DeviceA.Apply(Change(Guid.NewGuid(), TwoDeviceSyncSimulator.DeviceAId, timestamp.AddSeconds(1), "B"));

        var message = simulator.CreateMessage(TwoDeviceSyncSimulator.DeviceAId, TwoDeviceSyncSimulator.DeviceBId);

        Assert.Equal(2, message.Changes.Count);
        Assert.Equal(2, simulator.Deliver(message));
        Assert.Equal(0, simulator.Deliver(message));
        Assert.Empty(simulator.CreateMessage(TwoDeviceSyncSimulator.DeviceAId, TwoDeviceSyncSimulator.DeviceBId).Changes);
        Assert.Equal(2, simulator.DeviceB.Changes.Count);
    }

    private static ChangeRecord Change(Guid entityId, string deviceId, DateTimeOffset timestamp, string value)
        => new(Guid.NewGuid(), deviceId, "shopping", entityId, 1, timestamp, false, $"{{\"value\":\"{value}\"}}");
}