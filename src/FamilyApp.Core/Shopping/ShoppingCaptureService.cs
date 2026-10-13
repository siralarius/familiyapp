namespace FamilyApp.Core.Shopping;

public interface IShoppingCaptureStore
{
    Task<bool> AddIfMissingAsync(ShoppingItem item);
}

// A native assistant's success response must follow a durable local write.
public sealed class ShoppingCaptureService(IShoppingCaptureStore store)
{
    public event Action? Changed;
    public async Task<string> AddAsync(string product, string? quantity = null)
    {
        if (product?.Length > 200 || quantity?.Length > 100 || product?.Any(char.IsControl) == true || quantity?.Any(char.IsControl) == true)
            throw new ArgumentException("Use a short product name and quantity.");
        var item = ShoppingItem.Create(product!, quantity);
        var added = await store.AddIfMissingAsync(item);
        if (added)
            foreach (var callback in Changed?.GetInvocationList() ?? [])
                try { ((Action)callback)(); } catch { /* UI failure cannot turn a persisted write into a failed Siri reply. */ }
        return added ? $"Added {item.Name} to your family shopping list." : $"{item.Name} is already on your family shopping list. Its quantity was kept.";
    }
}
