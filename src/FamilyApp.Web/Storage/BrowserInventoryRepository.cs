using System.Text.Json;
using FamilyApp.Core.Inventory;
using Microsoft.JSInterop;
namespace FamilyApp.Web.Storage;
public sealed class BrowserInventoryRepository(IJSRuntime js):IInventoryRepository
{
    private const string Key="familyapp.inventory.v1";
    public async Task<IReadOnlyList<InventoryItem>> GetAllAsync(){var json=await js.InvokeAsync<string?>("localStorage.getItem",Key);return string.IsNullOrWhiteSpace(json)?[]:JsonSerializer.Deserialize<List<InventoryItem>>(json)??[];}
    public Task SaveAsync(IReadOnlyList<InventoryItem> items)=>js.InvokeVoidAsync("localStorage.setItem",Key,JsonSerializer.Serialize(items)).AsTask();
}
