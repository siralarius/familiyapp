namespace FamilyApp.Core.Meals;
public enum MealType { Breakfast, Lunch, Dinner }
public sealed record MealPlanEntry(Guid Id, DateOnly Date, MealType MealType, string Description, string? Notes)
{
 public static MealPlanEntry Create(DateOnly date,MealType type,string description,string? notes=null)=>new(Guid.NewGuid(),date,type,Clean(description),Optional(notes));
 public MealPlanEntry Update(MealType type,string description,string? notes)=>this with{MealType=type,Description=Clean(description),Notes=Optional(notes)};
 private static string Clean(string value)=>string.IsNullOrWhiteSpace(value)?throw new ArgumentException("Meal description is required.",nameof(value)):value.Trim();
 private static string? Optional(string? value)=>string.IsNullOrWhiteSpace(value)?null:value.Trim();
}
