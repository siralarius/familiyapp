using System.Runtime.CompilerServices;
using System.Threading.Channels;
using FamilyApp.Sync.Discovery;
using Xunit;

namespace FamilyApp.Sync.Tests;

public class PeerDiscoveryContractTests
{
    [Fact]
    public async Task Discovery_stream_reports_available_lost_and_state_changes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var discovery = new FakePeerDiscovery();
        var endpoint = new PeerServiceEndpoint("Phone A", PeerDiscoveryDefaults.BonjourServiceType, "local.");
        await using var events = discovery.WatchAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);

        discovery.Publish(new PeerDiscoveryEvent(PeerDiscoveryEventKind.Available, endpoint));
        Assert.True(await events.MoveNextAsync());
        Assert.Equal(PeerDiscoveryEventKind.Available, events.Current.Kind);
        Assert.Equal(endpoint, events.Current.Endpoint);

        discovery.Publish(new PeerDiscoveryEvent(PeerDiscoveryEventKind.Lost, endpoint));
        Assert.True(await events.MoveNextAsync());
        Assert.Equal(PeerDiscoveryEventKind.Lost, events.Current.Kind);

        discovery.Publish(new PeerDiscoveryEvent(PeerDiscoveryEventKind.StateChanged, State: "Ready"));
        Assert.True(await events.MoveNextAsync());
        Assert.Equal("Ready", events.Current.State);
    }

    [Fact]
    public void Bonjour_service_type_uses_tcp_and_has_no_fixed_host_address()
    {
        var endpoint = new PeerServiceEndpoint("Phone A", PeerDiscoveryDefaults.BonjourServiceType, "local.");

        Assert.Equal("_familyapp-sync._tcp", endpoint.Type);
        Assert.Equal("Phone A._familyapp-sync._tcp.local.", endpoint.Id);
    }

    private sealed class FakePeerDiscovery : IPeerDiscovery
    {
        private readonly Channel<PeerDiscoveryEvent> _events = Channel.CreateUnbounded<PeerDiscoveryEvent>();

        public void Publish(PeerDiscoveryEvent peerEvent) => _events.Writer.TryWrite(peerEvent);

        public async IAsyncEnumerable<PeerDiscoveryEvent> WatchAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var peerEvent in _events.Reader.ReadAllAsync(cancellationToken))
                yield return peerEvent;
        }
    }
}