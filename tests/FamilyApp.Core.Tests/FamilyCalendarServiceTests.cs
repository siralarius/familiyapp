using FamilyApp.Core.Calendar;
using Xunit;
namespace FamilyApp.Core.Tests;
public class FamilyCalendarServiceTests
{
 [Fact] public async Task Range_returns_overlapping_events_in_order(){var r=new Repo();var s=new FamilyCalendarService(r);var d=new DateTime(2026,10,5,0,0,0);await s.AddAsync("Dinner",d.AddHours(18),d.AddHours(19));await s.AddAsync("School",d.AddHours(8),d.AddHours(9),"Alex");var events=await s.GetRangeAsync(d,d.AddDays(1));Assert.Equal(new[]{"School","Dinner"},events.Select(x=>x.Title));}
 [Fact] public void End_must_be_after_start(){var d=DateTime.Now;Assert.Throws<ArgumentException>(()=>FamilyEvent.Create("Bad",d,d));}
 private sealed class Repo:IFamilyEventRepository{private List<FamilyEvent>x=[];public Task<IReadOnlyList<FamilyEvent>> GetAllAsync()=>Task.FromResult<IReadOnlyList<FamilyEvent>>(x.ToList());public Task SaveAsync(IReadOnlyList<FamilyEvent> e){x=e.ToList();return Task.CompletedTask;}}
}
