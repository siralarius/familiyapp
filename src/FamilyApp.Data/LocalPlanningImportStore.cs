using System.Text.Json;
using FamilyApp.Core.Meals;
using FamilyApp.Core.Planning;
using FamilyApp.Core.Shopping;
using FamilyApp.Sync.ChangeLog;

namespace FamilyApp.Data;

// Use one persisted change-log batch for meals, purchases and the replay receipt.
public sealed class LocalPlanningImportStore(ChangeLogEngine engine, string deviceId) : IPlanningImportStore
{
    public Task<PlanningImportResult> SaveAsync(PreparedPlanningImport draft)
    {
        PlanningImportResult result = new(0, 0, 0);
        engine.Update(current =>
        {
            if (current.Any(x => x.EntityType == "planning-import" && x.EntityId == draft.ImportId))
            {
                result = result with { AlreadyImported = true };
                return [];
            }
            var existingMeals = current.Where(x => x.EntityType == "meals" && !x.IsTombstone)
                .Select(x => JsonSerializer.Deserialize<MealPlanEntry>(x.Payload)!).ToArray();
            foreach (var meal in draft.Meals)
                if (existingMeals.Any(x => x.Date == meal.Date && x.MealType == meal.MealType))
                    throw new InvalidOperationException($"{meal.Date:yyyy-MM-dd} {meal.MealType} already has a meal. Uncheck that draft meal or edit the existing plan first.");
            var names = current.Where(x => x.EntityType == "shopping" && !x.IsTombstone)
                .Select(x => JsonSerializer.Deserialize<ShoppingItem>(x.Payload)!)
                .Where(x => !x.IsChecked).Select(x => x.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var changes = new List<ChangeRecord>();
            foreach (var meal in draft.Meals) changes.Add(Create("meals", meal.Id, meal));
            var skipped = 0;
            foreach (var item in draft.Shopping)
                if (names.Add(item.Name.Trim())) changes.Add(Create("shopping", item.Id, item)); else skipped++;
            result = new(draft.Meals.Count, draft.Shopping.Count - skipped, skipped);
            changes.Add(Create("planning-import", draft.ImportId, result));
            return changes;
        });
        return Task.FromResult(result);
    }
    private ChangeRecord Create<T>(string type, Guid id, T payload) => new(
        Guid.NewGuid(), deviceId, type, id, 1, DateTimeOffset.UtcNow, false, JsonSerializer.Serialize(payload));
}
