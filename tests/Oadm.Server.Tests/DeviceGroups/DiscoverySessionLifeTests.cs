using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Oadm.Core.Auth;
using Oadm.Core.Discovery;
using Oadm.Core.Security;
using Oadm.Core.Vapix;
using Oadm.Server.AddDevices;
using Oadm.Server.Discovery;
using Oadm.Server.Tests.Support;

namespace Oadm.Server.Tests.DeviceGroups;

/// <summary>
/// An add page session keeps its owner and its logins as long as the session exists or a watch of it is open, however long
/// the page stays idle; a session that ended is forgotten after the lifetime.
/// </summary>
public sealed class DiscoverySessionLifeTests
{
    private static readonly AuthenticatedCaller Anna = new(Guid.NewGuid(), "anna", BuiltInRoles.OperatorId, PermissionSet.All, "local", "", "h-anna");

    [Fact]
    public void AnOwnerEntryStaysWhileItsSessionExistsOrAWatchHoldsItAndIsForgottenAfterwards()
    {
        var time = new FakeTimeProvider();
        var backend = new Sessions("open");
        var active = backend.Active;
        var owners = new DiscoverySessionOwners(time, backend);
        owners.Register("open", Anna);
        owners.Register("watched", Anna);
        owners.Register("idle", Anna);
        var other = Anna with { UserId = Guid.NewGuid() };
        Assert.Null(owners.Hold("watched", other)); // another user's session: nothing to hold
        var hold = owners.Hold("watched", Anna);
        Assert.NotNull(hold);

        time.Advance(DiscoverySessionOwners.Lifetime + TimeSpan.FromMinutes(2));
        owners.Register("prune", Anna); // pruning runs now and then, on a start

        Assert.True(owners.Owns("open", Anna));
        Assert.True(owners.Owns("watched", Anna));
        Assert.False(owners.Owns("idle", Anna));

        // The watch ended and the session was stopped: forgotten one lifetime after the last use.
        hold!.Dispose();
        active.Clear();
        time.Advance(DiscoverySessionOwners.Lifetime + TimeSpan.FromMinutes(2));
        owners.Register("prune-again", Anna);
        Assert.False(owners.Owns("watched", Anna));
        Assert.False(owners.Owns("open", Anna));
    }

    [Fact]
    public async Task ALoginSessionOfAnExistingDiscoverySessionKeepsTheCredentialListAfterAnIdleHour()
    {
        await using var host = await TestServerHost.StartAsync();
        var time = new FakeTimeProvider();
        var backend = new Sessions("open");
        using var authenticator = new DiscoveryAuthenticator(
            host.Get<CredentialListStore>(), host.Get<IVapixConnector>(), time, NullLogger<DiscoveryAuthenticator>.Instance, sessions: backend);
        authenticator.AllowCredentialList("open");
        authenticator.AllowCredentialList("ended");

        time.Advance(TimeSpan.FromHours(1));
        authenticator.SetExpectedSerial("another", "ACCC8E000001"); // any use of a session prunes the idle ones

        Assert.True(authenticator.UsesCredentialList("open"));
        Assert.False(authenticator.UsesCredentialList("ended")); // gone: a session made again tries only typed passwords
    }

    /// <summary>Only which sessions exist.</summary>
    private sealed class Sessions(params string[] active) : IDiscoveryBackend
    {
        public HashSet<string> Active { get; } = [.. active];

        public bool IsActive(string sessionId) => Active.Contains(sessionId);

        public Task<DiscoverySession> StartZeroConfAsync(CancellationToken ct, DiscoveryRoutes? routes = null) => throw new NotSupportedException();

        public void StopScan(string sessionId) => throw new NotSupportedException();

        public Task<DiscoverySession> StartRangeScanAsync(string fromAddress, string toAddress, CancellationToken ct, DiscoveryRoutes? routes = null) => throw new NotSupportedException();

        public Task<DiscoverySession> StartAddressProbeAsync(string input, bool importLine, CancellationToken ct, Guid? siteId = null, string? expectedSerial = null, DiscoveryRoutes? routes = null) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<DiscoveryEvent> WatchAsync(string sessionId, CancellationToken ct) => throw new NotSupportedException();

        public Task StopAsync(string sessionId) => throw new NotSupportedException();

        public DiscoveredDevice? FindDiscovered(string sessionId, string discoveredId) => throw new NotSupportedException();
    }
}
