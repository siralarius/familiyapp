using Xunit;

namespace FamilyApp.Sync.Tests;

public class ArchitectureSmokeTests
{
    [Fact]
    public void Sync_assembly_is_available() => Assert.NotNull(typeof(Sync.SyncMarker).Assembly);
}
