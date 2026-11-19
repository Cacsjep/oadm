using System.Text.Json;

using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.Provisioning.Engine;
using Oadm.Plugins.Provisioning.Model;
using Oadm.Plugins.Provisioning.Nodes;
using Oadm.Sdk.Network;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Provisioning.Tests;

/// <summary>The methods and the tools of the plugin, with the host's rules (FakeCoreContext).</summary>
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
        _ctx = new FakeCoreContext { AutoAdd = _autoAdd, Secrets = new FakeSecrets(), DataDirectory = _data };
        _plugin = new ProvisioningPlugin(new ProvisioningOptions(), new FakeTimeProvider(), new FakeProbe());
        await _plugin.StartAsync(_ctx, CancellationToken.None);
        _admin = _ctx.CallAsAllDevices();
    }

    public async Task DisposeAsync()
    {
        await _plugin.DisposeAsync();
        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, recursive: true);
        }
    }

    private readonly string _data = Path.Combine(Path.GetTempPath(), "oadm-prov-tests", Guid.NewGuid().ToString("N"));

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
    public async Task The_first_profile_without_errors_becomes_active_when_saved()
    {
        var draft = await CallAsync<ProfileReply>(ProvisioningMethods.SaveProfile, new SaveProfileRequest(NewProfile(Harness.Node("time", NodeKind.DateTime, new DateTimeSettings())), Password));
        Assert.Null((await CallAsync<StatusReply>(ProvisioningMethods.GetStatus)).ActiveProfileId);

        var first = await CallAsync<ProfileReply>(ProvisioningMethods.SaveProfile, new SaveProfileRequest(NewProfile() with { Name = "Parking" }, Password));
        Assert.Equal(first.Profile.Id, (await CallAsync<StatusReply>(ProvisioningMethods.GetStatus)).ActiveProfileId);

        await CallAsync<ProfileReply>(ProvisioningMethods.SaveProfile, new SaveProfileRequest(NewProfile() with { Name = "Gate" }, Password));
        Assert.Equal(first.Profile.Id, (await CallAsync<StatusReply>(ProvisioningMethods.GetStatus)).ActiveProfileId);
        Assert.False(draft.Check.IsValid);
    }

    [Fact]
    public async Task A_node_password_and_file_are_kept_by_id_and_checked_in_the_profile()
    {
        var secret = await CallAsync<SecretReply>(ProvisioningMethods.StoreSecret, new SecretRequest("Op-Pass-77"));
        var upload = ((FakeUploadedFiles)_admin.Uploads).Add("P3265-LVE_12_6_95.bin", [1, 2, 3]);
        var file = await CallAsync<StoredFile>(ProvisioningMethods.StoreFile, new FileRequest(upload.Id));
        Assert.Equal(("P3265-LVE_12_6_95.bin", 3L, upload.Sha256), (file.Name, file.Size, file.Sha256));

        var profile = NewProfile(
            Harness.Node("users", NodeKind.Users, new UsersSettings([new UserAccount("op1", CameraRole.Operator, SecretId: secret.Id)])),
            Harness.Node("os", NodeKind.UpgradeFiles, new UpgradeFilesSettings([new OsFile(file.Id, file.Name)])),
            Harness.Node("gone", NodeKind.SdCard, new SdCardSettings(Encrypt: true, SecretId: Guid.NewGuid())));
        var saved = await CallAsync<ProfileReply>(ProvisioningMethods.SaveProfile, new SaveProfileRequest(profile, Password));

        Assert.Equal(["gone"], saved.Check.NodeErrors.Keys);
        Assert.Equal("The passphrase is no longer on the server. Type it again.", saved.Check.NodeErrors["gone"]);
        Assert.All(_ctx.PluginSettings.Values.Values, v => Assert.DoesNotContain("Op-Pass-77", v, StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(_data, "files", file.Id.ToString("N") + ".bin")));
        Assert.Equal("Stored a password for a profile", _plugin.AuditDetail(ProvisioningMethods.StoreSecret, """{"value":"Op-Pass-77"}"""));

        var refused = await Assert.ThrowsAsync<KeyNotFoundException>(() => CallAsync<StoredFile>(ProvisioningMethods.StoreFile, new FileRequest("0123456789abcdef0123456789abcdef")));
        Assert.Equal("The uploaded file is no longer on the server. Upload it again.", refused.Message);
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
    public async Task On_needs_an_active_profile_without_errors_and_warns_about_an_overlapping_pool()
    {
        var off = await CallAsync<StatusReply>(ProvisioningMethods.GetStatus);
        Assert.False(off.On);
        Assert.Equal(ProvisioningPlugin.ChooseActiveFirst, off.TurnOnRefusal);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => CallAsync<StatusReply>(ProvisioningMethods.TurnOn));
        Assert.Equal(ProvisioningPlugin.ChooseActiveFirst, refused.Message);

        _ctx.FakeServices.Offer<IDhcpServerLeases>(_dhcp);
        var saved = await CallAsync<ProfileReply>(ProvisioningMethods.SaveProfile, new SaveProfileRequest(NewProfile(Harness.Node("ip", NodeKind.Network, Harness.Pool("192.168.15.150", "192.168.15.160"))), Password));
        Assert.True(saved.Check.IsValid); // the first one without errors: active
        var on = await CallAsync<StatusReply>(ProvisioningMethods.TurnOn);

        Assert.True(on.On);
        Assert.Equal("admin", on.OnBy);
        Assert.Null(on.TurnOnRefusal);
        Assert.Equal(["The pool overlaps the DHCP server's range 192.168.15.100 to 192.168.15.200."], on.Warnings);
        Assert.Contains(_ctx.PublishedEvents, e => e.Topic == ProvisioningMethods.StatusTopic && e.Audience == PluginEventAudience.AllDevices);
        Assert.True((await CallAsync<StateReply>(ProvisioningMethods.State)).On);
        Assert.Equal("Turned provisioning on", _plugin.AuditDetail(ProvisioningMethods.TurnOn, null));

        var again = await Assert.ThrowsAsync<InvalidOperationException>(() => CallAsync<StatusReply>(ProvisioningMethods.TurnOn));
        Assert.Equal(ProvisioningService.AlreadyOn, again.Message);
        var delete = await Assert.ThrowsAsync<InvalidOperationException>(() => CallAsync<ProfilesReply>(ProvisioningMethods.DeleteProfile, new IdRequest(saved.Profile.Id)));
        Assert.Equal(ProvisioningPlugin.ActiveWhileOn, delete.Message);

        var turnedOff = await CallAsync<StatusReply>(ProvisioningMethods.TurnOff);
        Assert.False(turnedOff.On);
        Assert.False((await CallAsync<StateReply>(ProvisioningMethods.State)).On);
        Assert.Equal("Turned provisioning off", _plugin.AuditDetail(ProvisioningMethods.TurnOff, null));
        await CallAsync<ProfilesReply>(ProvisioningMethods.DeleteProfile, new IdRequest(saved.Profile.Id)); // off: the active one goes too
    }

    [Fact]
    public async Task On_survives_a_restart_of_the_plugin()
    {
        await CallAsync<ProfileReply>(ProvisioningMethods.SaveProfile, new SaveProfileRequest(NewProfile(), Password));
        await CallAsync<StatusReply>(ProvisioningMethods.TurnOn);

        await _plugin.StopAsync(CancellationToken.None);
        await _plugin.StartAsync(_ctx, CancellationToken.None);

        var status = await CallAsync<StatusReply>(ProvisioningMethods.GetStatus);
        Assert.True(status.On);
        Assert.Equal("admin", status.OnBy);
        Assert.Equal(1, _autoAdd.Watchers); // the old service stopped watching
    }

    [Fact]
    public async Task Whether_it_is_on_is_for_every_signed_in_user_and_everything_else_for_users_with_all_devices()
    {
        var inside = _ctx.Add(new FakeDevice { Address = "10.0.0.48" });
        var outside = _ctx.Add(new FakeDevice { Address = "10.0.0.49", Serial = "ACCC8E000002" });
        var canary = new PluginCanary(_ctx, _plugin, inside, outside);
        foreach (var method in new[]
        {
            ProvisioningMethods.GetStatus, ProvisioningMethods.ListProfiles, ProvisioningMethods.NodeKinds, ProvisioningMethods.Credentials,
            ProvisioningMethods.TurnOn, ProvisioningMethods.TurnOff, ProvisioningMethods.VapixCommands,
        })
        {
            canary.AllDevicesOnly(method);
        }

        canary.AllDevicesOnly(ProvisioningMethods.GetProfile, """{"id":"00000000-0000-0000-0000-000000000001"}""")
            .AllDevicesOnly(ProvisioningMethods.CheckProfile, """{"profile":{"name":"x"}}""")
            .AllDevicesOnly(ProvisioningMethods.SaveProfile, """{"profile":{"name":"x"}}""")
            .AllDevicesOnly(ProvisioningMethods.DeleteProfile, """{"id":"00000000-0000-0000-0000-000000000001"}""")
            .AllDevicesOnly(ProvisioningMethods.ActivateProfile, """{"id":"00000000-0000-0000-0000-000000000001"}""")
            .AllDevicesOnly(ProvisioningMethods.RunAgain, "{\"deviceIds\":[\"" + inside.Id + "\"]}")
            .AllDevicesOnly(ProvisioningMethods.GetRun, "{\"deviceId\":\"" + inside.Id + "\"}")
            .AllDevicesOnly(ProvisioningMethods.StoreSecret, """{"value":"x"}""")
            .AllDevicesOnly(ProvisioningMethods.StoreFile, """{"uploadId":"0123456789abcdef0123456789abcdef"}""");
        await canary.RunAsync();

        // Whether it is on: the Add devices page of any user, also with a device group.
        var group = _ctx.CallAsGroup([inside.Id]);
        Assert.False(_plugin.RequiresAllDevices(ProvisioningMethods.State));
        Assert.Null(_plugin.RequiredPermission(ProvisioningMethods.State));
        Assert.False(PluginJson.Parse<StateReply>(await _ctx.InvokeAsync(_plugin, group, ProvisioningMethods.State, null), ProvisioningJson.Options).On);

        Assert.All(_plugin.Permissions, p => Assert.False(p.OperatorDefault));
        Assert.Equal(PermissionKeys.Show, _plugin.RequiredPermission(ProvisioningMethods.GetStatus));
        Assert.Equal(PermissionKeys.Manage, _plugin.RequiredPermission(ProvisioningMethods.TurnOn));
        Assert.Equal(PermissionKeys.Manage, _plugin.RequiredPermission(ProvisioningMethods.RunAgain));
        Assert.Equal(UserRole.Admin, _plugin.RequiredRole("tool.turn_on"));
        Assert.True(_plugin.IsAudited(ProvisioningMethods.TurnOn));
        Assert.True(_plugin.IsAudited(ProvisioningMethods.TurnOff));
        Assert.False(_plugin.IsAudited("tool.turn_on.preview"));
        Assert.False(_plugin.HasPage);
        var task = Assert.Single(_plugin.TaskPlugins);
        Assert.Equal(ProvisioningInfo.TaskPluginId, task.Id);
        Assert.False(task.ShowInMenus);
        Assert.Equal(8, task.MaxParallelDevices);
        Assert.Equal("Provision with Lobby", task.GetTaskName(JsonSerializer.Serialize(new RunPayload { Profile = NewProfile() }, ProvisioningJson.Options)));
    }

    [Fact]
    public async Task The_tools_cover_the_dialog_and_a_preview_changes_nothing()
    {
        var names = _plugin.Tools.Select(t => t.Name).ToList();
        Assert.Equal(["status", "profiles", "profile", "credentials", "vapix_commands", "camera", "profile_save", "secret_store", "file_store", "profile_delete", "profile_activate", "turn_on", "turn_off", "run_again"], names);
        Assert.All(_plugin.Tools, t => Assert.True(t.Name.Length <= PluginTool.MaxNameLength));

        var saved = await CallAsync<ProfileReply>(ProvisioningMethods.SaveProfile, new SaveProfileRequest(NewProfile(), Password));
        await CallAsync<StatusReply>(ProvisioningMethods.ActivateProfile, new IdRequest(saved.Profile.Id));

        var preview = await _ctx.InvokeAsync(_plugin, _admin, "tool.turn_on.preview", "{}");
        Assert.Contains("Lobby", preview, StringComparison.Ordinal);
        Assert.False(_plugin.Service!.IsOn);

        var savePreview = await _ctx.InvokeAsync(_plugin, _admin, "tool.profile_save.preview", """{"name":"Gate","nodes":[{"id":"set-password","kind":"set_password","settings":"{\"credential_id\":\"00000000-0000-0000-0000-000000000001\"}"},{"id":"net","kind":"network","settings":"{\"ip\":\"keep\"}"}]}""");
        Assert.Contains("\"nodes_with_errors\":2", savePreview, StringComparison.Ordinal);
        Assert.Single((await CallAsync<ProfilesReply>(ProvisioningMethods.ListProfiles)).Profiles);

        // The settings go both ways as JSON text in snake_case (a tool's schema has no value of any type).
        var profile = await _ctx.InvokeAsync(_plugin, _admin, "tool.profile", "{\"id\":\"" + saved.Profile.Id + "\"}");
        Assert.Contains("credential_id", profile, StringComparison.Ordinal);
        var refused = await _ctx.InvokeAsync(_plugin, _admin, "tool.profile_save.preview", """{"name":"Gate","nodes":[{"id":"set-password","kind":"set_password","settings":"[1]"}]}""");
        Assert.Contains("JSON object", refused, StringComparison.Ordinal);

        var status = await _ctx.InvokeAsync(_plugin, _admin, "tool.status", "{}");
        Assert.Contains("\"on\":false", status, StringComparison.Ordinal);
        Assert.Contains("\"active_profile\":\"Lobby\"", status, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, status + preview + savePreview, StringComparison.Ordinal);

        var nothing = await _ctx.InvokeAsync(_plugin, _admin, "tool.run_again.preview", "{\"device_ids\":[\"" + Guid.NewGuid() + "\"]}");
        Assert.Contains("None of these cameras has a failed provisioning task to run again.", nothing, StringComparison.Ordinal);
    }
}
