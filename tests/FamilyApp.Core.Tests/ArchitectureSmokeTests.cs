namespace FamilyApp.Core.Tests;

public class ArchitectureSmokeTests
{
    [Fact]
    public void Core_assembly_is_available() => Assert.NotNull(typeof(Core.FamilyAppMarker).Assembly);
}
