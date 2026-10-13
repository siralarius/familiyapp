using FamilyApp.Core.Shopping;
using FamilyApp.Data;
using FamilyApp.Sync.ChangeLog;
using Xunit;

namespace FamilyApp.Data.Tests;
public sealed class ShoppingCaptureTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), "family-siri-" + Guid.NewGuid() + ".db");
    private ChangeLogEngine Open() => new(new SqliteChangeLogStore(path));
    [Fact]
    public async Task AssistantSaveSurvivesRestartAndSyncAndKeepsExistingQuantityOnRetry()
    {
        var engine = Open(); var capture = new ShoppingCaptureService(new LocalShoppingCaptureStore(engine, "phone-a"));
        Assert.StartsWith("Added", await capture.AddAsync(" Milk ", "1 litre"));
        Assert.Contains("already", await capture.AddAsync("milk", "2 litres"));
        var restarted = Open(); var peer = new ChangeLogEngine(); peer.Merge(restarted.Changes);
        IShoppingRepository repository = new LocalFamilyRepositories(peer, "phone-b");
        var item = Assert.Single(await repository.GetAllAsync());
        Assert.Equal("Milk", item.Name); Assert.Equal("1 litre", item.Quantity);
        Assert.Single(engine.Changes);
    }
    [Fact]
    public async Task ConcurrentCapturesAddOneOpenProductAndAllowNewPurchaseAfterCheckingIt()
    {
        var engine = Open(); var capture = new ShoppingCaptureService(new LocalShoppingCaptureStore(engine, "a"));
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => capture.AddAsync("Milk"))));
        Assert.Single(engine.Current());
        IShoppingRepository repository = new LocalFamilyRepositories(engine, "a");
        var item = Assert.Single(await repository.GetAllAsync()); await repository.SaveAsync([item.SetChecked(true)]);
        Assert.StartsWith("Added", await capture.AddAsync("Milk"));
        Assert.Equal(2, (await repository.GetAllAsync()).Count);
    }
    [Fact]
    public async Task InvalidInputAndFailedPersistenceNeverProduceAssistantSuccess()
    {
        var engine = new ChangeLogEngine(new FailingStore());
        var capture = new ShoppingCaptureService(new LocalShoppingCaptureStore(engine, "a"));
        await Assert.ThrowsAsync<ArgumentException>(() => capture.AddAsync(" "));
        await Assert.ThrowsAsync<ArgumentException>(() => capture.AddAsync("Milk\nBread"));
        await Assert.ThrowsAsync<IOException>(() => capture.AddAsync("Milk"));
        Assert.Empty(engine.Changes);
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
