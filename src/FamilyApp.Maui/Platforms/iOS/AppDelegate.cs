using Foundation;
using UIKit;

namespace FamilyApp.Maui;

[Register("AppDelegate")]
public sealed class AppDelegate : MauiUIApplicationDelegate
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    public override bool OpenUrl(UIApplication app, NSUrl url, NSDictionary options)
    {
        if (Platforms.iOS.SiriShoppingShortcutHandler.TryHandleUrl(app, url))
        {
            return true;
        }

        return base.OpenUrl(app, url, options);
    }
}
