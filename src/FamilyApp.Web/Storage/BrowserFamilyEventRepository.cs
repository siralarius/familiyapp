using System.Text.Json;
using FamilyApp.Core.Calendar;
using Microsoft.JSInterop;
namespace FamilyApp.Web.Storage;
public sealed class BrowserFamilyEventRepository(IJSRuntime js):IFamilyEventRepository
{
 private const string Key="familyapp.calendar.v1";
 public async Task<IReadOnlyList<FamilyEvent>> GetAllAsync(){var json=await js.InvokeAsync<string?>("localStorage.getItem",Key);return string.IsNullOrWhiteSpace(json)?[]:JsonSerializer.Deserialize<List<FamilyEvent>>(json)??[];}
 public Task SaveAsync(IReadOnlyList<FamilyEvent> events)=>js.InvokeVoidAsync("localStorage.setItem",Key,JsonSerializer.Serialize(events)).AsTask();
}
