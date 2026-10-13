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

    [Fact]
    public async Task Voice_add_reports_success_only_after_save()
    {
        var repo = new MemoryRepository();
        var service = new ShoppingListService(repo);

        var result = await service.AddFromVoiceAsync("  Milk  ");

        Assert.True(result.Succeeded);
        Assert.Equal("Milk", result.ItemName);
        Assert.Contains("Added", result.Message);
        Assert.Single(await service.GetAllAsync());
    }

    [Fact]
    public async Task Voice_add_returns_clear_message_for_missing_name()
    {
        var service = new ShoppingListService(new MemoryRepository());

        var result = await service.AddFromVoiceAsync("   ");

        Assert.False(result.Succeeded);
        Assert.Equal("Please say the product name you want to add.", result.Message);
    }

    [Fact]
    public async Task Voice_add_returns_clear_message_for_repository_failures()
    {
        var service = new ShoppingListService(new ThrowingRepository());

        var result = await service.AddFromVoiceAsync("Milk");

        Assert.False(result.Succeeded);
        Assert.Equal("I could not save that item. Please try again.", result.Message);
    }

    [Fact]
    public void Siri_shortcut_url_parses_item_name()
    {
        var parsed = ShoppingShortcutUrl.TryGetItemName("familyapp://shopping/add?item=oat%20milk", out var itemName);

        Assert.True(parsed);
        Assert.Equal("oat milk", itemName);
    }

    [Fact]
    public void Siri_shortcut_url_rejects_other_routes()
    {
        var parsed = ShoppingShortcutUrl.TryGetItemName("familyapp://calendar/add?item=oat%20milk", out _);

        Assert.False(parsed);
    }

    private sealed class MemoryRepository : IShoppingRepository
    {
        private List<ShoppingItem> _items = [];
        public Task<IReadOnlyList<ShoppingItem>> GetAllAsync() => Task.FromResult<IReadOnlyList<ShoppingItem>>(_items.ToList());
        public Task SaveAsync(IReadOnlyList<ShoppingItem> items) { _items = items.ToList(); return Task.CompletedTask; }
    }

    private sealed class ThrowingRepository : IShoppingRepository
    {
        public Task<IReadOnlyList<ShoppingItem>> GetAllAsync() => Task.FromResult<IReadOnlyList<ShoppingItem>>([]);
        public Task SaveAsync(IReadOnlyList<ShoppingItem> items) => throw new InvalidOperationException("Disk offline");
    }
}
