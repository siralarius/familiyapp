using FamilyApp.Core.Inventory;
using FamilyApp.Core.Shopping;
using Xunit;
namespace FamilyApp.Core.Tests;
public class InventoryServiceTests
{
 [Fact] public async Task Low_auto_item_is_added_to_shopping_once(){var shoppingRepo=new ShopRepo();var service=new InventoryService(new InvRepo(),new ShoppingListService(shoppingRepo));await service.AddAsync("Milk",1,1,true);var item=Assert.Single(await service.GetAllAsync());await service.AddLowToShoppingAsync(item.Id);Assert.Single(await shoppingRepo.GetAllAsync());}
 [Fact] public void Item_reports_low_at_threshold(){Assert.True(InventoryItem.Create("Eggs",6,6).IsLow);Assert.False(InventoryItem.Create("Eggs",7,6).IsLow);}
 private sealed class InvRepo:IInventoryRepository{private List<InventoryItem> x=[];public Task<IReadOnlyList<InventoryItem>> GetAllAsync()=>Task.FromResult<IReadOnlyList<InventoryItem>>(x.ToList());public Task SaveAsync(IReadOnlyList<InventoryItem> i){x=i.ToList();return Task.CompletedTask;}}
 private sealed class ShopRepo:IShoppingRepository{private List<ShoppingItem>x=[];public Task<IReadOnlyList<ShoppingItem>> GetAllAsync()=>Task.FromResult<IReadOnlyList<ShoppingItem>>(x.ToList());public Task SaveAsync(IReadOnlyList<ShoppingItem> i){x=i.ToList();return Task.CompletedTask;}}
}
