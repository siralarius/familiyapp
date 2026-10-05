namespace FamilyApp.Core.Meals;
public sealed class MealPlanService(IMealPlanRepository repository)
{
 public async Task<IReadOnlyList<MealPlanEntry>> GetWeekAsync(DateOnly weekStart){var end=weekStart.AddDays(7);return (await repository.GetAllAsync()).Where(x=>x.Date>=weekStart&&x.Date<end).OrderBy(x=>x.Date).ThenBy(x=>x.MealType).ToList();}
 public async Task AddAsync(DateOnly date,MealType type,string description,string? notes=null){var all=(await repository.GetAllAsync()).ToList();all.Add(MealPlanEntry.Create(date,type,description,notes));await repository.SaveAsync(all);}
 public async Task UpdateAsync(Guid id,MealType type,string description,string? notes){var all=(await repository.GetAllAsync()).ToList();var i=all.FindIndex(x=>x.Id==id);if(i>=0)all[i]=all[i].Update(type,description,notes);await repository.SaveAsync(all);}
 public async Task RemoveAsync(Guid id){var all=(await repository.GetAllAsync()).Where(x=>x.Id!=id).ToList();await repository.SaveAsync(all);}
 public static DateOnly StartOfWeek(DateOnly date)=>date.AddDays(-(((int)date.DayOfWeek+6)%7));
}
