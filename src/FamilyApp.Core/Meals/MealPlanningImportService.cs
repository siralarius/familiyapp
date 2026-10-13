using System.Globalization;
using System.Text;
using System.Text.Json;
using FamilyApp.Core.Shopping;

namespace FamilyApp.Core.Meals;

public sealed class MealPlanningImportService(IMealPlanRepository mealRepository, IShoppingRepository shoppingRepository)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private static readonly Dictionary<string, int> DayOffsets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["monday"] = 0,
        ["lunes"] = 0,
        ["tuesday"] = 1,
        ["martes"] = 1,
        ["wednesday"] = 2,
        ["miercoles"] = 2,
        ["miércoles"] = 2,
        ["thursday"] = 3,
        ["jueves"] = 3,
        ["friday"] = 4,
        ["viernes"] = 4,
        ["saturday"] = 5,
        ["sabado"] = 5,
        ["sábado"] = 5,
        ["sunday"] = 6,
        ["domingo"] = 6
    };

    public async Task<MealPlanningImportDraftResult> BuildDraftAsync(string rawJson, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
            return MealPlanningImportDraftResult.Fail("Paste the JSON export from ChatGPT before importing.");

        ImportContract? contract;
        try
        {
            contract = JsonSerializer.Deserialize<ImportContract>(rawJson, JsonOptions);
        }
        catch (JsonException ex)
        {
            return MealPlanningImportDraftResult.Fail($"Invalid JSON: {ex.Message}");
        }

        if (contract is null)
            return MealPlanningImportDraftResult.Fail("The JSON export is empty.");

        if (!IsSupportedVersion(contract.Version))
            return MealPlanningImportDraftResult.Fail("Unsupported import version. Use contract version 1.");

        var meals = await mealRepository.GetAllAsync();
        var shoppingItems = await shoppingRepository.GetAllAsync();

        var draft = new MealPlanningImportDraft
        {
            Version = string.IsNullOrWhiteSpace(contract.Version) ? "1" : contract.Version.Trim(),
            WeekStart = ParseDate(contract.WeekStart)
        };

        foreach (var question in contract.UnresolvedQuestions ?? [])
        {
            if (!string.IsNullOrWhiteSpace(question))
                draft.UnresolvedQuestions.Add(question.Trim());
        }

        var seenMeals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var meal in contract.Meals ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = BuildMealDraft(meal, draft.WeekStart, meals);
            if (!string.IsNullOrWhiteSpace(row.Dish) && ParseDate(row.Date) is DateOnly date &&
                TryParseMealType(row.MealSlot, out var mealType))
            {
                var key = $"{date:yyyy-MM-dd}|{mealType}|{row.Dish.Trim()}";
                if (!seenMeals.Add(key))
                {
                    row.Include = false;
                    row.Issues.Add("Duplicate meal in this import file.");
                }
            }

            if (row.Issues.Count > 0)
                draft.UnresolvedQuestions.AddRange(row.Issues.Select(x => $"Meal '{row.DishOrPlaceholder}': {x}"));

            draft.Meals.Add(row);
        }

        var seenShopping = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in contract.ShoppingItems ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = BuildShoppingDraft(item, shoppingItems);
            var key = $"{Normalize(row.Name)}|{Normalize(BuildQuantity(row.Quantity, row.Unit))}";
            if (!string.IsNullOrWhiteSpace(row.Name) && !seenShopping.Add(key))
            {
                row.Include = false;
                row.Issues.Add("Duplicate shopping item in this import file.");
            }

            if (row.Issues.Count > 0)
                draft.UnresolvedQuestions.AddRange(row.Issues.Select(x => $"Shopping '{row.NameOrPlaceholder}': {x}"));

            draft.ShoppingItems.Add(row);
        }

        if (draft.Meals.Count == 0 && draft.ShoppingItems.Count == 0)
            return MealPlanningImportDraftResult.Fail("No meals or shopping items were found in the import JSON.");

        return MealPlanningImportDraftResult.Success(draft);
    }

    public async Task<MealPlanningImportSaveResult> SaveDraftAsync(MealPlanningImportDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var validationErrors = ValidateDraftForSave(draft);
        if (validationErrors.Count > 0)
            return MealPlanningImportSaveResult.Fail(validationErrors);

        var meals = (await mealRepository.GetAllAsync()).ToList();
        var shoppingItems = (await shoppingRepository.GetAllAsync()).ToList();

        var mealsAdded = 0;
        var mealsUpdated = 0;
        var mealsSkipped = 0;
        var shoppingAdded = 0;
        var shoppingSkipped = 0;

        foreach (var row in draft.Meals.Where(x => x.Include))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var date = ParseDate(row.Date)!.Value;
            TryParseMealType(row.MealSlot, out var mealType);
            var description = row.Dish.Trim();
            var notes = NormalizeOptional(row.Notes);

            var exact = meals.FirstOrDefault(x => x.Date == date && x.MealType == mealType &&
                                                  string.Equals(x.Description, description, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                mealsSkipped++;
                continue;
            }

            var sameSlot = meals.FirstOrDefault(x => x.Date == date && x.MealType == mealType);
            if (sameSlot is not null)
            {
                if (!row.ReplaceExisting)
                {
                    mealsSkipped++;
                    continue;
                }

                var index = meals.FindIndex(x => x.Id == sameSlot.Id);
                meals[index] = sameSlot.Update(mealType, description, notes);
                mealsUpdated++;
                continue;
            }

            meals.Add(MealPlanEntry.Create(date, mealType, description, notes));
            mealsAdded++;
        }

        foreach (var row in draft.ShoppingItems.Where(x => x.Include))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = row.Name.Trim();
            var quantity = BuildQuantity(row.Quantity, row.Unit);

            var alreadyExists = shoppingItems.Any(x =>
                string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(quantity) || string.Equals(NormalizeOptional(x.Quantity), quantity,
                    StringComparison.OrdinalIgnoreCase)));
            if (alreadyExists || shoppingItems.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                shoppingSkipped++;
                continue;
            }

            shoppingItems.Add(ShoppingItem.Create(name, quantity));
            shoppingAdded++;
        }

        if (mealsAdded > 0 || mealsUpdated > 0)
            await mealRepository.SaveAsync(meals);

        if (shoppingAdded > 0)
            await shoppingRepository.SaveAsync(shoppingItems);

        return MealPlanningImportSaveResult.Success(
            mealsAdded,
            mealsUpdated,
            mealsSkipped,
            shoppingAdded,
            shoppingSkipped);
    }

    private static List<string> ValidateDraftForSave(MealPlanningImportDraft draft)
    {
        var errors = new List<string>();
        foreach (var row in draft.Meals.Where(x => x.Include))
        {
            if (!row.IsAgreed)
                errors.Add($"Meal '{row.DishOrPlaceholder}' is not marked as agreed.");
            if (ParseDate(row.Date) is null)
                errors.Add($"Meal '{row.DishOrPlaceholder}' needs a valid date.");
            if (!TryParseMealType(row.MealSlot, out _))
                errors.Add($"Meal '{row.DishOrPlaceholder}' needs a meal slot (breakfast/lunch/dinner).");
            if (string.IsNullOrWhiteSpace(row.Dish))
                errors.Add("A selected meal is missing the dish name.");
        }

        foreach (var row in draft.ShoppingItems.Where(x => x.Include))
        {
            if (string.IsNullOrWhiteSpace(row.Name))
                errors.Add("A selected shopping item is missing the product name.");
            if (row.IsInferred && !row.ConfirmInferred)
                errors.Add($"Confirm inferred shopping item '{row.NameOrPlaceholder}' before saving.");
        }

        return errors;
    }

    private static MealPlanningMealDraft BuildMealDraft(ImportMeal meal, DateOnly? weekStart, IReadOnlyList<MealPlanEntry> existingMeals)
    {
        var agreed = ResolveAgreement(meal);
        var row = new MealPlanningMealDraft
        {
            Date = ResolveDate(meal.Date, meal.Day, weekStart)?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
            MealSlot = NormalizeOptional(meal.MealSlot) ?? string.Empty,
            Dish = NormalizeOptional(meal.Dish) ?? string.Empty,
            Notes = NormalizeOptional(meal.Notes),
            IsAgreed = agreed,
            Include = agreed
        };

        if (string.IsNullOrWhiteSpace(row.Dish))
            row.Issues.Add("Dish is required.");

        if (string.IsNullOrWhiteSpace(row.Date))
            row.Issues.Add("Date is missing. Provide a date or day with weekStart.");

        if (!TryParseMealType(row.MealSlot, out var mealType))
        {
            row.Issues.Add("Meal slot is missing or invalid.");
        }
        else
        {
            row.MealSlot = ToMealSlotValue(mealType);
        }

        if (!row.IsAgreed)
            row.Issues.Add("Tentative or unclear meal: review before saving.");

        if (ParseDate(row.Date) is DateOnly date && TryParseMealType(row.MealSlot, out mealType))
        {
            var existingAtSlot = existingMeals.FirstOrDefault(x => x.Date == date && x.MealType == mealType);
            if (existingAtSlot is not null)
            {
                if (string.Equals(existingAtSlot.Description, row.Dish, StringComparison.OrdinalIgnoreCase))
                {
                    row.Include = false;
                    row.Issues.Add("This meal already exists in the planner.");
                }
                else
                {
                    row.Include = false;
                    row.HasConflict = true;
                    row.ConflictDescription = existingAtSlot.Description;
                    row.Issues.Add($"Existing {mealType} is '{existingAtSlot.Description}'. Choose replace to overwrite.");
                }
            }
        }

        return row;
    }

    private static MealPlanningShoppingDraft BuildShoppingDraft(ImportShoppingItem item, IReadOnlyList<ShoppingItem> existingShopping)
    {
        var source = NormalizeOptional(item.Source) ?? "explicit";
        var isInferred = IsInferredSource(source);
        var row = new MealPlanningShoppingDraft
        {
            Name = NormalizeOptional(item.Name) ?? string.Empty,
            Quantity = NormalizeOptional(item.Quantity),
            Unit = NormalizeOptional(item.Unit),
            Source = source,
            IsInferred = isInferred,
            ConfirmInferred = !isInferred,
            Include = true
        };

        if (string.IsNullOrWhiteSpace(row.Name))
        {
            row.Include = false;
            row.Issues.Add("Product name is required.");
            return row;
        }

        if (isInferred)
        {
            row.Include = false;
            row.Issues.Add("Inferred ingredient: confirm before adding to shopping list.");
        }

        var quantity = BuildQuantity(row.Quantity, row.Unit);
        var exists = existingShopping.Any(x => string.Equals(x.Name, row.Name, StringComparison.OrdinalIgnoreCase) &&
                                               (string.IsNullOrWhiteSpace(quantity) ||
                                                string.Equals(NormalizeOptional(x.Quantity), quantity, StringComparison.OrdinalIgnoreCase)))
                     || existingShopping.Any(x => string.Equals(x.Name, row.Name, StringComparison.OrdinalIgnoreCase));
        if (exists)
        {
            row.Include = false;
            row.Issues.Add("This product already exists in the shopping list.");
        }

        return row;
    }

    private static bool ResolveAgreement(ImportMeal meal)
    {
        if (meal.Agreed is bool agreed)
            return agreed;

        var value = Normalize(meal.Agreement);
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value switch
        {
            "agreed" or "agreement" or "confirmed" or "confirmado" or "confirmada" or "acordado" or "acordada" => true,
            _ => false
        };
    }

    private static bool TryParseMealType(string? value, out MealType mealType)
    {
        var normalized = Normalize(value);
        switch (normalized)
        {
            case "breakfast":
            case "desayuno":
                mealType = MealType.Breakfast;
                return true;
            case "lunch":
            case "almuerzo":
            case "comida":
                mealType = MealType.Lunch;
                return true;
            case "dinner":
            case "cena":
                mealType = MealType.Dinner;
                return true;
            default:
                mealType = default;
                return false;
        }
    }

    private static DateOnly? ResolveDate(string? date, string? day, DateOnly? weekStart)
    {
        var parsedDate = ParseDate(date);
        if (parsedDate is not null)
            return parsedDate;

        if (weekStart is null || string.IsNullOrWhiteSpace(day))
            return null;

        var normalizedDay = Normalize(day);
        return DayOffsets.TryGetValue(normalizedDay, out var offset) ? weekStart.Value.AddDays(offset) : null;
    }

    private static DateOnly? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return DateOnly.TryParse(value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    private static bool IsSupportedVersion(string? version)
    {
        var normalized = Normalize(version);
        return normalized is "1" or "1.0" or "v1" or "v1.0";
    }

    private static bool IsInferredSource(string source)
    {
        var normalized = Normalize(source);
        return normalized is "inferred" or "ingredient" or "inferencia" or "inferido";
    }

    private static string? BuildQuantity(string? quantity, string? unit)
    {
        var q = NormalizeOptional(quantity);
        var u = NormalizeOptional(unit);
        if (q is null && u is null)
            return null;
        if (q is null)
            return u;
        if (u is null)
            return q;
        return $"{q} {u}";
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                builder.Append(c);
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string ToMealSlotValue(MealType mealType) => mealType switch
    {
        MealType.Breakfast => "breakfast",
        MealType.Lunch => "lunch",
        _ => "dinner"
    };

    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class ImportContract
    {
        public string? Version { get; set; }
        public string? WeekStart { get; set; }
        public List<ImportMeal>? Meals { get; set; }
        public List<ImportShoppingItem>? ShoppingItems { get; set; }
        public List<string>? UnresolvedQuestions { get; set; }
    }

    private sealed class ImportMeal
    {
        public string? Date { get; set; }
        public string? Day { get; set; }
        public string? MealSlot { get; set; }
        public string? Dish { get; set; }
        public string? Notes { get; set; }
        public string? Agreement { get; set; }
        public bool? Agreed { get; set; }
    }

    private sealed class ImportShoppingItem
    {
        public string? Name { get; set; }
        public string? Quantity { get; set; }
        public string? Unit { get; set; }
        public string? Source { get; set; }
    }
}

public sealed class MealPlanningImportDraft
{
    public string Version { get; set; } = "1";
    public DateOnly? WeekStart { get; set; }
    public List<MealPlanningMealDraft> Meals { get; } = [];
    public List<MealPlanningShoppingDraft> ShoppingItems { get; } = [];
    public List<string> UnresolvedQuestions { get; } = [];
}

public sealed class MealPlanningMealDraft
{
    public string Date { get; set; } = string.Empty;
    public string MealSlot { get; set; } = string.Empty;
    public string Dish { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public bool IsAgreed { get; set; }
    public bool Include { get; set; }
    public bool HasConflict { get; set; }
    public bool ReplaceExisting { get; set; }
    public string? ConflictDescription { get; set; }
    public List<string> Issues { get; } = [];
    public string DishOrPlaceholder => string.IsNullOrWhiteSpace(Dish) ? "(unnamed meal)" : Dish;
}

public sealed class MealPlanningShoppingDraft
{
    public string Name { get; set; } = string.Empty;
    public string? Quantity { get; set; }
    public string? Unit { get; set; }
    public string Source { get; set; } = "explicit";
    public bool IsInferred { get; set; }
    public bool ConfirmInferred { get; set; }
    public bool Include { get; set; }
    public List<string> Issues { get; } = [];
    public string NameOrPlaceholder => string.IsNullOrWhiteSpace(Name) ? "(unnamed item)" : Name;
}

public sealed class MealPlanningImportDraftResult
{
    private MealPlanningImportDraftResult(bool isSuccess, MealPlanningImportDraft? draft, IReadOnlyList<string> errors)
    {
        IsSuccess = isSuccess;
        Draft = draft;
        Errors = errors;
    }

    public bool IsSuccess { get; }
    public MealPlanningImportDraft? Draft { get; }
    public IReadOnlyList<string> Errors { get; }

    public static MealPlanningImportDraftResult Success(MealPlanningImportDraft draft) => new(true, draft, []);

    public static MealPlanningImportDraftResult Fail(string error) => new(false, null, [error]);
}

public sealed class MealPlanningImportSaveResult
{
    private MealPlanningImportSaveResult(
        bool isSuccess,
        IReadOnlyList<string> errors,
        int mealsAdded,
        int mealsUpdated,
        int mealsSkipped,
        int shoppingAdded,
        int shoppingSkipped)
    {
        IsSuccess = isSuccess;
        Errors = errors;
        MealsAdded = mealsAdded;
        MealsUpdated = mealsUpdated;
        MealsSkipped = mealsSkipped;
        ShoppingAdded = shoppingAdded;
        ShoppingSkipped = shoppingSkipped;
    }

    public bool IsSuccess { get; }
    public IReadOnlyList<string> Errors { get; }
    public int MealsAdded { get; }
    public int MealsUpdated { get; }
    public int MealsSkipped { get; }
    public int ShoppingAdded { get; }
    public int ShoppingSkipped { get; }

    public static MealPlanningImportSaveResult Success(
        int mealsAdded,
        int mealsUpdated,
        int mealsSkipped,
        int shoppingAdded,
        int shoppingSkipped)
        => new(true, [], mealsAdded, mealsUpdated, mealsSkipped, shoppingAdded, shoppingSkipped);

    public static MealPlanningImportSaveResult Fail(IReadOnlyList<string> errors)
        => new(false, errors, 0, 0, 0, 0, 0);
}
