using FamilyApp.Core.Meals;
using FamilyApp.Core.Shopping;
using Xunit;

namespace FamilyApp.Core.Tests;

public class MealPlanningImportServiceTests
{
    [Fact]
    public async Task Build_draft_resolves_spanish_day_and_meal_slot()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repos = new MemoryRepositories();
        var service = new MealPlanningImportService(repos, repos);

        var result = await service.BuildDraftAsync(
            """
            {
              "version": "1",
              "weekStart": "2026-10-12",
              "meals": [
                {
                  "day": "martes",
                  "mealSlot": "cena",
                  "dish": "Tacos",
                  "agreed": true
                }
              ]
            }
            """, cancellationToken);

        Assert.True(result.IsSuccess);
        var draft = Assert.IsType<MealPlanningImportDraft>(result.Draft);
        var meal = Assert.Single(draft.Meals);
        Assert.Equal("2026-10-13", meal.Date);
        Assert.Equal("dinner", meal.MealSlot);
        Assert.True(meal.IsAgreed);
        Assert.True(meal.Include);

        var saveResult = await service.SaveDraftAsync(draft, cancellationToken);
        Assert.True(saveResult.IsSuccess);
        var savedMeal = Assert.Single(repos.CurrentMeals());
        Assert.Equal(new DateOnly(2026, 10, 13), savedMeal.Date);
        Assert.Equal(MealType.Dinner, savedMeal.MealType);
        Assert.Equal("Tacos", savedMeal.Description);
    }

    [Fact]
    public async Task Tentative_meals_are_not_selected_and_cannot_be_saved_without_confirmation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repos = new MemoryRepositories();
        var service = new MealPlanningImportService(repos, repos);

        var result = await service.BuildDraftAsync(
            """
            {
              "version": "1",
              "weekStart": "2026-10-12",
              "meals": [
                {
                  "day": "monday",
                  "mealSlot": "dinner",
                  "dish": "Maybe pasta",
                  "agreement": "suggested"
                }
              ]
            }
            """, cancellationToken);

        var draft = Assert.IsType<MealPlanningImportDraft>(result.Draft);
        var meal = Assert.Single(draft.Meals);
        Assert.False(meal.IsAgreed);
        Assert.False(meal.Include);

        meal.Include = true;
        var saveResult = await service.SaveDraftAsync(draft, cancellationToken);

        Assert.False(saveResult.IsSuccess);
        Assert.Contains(saveResult.Errors, x => x.Contains("not marked as agreed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Inferred_shopping_items_require_confirmation_before_save()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repos = new MemoryRepositories();
        var service = new MealPlanningImportService(repos, repos);

        var result = await service.BuildDraftAsync(
            """
            {
              "version": "1",
              "shoppingItems": [
                {
                  "name": "Tomate",
                  "quantity": "2",
                  "unit": "kg",
                  "source": "inferred"
                }
              ]
            }
            """, cancellationToken);

        var draft = Assert.IsType<MealPlanningImportDraft>(result.Draft);
        var item = Assert.Single(draft.ShoppingItems);
        Assert.True(item.IsInferred);
        Assert.False(item.Include);

        item.Include = true;
        item.ConfirmInferred = false;
        var saveResult = await service.SaveDraftAsync(draft, cancellationToken);

        Assert.False(saveResult.IsSuccess);
        Assert.Contains(saveResult.Errors, x => x.Contains("Confirm inferred shopping item", StringComparison.OrdinalIgnoreCase));

        item.ConfirmInferred = true;
        saveResult = await service.SaveDraftAsync(draft, cancellationToken);

        Assert.True(saveResult.IsSuccess);
        Assert.Equal(1, saveResult.ShoppingAdded);
    }

    [Fact]
    public async Task Reimport_does_not_duplicate_existing_meals_or_shopping()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repos = new MemoryRepositories();
        await repos.SeedMealAsync(MealPlanEntry.Create(new DateOnly(2026, 10, 14), MealType.Dinner, "Curry"));
        await repos.SeedShoppingAsync(ShoppingItem.Create("Milk", "1 L"));
        var service = new MealPlanningImportService(repos, repos);

        var result = await service.BuildDraftAsync(
            """
            {
              "version": "1",
              "meals": [
                {
                  "date": "2026-10-14",
                  "mealSlot": "dinner",
                  "dish": "Curry",
                  "agreed": true
                }
              ],
              "shoppingItems": [
                {
                  "name": "Milk",
                  "quantity": "1",
                  "unit": "L",
                  "source": "explicit"
                }
              ]
            }
            """, cancellationToken);

        var draft = Assert.IsType<MealPlanningImportDraft>(result.Draft);
        var meal = Assert.Single(draft.Meals);
        var shopping = Assert.Single(draft.ShoppingItems);
        Assert.False(meal.Include);
        Assert.False(shopping.Include);
        meal.Include = true;
        shopping.Include = true;

        var saveResult = await service.SaveDraftAsync(draft, cancellationToken);

        Assert.True(saveResult.IsSuccess);
        Assert.Equal(0, saveResult.MealsAdded);
        Assert.Equal(0, saveResult.ShoppingAdded);
        Assert.Equal(1, saveResult.MealsSkipped);
        Assert.Equal(1, saveResult.ShoppingSkipped);
    }

    private sealed class MemoryRepositories : IMealPlanRepository, IShoppingRepository
    {
        private List<MealPlanEntry> _meals = [];
        private List<ShoppingItem> _shoppingItems = [];

        Task<IReadOnlyList<MealPlanEntry>> IMealPlanRepository.GetAllAsync() =>
            Task.FromResult<IReadOnlyList<MealPlanEntry>>(_meals.ToList());

        Task<IReadOnlyList<ShoppingItem>> IShoppingRepository.GetAllAsync() =>
            Task.FromResult<IReadOnlyList<ShoppingItem>>(_shoppingItems.ToList());

        Task IMealPlanRepository.SaveAsync(IReadOnlyList<MealPlanEntry> entries)
        {
            _meals = entries.ToList();
            return Task.CompletedTask;
        }

        Task IShoppingRepository.SaveAsync(IReadOnlyList<ShoppingItem> items)
        {
            _shoppingItems = items.ToList();
            return Task.CompletedTask;
        }

        public IReadOnlyList<MealPlanEntry> CurrentMeals() => _meals.ToList();

        public Task SeedMealAsync(MealPlanEntry meal)
        {
            _meals.Add(meal);
            return Task.CompletedTask;
        }

        public Task SeedShoppingAsync(ShoppingItem item)
        {
            _shoppingItems.Add(item);
            return Task.CompletedTask;
        }
    }
}
