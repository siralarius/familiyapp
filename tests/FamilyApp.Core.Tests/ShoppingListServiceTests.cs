using FamilyApp.Core.Shopping;
using Xunit;

namespace FamilyApp.Core.Tests;

public class ShoppingListServiceTests
{
    [Fact]
    public async Task Add_toggle_update_and_remove_round_trip()
    {
        var repo = new MemoryRepository();
        var service = new ShoppingListService(repo);
        await service.AddAsync("  Milk  ", "2", "Dairy");
        var item = Assert.Single(await service.GetAllAsync());
        Assert.Equal("Milk", item.Name);
        await service.ToggleAsync(item.Id);
        Assert.True(Assert.Single(await service.GetAllAsync()).IsChecked);
        await service.UpdateAsync(item.Id, "Oat milk", "1", "Dairy");
        Assert.Equal("Oat milk", Assert.Single(await service.GetAllAsync()).Name);
        await service.RemoveAsync(item.Id);
        Assert.Empty(await service.GetAllAsync());
    }

    [Fact]
    public void Empty_name_is_rejected() => Assert.Throws<ArgumentException>(() => ShoppingItem.Create("  "));

    private sealed class MemoryRepository : IShoppingRepository
    {
        private List<ShoppingItem> _items = [];
        public Task<IReadOnlyList<ShoppingItem>> GetAllAsync() => Task.FromResult<IReadOnlyList<ShoppingItem>>(_items.ToList());
        public Task SaveAsync(IReadOnlyList<ShoppingItem> items) { _items = items.ToList(); return Task.CompletedTask; }
    }
}
