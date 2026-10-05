using FamilyApp.Core.Calendar;
using FamilyApp.Core.Inventory;
using FamilyApp.Core.Meals;
using FamilyApp.Core.Shopping;
using FamilyApp.Data;
using FamilyApp.Sync.ChangeLog;
using FamilyApp.Sync.Pairing;
using FamilyApp.Sync.Transport;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyApp.Maui;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
        builder.Services.AddMauiBlazorWebView();
        var deviceId = Preferences.Default.Get("device-id", "");
        if (!Guid.TryParse(deviceId, out var parsedId))
        {
            parsedId = Guid.NewGuid();
            Preferences.Default.Set("device-id", parsedId.ToString());
        }
        builder.Services.AddSingleton(new ChangeLogEngine(new SqliteChangeLogStore(
            Path.Combine(FileSystem.AppDataDirectory, "family.db"))));
        builder.Services.AddSingleton(sp => new LocalFamilyRepositories(
            sp.GetRequiredService<ChangeLogEngine>(), parsedId.ToString("N")));
        builder.Services.AddSingleton<IShoppingRepository>(sp => sp.GetRequiredService<LocalFamilyRepositories>());
        builder.Services.AddSingleton<IInventoryRepository>(sp => sp.GetRequiredService<LocalFamilyRepositories>());
        builder.Services.AddSingleton<IMealPlanRepository>(sp => sp.GetRequiredService<LocalFamilyRepositories>());
        builder.Services.AddSingleton<IFamilyEventRepository>(sp => sp.GetRequiredService<LocalFamilyRepositories>());
        builder.Services.AddSingleton<ShoppingListService>();
        builder.Services.AddSingleton<InventoryService>();
        builder.Services.AddSingleton<MealPlanService>();
        builder.Services.AddSingleton<FamilyCalendarService>();
        builder.Services.AddSingleton<IPeerSyncStatusStore, NativeSyncStatusStore>();
        builder.Services.AddSingleton<PeerSyncStatusService>();
        builder.Services.AddSingleton<ISecurePairingStore, NativeSecurePairingStore>();
        builder.Services.AddSingleton<IPairingKeyAgreement, EcdhPairingKeyAgreement>();
        builder.Services.AddSingleton<PairingCoordinator>();
        builder.Services.AddSingleton<NativeDeviceSync>();
        builder.Services.AddSingleton<IDevicePairingService>(sp => sp.GetRequiredService<NativeDeviceSync>());
        return builder.Build();
    }
}
