using System.Text.Json;

using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.Provisioning.Engine;
using Oadm.Plugins.Provisioning.Model;
using Oadm.Plugins.Provisioning.Nodes;
using Oadm.Sdk.Network;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Provisioning.Tests;

/// <summary>The page methods and the tools of the plugin, with the host's rules (FakeCoreContext).</summary>
public sealed class PluginTests : IAsyncLifetime
{
    private const string Password = "Typed-Pass-1";

    private readonly SafeDevices _devices = new();
    private readonly FakeDhcp _dhcp = new();
    private FakeAutoAdd _autoAdd = null!;
    private FakeCoreContext _ctx = null!;
    private ProvisioningPlugin _plugin = null!;
    private FakeCallContext _admin = null!;

    public async Task InitializeAsync()
    {
        _autoAdd = new FakeAutoAdd(_devices);
        _ctx = new FakeCoreContext { AutoAdd = _autoAdd };
        _plugin = new ProvisioningPlugin(new ProvisioningOptions(), new FakeTimeProvider(), new FakeProbe());
        await _plugin.StartAsync(_ctx, CancellationToken.None);
        _admin = _ctx.CallAsAllDevices();
    }

    public async Task DisposeAsync() => await _plugin.DisposeAsync();

    private async Task<T> CallAsync<T>(string method, object? payload = null) =>
        PluginJson.Parse<T>(await _ctx.InvokeAsync(_plugin, _admin, method, payload is null ? null : JsonSerializer.Serialize(payload, ProvisioningJson.Options)), ProvisioningJson.Options);

    private static Profile NewProfile(params FlowNode[] nodes)
    {
        var profile = ProfileStore.NewProfile("Lobby");
        return profile with { Nodes = [.. profile.Nodes, .. nodes] };
    }

    [Fact]
    public async Task A_typed_password_goes_into_the_credential_list_and_never_into_the_settings()
    {
        var saved = await CallAsync<ProfileReply>(ProvisioningMethods.SaveProfile, new SaveProfileRequest(NewProfile(), Password));

        Assert.True(saved.Check.IsValid);
        Assert.Contains("credential root", _autoAdd.Calls);
        var id = NodeSettings.Read<SetPasswordSettings>(saved.Profile.Nodes[0].Settings)!.CredentialId;
        Assert.True(_autoAdd.Credentials.ContainsKey(id!.Value));
        Assert.All(_ctx.PluginSettings.Values.Values, v => Assert.DoesNotContain(Password, v, StringComparison.Ordinal));
        Assert.DoesNotContain(Password, _plugin.AuditDetail(ProvisioningMethods.SaveProfile, JsonSerializer.Serialize(new SaveProfileRequest(NewProfile(), Password))) ?? string.Empty, StringComparison.Ordinal);
        Assert.Single((await CallAsync<ProfilesReply>(ProvisioningMethods.ListProfiles)).Profiles);
    }

