using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CoreFoundation;
using FamilyApp.Sync.Discovery;
using Network;

namespace FamilyApp.Maui.Platforms.iOS;

public sealed class BonjourPeerDiscovery : IPeerDiscovery
{
    public async IAsyncEnumerable<PeerDiscoveryEvent> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var events = Channel.CreateBounded<PeerDiscoveryEvent>(new BoundedChannelOptions(256)
        { FullMode = BoundedChannelFullMode.DropOldest });
        using var queue = new DispatchQueue("familyapp.discovery");
        using var parameters = NWParameters.CreateTcp();
        using var browser = new NWBrowser(
            NWBrowserDescriptor.CreateBonjourService(PeerDiscoveryDefaults.BonjourServiceType),
            parameters);

        browser.IndividualChangesDelegate = (previous, current) =>
        {
            var result = current ?? previous;
            if (result is null) return;
            using var nativeEndpoint = result.EndPoint;
            if (!TryCreateEndpoint(nativeEndpoint, out var endpoint))
                return;

            var kind = current is null ? PeerDiscoveryEventKind.Lost : PeerDiscoveryEventKind.Available;
            events.Writer.TryWrite(new PeerDiscoveryEvent(kind, endpoint));
        };

        browser.SetStateChangesHandler((state, error) =>
            events.Writer.TryWrite(new PeerDiscoveryEvent(
                PeerDiscoveryEventKind.StateChanged,
                State: state.ToString(),
                Error: error?.ErrorCode.ToString())));

        browser.SetDispatchQueue(queue);
        browser.Start();
        try
        {
            await foreach (var peerEvent in events.Reader.ReadAllAsync(cancellationToken))
                yield return peerEvent;
        }
        finally
        {
            browser.Cancel();
            events.Writer.TryComplete();
        }
    }

    private static bool TryCreateEndpoint(NWEndpoint endpoint, out PeerServiceEndpoint peerEndpoint)
    {
        var name = endpoint.BonjourServiceName;
        var type = endpoint.BonjourServiceType;
        var domain = endpoint.BonjourServiceDomain;
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(domain))
        {
            peerEndpoint = null!;
            return false;
        }

        peerEndpoint = new PeerServiceEndpoint(name, type, domain);
        return true;
    }
}
