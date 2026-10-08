using System.Text.Json;
using FamilyApp.Core.Calendar;
using FamilyApp.Core.Inventory;
using FamilyApp.Core.Meals;
using FamilyApp.Core.Shopping;
using FamilyApp.Sync.ChangeLog;

namespace FamilyApp.Data;

// Each edit becomes an entity change, so unrelated edits are not overwritten by a list snapshot.
public sealed class LocalFamilyRepositories(ChangeLogEngine engine, string deviceId) :
    IShoppingRepository, IInventoryRepository, IMealPlanRepository, IFamilyEventRepository
{
    Task<IReadOnlyList<ShoppingItem>> IShoppingRepository.GetAllAsync() => Read<ShoppingItem>("shopping");
    Task<IReadOnlyList<InventoryItem>> IInventoryRepository.GetAllAsync() => Read<InventoryItem>("inventory");
    Task<IReadOnlyList<MealPlanEntry>> IMealPlanRepository.GetAllAsync() => Read<MealPlanEntry>("meals");
    Task<IReadOnlyList<FamilyEvent>> IFamilyEventRepository.GetAllAsync() => Read<FamilyEvent>("calendar");
    Task IShoppingRepository.SaveAsync(IReadOnlyList<ShoppingItem> items) => Save("shopping", items, x => x.Id);
    Task IInventoryRepository.SaveAsync(IReadOnlyList<InventoryItem> items) => Save("inventory", items, x => x.Id);
    Task IMealPlanRepository.SaveAsync(IReadOnlyList<MealPlanEntry> items) => Save("meals", items, x => x.Id);
    Task IFamilyEventRepository.SaveAsync(IReadOnlyList<FamilyEvent> items) => Save("calendar", items, x => x.Id);

    private readonly Dictionary<string, Dictionary<Guid, string>> _baselines = [];
    private readonly object _gate = new();

    private Task<IReadOnlyList<T>> Read<T>(string type)
    {
        lock (_gate)
        {
            var current = engine.Current().Where(x => x.EntityType == type).ToArray();
            _baselines[type] = current.ToDictionary(x => x.EntityId, x => x.Payload);
            return Task.FromResult<IReadOnlyList<T>>(current.Select(x =>
                JsonSerializer.Deserialize<T>(x.Payload) ?? throw new InvalidDataException("Invalid household item.")).ToArray());
        }
    }

    private Task Save<T>(string type, IReadOnlyList<T> items, Func<T, Guid> getId)
    {
        lock (_gate)
        {
            if (!_baselines.TryGetValue(type, out var baseline))
                throw new InvalidOperationException("Load household items before saving edits.");
            var desired = items.ToDictionary(getId, x => JsonSerializer.Serialize(x));
            engine.Update(current =>
            {
                var records = current.Where(x => x.EntityType == type).ToDictionary(x => x.EntityId);
                var changes = new List<ChangeRecord>();
                foreach (var (id, payload) in desired)
                    if (!baseline.TryGetValue(id, out var old) || old != payload)
                        changes.Add(Create(id, payload, false));
                foreach (var id in baseline.Keys.Except(desired.Keys))
                    changes.Add(Create(id, "", true));
                return changes;

                ChangeRecord Create(Guid id, string payload, bool deleted) => new(
                    Guid.NewGuid(), deviceId, type, id,
                    checked((records.GetValueOrDefault(id)?.Version ?? 0) + 1),
                    DateTimeOffset.UtcNow, deleted, payload);
            });
            _baselines[type] = desired;
            return Task.CompletedTask;
        }
    }
}
