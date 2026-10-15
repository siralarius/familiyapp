using System.Text.Json;
using FamilyApp.Sync.ChangeLog;
using Xunit;

namespace FamilyApp.Sync.Tests;

public class ChangeLogEngineTests
{
    public static TheoryData<string> Domains => new()
    {
        "shopping", "inventory", "meal", "calendar"
    };

    [Theory]
    [MemberData(nameof(Domains))]
    public void Concurrent_changes_converge_regardless_of_merge_order(string entityType)
    {
        var entityId = Guid.NewGuid();
        var timestamp = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        var fromA = Change(entityType, entityId, "device-a", 2, timestamp, "{\"value\":\"A\"}");
        var fromB = Change(entityType, entityId, "device-b", 2, timestamp, "{\"value\":\"B\"}");

        var first = new ChangeLogEngine();
        first.Merge([fromA, fromB]);

        var second = new ChangeLogEngine();
        second.Merge([fromB, fromA]);

        Assert.Equal(first.GetCurrent(entityType, entityId), second.GetCurrent(entityType, entityId));
        Assert.Equal(fromB, first.GetCurrent(entityType, entityId));
    }

    [Fact]
    public void Applying_same_change_twice_is_idempotent()
    {
        var change = Change("shopping", Guid.NewGuid(), "device-a", 1, DateTimeOffset.UtcNow, "{}");
        var engine = new ChangeLogEngine();

        Assert.True(engine.Apply(change));
        Assert.False(engine.Apply(change));
        Assert.Single(engine.Changes);
    }

    [Fact]
    public void Higher_version_wins_even_with_older_timestamp()
    {
        var id = Guid.NewGuid();
        var newerTimestamp = Change("inventory", id, "device-a", 1, new(2026, 10, 5, 13, 0, 0, TimeSpan.Zero), "{\"quantity\":1}");
        var higherVersion = Change("inventory", id, "device-b", 2, new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero), "{\"quantity\":2}");
        var engine = new ChangeLogEngine();

        engine.Merge([newerTimestamp, higherVersion]);

        Assert.Equal(higherVersion, engine.GetCurrent("inventory", id));
    }

    [Fact]
    public void Tombstone_removes_entity_from_live_view_but_remains_in_log()
    {
        var id = Guid.NewGuid();
        var original = Change("calendar", id, "device-a", 1, DateTimeOffset.UtcNow, "{}");
        var deleted = Change("calendar", id, "device-a", 2, original.Timestamp.AddSeconds(1), "", true);
        var engine = new ChangeLogEngine();

        engine.Merge([original, deleted]);

        Assert.Empty(engine.Current());
        Assert.Equal(deleted, engine.GetCurrent("calendar", id));
        Assert.Equal(2, engine.Changes.Count);
    }

    [Fact]
    public void Terminal_import_tombstone_uses_existing_serialization_and_version_ordering()
    {
        var id = Guid.NewGuid();
        var removed = Change("shopping", id, "a", long.MaxValue, DateTimeOffset.UtcNow, "", true);
        var json = JsonSerializer.Serialize(removed);
        Assert.DoesNotContain("IsPermanentTombstone", json);
        var restored = JsonSerializer.Deserialize<ChangeRecord>(json)!;
        Assert.Equal(removed, restored);
        var edit = Change("shopping", id, "b", long.MaxValue - 1, removed.Timestamp.AddDays(1), "{}");
        foreach (var records in new[] { new[] { restored, edit }, new[] { edit, restored } })
        {
            var engine = new ChangeLogEngine(); engine.Merge(records);
            Assert.Equal(removed, engine.GetCurrent("shopping", id));
            Assert.Empty(engine.Current());
        }
    }

    private static ChangeRecord Change(
        string entityType, Guid entityId, string deviceId, long version,
        DateTimeOffset timestamp, string payload, bool tombstone = false)
        => new(Guid.NewGuid(), deviceId, entityType, entityId, version, timestamp, tombstone, payload);
}
