using System.Text.Json;
using FamilyApp.Core.Meals;
using Microsoft.JSInterop;
namespace FamilyApp.Web.Storage;
public sealed class BrowserMealPlanRepository(IJSRuntime js):IMealPlanRepository
{
 private const string Key="familyapp.meals.v1";
 public async Task<IReadOnlyList<MealPlanEntry>> GetAllAsync(){var json=await js.InvokeAsync<string?>("localStorage.getItem",Key);return string.IsNullOrWhiteSpace(json)?[]:JsonSerializer.Deserialize<List<MealPlanEntry>>(json)??[];}
 public Task SaveAsync(IReadOnlyList<MealPlanEntry> entries)=>js.InvokeVoidAsync("localStorage.setItem",Key,JsonSerializer.Serialize(entries)).AsTask();
}
