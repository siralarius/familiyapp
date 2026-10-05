namespace FamilyApp.Core.Inventory;
public interface IInventoryRepository { Task<IReadOnlyList<InventoryItem>> GetAllAsync(); Task SaveAsync(IReadOnlyList<InventoryItem> items); }
