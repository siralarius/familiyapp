using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using FamilyApp.Core.Meals;
using FamilyApp.Core.Shopping;

namespace FamilyApp.Core.Planning;

public sealed class PlanningDraft
{
    public int Version { get; set; }
    public Guid ImportId { get; set; }
    public DateOnly? WeekStart { get; set; }
    public List<DraftMeal> Meals { get; set; } = [];
    public List<DraftPurchase> Shopping { get; set; } = [];
    public List<string> Questions { get; set; } = [];

    public static PlanningDraft Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 128_000)
            throw new ArgumentException("Paste the exported plan (at most 128 KB).");
        json = json.Trim();
        if (json.StartsWith("```json", StringComparison.Ordinal) && json.EndsWith("```", StringComparison.Ordinal))
            json = json[7..^3].Trim();
        try
        {
            var draft = JsonSerializer.Deserialize<PlanningDraft>(json, JsonOptions)
                ?? throw new ArgumentException("The plan is empty.");
            draft.ValidateStructure();
            return draft;
        }
        catch (JsonException) { throw new ArgumentException("The export is invalid. Ask ChatGPT for the Family App JSON format again; dates must use YYYY-MM-DD."); }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter<MealType>(allowIntegerValues: false) }
    };

    public void ValidateStructure()
    {
        if (Version != 1 || ImportId == Guid.Empty)
            throw new ArgumentException("Use a version 1 export with a unique importId.");
        if (Meals is null || Shopping is null || Questions is null || Meals.Count > 100 || Shopping.Count > 100 || Questions.Count > 30 ||
            Meals.Any(x => x is null) || Shopping.Any(x => x is null) || Questions.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("The plan contains invalid or too many entries.");
        foreach (var meal in Meals) ValidateText(meal.Dish, "Meal", required: false);
        foreach (var item in Shopping)
        {
            ValidateText(item.Name, "Product", required: false);
            ValidateText(item.Quantity, "Quantity", required: false);
            ValidateText(item.Unit, "Unit", required: false);
            if (item.Source is not ("explicit" or "inferred"))
                throw new ArgumentException("Each purchase must say whether it is explicit or inferred.");
        }
        foreach (var question in Questions) ValidateText(question, "Question");
    }

    public PreparedPlanningImport Prepare()
    {
        ValidateStructure();
        if (WeekStart is not DateOnly start || start == DateOnly.MinValue || start > DateOnly.MaxValue.AddDays(-6))
            throw new ArgumentException("Choose the first date of your planning week.");
        if (Questions.Count != 0) throw new ArgumentException("Resolve and dismiss each question before saving.");
        var meals = new List<MealPlanEntry>();
        foreach (var (row, index) in Meals.Select((row, index) => (row, index)).Where(x => x.row.Include))
        {
            if (row.Date is not DateOnly date || date < start || date > start.AddDays(6) || row.Slot is not MealType slot || !Enum.IsDefined(slot))
                throw new ArgumentException("Every selected meal needs a date in this week and a meal slot.");
            ValidateText(row.Dish, "Meal");
            meals.Add(MealPlanEntry.Create(date, slot, row.Dish!) with { Id = EntryId("meals", index) });
        }
        if (meals.GroupBy(x => (x.Date, x.MealType)).Any(x => x.Count() > 1))
            throw new ArgumentException("Choose only one meal for each date and slot.");
        var shopping = Shopping.Select((row, index) => (row, index)).Where(x => x.row.Include).Select(entry =>
        {
            var (x, index) = entry;
            return CreatePurchase(x, index);
        }).ToArray();
        ShoppingItem CreatePurchase(DraftPurchase x, int index)
        {
            ValidateText(x.Name, "Product");
            if (!string.IsNullOrWhiteSpace(x.Unit) && string.IsNullOrWhiteSpace(x.Quantity))
                throw new ArgumentException("A unit needs a stated quantity. Clear the unit if the amount is unknown.");
            return ShoppingItem.Create(x.Name!, string.Join(" ", new[] { x.Quantity?.Trim(), x.Unit?.Trim() }.Where(s => !string.IsNullOrEmpty(s)))) with { Id = EntryId("shopping", index) };
        }
        if (meals.Count + shopping.Length == 0) throw new ArgumentException("Select at least one meal or purchase to save.");
        return new(ImportId, meals, shopping);
    }

    private Guid EntryId(string type, int index) => new(SHA256.HashData(Encoding.UTF8.GetBytes($"{ImportId:N}/{type}/{index}"))[..16]);

    private static void ValidateText(string? value, string label, bool required = true)
    {
        if (required && string.IsNullOrWhiteSpace(value) || value?.Length > 500 || value?.Any(char.IsControl) == true)
            throw new ArgumentException($"{label} must be readable text of at most 500 characters.");
    }
}

public sealed class DraftMeal
{
    public DateOnly? Date { get; set; }
    public MealType? Slot { get; set; }
    public string? Dish { get; set; }
    public bool Agreed { get; set; }
    [JsonIgnore] public bool Include { get; set; }
}
public sealed class DraftPurchase
{
    public string? Name { get; set; }
    public string? Quantity { get; set; }
    public string? Unit { get; set; }
    public string Source { get; set; } = "explicit";
    [JsonIgnore] public bool Include { get; set; }
}
public sealed record PreparedPlanningImport(Guid ImportId, IReadOnlyList<MealPlanEntry> Meals, IReadOnlyList<ShoppingItem> Shopping);
public sealed record PlanningImportResult(int MealsAdded, int PurchasesAdded, int PurchasesSkipped, bool AlreadyImported = false);
public interface IPlanningImportStore
{
    Task<PlanningImportResult> SaveAsync(PreparedPlanningImport draft);
}
public sealed class PlanningImportService(IPlanningImportStore store)
{
    public PlanningDraft Read(string json)
    {
        var draft = PlanningDraft.Parse(json);
        foreach (var meal in draft.Meals) meal.Include = meal.Agreed;
        foreach (var item in draft.Shopping) item.Include = item.Source == "explicit";
        return draft;
    }
    public Task<PlanningImportResult> SaveAsync(PlanningDraft draft) => store.SaveAsync(draft.Prepare());
}
