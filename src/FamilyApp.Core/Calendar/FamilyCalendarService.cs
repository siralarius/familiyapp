namespace FamilyApp.Core.Calendar;
public sealed class FamilyCalendarService(IFamilyEventRepository repository)
{
 public async Task<IReadOnlyList<FamilyEvent>> GetRangeAsync(DateTime from,DateTime to)=>(await repository.GetAllAsync()).Where(x=>x.Start<to&&x.End>from).OrderBy(x=>x.Start).ToList();
 public async Task AddAsync(string title,DateTime start,DateTime end,string? member=null,string? notes=null){var all=(await repository.GetAllAsync()).ToList();all.Add(FamilyEvent.Create(title,start,end,member,notes));await repository.SaveAsync(all);}
 public async Task UpdateAsync(Guid id,string title,DateTime start,DateTime end,string? member,string? notes){var all=(await repository.GetAllAsync()).ToList();var i=all.FindIndex(x=>x.Id==id);if(i>=0)all[i]=all[i].Update(title,start,end,member,notes);await repository.SaveAsync(all);}
 public async Task RemoveAsync(Guid id){var all=(await repository.GetAllAsync()).Where(x=>x.Id!=id).ToList();await repository.SaveAsync(all);}
}
