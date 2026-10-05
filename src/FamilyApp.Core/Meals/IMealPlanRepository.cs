namespace FamilyApp.Core.Meals;
public interface IMealPlanRepository { Task<IReadOnlyList<MealPlanEntry>> GetAllAsync(); Task SaveAsync(IReadOnlyList<MealPlanEntry> entries); }
