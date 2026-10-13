namespace FamilyApp.Core.Shopping;

public readonly record struct ShoppingVoiceAddResult(bool Succeeded, string Message, string? ItemName)
{
    public static ShoppingVoiceAddResult Success(string itemName) =>
        new(true, $"Added {itemName} to your shopping list.", itemName);

    public static ShoppingVoiceAddResult Failure(string message) =>
        new(false, message, null);
}
