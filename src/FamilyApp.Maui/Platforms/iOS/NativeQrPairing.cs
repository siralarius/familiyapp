using AVFoundation;
using CoreFoundation;
using CoreAnimation;
using CoreGraphics;
using CoreImage;
using Foundation;
using UIKit;

namespace FamilyApp.Maui.Platforms.iOS;

public sealed class NativeQrCodeRenderer : IQrCodeRenderer
{
    private readonly CIContext _context = CIContext.Create();

    public Task<string> RenderPngDataUrlAsync(string payload, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(payload)) throw new ArgumentException("The QR payload is required.", nameof(payload));

        using var filter = CIFilter.CreateQRCodeGenerator();
        if (filter is null) throw new InvalidOperationException("QR code rendering is unavailable.");

        filter.SetDefaults();
        filter.Message = NSData.FromString(payload, NSStringEncoding.UTF8);
        filter.CorrectionLevel = "M";

        using var output = filter.OutputImage ?? throw new InvalidOperationException("QR code rendering failed.");
        using var scaled = output.ImageByApplyingTransform(CGAffineTransform.MakeScale(10, 10));
        using var image = _context.CreateCGImage(scaled, scaled.Extent);
        using var png = UIImage.FromImage(image).AsPNG();
        return Task.FromResult($"data:image/png;base64,{png.GetBase64EncodedString(NSDataBase64EncodingOptions.None)}");
    }
}

public sealed class NativeQrScanner : IQrScanner
{
    public async Task<QrScanResult> ScanAsync(CancellationToken cancellationToken = default)
    {
        var cameraStatus = AVCaptureDevice.GetAuthorizationStatus(AVMediaType.Video);
        if (cameraStatus is AVAuthorizationStatus.Denied or AVAuthorizationStatus.Restricted)
            return new QrScanResult(QrScanStatus.PermissionDenied);

        if (cameraStatus == AVAuthorizationStatus.NotDetermined)
        {
            var granted = await AVCaptureDevice.RequestAccessForMediaTypeAsync(AVMediaType.Video);
            if (!granted) return new QrScanResult(QrScanStatus.PermissionDenied);
        }

        return await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var presenter = GetPresenter();
            if (presenter is null) return new QrScanResult(QrScanStatus.Unsupported);

            var resultSource = new TaskCompletionSource<QrScanResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var scanner = new QrScannerViewController(resultSource);
            using var registration = cancellationToken.Register(() => resultSource.TrySetResult(new QrScanResult(QrScanStatus.Canceled)));
            await presenter.PresentViewControllerAsync(scanner, true);
            return await resultSource.Task;
        });
    }

    private static UIViewController? GetPresenter()
    {
        var scene = UIApplication.SharedApplication.ConnectedScenes
            .OfType<UIWindowScene>()
            .FirstOrDefault();
        var root = scene?.Windows.FirstOrDefault(window => window.IsKeyWindow)?.RootViewController;
        while (root?.PresentedViewController is { } presented) root = presented;
        return root;
    }

    private sealed class QrScannerViewController(TaskCompletionSource<QrScanResult> resultSource)
        : UIViewController, IAVCaptureMetadataOutputObjectsDelegate
    {
        private readonly AVCaptureSession _session = new();
        private AVCaptureVideoPreviewLayer? _preview;
        private bool _completed;

        public override void ViewDidLoad()
        {
            base.ViewDidLoad();
            View!.BackgroundColor = UIColor.Black;
            var device = AVCaptureDevice.GetDefaultDevice(AVMediaType.Video);
            if (device is null)
            {
                Complete(new QrScanResult(QrScanStatus.Unsupported));
                return;
            }

            NSError? inputError = null;
            var input = AVCaptureDeviceInput.FromDevice(device, out inputError);
            if (input is null || !_session.CanAddInput(input))
            {
                Complete(new QrScanResult(QrScanStatus.Unsupported));
                return;
            }

            _session.AddInput(input);
            var output = new AVCaptureMetadataOutput();
            if (!_session.CanAddOutput(output))
            {
                Complete(new QrScanResult(QrScanStatus.Unsupported));
                return;
            }

            _session.AddOutput(output);
            output.SetDelegate(this, DispatchQueue.MainQueue);
            output.MetadataObjectTypes = [AVMetadataObjectType.QRCode];

            _preview = new AVCaptureVideoPreviewLayer(_session)
            {
                VideoGravity = AVLayerVideoGravity.ResizeAspectFill
            };
            View.Layer.AddSublayer(_preview);

            var cancel = new UIButton(UIButtonType.System)
            {
                Frame = new CGRect(16, 52, 84, 40)
            };
            cancel.SetTitle("Cancel", UIControlState.Normal);
            cancel.SetTitleColor(UIColor.White, UIControlState.Normal);
            cancel.TouchUpInside += (_, _) => Complete(new QrScanResult(QrScanStatus.Canceled));
            View.AddSubview(cancel);
        }

        public override void ViewDidAppear(bool animated)
        {
            base.ViewDidAppear(animated);
            _session.StartRunning();
        }

        public override void ViewDidDisappear(bool animated)
        {
            if (_session.Running) _session.StopRunning();
            base.ViewDidDisappear(animated);
        }

        public override void ViewDidLayoutSubviews()
        {
            base.ViewDidLayoutSubviews();
            if (_preview is not null) _preview.Frame = View!.Bounds;
        }

        [Export("metadataOutput:didOutputMetadataObjects:fromConnection:")]
        public void DidOutputMetadataObjects(AVCaptureMetadataOutput output, AVMetadataObject[] metadataObjects,
            AVCaptureConnection connection)
        {
            if (_completed) return;

            var payload = metadataObjects
                .OfType<AVMetadataMachineReadableCodeObject>()
                .FirstOrDefault(code => code.Type == AVMetadataObjectType.QRCode)?
                .StringValue;
            if (!string.IsNullOrWhiteSpace(payload))
                Complete(new QrScanResult(QrScanStatus.Success, payload));
        }

        private void Complete(QrScanResult result)
        {
            if (_completed) return;
            _completed = true;
            if (_session.Running) _session.StopRunning();
            resultSource.TrySetResult(result);
            _ = DismissViewControllerAsync(true);
        }
    }
}
