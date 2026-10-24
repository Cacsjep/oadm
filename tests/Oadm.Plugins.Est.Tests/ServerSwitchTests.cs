using Oadm.Core.Plugins;
using Oadm.Plugins.Est.Serving;
using Oadm.Plugins.Pki.Servers;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Licensing;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Est.Tests;

/// <summary>
/// The EST server as a PRO part of PKI (docs/spec/pki.md "One plugin"): off by default, Turn on / Turn off on its page with
/// the stored choice, its port open only while it runs, only with OADM PRO, and the data of OADM 4.1 taken over.
/// </summary>
public sealed class ServerSwitchTests : IAsyncLifetime
{
    private readonly FakeAuthority _ca = new();
    private readonly EstPlugin _plugin = new();
    private FakeCoreContext _core = null!;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _plugin.DisposeAsync();
        _ca.Dispose();
    }

    private async Task StartAsync(FakeProLicense pro, bool? on = null, Action<FakeCoreContext>? before = null)
    {
        _core = new FakeCoreContext { Pro = pro };
        _core.FakeServices.Offer<IDeviceCertificateAuthority>(_ca);
        if (on is { } choice)
        {
            await _core.Settings.SetAsync(ServerSwitch<EstService>.SettingKey, choice ? "true" : "false", CancellationToken.None);
        }

        before?.Invoke(_core);
        await _plugin.StartAsync(_core, CancellationToken.None);
    }

    private async Task<ServerSwitchState> CallAsync(string method) =>
        ServerSwitchMethods.Deserialize(await _core.InvokeAsync(_plugin, _core.CallAsAllDevices(), method, null));

    private IEnumerable<InMemoryRelay> OpenListeners => _core.FakeNetwork.Listeners.Where(l => !l.IsDisposed);

    [Fact]
    public async Task OffByDefaultTheServerOpensNoPortAndThePageOffersTurnOn()
    {
        await StartAsync(FakeProLicense.ActivePro());

        Assert.Null(_plugin.Service);
        Assert.Empty(_core.FakeNetwork.Listeners);
        Assert.Equal(new ServerSwitchState(false, false, true, ServerSwitchMethods.OffText), await CallAsync(ServerSwitchMethods.Get));
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => _plugin.InvokeAsync(EstMethods.GetState, null, CancellationToken.None));
        Assert.Equal(EstPlugin.NotRunning, refused.Message);
    }

    [Fact]
    public async Task TurnOnOpensThePortAndKeepsTheChoiceOverARestartTurnOffClosesIt()
    {
        await StartAsync(FakeProLicense.ActivePro());

        var on = await CallAsync(ServerSwitchMethods.TurnOn);

        Assert.Equal(new ServerSwitchState(true, true, true, string.Empty), on);
        Assert.NotNull(_plugin.Service);
        Assert.Equal(EstPluginInfo.DefaultPort, Assert.Single(OpenListeners).Port);
        Assert.Equal("true", await _core.Settings.GetAsync(ServerSwitch<EstService>.SettingKey, CancellationToken.None));
        Assert.Contains(_core.PublishedEvents, e => e.Topic == ServerSwitchMethods.Topic);

        // The core plugin stops and starts again (server restart): the server runs again by itself.
        await _plugin.StopAsync(CancellationToken.None);
        Assert.Empty(OpenListeners);
        await _plugin.StartAsync(_core, CancellationToken.None);
        Assert.Single(OpenListeners);

        var off = await CallAsync(ServerSwitchMethods.TurnOff);

        Assert.Equal(new ServerSwitchState(false, false, true, ServerSwitchMethods.OffText), off);
        Assert.Null(_plugin.Service);
        Assert.Empty(OpenListeners);
        Assert.Equal("false", await _core.Settings.GetAsync(ServerSwitch<EstService>.SettingKey, CancellationToken.None));
    }

    [Fact]
    public async Task WithoutOadmProTurnOnIsRefusedAndALicenseStartsAndStopsTheKeptChoice()
    {
        var pro = new FakeProLicense(); // no license
        await StartAsync(pro, on: true);

        Assert.Null(_plugin.Service);
        Assert.Empty(_core.FakeNetwork.Listeners);
        Assert.Equal(new ServerSwitchState(true, false, false, ProLicenseTexts.NeedsPro), await CallAsync(ServerSwitchMethods.Get));
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => _plugin.InvokeAsync(ServerSwitchMethods.TurnOn, null, CancellationToken.None));
        Assert.Equal(ProLicenseTexts.NeedsPro, refused.Message);

        // A license (or trial) starts the server with the kept choice; when it ends the server stops and the choice stays.
        pro.Current = FakeProLicense.ActivePro().Current;
        await Eventually.TrueAsync(() => _plugin.Service is not null && OpenListeners.Count() == 1, "the server starts with the license");
        pro.Current = ProLicenseInfo.None;
        await Eventually.TrueAsync(() => _plugin.Service is null && !OpenListeners.Any(), "the server stops without the license");
        Assert.Equal("true", await _core.Settings.GetAsync(ServerSwitch<EstService>.SettingKey, CancellationToken.None));
        pro.Current = FakeProLicense.ActivePro().Current;
        await Eventually.TrueAsync(() => _plugin.Service is not null, "a new license starts it again");
    }

    [Fact]
    public async Task WithoutOadmProTheKeptChoiceCanBeTurnedOffSoALaterLicenseStartsNothing()
    {
        var pro = new FakeProLicense(); // a 4.1 choice "on" on a server whose license ran out
        await StartAsync(pro, on: true);

        await _plugin.InvokeAsync(ServerSwitchMethods.TurnOff, null, CancellationToken.None);
        Assert.Equal("false", await _core.Settings.GetAsync(ServerSwitch<EstService>.SettingKey, CancellationToken.None));

        pro.Current = FakeProLicense.ActivePro().Current;
        await Eventually.TrueAsync(async () => (await CallAsync(ServerSwitchMethods.Get)).ProActive, "the page sees the license");
        Assert.Null(_plugin.Service);
        Assert.Empty(OpenListeners);
    }

    [Fact]
    public async Task TheDataOf41IsTakenOverTheChoiceTheSettingsAndTheRecords()
    {
        // As a 4.1 server stored it under the core plugin id oadm.est, plus the choice the host took over from Plugins.Enabled.
        Assert.Equal(MergedPlugins.ServerOnSetting, ServerSwitch<EstService>.SettingKey);
        var issued = new IssuedCertificate
        {
            SerialNumber = "7DFD0549", DeviceId = Guid.NewGuid(), Device = "10.0.0.48", Profile = "https",
            IssuedUtc = DateTime.UtcNow.AddDays(-10), NotAfterUtc = DateTime.UtcNow.AddDays(355),
        };
        await StartAsync(FakeProLicense.ActivePro(), on: true, core =>
        {
            core.Settings.SetAsync(EstStore.ConfigKey, """{"port":9455,"interfaceId":"all","enrollmentsKept":500}""", CancellationToken.None).GetAwaiter().GetResult();
            core.PluginRecords.SetManyAsync(EstStore.CertificatesKey, [new(issued.SerialNumber, EstJson.Serialize(issued))], CancellationToken.None).GetAwaiter().GetResult();
        });

        Assert.NotNull(_plugin.Service);
        Assert.Equal(9455, Assert.Single(OpenListeners).Port);
        Assert.Equal(500, _plugin.Service.Store.Config.EnrollmentsKept);
        Assert.Equal(1, _plugin.Service.Store.CertificateCount);
    }

    [Fact]
    public async Task TurningOnAndOffIsAChangeOfTheServerReadingIsForEveryone()
    {
        await StartAsync(FakeProLicense.ActivePro());

        Assert.Equal(UserRole.Operator, _plugin.RequiredRole(ServerSwitchMethods.Get));
        Assert.False(_plugin.RequiresAllDevices(ServerSwitchMethods.Get));
        foreach (var method in new[] { ServerSwitchMethods.TurnOn, ServerSwitchMethods.TurnOff })
        {
            Assert.Equal(UserRole.Admin, _plugin.RequiredRole(method));
            Assert.True(((ICorePlugin)_plugin).IsAudited(method));
            Assert.True(_plugin.RequiresAllDevices(method));
            Assert.Null(((ICorePlugin)_plugin).RequiredPermission(method)); // "manage" of oadm.est: the key of 4.1
        }

        var device = _core.Add(new FakeDevice { Serial = "ACCC8E000001" });
        await Assert.ThrowsAnyAsync<Exception>(() => _core.InvokeAsync(_plugin, _core.CallAsGroup([device.Id]), ServerSwitchMethods.TurnOn, null));
        Assert.Null(_plugin.Service);
        Assert.False((await CallAsync(ServerSwitchMethods.Get)).On);
    }
}
