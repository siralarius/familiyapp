namespace FamilyApp.Sync.ChangeLog;

public sealed class ChangeLogEngine
{
    private readonly Dictionary<Guid, ChangeRecord> _changes = [];
    private readonly Dictionary<EntityKey, ChangeRecord> _current = [];

    public IReadOnlyCollection<ChangeRecord> Changes => _changes.Values
        .OrderBy(x => x.Timestamp).ThenBy(x => x.DeviceId, StringComparer.Ordinal).ThenBy(x => x.ChangeId)
        .ToArray();

    public bool Apply(ChangeRecord change)
    {
        if (!_changes.TryAdd(change.ChangeId, change))
            return false;

        var key = new EntityKey(change.EntityType, change.EntityId);
        if (!_current.TryGetValue(key, out var existing) || Compare(change, existing) > 0)
            _current[key] = change;

        return true;
    }

    public int Merge(IEnumerable<ChangeRecord> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var applied = 0;
        foreach (var change in changes)
            if (Apply(change))
                applied++;
        return applied;
    }

    public ChangeRecord? GetCurrent(string entityType, Guid entityId)
        => _current.GetValueOrDefault(new EntityKey(entityType, entityId));

    public IReadOnlyCollection<ChangeRecord> Current(bool includeTombstones = false)
        => _current.Values
            .Where(x => includeTombstones || !x.IsTombstone)
            .OrderBy(x => x.EntityType, StringComparer.Ordinal)
            .ThenBy(x => x.EntityId)
            .ToArray();

    private static int Compare(ChangeRecord left, ChangeRecord right)
    {
        var result = left.Version.CompareTo(right.Version);
        if (result != 0) return result;
        result = left.Timestamp.CompareTo(right.Timestamp);
        if (result != 0) return result;
        result = string.CompareOrdinal(left.DeviceId, right.DeviceId);
        if (result != 0) return result;
        return left.ChangeId.CompareTo(right.ChangeId);
    }

    private readonly record struct EntityKey(string EntityType, Guid EntityId);
}
