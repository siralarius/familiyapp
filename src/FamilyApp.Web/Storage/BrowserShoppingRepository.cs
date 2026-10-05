using System.Text.Json;
using FamilyApp.Core.Shopping;
using Microsoft.JSInterop;

namespace FamilyApp.Web.Storage;

public sealed class BrowserShoppingRepository(IJSRuntime js) : IShoppingRepository
{
    private const string Key = "familyapp.shopping.v1";

    public async Task<IReadOnlyList<ShoppingItem>> GetAllAsync()
    {
        var json = await js.InvokeAsync<string?>("localStorage.getItem", Key);
        return string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<ShoppingItem>>(json) ?? [];
    }

    public Task SaveAsync(IReadOnlyList<ShoppingItem> items) =>
        js.InvokeVoidAsync("localStorage.setItem", Key, JsonSerializer.Serialize(items)).AsTask();
}
