namespace FamilyApp.Core.Shopping;

public sealed record ShoppingItem(Guid Id, string Name, string? Quantity, string? Category, bool IsChecked)
{
    public static ShoppingItem Create(string name, string? quantity = null, string? category = null) =>
        new(Guid.NewGuid(), NormalizeRequired(name), Normalize(quantity), Normalize(category), false);

    public ShoppingItem Rename(string name, string? quantity, string? category) =>
        this with { Name = NormalizeRequired(name), Quantity = Normalize(quantity), Category = Normalize(category) };

    public ShoppingItem SetChecked(bool value) => this with { IsChecked = value };

    private static string NormalizeRequired(string value) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Item name is required.", nameof(value)) : value.Trim();
    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
