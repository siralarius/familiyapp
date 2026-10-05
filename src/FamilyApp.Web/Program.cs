using FamilyApp.Core.Inventory;
using FamilyApp.Core.Shopping;
using FamilyApp.Shared.Components;
using FamilyApp.Web.Storage;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<Routes>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");
builder.Services.AddScoped<IShoppingRepository, BrowserShoppingRepository>();
builder.Services.AddScoped<ShoppingListService>();
builder.Services.AddScoped<IInventoryRepository, BrowserInventoryRepository>();
builder.Services.AddScoped<InventoryService>();
await builder.Build().RunAsync();
