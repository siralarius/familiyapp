namespace FamilyApp.Core.Shopping;

public sealed class ShoppingListService(IShoppingRepository repository)
{
    public Task<IReadOnlyList<ShoppingItem>> GetAllAsync() => repository.GetAllAsync();

    public async Task AddAsync(string name, string? quantity = null, string? category = null)
    {
        var items = (await repository.GetAllAsync()).ToList();
        items.Add(ShoppingItem.Create(name, quantity, category));
        await repository.SaveAsync(items);
    }

    public async Task AddIfMissingAsync(string name, string? quantity = null, string? category = null)
    {
        var items = (await repository.GetAllAsync()).ToList();
        if (items.Any(x => !x.IsChecked && string.Equals(x.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))) return;
        items.Add(ShoppingItem.Create(name, quantity, category));
        await repository.SaveAsync(items);
    }

    public async Task ToggleAsync(Guid id)
    {
        var items = (await repository.GetAllAsync()).ToList();
        var index = items.FindIndex(x => x.Id == id);
        if (index >= 0) items[index] = items[index].SetChecked(!items[index].IsChecked);
        await repository.SaveAsync(items);
    }

    public async Task UpdateAsync(Guid id, string name, string? quantity, string? category)
    {
        var items = (await repository.GetAllAsync()).ToList();
        var index = items.FindIndex(x => x.Id == id);
        if (index >= 0) items[index] = items[index].Rename(name, quantity, category);
        await repository.SaveAsync(items);
    }

    public async Task RemoveAsync(Guid id)
    {
        var items = (await repository.GetAllAsync()).Where(x => x.Id != id).ToList();
        await repository.SaveAsync(items);
    }

    public async Task<ShoppingVoiceAddResult> AddFromVoiceAsync(string name, string? quantity = null, string? category = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return ShoppingVoiceAddResult.Failure("Please say the product name you want to add.");
        }

        var normalizedName = name.Trim();
        try
        {
            await AddAsync(normalizedName, quantity, category);
            var exists = (await repository.GetAllAsync()).Any(x =>
                string.Equals(x.Name, normalizedName, StringComparison.OrdinalIgnoreCase));
            return exists
                ? ShoppingVoiceAddResult.Success(normalizedName)
                : ShoppingVoiceAddResult.Failure("I could not confirm the item was saved. Please try again.");
        }
        catch (ArgumentException)
        {
            return ShoppingVoiceAddResult.Failure("Please say the product name you want to add.");
        }
        catch
        {
            return ShoppingVoiceAddResult.Failure("I could not save that item. Please try again.");
        }
    }
}
