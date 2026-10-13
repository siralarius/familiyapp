using System.Runtime.InteropServices;
using FamilyApp.Core.Shopping;
using ObjCRuntime;

namespace FamilyApp.Maui.Platforms.iOS;

public static class NativeShoppingIntents
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ShoppingHandler(IntPtr request, IntPtr product, IntPtr quantity);
    private static readonly ShoppingHandler Handler = Handle;
    private static ShoppingCaptureService? _shopping;
    public static void Register(ShoppingCaptureService shopping)
    {
        _shopping = shopping;
        RegisterHandler(Handler);
    }
    [DllImport("__Internal", EntryPoint = "FamilyShoppingRegister")]
    private static extern void RegisterHandler(ShoppingHandler handler);
    [DllImport("__Internal", EntryPoint = "FamilyShoppingComplete")]
    private static extern void Complete(string request, int succeeded, string message);

    [MonoPInvokeCallback(typeof(ShoppingHandler))]
    private static void Handle(IntPtr request, IntPtr product, IntPtr quantity)
    {
        // Copy Swift's temporary strings before returning to its caller.
        var id = Marshal.PtrToStringUTF8(request)!;
        var name = Marshal.PtrToStringUTF8(product)!;
        var amount = Marshal.PtrToStringUTF8(quantity);
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            string message; var succeeded = 0;
            try
            {
                message = await _shopping!.AddAsync(name, amount);
                succeeded = 1;
            }
            catch (ArgumentException exception) { message = exception.Message; }
            catch { message = "Family App could not save this product. Open the app and try again; no success has been confirmed."; }
            Complete(id, succeeded, message);
        });
    }
}
