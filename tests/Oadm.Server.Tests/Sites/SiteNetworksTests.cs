using System.Net;

using Oadm.Contracts.SiteNode;
using Oadm.Core.Sites;
using Oadm.Server.Sites;

namespace Oadm.Server.Tests.Sites;

/// <summary>What a site's node reaches: a range is compared with each network's bounds, never address by address.</summary>
public sealed class SiteNetworksTests
{
    private static SiteNetworks Networks(params string[] subnets)
    {
        var status = new NodeStatus();
        status.Cards.Add(new NetworkCard { Id = "cam", Addresses = { "192.168.0.5/24", "fd00::5/64" }, Reached = true });
        status.Cards.Add(new NetworkCard { Id = "office", Addresses = { "10.0.0.7/24" }, Reached = false });
        status.ExtraSubnets.AddRange(subnets);
        return SiteNetworks.From(status);
    }

    [Theory]
    [InlineData("192.168.0.1", "192.168.0.254", true)]
    [InlineData("192.168.0.200", "192.168.1.20", true)]  // overlaps the end
    [InlineData("192.167.255.0", "192.168.0.0", true)]   // overlaps the start
    [InlineData("192.168.0.20", "192.168.0.10", true)]   // reversed
    [InlineData("192.168.1.0", "192.168.1.255", false)]
    [InlineData("10.0.0.1", "10.0.0.254", false)]        // the office card is not reached
    [InlineData("172.16.5.9", "172.16.5.9", true)]       // a single extra address
    [InlineData("172.16.5.10", "172.16.5.20", false)]
    [InlineData("10.20.0.0", "10.20.255.255", true)]     // an extra /16
    [InlineData("10.21.0.0", "10.21.255.255", false)]
    public void ARangeIsReachedWhenItTouchesANetwork(string first, string last, bool reached)
    {
        var networks = Networks("172.16.5.9", "10.20.0.0/16");
        Assert.Equal(reached, networks.Intersects(IPAddress.Parse(first), IPAddress.Parse(last)));
    }

    [Fact]
    public void TheRangeAnswerIsTheSameAsAskingEveryAddress()
    {
        var networks = Networks("192.168.4.0/22", "192.168.9.128/25");
        ISiteNetwork perAddress = new PerAddress(networks);
        for (var start = 0; start < 16; start++)
        {
            var first = IPAddress.Parse($"192.168.{start}.0");
            var last = IPAddress.Parse($"192.168.{start}.127");
            Assert.Equal(perAddress.ReachesRange(Guid.Empty, first, last), networks.Intersects(first, last));
        }
    }

    [Fact]
    public void AWholeAddressSpaceNetworkReachesEverything()
    {
        var networks = Networks("0.0.0.0/0");
        Assert.True(networks.Intersects(IPAddress.Parse("1.2.3.4"), IPAddress.Parse("1.2.3.4")));
    }

    /// <summary>The interface's default: every address of the range through Reaches.</summary>
    private sealed class PerAddress(SiteNetworks networks) : ISiteNetwork
    {
        public bool Reaches(Guid siteId, IPAddress address) => networks.Contains(address);

        public Task<IReadOnlyList<IPAddress>> ResolveAsync(Guid siteId, string name, CancellationToken ct) => throw new NotSupportedException();

        public Task<string?> ReverseLookupAsync(Guid siteId, IPAddress address, CancellationToken ct) => throw new NotSupportedException();

        public Task<bool> PingAsync(Guid siteId, string address, TimeSpan timeout, CancellationToken ct) => throw new NotSupportedException();

        public Task<bool> ProbeTcpAsync(Guid siteId, string address, int port, TimeSpan timeout, CancellationToken ct) => throw new NotSupportedException();

        public Task<IPAddress?> LocalAddressForAsync(Guid siteId, string address, CancellationToken ct) => throw new NotSupportedException();

        public IAsyncEnumerable<SiteBrowseResult> BrowseAsync(Guid siteId, TimeSpan duration, CancellationToken ct) => throw new NotSupportedException();

        public IAsyncEnumerable<SiteRangeScanResult> RangeScanAsync(Guid siteId, IPAddress first, IPAddress last, int parallelism, TimeSpan timeout, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
