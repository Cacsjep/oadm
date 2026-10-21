using System.Security.Cryptography.X509Certificates;

using Grpc.Core;

using Microsoft.Extensions.Time.Testing;

using Oadm.Contracts.SiteNode;
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

        var error = Assert.Throws<NodeRefusalException>(() => _state.StartPairing(Link(server), new ServerPairing("ABCD1234", [1], [2]).Round1));

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
        var other = Assert.Throws<NodeRefusalException>(() => _state.StartPairing(Link(SiteTls.CreateIdentity("other")), new ServerPairing("ABCD1234", [1], [2]).Round1));
        Assert.Equal(AdminTexts.BoundToAnotherServer, other.Message);
    }

    [Fact]
    public void FiveWrongCodesMakeANewCodeAndRefuseBindingForTenMinutes()
    {
        var server = SiteTls.CreateIdentity("server");
        var first = _state.PairingCode;
        var generation = _state.PairingCodeGeneration;
        for (var i = 0; i < 4; i++)
        {
            var wrong = Assert.Throws<NodeRefusalException>(() => Bind(Link(server), Other(first)));
            Assert.Equal(StatusCode.PermissionDenied, wrong.Code);
            Assert.Equal(AdminTexts.WrongCode, wrong.Message);
        }

        var fifth = Assert.Throws<NodeRefusalException>(() => Bind(Link(server), Other(first)));
        Assert.Equal(StatusCode.ResourceExhausted, fifth.Code);
        Assert.NotEqual(first, _state.PairingCode);
        Assert.NotEqual(generation, _state.PairingCodeGeneration); // the app forgets the code it shows
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
    public void ACodeLastsTenMinutesAndAPairingStartedWithItStillBinds()
    {
        var first = _state.CurrentCode();
        var generation = _state.PairingCodeGeneration;
        Assert.Equal(_time.GetUtcNow() + BindingState.CodeLifetime, first.ValidUntil);

        _time.Advance(BindingState.CodeLifetime - TimeSpan.FromSeconds(1));
        Assert.Equal(first.Code, _state.PairingCode);

        // A pairing starts with the code, then the code runs out: the pairing still binds with it.
        var server = SiteTls.CreateIdentity("server");
        var link = Link(server);
        var pairing = new ServerPairing(first.Code, link.NodeFingerprint, link.ServerFingerprint);
        var (round1, round2) = _state.StartPairing(link, pairing.Round1);
        _time.Advance(TimeSpan.FromSeconds(1));
        var second = _state.CurrentCode();
        Assert.NotEqual(first.Code, second.Code);
        Assert.NotEqual(generation, _state.PairingCodeGeneration);
        Assert.Equal(_time.GetUtcNow() + BindingState.CodeLifetime, second.ValidUntil);
        var (serverRound2, proof) = pairing.Continue(round1, round2);
        _state.Bind(link, "Building B", "OADM-SRV", serverRound2, proof);
        Assert.Equal(NodeBinding.Bound, _state.Binding);
    }

    [Fact]
    public void AnExpiredCodeNoLongerBinds()
    {
        var first = _state.PairingCode;
        _time.Advance(BindingState.CodeLifetime);

        var wrong = Assert.Throws<NodeRefusalException>(() => Bind(Link(SiteTls.CreateIdentity("server")), first));

        Assert.Equal(StatusCode.PermissionDenied, wrong.Code);
        Assert.Equal(NodeBinding.Open, _state.Binding);
    }

    [Fact]
    public void ARelayInBetweenFailsEvenWithTheRightCode()
    {
        // The node sees the relay's key; the server proves its own: the proofs do not match.
        var server = SiteTls.CreateIdentity("server");
        var relay = SiteTls.CreateIdentity("relay");
        var link = Link(relay);
        var pairing = new ServerPairing(_state.PairingCode, SiteTls.Fingerprint(SiteTls.CreateIdentity("relay-node-key")), SiteTls.Fingerprint(server));
        var (round1, round2) = _state.StartPairing(link, pairing.Round1);
        var (serverRound2, proof) = pairing.Continue(round1, round2);

        var error = Assert.Throws<NodeRefusalException>(() => _state.Bind(link, "Building B", "OADM-SRV", serverRound2, proof));

        Assert.Equal(StatusCode.PermissionDenied, error.Code);
        Assert.Equal(NodeBinding.Open, _state.Binding);
    }

    [Fact]
    public void BindWithoutPairStartIsRefusedAndAPairingIsUsedOnce()
    {
        var link = Link(SiteTls.CreateIdentity("server"));
        Assert.Equal(AdminTexts.PairStartFirst, Assert.Throws<NodeRefusalException>(() => _state.Bind(link, "a", "b", null, [1])).Message);
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
    public void ANewServerIdentityMustProveThePairingCodeAfterTheAdministratorAccepts()
    {
        var old = SiteTls.CreateIdentity("server");
        Bind(Link(old), _state.PairingCode);
        var replacement = SiteTls.CreateIdentity("server after a key loss");
        var hex = SiteTls.FingerprintHex(replacement);
        Assert.Throws<NodeRefusalException>(() => _state.AcceptNewServerIdentity());

        Assert.True(_state.NoteNewServerIdentity(hex, "10.0.0.9:5081"));
        Assert.Equal(hex, _store.Read(c => c.NewServerIdentity));
        Assert.Equal("10.0.0.9:5081", _store.Read(c => c.NewServerFrom));
        // Before Accept the new identity cannot even start a pairing.
        Assert.Equal(AdminTexts.BoundToAnotherServer, Assert.Throws<NodeRefusalException>(() => Bind(Link(replacement), _state.PairingCode)).Message);

        var code = _state.AcceptNewServerIdentity();

        // Accepting changes nothing by itself: the old identity stays pinned until the new one proves the code.
        Assert.Equal(code, _state.PairingCode);
        Assert.Equal(SiteTls.FingerprintHex(old), _state.Bound!.Fingerprint);
        Assert.True(_state.IsPairingOpenFor(hex));
        Assert.Equal(StatusCode.PermissionDenied, Assert.Throws<NodeRefusalException>(() => Bind(Link(replacement), Other(code))).Code);
        Assert.Equal(SiteTls.FingerprintHex(old), _state.Bound!.Fingerprint);

        Bind(Link(replacement), code);
        Assert.Equal(hex, _state.Bound!.Fingerprint);
        Assert.Empty(_store.Read(c => c.NewServerIdentity));
        Assert.False(_store.Read(c => c.NewServerAccepted));
    }

    [Fact]
    public void AWaitingNewIdentityIsNotReplacedByAnotherOneSilently()
    {
        Bind(Link(SiteTls.CreateIdentity("server")), _state.PairingCode);
        var first = SiteTls.FingerprintHex(SiteTls.CreateIdentity("first"));
        var second = SiteTls.FingerprintHex(SiteTls.CreateIdentity("second"));

        Assert.True(_state.NoteNewServerIdentity(first, "10.0.0.9:5081"));
        Assert.False(_state.NoteNewServerIdentity(second, "10.0.0.66:5081"));
        Assert.Equal(first, _store.Read(c => c.NewServerIdentity));
        Assert.Equal("10.0.0.9:5081", _store.Read(c => c.NewServerFrom));

        // An accepted one never gives way.
        _state.AcceptNewServerIdentity();
        _time.Advance(BindingState.NewIdentityHold);
        Assert.False(_state.NoteNewServerIdentity(second, "10.0.0.66:5081"));
        Assert.Equal(first, _store.Read(c => c.NewServerIdentity));
    }

    [Fact]
    public void AnUnansweredNewIdentityGivesWayAfterAnHour()
    {
        Bind(Link(SiteTls.CreateIdentity("server")), _state.PairingCode);
        var first = SiteTls.FingerprintHex(SiteTls.CreateIdentity("first"));
        var second = SiteTls.FingerprintHex(SiteTls.CreateIdentity("second"));
        _state.NoteNewServerIdentity(first, "10.0.0.9:5081");

        _time.Advance(BindingState.NewIdentityHold);

        Assert.True(_state.NoteNewServerIdentity(second, "10.0.0.66:5081"));
        Assert.Equal(second, _store.Read(c => c.NewServerIdentity));
    }

    [Fact]
    public async Task ParallelLinksNeverGetMoreThanFiveGuesses()
    {
        // 12 links with a started pairing each bind with wrong codes at once: proofs are checked one at a time, so the
        // lock comes after exactly 5 and the rest are refused without a check.
        var server = SiteTls.CreateIdentity("server");
        var code = _state.PairingCode;
        var attempts = new List<(SiteLink Link, PakeRound2 Round2, byte[] Proof)>();
        for (var i = 0; i < 12; i++)
        {
            var link = new SiteLink(worker: false, $"10.0.{i}.5:51000", server, _identity.Fingerprint);
            var pairing = new ServerPairing(Other(code), link.NodeFingerprint, link.ServerFingerprint);
            var (round1, round2) = _state.StartPairing(link, pairing.Round1);
            var (serverRound2, proof) = pairing.Continue(round1, round2);
            attempts.Add((link, serverRound2, proof));
        }

        var results = await Task.WhenAll(attempts.Select(a => Task.Run(() =>
            Assert.Throws<NodeRefusalException>(() => _state.Bind(a.Link, "Building B", "OADM-SRV", a.Round2, a.Proof)))));

        Assert.Equal(4, results.Count(r => r.Code == StatusCode.PermissionDenied));
        Assert.Equal(8, results.Count(r => r.Code == StatusCode.ResourceExhausted));
        Assert.Equal(5, _state.WrongCodes.Count);
    }

    [Fact]
    public void PairStartIsLimitedPerAddressAndInAll()
    {
        var server = SiteTls.CreateIdentity("server");
        for (var i = 0; i < BindingState.PairAttemptsPerAddress; i++)
            _state.StartPairing(Link(server), new ServerPairing(_state.PairingCode, [1], [2]).Round1);

        var refused = Assert.Throws<NodeRefusalException>(() => _state.StartPairing(Link(server), new ServerPairing(_state.PairingCode, [1], [2]).Round1));
        Assert.Equal(StatusCode.ResourceExhausted, refused.Code);
        Assert.Equal(AdminTexts.TooManyPairingAttempts, refused.Message);

        // One /64 counts as one address; other addresses still get in until the total.
        var sameNet = new SiteLink(worker: false, "[2001:db8:1:2::5]:51000", server, _identity.Fingerprint);
        var sameNetOther = new SiteLink(worker: false, "[2001:db8:1:2::77]:51000", server, _identity.Fingerprint);
        for (var i = 0; i < BindingState.PairAttemptsPerAddress; i++)
            _state.StartPairing(sameNet, new ServerPairing(_state.PairingCode, [1], [2]).Round1);
        Assert.Throws<NodeRefusalException>(() => _state.StartPairing(sameNetOther, new ServerPairing(_state.PairingCode, [1], [2]).Round1));

        for (var i = 0; i < BindingState.PairAttemptsTotal - 2 * BindingState.PairAttemptsPerAddress; i++)
            _state.StartPairing(new SiteLink(worker: false, $"10.9.{i}.1:51000", server, _identity.Fingerprint), new ServerPairing(_state.PairingCode, [1], [2]).Round1);
        Assert.Equal(StatusCode.ResourceExhausted, Assert.Throws<NodeRefusalException>(() =>
            _state.StartPairing(new SiteLink(worker: false, "10.99.0.1:51000", server, _identity.Fingerprint), new ServerPairing(_state.PairingCode, [1], [2]).Round1)).Code);

        // A minute later every address may try again.
        _time.Advance(TimeSpan.FromMinutes(1));
        _state.StartPairing(Link(server), new ServerPairing(_state.PairingCode, [1], [2]).Round1);
    }

    [Fact]
    public void AnInvalidRoundOneIsRefusedWithoutCountingAsAWrongCode()
    {
        var round1 = new ServerPairing("ABCD1234", [1], [2]).Round1;
        round1.Gx1 = Google.Protobuf.ByteString.CopyFrom([1]);

        var error = Assert.Throws<NodeRefusalException>(() => _state.StartPairing(Link(SiteTls.CreateIdentity("server")), round1));

        Assert.Equal(StatusCode.InvalidArgument, error.Code);
        Assert.Empty(_state.WrongCodes);
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
        new(worker: false, "10.0.0.5:51000", server, _identity.Fingerprint);

    private void Bind(SiteLink link, string code)
    {
        var pairing = new ServerPairing(code, link.NodeFingerprint, link.ServerFingerprint);
        var (round1, round2) = _state.StartPairing(link, pairing.Round1);
        var (serverRound2, proof) = pairing.Continue(round1, round2);
        var reply = _state.Bind(link, "Building B", "OADM-SRV", serverRound2, proof);
        Assert.True(pairing.Check(reply));
    }

    private static string Other(string code) => (code[0] == '1' ? "2" : "1") + code[1..];
}
