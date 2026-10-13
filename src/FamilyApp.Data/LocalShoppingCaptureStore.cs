using System.Text.Json;
using FamilyApp.Core.Shopping;
using FamilyApp.Sync.ChangeLog;

namespace FamilyApp.Data;
public sealed class LocalShoppingCaptureStore(ChangeLogEngine engine, string deviceId) : IShoppingCaptureStore
{
    public Task<bool> AddIfMissingAsync(ShoppingItem item)
    {
        var added = false;
        engine.Update(current =>
        {
            var items = current.Where(x => x.EntityType == "shopping" && !x.IsTombstone)
                .Select(x => JsonSerializer.Deserialize<ShoppingItem>(x.Payload)!);
            if (items.Any(x => !x.IsChecked && string.Equals(x.Name.Trim(), item.Name.Trim(), StringComparison.OrdinalIgnoreCase))) return [];
            added = true;
            return [new ChangeRecord(Guid.NewGuid(), deviceId, "shopping", item.Id, 1,
                DateTimeOffset.UtcNow, false, JsonSerializer.Serialize(item))];
        });
        return Task.FromResult(added);
    }
}
