namespace FamilyApp.Core.Shopping;

public interface IShoppingRepository
{
    Task<IReadOnlyList<ShoppingItem>> GetAllAsync();
    Task SaveAsync(IReadOnlyList<ShoppingItem> items);
}
