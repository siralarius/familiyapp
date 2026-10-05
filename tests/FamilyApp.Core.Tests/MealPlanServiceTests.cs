using FamilyApp.Core.Meals;
using Xunit;
namespace FamilyApp.Core.Tests;
public class MealPlanServiceTests
{
 [Fact] public async Task Week_query_only_returns_selected_week(){var r=new Repo();var s=new MealPlanService(r);var monday=new DateOnly(2026,10,5);await s.AddAsync(monday,MealType.Dinner,"Pasta");await s.AddAsync(monday.AddDays(7),MealType.Dinner,"Curry");var week=await s.GetWeekAsync(monday);Assert.Single(week);Assert.Equal("Pasta",week[0].Description);}
 [Fact] public void Start_of_week_is_monday(){Assert.Equal(new DateOnly(2026,10,5),MealPlanService.StartOfWeek(new DateOnly(2026,10,11)));}
 private sealed class Repo:IMealPlanRepository{private List<MealPlanEntry>x=[];public Task<IReadOnlyList<MealPlanEntry>> GetAllAsync()=>Task.FromResult<IReadOnlyList<MealPlanEntry>>(x.ToList());public Task SaveAsync(IReadOnlyList<MealPlanEntry> e){x=e.ToList();return Task.CompletedTask;}}
}
