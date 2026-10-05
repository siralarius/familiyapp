namespace FamilyApp.Sync.Discovery;

public static class PeerDiscoveryDefaults
{
    public const string BonjourServiceType = "_familyapp-sync._tcp";
}

public sealed record PeerServiceEndpoint(string Name, string Type, string Domain)
{
    public string Id => $"{Name}.{Type}.{Domain}";
}

public enum PeerDiscoveryEventKind
{
    Available,
    Lost,
    StateChanged
}

public sealed record PeerDiscoveryEvent(
    PeerDiscoveryEventKind Kind,
    PeerServiceEndpoint? Endpoint = null,
    string? State = null,
    string? Error = null);

public interface IPeerDiscovery
{
    IAsyncEnumerable<PeerDiscoveryEvent> WatchAsync(
        CancellationToken cancellationToken = default);
}