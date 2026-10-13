namespace FamilyApp.Core.Shopping;

public static class ShoppingShortcutUrl
{
    public static bool TryGetItemName(string? rawUrl, out string itemName)
    {
        itemName = string.Empty;
        if (string.IsNullOrWhiteSpace(rawUrl) || !Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, "familyapp", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, "shopping", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.AbsolutePath, "/add", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var queryPart in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = queryPart.Split('=', 2);
            if (parts.Length == 0 || !string.Equals(parts[0], "item", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            itemName = Uri.UnescapeDataString(parts.Length > 1 ? parts[1] : string.Empty).Trim();
            break;
        }

        return !string.IsNullOrWhiteSpace(itemName);
    }
}
