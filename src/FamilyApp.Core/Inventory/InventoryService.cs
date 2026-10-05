using FamilyApp.Core.Shopping;
namespace FamilyApp.Core.Inventory;

public sealed class InventoryService(IInventoryRepository repository, ShoppingListService shopping)
{
    public Task<IReadOnlyList<InventoryItem>> GetAllAsync() => repository.GetAllAsync();
    public async Task AddAsync(string name, decimal quantity, decimal threshold, bool autoAdd)
    {
        var items=(await repository.GetAllAsync()).ToList(); var item=InventoryItem.Create(name,quantity,threshold,autoAdd); items.Add(item); await repository.SaveAsync(items); await SyncLowItem(item);
    }
    public async Task UpdateAsync(Guid id,string name,decimal quantity,decimal threshold,bool autoAdd)
    {
        var items=(await repository.GetAllAsync()).ToList(); var i=items.FindIndex(x=>x.Id==id); if(i<0)return; items[i]=items[i].Update(name,quantity,threshold,autoAdd); await repository.SaveAsync(items); await SyncLowItem(items[i]);
    }
    public async Task AddLowToShoppingAsync(Guid id) { var item=(await repository.GetAllAsync()).FirstOrDefault(x=>x.Id==id); if(item is not null && item.IsLow) await shopping.AddIfMissingAsync(item.Name, category:"Household"); }
    public async Task RemoveAsync(Guid id) { var items=(await repository.GetAllAsync()).Where(x=>x.Id!=id).ToList(); await repository.SaveAsync(items); }
    private Task SyncLowItem(InventoryItem item) => item.IsLow && item.AutoAddToShoppingList ? shopping.AddIfMissingAsync(item.Name, category:"Household") : Task.CompletedTask;
}
