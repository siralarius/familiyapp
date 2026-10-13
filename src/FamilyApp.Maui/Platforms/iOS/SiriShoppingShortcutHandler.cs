using FamilyApp.Core.Shopping;
using FamilyApp.Data;
using FamilyApp.Sync.ChangeLog;
using Foundation;
using Microsoft.Maui.Storage;
using UIKit;

namespace FamilyApp.Maui.Platforms.iOS;

internal static class SiriShoppingShortcutHandler
{
    private const string DeviceIdKey = "device-id";
    private const string DataFileName = "family.db";
    private const string SiriResultKey = "siri-shopping-last-result";
    private const string SiriResultAtKey = "siri-shopping-last-result-at";

    public static bool TryHandleUrl(UIApplication application, NSUrl url)
    {
        if (!TryGetRequestedItem(url, out var itemName))
        {
            return false;
        }

        var result = AddItemAsync(itemName).GetAwaiter().GetResult();
        Preferences.Default.Set(SiriResultKey, result.Message);
        Preferences.Default.Set(SiriResultAtKey, DateTimeOffset.UtcNow.ToString("O"));
        return true;
    }

    private static async Task<ShoppingVoiceAddResult> AddItemAsync(string itemName)
    {
        var deviceId = Preferences.Default.Get(DeviceIdKey, "");
        if (!Guid.TryParse(deviceId, out var parsedId))
        {
            parsedId = Guid.NewGuid();
            Preferences.Default.Set(DeviceIdKey, parsedId.ToString());
        }

        var databasePath = Path.Combine(FileSystem.AppDataDirectory, DataFileName);
        var changeLog = new ChangeLogEngine(new SqliteChangeLogStore(databasePath));
        IShoppingRepository repository = new LocalFamilyRepositories(changeLog, parsedId.ToString("N"));
        var shopping = new ShoppingListService(repository);
        return await shopping.AddFromVoiceAsync(itemName);
    }

    private static bool TryGetRequestedItem(NSUrl url, out string itemName)
    {
        return ShoppingShortcutUrl.TryGetItemName(url.AbsoluteString, out itemName);
    }
}
