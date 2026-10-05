namespace FamilyApp.Core.Calendar;
public interface IFamilyEventRepository { Task<IReadOnlyList<FamilyEvent>> GetAllAsync(); Task SaveAsync(IReadOnlyList<FamilyEvent> events); }
