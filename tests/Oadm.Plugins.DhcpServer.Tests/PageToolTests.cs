using System.Text.Json.Nodes;

using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.DhcpServer.Leases;
using Oadm.Plugins.DhcpServer.Tools;
using Oadm.Sdk.Plugins;

using Testing = Oadm.Sdk.Testing;

namespace Oadm.Plugins.DhcpServer.Tests;

/// <summary>
/// The page's lease actions as tools (docs/spec/mcp.md "Plugins", decision 2026-11-15): previews with the page's checks that
/// change nothing, runs through the page's methods, the page's rights (Administrator, "manage", all devices) and texts.
/// </summary>
public sealed class PageToolTests
{
    private const string Cam = "AC:CC:8E:5F:60:71";

    private static async Task<(Testing.FakeCoreContext Ctx, DhcpServerPlugin Plugin, FakeTimeProvider Time)> StartAsync()
    {
        var ctx = new Testing.FakeCoreContext();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero));
        var plugin = new DhcpServerPlugin(Options.Test(new FakeDhcpNetwork(), time: time));
        await plugin.StartAsync(ctx, CancellationToken.None);
        return (ctx, plugin, time);
    }

    private static JsonNode Answer(string? json) => JsonNode.Parse(json!)!;

    private static string State(DhcpServerPlugin plugin) => DhcpJson.Serialize(plugin.Service!.GetState(full: true));

    private static void Bind(DhcpServerPlugin plugin, FakeTimeProvider time, string mac, string address)
    {
        var now = time.GetUtcNow().UtcDateTime;
        Assert.True(plugin.Service!.Leases.TryBind(Mac.Of(mac), Ip.Of(address), new AddressPool(Ip.Of("10.0.0.100"), Ip.Of("10.0.0.200"), new HashSet<uint>()), now, now.AddHours(24), "cam", allowNew: true));
    }

    [Fact]
    public void The_tools_have_the_rights_of_the_page_methods_they_wrap()
    {
        ICorePlugin core = new DhcpServerPlugin();
        foreach (var (tool, page) in new[]
                 {
                     ("static_lease_save", DhcpServerMethods.SaveStatic), ("static_lease_delete", DhcpServerMethods.DeleteStatic),
                     ("lease_make_static", DhcpServerMethods.MakeStatic), ("lease_release", DhcpServerMethods.Release),
                 })
        {
            var run = "tool." + tool;
            Assert.Equal(page, DhcpPageTools.Set.PageMethodOf(run));
            Assert.Equal(UserRole.Admin, core.RequiredRole(run));
            Assert.Equal(UserRole.Admin, core.RequiredRole(run + ".preview"));
            Assert.Equal(core.RequiredPermission(page), core.RequiredPermission(run));
            Assert.True(core.RequiresAllDevices(run));
            Assert.True(core.RequiresAllDevices(run + ".preview"));
            Assert.True(core.IsAudited(run));
            Assert.False(core.IsAudited(run + ".preview"));
        }

        Assert.True(DhcpPageTools.Set.ToolOf("tool.static_lease_delete")!.Destructive);
        Assert.True(DhcpPageTools.Set.ToolOf("tool.lease_release")!.Destructive);
        Assert.False(DhcpPageTools.Set.ToolOf("tool.static_lease_save")!.Destructive);
    }

    [Fact]
    public async Task Save_previews_with_the_page_checks_writes_nothing_and_the_run_saves_once()
    {
        var (ctx, plugin, _) = await StartAsync();
        await using var _ = plugin;
        var admin = ctx.CallAsAllDevices();
        var payload = """{"mac":"b8-a4-4f-63-13-39","address":"10.0.0.148","name":"Lobby"}""";
        var before = State(plugin);

        var plan = Answer(await ctx.InvokeAsync(plugin, admin, "tool.static_lease_save.preview", payload));
        Assert.Equal("add", plan["change"]!.GetValue<string>());
        Assert.Equal("B8:A4:4F:63:13:39", plan["mac"]!.GetValue<string>());
        Assert.Equal(1, plan["will_change"]!.GetValue<int>());
        Assert.Equal("static lease", plan["unit"]!.GetValue<string>());
        Assert.Equal(before, State(plugin));

        Assert.Equal("B8:A4:4F:63:13:39", Answer(await ctx.InvokeAsync(plugin, admin, "tool.static_lease_save", payload))["mac"]!.GetValue<string>());
        var lease = plugin.Service!.FindLease("B8:A4:4F:63:13:39")!;
        Assert.Equal(("10.0.0.148", "Lobby", true), (lease.Address, lease.Name, lease.IsStatic));

        // The same lease again: the page's duplicate text, for the preview and the run.
        foreach (var method in new[] { "tool.static_lease_save.preview", "tool.static_lease_save" })
        {
            var again = Answer(await ctx.InvokeAsync(plugin, admin, method, payload));
            Assert.Equal("mac", again["errors"]![0]!["field"]!.GetValue<string>());
            Assert.Equal("This device already has a static lease (10.0.0.148).", again["errors"]![0]!["error"]!.GetValue<string>());
        }

        // Another device on that address, and an edit of it.
        var taken = Answer(await ctx.InvokeAsync(plugin, admin, "tool.static_lease_save.preview", """{"mac":"AC:CC:8E:00:00:02","address":"10.0.0.148"}"""));
        Assert.Equal("Already reserved for B8:A4:4F:63:13:39.", taken["errors"]![0]!["error"]!.GetValue<string>());
        var edit = Answer(await ctx.InvokeAsync(plugin, admin, "tool.static_lease_save.preview", """{"mac":"B8:A4:4F:63:13:39","address":"10.0.0.149","original_mac":"B8:A4:4F:63:13:39"}"""));
        Assert.Equal("edit", edit["change"]!.GetValue<string>());
    }

    [Fact]
    public async Task Invalid_input_gets_the_page_texts_under_the_fields()
    {
        var (ctx, plugin, _) = await StartAsync();
        await using var _ = plugin;
        var admin = ctx.CallAsAllDevices();
        var before = State(plugin);

        var bad = Answer(await ctx.InvokeAsync(plugin, admin, "tool.static_lease_save.preview", $$"""{"mac":"B8:A4","address":"10.0.0.x","name":"{{new string('x', 64)}}"}"""));
        var errors = bad["errors"]!.AsArray().ToDictionary(e => e!["field"]!.GetValue<string>(), e => e!["error"]!.GetValue<string>());
        Assert.Equal(DhcpPageTools.EnterMac, errors["mac"]);
        Assert.Equal("Enter an IPv4 address, e.g. 10.0.0.100.", errors["address"]);
        Assert.Equal("At most 63 characters.", errors["name"]);
        Assert.Equal("mac", Answer(await ctx.InvokeAsync(plugin, admin, "tool.lease_release.preview", """{"mac":"nope"}"""))["errors"]![0]!["field"]!.GetValue<string>());
        Assert.Equal("original_mac", Answer(await ctx.InvokeAsync(plugin, admin, "tool.static_lease_save.preview", """{"mac":"B8:A4:4F:63:13:39","address":"10.0.0.5","original_mac":"x"}"""))["errors"]![0]!["field"]!.GetValue<string>());
        Assert.Equal(before, State(plugin));
    }

    [Fact]
    public async Task Make_static_release_and_delete_check_the_lease_first_and_run_once()
    {
        var (ctx, plugin, time) = await StartAsync();
        await using var _ = plugin;
        var admin = ctx.CallAsAllDevices();
        Bind(plugin, time, Cam, "10.0.0.100");
        Bind(plugin, time, "AC:CC:8E:5F:60:72", "10.0.0.101");
        var before = State(plugin);

        // Previews: the lease and its state, nothing changed.
        var make = Answer(await ctx.InvokeAsync(plugin, admin, "tool.lease_make_static.preview", $$"""{"mac":"{{Cam}}"}"""));
        Assert.Equal("10.0.0.100", make["address"]!.GetValue<string>());
        Assert.Equal("lease", make["unit"]!.GetValue<string>());
        Assert.Equal("Active", make["state"]!.GetValue<string>());
        await ctx.InvokeAsync(plugin, admin, "tool.lease_release.preview", """{"mac":"AC:CC:8E:5F:60:72"}""");
        var notStatic = await Assert.ThrowsAsync<KeyNotFoundException>(() => ctx.InvokeAsync(plugin, admin, "tool.static_lease_delete.preview", $$"""{"mac":"{{Cam}}"}"""));
        Assert.Equal(DhcpPageTools.StaticGone, notStatic.Message);
        Assert.Equal(before, State(plugin));

        await ctx.InvokeAsync(plugin, admin, "tool.lease_make_static", $$"""{"mac":"{{Cam}}"}""");
        Assert.True(plugin.Service!.FindLease(Cam)!.IsStatic);
        var twice = await Assert.ThrowsAsync<KeyNotFoundException>(() => ctx.InvokeAsync(plugin, admin, "tool.lease_make_static", $$"""{"mac":"{{Cam}}"}"""));
        Assert.Equal(DhcpPageTools.DynamicGone, twice.Message);

        await ctx.InvokeAsync(plugin, admin, "tool.lease_release", """{"mac":"AC:CC:8E:5F:60:72"}""");
        Assert.Null(plugin.Service.FindLease("AC:CC:8E:5F:60:72"));
        var released = await Assert.ThrowsAsync<KeyNotFoundException>(() => ctx.InvokeAsync(plugin, admin, "tool.lease_release", """{"mac":"AC:CC:8E:5F:60:72"}"""));
        Assert.Equal(DhcpPageTools.LeaseGone, released.Message);

        await ctx.InvokeAsync(plugin, admin, "tool.static_lease_delete", $$"""{"mac":"{{Cam}}"}""");
        Assert.Null(plugin.Service.FindLease(Cam));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => ctx.InvokeAsync(plugin, admin, "tool.static_lease_delete", $$"""{"mac":"{{Cam}}"}"""));
    }

    [Fact]
    public async Task A_group_user_changes_no_lease()
    {
        var (ctx, plugin, time) = await StartAsync();
        await using var _ = plugin;
        var device = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.48" });
        var group = ctx.CallAsGroup([device.Id], "guard");
        Bind(plugin, time, Cam, "10.0.0.100");
        var before = State(plugin);
        foreach (var (method, payload) in new[]
                 {
                     ("tool.static_lease_save.preview", """{"mac":"B8:A4:4F:63:13:39","address":"10.0.0.148"}"""),
                     ("tool.static_lease_save", """{"mac":"B8:A4:4F:63:13:39","address":"10.0.0.148"}"""),
                     ("tool.lease_release", $$"""{"mac":"{{Cam}}"}"""),
                     ("tool.lease_make_static.preview", $$"""{"mac":"{{Cam}}"}"""),
                     ("tool.settings_save.preview", """{"enabled":false}"""),
                     ("tool.settings_save", """{"enabled":false}"""),
                 })
        {
            await Assert.ThrowsAsync<DeviceAccessRefusedException>(() => ctx.InvokeAsync(plugin, group, method, payload));
        }

        Assert.Equal(before, State(plugin));
    }

    [Fact]
    public void Settings_save_has_the_rights_of_the_page_save()
    {
        ICorePlugin core = new DhcpServerPlugin();
        Assert.Equal(DhcpServerMethods.Save, DhcpPageTools.Set.PageMethodOf("tool.settings_save"));
        Assert.Equal(UserRole.Admin, core.RequiredRole("tool.settings_save.preview"));
        Assert.Equal(core.RequiredPermission(DhcpServerMethods.Save), core.RequiredPermission("tool.settings_save"));
        Assert.True(core.RequiresAllDevices("tool.settings_save"));
        Assert.True(core.IsAudited("tool.settings_save"));
        Assert.False(core.IsAudited("tool.settings_save.preview"));
    }

    [Fact]
    public async Task Settings_save_previews_with_the_warnings_writes_nothing_and_the_run_turns_the_server_on_once()
    {
        var (ctx, plugin, _) = await StartAsync();
        await using var _ = plugin;
        var admin = ctx.CallAsAllDevices();
        var payload = """{"enabled":true,"interface":"Ethernet","range_start":"10.0.0.100","range_end":"10.0.0.199","auto_add_axis_devices":true,"lease_hours":12}""";
        var before = State(plugin);

        var plan = Answer(await ctx.InvokeAsync(plugin, admin, "tool.settings_save.preview", payload));
        Assert.Equal(("turn_on", "Ethernet", "DHCP server"), (plan["change"]!.GetValue<string>(), plan["interface"]!.GetValue<string>(), plan["unit"]!.GetValue<string>()));
        var warnings = plan["warnings"]!.AsArray().Select(w => w!.GetValue<string>()).ToList();
        Assert.Contains(DhcpPageTools.HandsOutAddresses, warnings);
        Assert.Contains(DhcpPageTools.AddsDevices, warnings);
        Assert.Equal(12, plan["lease_hours"]!.GetValue<int>());
        Assert.Equal(before, State(plugin));
        Assert.False(plugin.Service!.Config.Enabled);

        var done = Answer(await ctx.InvokeAsync(plugin, admin, "tool.settings_save", payload));
        Assert.True(done["enabled"]!.GetValue<bool>());
        var config = plugin.Service.Config;
        Assert.Equal((true, "eth-id", "10.0.0.100", "10.0.0.199", true, 12), (config.Enabled, config.InterfaceId, config.RangeStart, config.RangeEnd, config.AutoAddAxisDevices, config.LeaseHours));

        // A change keeps the fields it leaves out; turning off says what the devices keep.
        var off = Answer(await ctx.InvokeAsync(plugin, admin, "tool.settings_save.preview", """{"enabled":false}"""));
        Assert.Equal("turn_off", off["change"]!.GetValue<string>());
        Assert.Equal([DhcpPageTools.LeasesStay], off["warnings"]!.AsArray().Select(w => w!.GetValue<string>()));
        Assert.Equal("10.0.0.100", off["range_start"]!.GetValue<string>());
        Assert.Equal(12, off["lease_hours"]!.GetValue<int>());
        await ctx.InvokeAsync(plugin, admin, "tool.settings_save", """{"enabled":false}""");
        Assert.Equal((false, "10.0.0.100"), (plugin.Service.Config.Enabled, plugin.Service.Config.RangeStart));
    }

    [Fact]
    public async Task Settings_save_answers_the_page_texts_under_the_fields_and_changes_nothing()
    {
        var (ctx, plugin, _) = await StartAsync();
        await using var _ = plugin;
        var admin = ctx.CallAsAllDevices();
        var before = State(plugin);

        var nic = Answer(await ctx.InvokeAsync(plugin, admin, "tool.settings_save.preview", """{"enabled":true,"interface":"Nowhere"}"""));
        Assert.Equal(("interface", DhcpPageTools.NoInterface), (nic["errors"]![0]!["field"]!.GetValue<string>(), nic["errors"]![0]!["error"]!.GetValue<string>()));

        var range = Answer(await ctx.InvokeAsync(plugin, admin, "tool.settings_save", """{"enabled":true,"interface":"eth-id","range_start":"10.0.0.x","range_end":"192.168.0.1"}"""));
        var fields = range["errors"]!.AsArray().Select(e => e!["field"]!.GetValue<string>()).ToList();
        Assert.Contains("range_start", fields);
        Assert.Contains("range_end", fields);

        var lease = Answer(await ctx.InvokeAsync(plugin, admin, "tool.settings_save.preview", """{"lease_hours":0}"""));
        Assert.Equal(("lease_hours", "Enter 1 to 720 hours."), (lease["errors"]![0]!["field"]!.GetValue<string>(), lease["errors"]![0]!["error"]!.GetValue<string>()));
        Assert.Equal(before, State(plugin));
    }
}
