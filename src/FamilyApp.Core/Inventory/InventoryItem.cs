namespace FamilyApp.Core.Inventory;

public sealed record InventoryItem(Guid Id, string Name, decimal Quantity, decimal LowThreshold, bool AutoAddToShoppingList)
{
    public bool IsLow => Quantity <= LowThreshold;
    public static InventoryItem Create(string name, decimal quantity, decimal lowThreshold, bool autoAdd = false) => new(Guid.NewGuid(), Clean(name), NonNegative(quantity), NonNegative(lowThreshold), autoAdd);
    public InventoryItem Update(string name, decimal quantity, decimal lowThreshold, bool autoAdd) => this with { Name = Clean(name), Quantity = NonNegative(quantity), LowThreshold = NonNegative(lowThreshold), AutoAddToShoppingList = autoAdd };
    private static string Clean(string value) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Item name is required.", nameof(value)) : value.Trim();
    private static decimal NonNegative(decimal value) => value < 0 ? throw new ArgumentOutOfRangeException(nameof(value)) : value;
}
