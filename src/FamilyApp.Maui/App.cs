using FamilyApp.Shared.Components;
using Microsoft.AspNetCore.Components.WebView.Maui;

namespace FamilyApp.Maui;

public sealed class App(NativeDeviceSync sync) : Application
{
    protected override Window CreateWindow(IActivationState? activationState)
    {
        var view = new BlazorWebView { HostPage = "wwwroot/index.html" };
        view.RootComponents.Add(new RootComponent
        {
            Selector = "#app", ComponentType = typeof(Routes),
            Parameters = new Dictionary<string, object?>
            { [nameof(Routes.AdditionalAssemblies)] = new[] { typeof(App).Assembly } }
        });
        var window = new Window(new ContentPage { Content = view, SafeAreaEdges = SafeAreaEdges.All });
        window.Activated += (_, _) => sync.Resume();
        window.Stopped += (_, _) => sync.Suspend();
        window.Destroying += (_, _) => sync.Suspend();
        return window;
    }
}
