using System.Text.Json;
using FamilyApp.Core.Shopping;
using FamilyApp.Data;
using FamilyApp.Sync.ChangeLog;
using Xunit;

namespace FamilyApp.Data.Tests;

public sealed class LocalPersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "family-tests-" + Guid.NewGuid());
    private ChangeLogEngine Open()
    {
        Directory.CreateDirectory(_directory);
        return new ChangeLogEngine(new SqliteChangeLogStore(Path.Combine(_directory, "family.db")));
    }

    [Fact]
    public async Task LocalEditAndDeletionSurviveRestart()
    {
        var engine = Open();
        IShoppingRepository repository = new LocalFamilyRepositories(engine, "phone-a");
        await repository.GetAllAsync();
        var item = ShoppingItem.Create("Milk");
        await repository.SaveAsync([item]);
        IShoppingRepository restarted = new LocalFamilyRepositories(Open(), "phone-a");
        Assert.Equal(item, Assert.Single(await restarted.GetAllAsync()));
        await restarted.SaveAsync([]);
        IShoppingRepository afterDelete = new LocalFamilyRepositories(Open(), "phone-a");
        Assert.Empty(await afterDelete.GetAllAsync());
        Assert.True(Assert.Single(Open().Current(true)).IsTombstone);
    }

    [Fact]
    public async Task RemoteChangesSurviveRestartAndDuplicateDelivery()
    {
        var engine = Open();
        var item = ShoppingItem.Create("Bread");
        var change = Change(item, 1);
        Assert.Equal(1, engine.Merge([change, change]));
        var restarted = Open();
        Assert.Equal(0, restarted.Merge([change]));
        IShoppingRepository repository = new LocalFamilyRepositories(restarted, "phone-a");
        Assert.Equal(item, Assert.Single(await repository.GetAllAsync()));
    }

    [Fact]
    public async Task SavingAnUnrelatedEditPreservesRemoteChangesReceivedAfterRead()
    {
        var engine = Open();
        IShoppingRepository repository = new LocalFamilyRepositories(engine, "phone-a");
        await repository.GetAllAsync();
        var remote = ShoppingItem.Create("Bread");
        engine.Apply(Change(remote, 1));
        var local = ShoppingItem.Create("Milk");
        await repository.SaveAsync([local]);
        var items = await repository.GetAllAsync();
        Assert.Equal(2, items.Count);
        Assert.Contains(remote, items);
        Assert.Contains(local, items);
    }

    [Fact]
    public void FailedPersistenceDoesNotExposeOrAcknowledgeAnInMemoryChange()
    {
        var engine = new ChangeLogEngine(new FailingStore());
        Assert.Throws<IOException>(() => engine.Apply(Change(ShoppingItem.Create("Milk"), 1)));
        Assert.Empty(engine.Changes);
        Assert.Empty(engine.Current());
    }

    [Fact]
    public async Task ConflictResolutionIsTheSameAfterRestart()
    {
        var engine = Open();
        var item = ShoppingItem.Create("Milk");
        engine.Merge([Change(item, 2), Change(item with { Name = "Bread" }, 1)]);
        IShoppingRepository repository = new LocalFamilyRepositories(Open(), "phone-a");
        Assert.Equal(item, Assert.Single(await repository.GetAllAsync()));
    }

    private static ChangeRecord Change(ShoppingItem item, long version) => new(
        Guid.NewGuid(), "phone-b", "shopping", item.Id, version, DateTimeOffset.UtcNow,
        false, JsonSerializer.Serialize(item));

    private sealed class FailingStore : IChangeLogStore
    {
        public IReadOnlyCollection<ChangeRecord> Load() => [];
        public void Append(IReadOnlyCollection<ChangeRecord> changes) => throw new IOException("Disk unavailable.");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