    [Fact]
    public async Task The_editors_check_is_the_check_of_activation()
    {
        var profile = NewProfile(Harness.Node("time", NodeKind.DateTime, new DateTimeSettings()));
        var saved = await CallAsync<ProfileReply>(ProvisioningMethods.SaveProfile, new SaveProfileRequest(profile, Password));
        var check = await CallAsync<ProfileCheckResult>(ProvisioningMethods.CheckProfile, new ProfileRequest(saved.Profile));
        Assert.Equal("Choose a time zone.", check.NodeErrors["time"]);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => CallAsync<StatusReply>(ProvisioningMethods.ActivateProfile, new IdRequest(saved.Profile.Id)));
        Assert.Equal("Fix the profile first. Date and time: Choose a time zone.", refused.Message);
        Assert.Null((await CallAsync<StatusReply>(ProvisioningMethods.GetStatus)).ActiveProfileId);
    }

    [Fact]
    public async Task Start_needs_a_running_dhcp_server_and_warns_about_an_overlapping_pool()
    {
        var saved = await CallAsync<ProfileReply>(ProvisioningMethods.SaveProfile, new SaveProfileRequest(NewProfile(Harness.Node("ip", NodeKind.SetIp, Harness.Pool("192.168.15.150", "192.168.15.160"))), Password));
        await CallAsync<StatusReply>(ProvisioningMethods.ActivateProfile, new IdRequest(saved.Profile.Id));

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => CallAsync<StatusReply>(ProvisioningMethods.Start));
        Assert.Equal("Turn on the DHCP server first.", refused.Message);

        _ctx.FakeServices.Offer<IDhcpServerLeases>(_dhcp);
        var started = await CallAsync<StatusReply>(ProvisioningMethods.Start);

        Assert.True(started.Started);
        Assert.Equal("admin", started.StartedBy);
        Assert.Equal(["The pool overlaps the DHCP server's range 192.168.15.100 to 192.168.15.200."], started.Warnings);
        Assert.Contains(_ctx.PublishedEvents, e => e.Topic == ProvisioningMethods.StatusTopic);

        var stopped = await CallAsync<StatusReply>(ProvisioningMethods.Stop);
        Assert.False(stopped.Started);
    }

    [Fact]
    public async Task Every_method_is_for_users_with_all_devices_and_administrators_by_default()
    {
        var inside = _ctx.Add(new FakeDevice { Address = "10.0.0.48" });
        var outside = _ctx.Add(new FakeDevice { Address = "10.0.0.49", Serial = "ACCC8E000002" });
        var canary = new PluginCanary(_ctx, _plugin, inside, outside);
        foreach (var method in new[]
        {
            ProvisioningMethods.GetStatus, ProvisioningMethods.ListProfiles, ProvisioningMethods.NodeKinds, ProvisioningMethods.Credentials,
            ProvisioningMethods.Start, ProvisioningMethods.Stop, ProvisioningMethods.ListCameras,
        })
        {
            canary.AllDevicesOnly(method);
        }

        canary.AllDevicesOnly(ProvisioningMethods.GetProfile, """{"id":"00000000-0000-0000-0000-000000000001"}""")
            .AllDevicesOnly(ProvisioningMethods.CheckProfile, """{"profile":{"name":"x"}}""")
            .AllDevicesOnly(ProvisioningMethods.SaveProfile, """{"profile":{"name":"x"}}""")
            .AllDevicesOnly(ProvisioningMethods.DeleteProfile, """{"id":"00000000-0000-0000-0000-000000000001"}""")
            .AllDevicesOnly(ProvisioningMethods.ActivateProfile, """{"id":"00000000-0000-0000-0000-000000000001"}""")
            .AllDevicesOnly(ProvisioningMethods.GetCamera, """{"mac":"AC:CC:8E:00:00:01"}""")
            .AllDevicesOnly(ProvisioningMethods.RunFailedAgain, """{"macs":["AC:CC:8E:00:00:01"]}""")
            .AllDevicesOnly(ProvisioningMethods.Continue, """{"macs":["AC:CC:8E:00:00:01"]}""")
            .AllDevicesOnly(ProvisioningMethods.RemoveCameras, """{"macs":["AC:CC:8E:00:00:01"]}""");
        await canary.RunAsync();

        Assert.All(_plugin.Permissions, p => Assert.False(p.OperatorDefault));
        Assert.Equal(PermissionKeys.Show, _plugin.RequiredPermission(ProvisioningMethods.GetStatus));
        Assert.Equal(PermissionKeys.Manage, _plugin.RequiredPermission(ProvisioningMethods.Start));
        Assert.Equal(UserRole.Admin, _plugin.RequiredRole("tool.start"));
        Assert.True(_plugin.IsAudited(ProvisioningMethods.Start));
        Assert.False(_plugin.IsAudited("tool.start.preview"));
    }

    [Fact]
    public async Task The_tools_cover_the_page_and_a_preview_changes_nothing()
    {
        var names = _plugin.Tools.Select(t => t.Name).ToList();
        Assert.Equal(["status", "profiles", "profile", "credentials", "cameras", "profile_save", "profile_delete", "profile_activate", "start", "stop", "cameras_run_failed_again", "cameras_continue", "cameras_remove"], names);
        Assert.All(_plugin.Tools, t => Assert.True(t.Name.Length <= PluginTool.MaxNameLength));

        var saved = await CallAsync<ProfileReply>(ProvisioningMethods.SaveProfile, new SaveProfileRequest(NewProfile(), Password));
        await CallAsync<StatusReply>(ProvisioningMethods.ActivateProfile, new IdRequest(saved.Profile.Id));
        _ctx.FakeServices.Offer<IDhcpServerLeases>(_dhcp);

        var preview = await _ctx.InvokeAsync(_plugin, _admin, "tool.start.preview", "{}");
        Assert.Contains("Lobby", preview, StringComparison.Ordinal);
        Assert.Null(_plugin.Engine!.Session);

        var savePreview = await _ctx.InvokeAsync(_plugin, _admin, "tool.profile_save.preview", """{"name":"Gate","nodes":[{"id":"set-password","kind":"set_password","settings":{"credential_id":"00000000-0000-0000-0000-000000000001"}},{"id":"add-to-oadm","kind":"add_to_oadm"}]}""");
        Assert.Contains("\"nodes_with_errors\":1", savePreview, StringComparison.Ordinal);
        Assert.Single((await CallAsync<ProfilesReply>(ProvisioningMethods.ListProfiles)).Profiles);

        var status = await _ctx.InvokeAsync(_plugin, _admin, "tool.status", "{}");
        Assert.Contains("\"dhcp_running\":true", status, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, status + preview + savePreview, StringComparison.Ordinal);
    }
}
