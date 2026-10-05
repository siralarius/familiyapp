namespace FamilyApp.Sync.ChangeLog;

public interface IChangeLogStore
{
    IReadOnlyCollection<ChangeRecord> Load();
    void Append(IReadOnlyCollection<ChangeRecord> changes);
}

public sealed class ChangeLogEngine
{
    private readonly object _gate = new();
    private readonly IChangeLogStore? _store;
    private readonly Dictionary<Guid, ChangeRecord> _changes = [];
    private readonly Dictionary<EntityKey, ChangeRecord> _current = [];

    public ChangeLogEngine(IChangeLogStore? store = null)
    {
        _store = store;
        if (store is not null)
            foreach (var change in store.Load()) ApplyInMemory(change);
    }

    public IReadOnlyCollection<ChangeRecord> Changes
    {
        get
        {
            lock (_gate)
                return _changes.Values.OrderBy(x => x.Timestamp)
                    .ThenBy(x => x.DeviceId, StringComparer.Ordinal).ThenBy(x => x.ChangeId).ToArray();
        }
    }

    public bool Apply(ChangeRecord change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return Merge([change]) != 0;
    }

    private bool ApplyInMemory(ChangeRecord change)
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
        lock (_gate)
        {
            var pending = changes.DistinctBy(x => x.ChangeId)
                .Where(x => !_changes.ContainsKey(x.ChangeId)).ToArray();
            // Persist the whole batch before publishing it or acknowledging delivery.
            if (pending.Length == 0) return 0;
            _store?.Append(pending);
            foreach (var change in pending) ApplyInMemory(change);
            return pending.Length;
        }
    }

    public int Update(Func<IReadOnlyCollection<ChangeRecord>, IEnumerable<ChangeRecord>> createChanges)
    {
        ArgumentNullException.ThrowIfNull(createChanges);
        lock (_gate) return Merge(createChanges(Current(includeTombstones: true)));
    }

    public ChangeRecord? GetCurrent(string entityType, Guid entityId)
    {
        lock (_gate) return _current.GetValueOrDefault(new EntityKey(entityType, entityId));
    }

    public IReadOnlyCollection<ChangeRecord> Current(bool includeTombstones = false)
    {
        lock (_gate) return _current.Values
            .Where(x => includeTombstones || !x.IsTombstone)
            .OrderBy(x => x.EntityType, StringComparer.Ordinal)
            .ThenBy(x => x.EntityId)
            .ToArray();
    }

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
