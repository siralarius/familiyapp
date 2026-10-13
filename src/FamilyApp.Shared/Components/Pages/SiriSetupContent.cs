namespace FamilyApp.Shared.Components.Pages;

public static class SiriSetupContent
{
    public const string ShortcutName = "Add item in Family App";
    public const string SiriPhraseExample = "Add to Family shopping list";
    public const string ShortcutUrlTemplate = "familyapp://shopping/add?item=[Provided Input]";
    public static string BuildShortcutUrl(string itemName) =>
        $"familyapp://shopping/add?item={Uri.EscapeDataString(itemName)}";
}
