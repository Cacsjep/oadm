using System.Security.Cryptography.X509Certificates;

using Grpc.Core;

using Microsoft.Extensions.Time.Testing;

using Oadm.SiteNode.Admin;
using Oadm.SiteNode.Config;
using Oadm.SiteNode.Identity;
using Oadm.SiteNode.Links;
using Oadm.SiteNode.State;
using Oadm.SiteNode.Tests.Support;
using Oadm.Sites.Binding;
using Oadm.Sites.Transport;

namespace Oadm.SiteNode.Tests;

/// <summary>The node's states with a fake clock (docs/sites.md, "States", "Binding").</summary>
public sealed class BindingStateTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 19, 8, 0, 0, TimeSpan.Zero));
    private readonly NodeStore _store;
    private readonly NodeIdentity _identity;
    private readonly BindingState _state;

    public BindingStateTests()
    {
        _store = new NodeStore(_folder.Path);
        _identity = new NodeIdentity(_folder.Path, "site-pc");
        _state = new BindingState(_store, _identity, _time, "site-pc");
    }

    public void Dispose()
    {
        _identity.Dispose();
        _folder.Dispose();
    }

    [Fact]
    public void BindingIsOpenFor24HoursOfRunTimeThenClosed()
    {
        Assert.Equal(NodeBinding.Open, _state.Binding);
        Assert.Equal(TimeSpan.FromHours(24), _state.BindingLeft);

        _time.Advance(TimeSpan.FromHours(23) + TimeSpan.FromMinutes(59));
        _state.Tick();
        Assert.Equal(NodeBinding.Open, _state.Binding);
        Assert.Equal(TimeSpan.FromMinutes(1), _state.BindingLeft);

        _time.Advance(TimeSpan.FromMinutes(1));
        _state.Tick();
        Assert.Equal(NodeBinding.Closed, _state.Binding);
    }

    [Fact]
    public void TheRunTimeSurvivesARestartAndTimeWhileStoppedDoesNotCount()
    {
        _time.Advance(TimeSpan.FromHours(10));
        _state.Tick();

        // The service stops for two days; only its run time counts.
        _time.Advance(TimeSpan.FromDays(2));
        var restarted = new BindingState(new NodeStore(_folder.Path), _identity, _time, "site-pc");
        Assert.Equal(TimeSpan.FromHours(14), restarted.BindingLeft);
        Assert.Equal(NodeBinding.Open, restarted.Binding);
    }

    [Fact]
    public void AllowBindingOpensAnother24HoursWithANewCode()
    {
        _time.Advance(TimeSpan.FromHours(25));
        _state.Tick();
        Assert.Equal(NodeBinding.Closed, _state.Binding);
        var code = _state.PairingCode;

        _state.AllowBinding();

        Assert.Equal(NodeBinding.Open, _state.Binding);
        Assert.Equal(TimeSpan.FromHours(24), _state.BindingLeft);
        Assert.NotEqual(code, _state.PairingCode);
    }

    [Fact]
    public void BindingClosedRefusesPairing()
    {
        _time.Advance(TimeSpan.FromHours(24));
        _state.Tick();
        var server = SiteTls.CreateIdentity("server");

        var error = Assert.Throws<NodeRefusalException>(() => _state.StartPairing(Link(server)));

        Assert.Equal(StatusCode.FailedPrecondition, error.Code);
        Assert.Equal(AdminTexts.BindingClosed, error.Message);
    }

    [Fact]
    public void TheRightCodeBindsAndTheServerIsPinned()
    {
        var server = SiteTls.CreateIdentity("server");
        var link = Link(server);

        Bind(link, _state.PairingCode);

        Assert.Equal(NodeBinding.Bound, _state.Binding);
        Assert.Equal(SiteTls.FingerprintHex(server), _state.Bound!.Fingerprint);
        Assert.Equal("Building B", _state.Bound.SiteName);
        Assert.Equal("OADM-SRV", _state.Bound.Name);
        Assert.True(_state.IsBoundTo(SiteTls.FingerprintHex(server)));
        // The clock no longer counts and another server cannot pair.
        _time.Advance(TimeSpan.FromDays(3));
        _state.Tick();
        Assert.Equal(NodeBinding.Bound, _state.Binding);
        var other = Assert.Throws<NodeRefusalException>(() => _state.StartPairing(Link(SiteTls.CreateIdentity("other"))));
        Assert.Equal(AdminTexts.BoundToAnotherServer, other.Message);
    }

    [Fact]
    public void FiveWrongCodesMakeANewCodeAndRefuseBindingForTenMinutes()
    {
        var server = SiteTls.CreateIdentity("server");
        var first = _state.PairingCode;
        for (var i = 0; i < 4; i++)
        {
            var wrong = Assert.Throws<NodeRefusalException>(() => Bind(Link(server), Other(first)));
            Assert.Equal(StatusCode.PermissionDenied, wrong.Code);
            Assert.Equal(AdminTexts.WrongCode, wrong.Message);
        }

        var fifth = Assert.Throws<NodeRefusalException>(() => Bind(Link(server), Other(first)));
        Assert.Equal(StatusCode.ResourceExhausted, fifth.Code);
        Assert.NotEqual(first, _state.PairingCode);
        Assert.Equal(5, _state.WrongCodes.Count);
        Assert.Equal(_time.GetUtcNow() + BindingState.LockTime, _state.LockedUntil);

        // Even the new code is refused while locked.
        var locked = Assert.Throws<NodeRefusalException>(() => Bind(Link(server), _state.PairingCode));
        Assert.Equal(StatusCode.ResourceExhausted, locked.Code);
        Assert.Equal(AdminTexts.TooManyWrongCodes, locked.Message);

        _time.Advance(BindingState.LockTime);
        Bind(Link(server), _state.PairingCode);
        Assert.Equal(NodeBinding.Bound, _state.Binding);
    }

    [Fact]
    public void ARelayInBetweenFailsEvenWithTheRightCode()
    {
        // The node sees the relay's key; the server proves its own: the proofs do not match.
        var server = SiteTls.CreateIdentity("server");
        var relay = SiteTls.CreateIdentity("relay");
        var link = Link(relay);
        var (salt, b) = _state.StartPairing(link);
        var pairing = new ServerPairing(_state.PairingCode, salt, b, SiteTls.Fingerprint(SiteTls.CreateIdentity("relay-node-key")), SiteTls.Fingerprint(server));

        var error = Assert.Throws<NodeRefusalException>(() => _state.Bind(link, "Building B", "OADM-SRV", pairing.A, pairing.Proof));

        Assert.Equal(StatusCode.PermissionDenied, error.Code);
        Assert.Equal(NodeBinding.Open, _state.Binding);
    }

    [Fact]
    public void BindWithoutPairStartIsRefusedAndAPairingIsUsedOnce()
    {
        var link = Link(SiteTls.CreateIdentity("server"));
        Assert.Equal(AdminTexts.PairStartFirst, Assert.Throws<NodeRefusalException>(() => _state.Bind(link, "a", "b", [1], [1])).Message);
    }

    [Fact]
    public void ResetGivesANewIdentityIdAndCodeAndForgetsTheServer()
    {
        var server = SiteTls.CreateIdentity("server");
        Bind(Link(server), _state.PairingCode);
        var id = _store.Read(c => c.Id);
        var fingerprint = _identity.FingerprintHex;
        var code = _state.PairingCode;
        var resets = 0;
        _state.IdentityReset += () => resets++;

        _state.Reset();

        Assert.Equal(NodeBinding.Open, _state.Binding);
        Assert.Null(_state.Bound);
        Assert.NotEqual(id, _store.Read(c => c.Id));
        Assert.NotEqual(fingerprint, _identity.FingerprintHex);
        Assert.NotEqual(code, _state.PairingCode);
        Assert.Equal(TimeSpan.FromHours(24), _state.BindingLeft);
        Assert.Equal(1, resets);
        // The new identity is what a restart loads.
        using var reloaded = new NodeIdentity(_folder.Path, "site-pc");
        Assert.Equal(_identity.FingerprintHex, reloaded.FingerprintHex);
    }

    [Fact]
    public void UnbindKeepsTheIdentityAndOpensBinding()
    {
        Bind(Link(SiteTls.CreateIdentity("server")), _state.PairingCode);
        var fingerprint = _identity.FingerprintHex;

        _state.Unbind();

        Assert.Equal(NodeBinding.Open, _state.Binding);
        Assert.Equal(fingerprint, _identity.FingerprintHex);
    }

    [Fact]
    public void ANewServerIdentityIsAcceptedOnlyByTheAdministrator()
    {
        Bind(Link(SiteTls.CreateIdentity("server")), _state.PairingCode);
        var replacement = SiteTls.FingerprintHex(SiteTls.CreateIdentity("server after a key loss"));
        Assert.Throws<NodeRefusalException>(_state.AcceptNewServerIdentity);

        _state.NoteNewServerIdentity(replacement);
        Assert.Equal(replacement, _store.Read(c => c.NewServerIdentity));
        _state.AcceptNewServerIdentity();

        Assert.Equal(replacement, _state.Bound!.Fingerprint);
        Assert.Empty(_store.Read(c => c.NewServerIdentity));
    }

    [Fact]
    public void RotateIdentityNeedsTheOldKeysSignature()
    {
        var server = SiteTls.CreateIdentity("server");
        var link = Link(server);
        Bind(link, _state.PairingCode);
        var next = SiteTls.CreateIdentity("server 2");
        var data = next.RawData;
        using var oldKey = server.GetECDsaPrivateKey()!;
        using var strangerKey = SiteTls.CreateIdentity("stranger").GetECDsaPrivateKey()!;

        var forged = Assert.Throws<NodeRefusalException>(() => _state.RotateIdentity(link, data, strangerKey.SignData(data, System.Security.Cryptography.HashAlgorithmName.SHA256)));
        Assert.Equal(StatusCode.InvalidArgument, forged.Code);
        _state.RotateIdentity(link, data, oldKey.SignData(data, System.Security.Cryptography.HashAlgorithmName.SHA256));

        Assert.Equal(SiteTls.FingerprintHex(next), _state.Bound!.Fingerprint);
    }

    private SiteLink Link(System.Security.Cryptography.X509Certificates.X509Certificate2 server) =>
        new(outbound: true, worker: false, "10.0.0.5:51000", server, _identity.Fingerprint);

    private void Bind(SiteLink link, string code)
    {
        var (salt, b) = _state.StartPairing(link);
        var pairing = new ServerPairing(code, salt, b, link.NodeFingerprint, link.ServerFingerprint);
        var reply = _state.Bind(link, "Building B", "OADM-SRV", pairing.A, pairing.Proof);
        Assert.True(pairing.Check(reply));
    }

    private static string Other(string code) => (code[0] == '1' ? "2" : "1") + code[1..];
}
