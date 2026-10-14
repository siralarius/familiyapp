using FamilyApp.Core.Meals;
using FamilyApp.Core.Planning;
using FamilyApp.Core.Shopping;
using FamilyApp.Data;
using FamilyApp.Sync.ChangeLog;
using Xunit;

namespace FamilyApp.Data.Tests;
public sealed class PlanningImportTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), "family-planning-" + Guid.NewGuid() + ".db");
    private ChangeLogEngine Open() => new(new SqliteChangeLogStore(path));
    private static PreparedPlanningImport Plan(Guid? id = null) => new(id ?? Guid.NewGuid(),
        [MealPlanEntry.Create(new(2026, 10, 13), MealType.Dinner, "Tortilla española")],
        [ShoppingItem.Create("Huevos", "6"), ShoppingItem.Create(" huevos ", "12")]);
    [Fact]
    public async Task ImportPersistsTogetherAndSyncReplayDoesNotDuplicatePurchasesOrMeals()
    {
        var engine = Open(); var plan = Plan();
        var result = await new LocalPlanningImportStore(engine, "a").SaveAsync(plan);
        Assert.Equal(new(1, 1, 1), result);
        var restarted = Open();
        Assert.True((await new LocalPlanningImportStore(restarted, "a").SaveAsync(plan)).AlreadyImported);
        Assert.Equal(4, restarted.Changes.Count);
        var peer = new ChangeLogEngine(); peer.Merge(restarted.Changes);
        var repositories = new LocalFamilyRepositories(peer, "b");
        Assert.Equal("Tortilla española", Assert.Single(await ((IMealPlanRepository)repositories).GetAllAsync()).Description);
        Assert.Equal("6", Assert.Single(await ((IShoppingRepository)repositories).GetAllAsync()).Quantity);
        Assert.True((await new LocalPlanningImportStore(peer, "b").SaveAsync(plan)).AlreadyImported);
        Assert.Equal(4, peer.Changes.Count);
    }
    [Fact]
    public async Task OfflineSkipSuppressesTheSameImportedPurchaseOnAnotherDevice()
    {
        var first = Open();
        var second = new ChangeLogEngine();
        IShoppingRepository firstShopping = new LocalFamilyRepositories(first, "a");
        await firstShopping.GetAllAsync();
        var existing = ShoppingItem.Create("Huevos", "2");
        await firstShopping.SaveAsync([existing]);
        var plan = new PreparedPlanningImport(Guid.NewGuid(), [], [ShoppingItem.Create(" huevos ", "6")]);
        Assert.Equal(1, (await new LocalPlanningImportStore(first, "a").SaveAsync(plan)).PurchasesSkipped);
        Assert.Equal(1, (await new LocalPlanningImportStore(second, "b").SaveAsync(plan)).PurchasesAdded);
        IShoppingRepository offlineShopping = new LocalFamilyRepositories(second, "b");
        var imported = Assert.Single(await offlineShopping.GetAllAsync());
        await offlineShopping.SaveAsync([imported with { Quantity = "12" }]);
        await offlineShopping.GetAllAsync();
        await offlineShopping.SaveAsync([imported with { Quantity = "18" }]);
        Assert.Equal(3, second.GetCurrent("shopping", imported.Id)!.Version);
        first.Merge(second.Changes); second.Merge(first.Changes);
        foreach (var engine in new[] { first, second })
        {
            IShoppingRepository shopping = new LocalFamilyRepositories(engine, "check");
            Assert.Equal(existing, Assert.Single(await shopping.GetAllAsync()));
            Assert.True((await new LocalPlanningImportStore(engine, "check").SaveAsync(plan)).AlreadyImported);
        }
        var restarted = Open();
        IShoppingRepository afterRestart = new LocalFamilyRepositories(restarted, "a");
        Assert.Equal(existing, Assert.Single(await afterRestart.GetAllAsync()));
        second.Merge(restarted.Changes); first.Merge(second.Changes);
        Assert.Equal(existing, Assert.Single(await afterRestart.GetAllAsync()));
        await afterRestart.SaveAsync([]);
        Assert.True((await new LocalPlanningImportStore(restarted, "a").SaveAsync(plan)).AlreadyImported);
        second.Merge(restarted.Changes);
        IShoppingRepository peerShopping = new LocalFamilyRepositories(second, "b");
        Assert.Empty(await afterRestart.GetAllAsync());
        Assert.Empty(await peerShopping.GetAllAsync());
    }
    [Fact]
    public async Task ExistingMealPreventsWholeImportWithoutOverwritingOrPartialShoppingSave()
    {
        var engine = Open(); var repositories = new LocalFamilyRepositories(engine, "a");
        var mealRepository = (IMealPlanRepository)repositories;
        await mealRepository.GetAllAsync();
        var existing = MealPlanEntry.Create(new(2026, 10, 13), MealType.Dinner, "Keep our dinner");
        await mealRepository.SaveAsync([existing]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new LocalPlanningImportStore(engine, "a").SaveAsync(Plan()));
        Assert.Equal(existing, Assert.Single(await mealRepository.GetAllAsync()));
        Assert.Empty(await ((IShoppingRepository)repositories).GetAllAsync()); Assert.Single(engine.Changes);
    }
    [Fact]
    public async Task ExistingPurchaseQuantityIsPreservedAndFailedStoragePublishesNothing()
    {
        var engine = Open(); IShoppingRepository repository = new LocalFamilyRepositories(engine, "a");
        await repository.GetAllAsync(); var eggs = ShoppingItem.Create("Huevos", "2"); await repository.SaveAsync([eggs]);
        var result = await new LocalPlanningImportStore(engine, "a").SaveAsync(Plan());
        Assert.Equal(0, result.PurchasesAdded); Assert.Equal(2, result.PurchasesSkipped);
        Assert.Equal(eggs, Assert.Single(await repository.GetAllAsync()));
        var failed = new ChangeLogEngine(new FailingStore());
        await Assert.ThrowsAsync<IOException>(() => new LocalPlanningImportStore(failed, "b").SaveAsync(Plan()));
        Assert.Empty(failed.Changes); Assert.Empty(failed.Current());
    }
    private sealed class FailingStore : IChangeLogStore
    {
        public IReadOnlyCollection<ChangeRecord> Load() => [];
        public void Append(IReadOnlyCollection<ChangeRecord> changes) => throw new IOException("Disk unavailable");
    }
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix);
    }
}
