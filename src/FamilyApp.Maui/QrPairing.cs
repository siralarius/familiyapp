namespace FamilyApp.Maui;

public enum QrScanStatus
{
    Success = 1,
    Canceled = 2,
    PermissionDenied = 3,
    Unsupported = 4
}

public sealed record QrScanResult(
    QrScanStatus Status,
    string? Payload = null);

public interface IQrScanner
{
    Task<QrScanResult> ScanAsync(CancellationToken cancellationToken = default);
}

public interface IQrCodeRenderer
{
    Task<string> RenderPngDataUrlAsync(string payload, CancellationToken cancellationToken = default);
}
