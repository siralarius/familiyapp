using System.Runtime.CompilerServices;
using System.Threading.Channels;
using FamilyApp.Sync.Discovery;
using Network;

namespace FamilyApp.Maui.Platforms.iOS;

public sealed class BonjourPeerDiscovery : IPeerDiscovery
{
    public async IAsyncEnumerable<PeerDiscoveryEvent> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var events = Channel.CreateUnbounded<PeerDiscoveryEvent>();
        using var browser = new NWBrowser(
            NWBrowserDescriptor.CreateBonjourService(PeerDiscoveryDefaults.BonjourServiceType),
            new NWParameters());

        browser.IndividualChangesDelegate = (current, previous) =>
        {
            var result = current ?? previous;
            if (result is null || !TryCreateEndpoint(result.EndPoint, out var endpoint))
                return;

            var kind = current is null ? PeerDiscoveryEventKind.Lost : PeerDiscoveryEventKind.Available;
            events.Writer.TryWrite(new PeerDiscoveryEvent(kind, endpoint));
        };

        browser.SetStateChangesHandler((state, error) =>
            events.Writer.TryWrite(new PeerDiscoveryEvent(
                PeerDiscoveryEventKind.StateChanged,
                State: state.ToString(),
                Error: error?.LocalizedDescription)));

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