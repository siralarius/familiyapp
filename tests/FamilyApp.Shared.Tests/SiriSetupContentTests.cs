using FamilyApp.Shared.Components.Pages;
using Xunit;

namespace FamilyApp.Shared.Tests;

public class SiriSetupContentTests
{
    [Fact]
    public void Shortcut_url_template_targets_shopping_add_endpoint()
    {
        Assert.Equal("familyapp://shopping/add?item=[Provided Input]", SiriSetupContent.ShortcutUrlTemplate);
    }

    [Fact]
    public void BuildShortcutUrl_escapes_item_name()
    {
        var url = SiriSetupContent.BuildShortcutUrl("oat milk");

        Assert.Equal("familyapp://shopping/add?item=oat%20milk", url);
    }
}
