using System.Diagnostics;
using System.Globalization;

using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.Radius.Api;
using Oadm.Plugins.Radius.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Testing;

using Xunit.Abstractions;

namespace Oadm.Plugins.Radius.Tests.Client;

/// <summary>
/// The page at its limits (CLAUDE.md "scale to thousands of devices"): 10,000 open sessions with batches of events every
/// 500 ms, the ended rows leaving in one step, a log page of 500 rows with live rows, the VLAN tag choices from 30,000
/// devices. Budgets about 10x the measured time; the times go to the test output.
/// </summary>
[Trait("Category", "Perf")]
public sealed class PagePerfTests(ITestOutputHelper output)
{
    private const int Sessions = RadiusPageLimits.MaxSessions;
    private const int Devices = 30_000;

    [Fact]
    public async Task Ten_thousand_sessions_load_update_in_batches_and_leave_in_one_step()
    {
        var server = new FakeRadiusBackend();
        var now = DateTime.UtcNow;
        server.Sessions.Clear();
        server.Sessions.AddRange(Enumerable.Range(0, Sessions).Select(i => FakeRadiusBackend.Session(now.AddMilliseconds(-i), Mac(i), "startingTls", "Starting TLS", guess: true)));
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var ctx = new FakePageContext(server);
        using var vm = new RadiusViewModel(ctx, time);
        var resets = 0;
        vm.Sessions.Rows.CollectionChanged += (_, e) => resets += e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset ? 1 : 0;

        var watch = Stopwatch.StartNew();
        await vm.Sessions.LoadAsync(CancellationToken.None);
        var load = watch.Elapsed;
        Assert.Equal(Sessions, vm.Sessions.Rows.Count);
        Assert.Equal(1, resets);

        // 20 batches of 500 changed rows each (a power cut: everyone moves on), then every session ends.
        watch.Restart();
        for (var b = 0; b < 20; b++)
        {
            vm.Sessions.Apply([.. server.Sessions.Skip(b * 500).Take(500).Select(s => s with { State = "receivingClientCertificate", StateText = "Receiving client certificate: fragment 3 of 5", Fragment = new FragmentInfo(3, 5) })]);
        }

        var batches = watch.Elapsed;
        Assert.Equal("10,000 sessions in progress", vm.Sessions.CountText);
        watch.Restart();
        vm.Sessions.Apply([.. server.Sessions.Select(s => s with { State = SessionStates.Authenticated, StateText = "Authenticated" })]);
        var ended = watch.Elapsed;
        Assert.Equal("0 sessions in progress", vm.Sessions.CountText);
        time.Advance(TimeSpan.FromSeconds(11));
        watch.Restart();
        vm.Sessions.Tick();
        var leave = watch.Elapsed;
        Assert.Empty(vm.Sessions.Rows);
        Assert.Equal(2, resets); // the load and the one removal

        output.WriteLine($"10,000 sessions: load {load.TotalMilliseconds:F0} ms, 20 batches of 500 {batches.TotalMilliseconds:F0} ms, all ended {ended.TotalMilliseconds:F0} ms, leave {leave.TotalMilliseconds:F0} ms");
        Assert.True(load < TimeSpan.FromSeconds(3), $"load took {load.TotalMilliseconds:F0} ms");
        Assert.True(batches < TimeSpan.FromSeconds(1), $"batches took {batches.TotalMilliseconds:F0} ms");
        Assert.True(ended < TimeSpan.FromSeconds(1), $"ending took {ended.TotalMilliseconds:F0} ms");
        Assert.True(leave < TimeSpan.FromSeconds(1), $"leaving took {leave.TotalMilliseconds:F0} ms");
    }

    [Fact]
    public async Task The_cards_and_the_policy_editor_with_30000_devices_and_a_full_log_page_stay_fast()
    {
        var server = new FakeRadiusBackend();
        var now = DateTime.UtcNow;
        server.Log = [.. Enumerable.Range(0, 50_000).Select(i => FakeRadiusBackend.LogEntry(now.AddSeconds(-i), Mac(i), i % 7 == 0 ? LogResults.Failed : LogResults.Authenticated, i % 7 == 0 ? "Client certificate expired on 2026-09-30" : null, null, 20, "10.1." + (i / 250).ToString(CultureInfo.InvariantCulture) + "." + (i % 250).ToString(CultureInfo.InvariantCulture)))];
        var devices = Enumerable.Range(0, Devices).Select(i => (IDeviceInfo)new FakeDevice
        {
            Serial = Mac(i).Replace(":", string.Empty, StringComparison.Ordinal),
            Address = "10.2." + (i / 250).ToString(CultureInfo.InvariantCulture) + "." + (i % 250).ToString(CultureInfo.InvariantCulture),
            Tags = ["Building " + (i % 40).ToString(CultureInfo.InvariantCulture), i % 2 == 0 ? "Outdoor" : "Indoor"],
        }).ToList();
        var ctx = new FakePageContext(server) { DeviceList = devices };
        using var vm = new RadiusViewModel(ctx);

        var watch = Stopwatch.StartNew();
        await vm.LoadServerAsync(CancellationToken.None);
        var serverTab = watch.Elapsed;

        // The policy editor: the group picker over every device with the tags of the devices, the policies and the server.
        watch.Restart();
        var editor = vm.Policies.NewEditor(null);
        editor.Picker.Tags.First(t => t.Name == "Outdoor").IsChecked = true;
        var canSave = editor.SaveBlockedReason;
        var policyEditor = watch.Elapsed;
        Assert.Equal(44, editor.Picker.Tags.Count); // 40 buildings, Outdoor, Indoor, PTZ, Lobby
        Assert.Equal(Devices / 2, editor.Picker.CoveredCount);
        Assert.Equal(RadiusRules.EnterName, canSave);

        watch.Restart();
        await vm.Log.LoadAsync(CancellationToken.None);
        var logPage = watch.Elapsed;
        Assert.Equal((500, "1-500 of 50,000"), (vm.Log.Rows.Count, vm.Log.PageText));

        watch.Restart();
        for (var b = 0; b < 20; b++)
        {
            vm.Log.Add([.. Enumerable.Range(0, 50).Select(i => FakeRadiusBackend.LogEntry(now.AddSeconds(b), Mac(i), LogResults.Authenticated, null, null, 20, null))]);
        }

        var live = watch.Elapsed;
        Assert.Equal(500, vm.Log.Rows.Count);

        output.WriteLine($"30,000 devices: cards {serverTab.TotalMilliseconds:F0} ms; policy editor with a tag {policyEditor.TotalMilliseconds:F0} ms; log page of 500 of 50,000 {logPage.TotalMilliseconds:F0} ms; 20 live batches of 50 {live.TotalMilliseconds:F0} ms");
        Assert.True(serverTab < TimeSpan.FromSeconds(1), $"the cards took {serverTab.TotalMilliseconds:F0} ms");
        Assert.True(policyEditor < TimeSpan.FromSeconds(3), $"the policy editor took {policyEditor.TotalMilliseconds:F0} ms");
        Assert.True(logPage < TimeSpan.FromSeconds(1), $"log page took {logPage.TotalMilliseconds:F0} ms");
        Assert.True(live < TimeSpan.FromSeconds(1), $"live rows took {live.TotalMilliseconds:F0} ms");
    }

    private static string Mac(int i) => "AC:CC:8E:" + string.Join(':', BitConverter.GetBytes(i).Take(3).Reverse().Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
}
