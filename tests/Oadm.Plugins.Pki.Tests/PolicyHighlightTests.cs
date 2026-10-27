using Avalonia.Controls;
using Avalonia.VisualTree;

using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.Pki.Client;
using Oadm.Plugins.Pki.Policies;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>
/// A jump from a server tab to the Policies tab (decision 2026-10-26, item 11): the policies of that server, or one policy,
/// are highlighted for 3 seconds; before the first read it waits for the policies, a reload keeps it, a search that would hide
/// one of them is cleared, a later jump starts its own 3 seconds.
/// </summary>
public sealed class PolicyHighlightTests
{
    private static string[] Highlighted(PoliciesViewModel page) => [.. page.Rows.Where(r => r.IsHighlighted).Select(r => r.Name)];

    private static async Task<(PkiHarness Pki, PoliciesViewModel Page, FakeTimeProvider Time)> StartAsync()
    {
        var pki = await PolicyPageTests.StartAsync();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        return (pki, new PoliciesViewModel(PolicyPageTests.Page(pki), time), time);
    }

    [Fact]
    public void Each_jump_names_the_policies_of_its_server_or_one_policy()
    {
        var id = Guid.NewGuid();
        PkiPolicy Of(string kind, Guid? policy = null) => new() { Id = policy ?? Guid.NewGuid(), Name = kind, Kind = kind };

        Assert.All(new[] { PolicyKinds.Dot1xServer, PolicyKinds.AllowCa, PolicyKinds.Vlan, PolicyKinds.Block }, k => Assert.True(PolicyHighlight.Dot1x.Matches(Of(k)), k));
        Assert.All(new[] { PolicyKinds.EstServer, PolicyKinds.Enrollment, PolicyKinds.SignInCa }, k => Assert.False(PolicyHighlight.Dot1x.Matches(Of(k)), k));
        Assert.All(new[] { PolicyKinds.EstServer, PolicyKinds.Enrollment, PolicyKinds.SignInCa }, k => Assert.True(PolicyHighlight.Est.Matches(Of(k)), k));
        Assert.All(new[] { PolicyKinds.HttpsCertificate, PolicyKinds.Dot1xCertificate, PolicyKinds.ExpiryWarning, PolicyKinds.AllowCa },
            k => Assert.False(PolicyHighlight.Est.Matches(Of(k)), k));
        Assert.True(PolicyHighlight.Policy(id).Matches(Of(PolicyKinds.Vlan, id)));
        Assert.False(PolicyHighlight.Policy(id).Matches(Of(PolicyKinds.Vlan)));
        Assert.Equal(TimeSpan.FromSeconds(3), PoliciesViewModel.HighlightTime);
    }

    [Fact]
    public async Task A_jump_before_the_first_read_highlights_the_8021x_policies_once_they_are_there_for_3_seconds()
    {
        var (pki, page, time) = await StartAsync();
        await using var _ = pki;
        PolicyRowViewModel? shown = null;
        page.HighlightShown += (_, row) => shown = row;

        page.Highlight(PolicyHighlight.Dot1x);
        Assert.Empty(page.Rows); // nothing read yet: it waits
        await page.LoadAsync(CancellationToken.None);

        Assert.Equal(["802.1X server", "OADM Root CA SERVER01", "Axis device ID", "Building B", "Storage room"], Highlighted(page));
        Assert.All(page.Rows.Where(r => !r.IsHighlighted), r => Assert.False(PolicyKinds.IsDot1x(r.Source.Kind)));
        Assert.Equal("802.1X server", shown?.Name); // the first one is scrolled into view

        time.Advance(TimeSpan.FromSeconds(2.9));
        Assert.Equal(5, Highlighted(page).Length);
        time.Advance(TimeSpan.FromSeconds(0.2));
        await Eventually.TrueAsync(() => Highlighted(page).Length == 0, "the highlight ends after 3 seconds");
    }

    [Fact]
    public async Task The_est_jump_highlights_only_the_est_policies_and_a_reload_keeps_them_highlighted()
    {
        var (pki, page, time) = await StartAsync();
        await using var _ = pki;
        await page.LoadAsync(CancellationToken.None);

        page.Highlight(PolicyHighlight.Est);
        Assert.Equal(["EST server"], Highlighted(page)); // EST not converted here: only its server policy

        // A change from elsewhere reads the policies again: the new rows stay highlighted until the 3 seconds end.
        time.Advance(TimeSpan.FromSeconds(1));
        await pki.InvokeAsync<PolicySaveReply>(PolicyMethods.SavePolicy, new PkiPolicy { Name = "Lobby", Kind = PolicyKinds.Vlan, Vlan = 40, Devices = new PolicyDevices { Serials = ["ACCC8E000002"] } });
        await page.LoadAsync(CancellationToken.None);
        Assert.Contains(page.Rows, r => r.Name == "Lobby");
        Assert.Equal(["EST server"], Highlighted(page));

        time.Advance(TimeSpan.FromSeconds(2.1));
        await Eventually.TrueAsync(() => Highlighted(page).Length == 0, "the highlight ends 3 seconds after the jump, not after the reload");
    }

    [Fact]
    public async Task A_jump_to_one_policy_clears_a_search_that_hides_it_and_a_later_jump_starts_its_own_time()
    {
        var (pki, page, time) = await StartAsync();
        await using var _ = pki;
        await page.LoadAsync(CancellationToken.None);
        var storage = page.Rows.Single(r => r.Name == "Storage room").Source;

        page.SearchText = "vlan";
        Assert.DoesNotContain(page.Rows, r => r.Name == "Storage room");
        page.Highlight(PolicyHighlight.Policy(storage.Id));
        Assert.Null(page.SearchText);
        Assert.Equal(["Storage room"], Highlighted(page));

        // A search that keeps it shown stays.
        page.SearchText = "storage";
        page.Highlight(PolicyHighlight.Policy(storage.Id));
        Assert.Equal("storage", page.SearchText);

        time.Advance(TimeSpan.FromSeconds(2));
        page.SearchText = null;
        page.Highlight(PolicyHighlight.Dot1x); // another jump: its own 3 seconds
        time.Advance(TimeSpan.FromSeconds(1.5));
        Assert.Equal(5, Highlighted(page).Length);
        time.Advance(TimeSpan.FromSeconds(1.6));
        await Eventually.TrueAsync(() => Highlighted(page).Length == 0, "the second highlight ends");
    }
}

/// <summary>
/// The jump through the page (PkiTabRequest, process-wide, so in the headless collection with the other tab requests) and
/// the policy group headers with their icons (ui:GridGroup, decision 2026-10-26 item 12).
/// </summary>
[Collection(HeadlessSessions.Name)]
public sealed class PolicyHighlightPageTests
{
    [Fact]
    public async Task Edit_policies_of_a_server_tab_opens_policies_with_its_highlight_taken_once()
    {
        await using var pki = await PolicyPageTests.StartAsync();
        var ctx = PolicyPageTests.Page(pki);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var vm = new PkiViewModel(ctx, new FakeClientTrust(), time: time);

        PkiTabRequest.Set(PkiViewModel.PoliciesTabId, PolicyHighlight.Dot1x);
        ctx.RequestTab(PkiPluginInfo.PluginId);
        Assert.True(vm.IsPoliciesTab);
        Assert.Null(PkiTabRequest.TakeHighlight()); // taken with the tab
        await vm.Policies.LoadAsync(CancellationToken.None);
        Assert.Equal(5, vm.Policies.Rows.Count(r => r.IsHighlighted));

        // A request without a highlight (Set alone) drops one left before.
        PkiTabRequest.Set(PkiViewModel.PoliciesTabId, PolicyHighlight.Est);
        PkiTabRequest.Set(PkiViewModel.PoliciesTabId);
        Assert.Null(PkiTabRequest.TakeHighlight());
        Assert.Equal(PkiViewModel.PoliciesTabId, PkiTabRequest.Take());
    }

    [Fact]
    public void The_policy_groups_carry_an_icon_key_and_plain_keys_have_none()
    {
        Assert.Equal(
            [("key", PolicyTableGroups.Issuing), ("server", PolicyTableGroups.Servers), ("network", PolicyTableGroups.Access), ("download", PolicyTableGroups.Enrollment), ("bell", PolicyTableGroups.Expiry)],
            new[] { PolicyTableGroups.Issuing, PolicyTableGroups.Servers, PolicyTableGroups.Access, PolicyTableGroups.Enrollment, PolicyTableGroups.Expiry }
                .Select(g => (PolicyTableGroups.KeyOf(g).IconKey!, PolicyTableGroups.KeyOf(g).Name)));
        Assert.Equal(PolicyTableGroups.KeyOf(PolicyTableGroups.Access), new GridGroup(PolicyTableGroups.Access, "network")); // one header per group
        Assert.Equal(PolicyTableGroups.Access, PolicyTableGroups.KeyOf(PolicyTableGroups.Access).ToString()); // the header's text

        // A plain string key (the Plugins page) and a group without an icon show no icon.
        Assert.Equal(false, GridGroup.HasIcon.Convert("Servers", typeof(bool), null, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Null(GridGroup.IconOf.Convert("Servers", typeof(object), null, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(false, GridGroup.HasIcon.Convert(new GridGroup("Other"), typeof(bool), null, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(true, GridGroup.HasIcon.Convert(PolicyTableGroups.KeyOf(PolicyTableGroups.Issuing), typeof(bool), null, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task The_group_headers_show_their_icon_and_highlighted_rows_get_their_class()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        await using var pki = await EstPolicyPageTests.StartAsync();
        var ctx = new PluginPageContext(pki.Plugin, pki.Hub) { DeviceList = [PolicyPageTests.Camera, PolicyPageTests.Other] };
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);

        var checks = await HeadlessEntry.Session.Dispatch(async () =>
        {
            var view = new PkiView();
            using var vm = new PkiViewModel(ctx, new FakeClientTrust(), time: time);
            var window = HeadlessPageTests.Host(view, 1700, 1100);
            window.Show();
            view.DataContext = vm;
            await vm.LoadAsync();
            await vm.Policies.LoadAsync(CancellationToken.None);
            vm.ShowTab(PkiViewModel.PoliciesTabId);
            HeadlessPageTests.Pump();
            vm.Policies.Highlight(PolicyHighlight.Est);
            HeadlessPageTests.Pump();
            HeadlessPageTests.Capture(window, outDir, "pki-policies-highlight.png");

            var grid = view.FindControl<DataGrid>("PoliciesGrid")!;
            var headers = grid.GetVisualDescendants().OfType<DataGridRowGroupHeader>().Where(h => h.IsEffectivelyVisible).Select(h =>
            {
                var icon = h.GetVisualDescendants().OfType<OadmIcon>().Single(i => i.Name != "PART_Chevron");
                var name = h.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("groupHeader")).Text;
                return (Name: name, icon.IsEffectivelyVisible, Matches: icon.Data is not null && ReferenceEquals(icon.Data, ToolbarButton.ResolveIcon(PolicyTableGroups.IconOf(name!))));
            }).ToList();
            var rows = grid.GetVisualDescendants().OfType<DataGridRow>().Where(r => r.IsEffectivelyVisible && r.DataContext is PolicyRowViewModel)
                .Select(r => (((PolicyRowViewModel)r.DataContext!).IsHighlighted, r.Classes.Contains("highlight"))).ToList();

            time.Advance(PoliciesViewModel.HighlightTime);
            await Eventually.TrueAsync(() => vm.Policies.Rows.All(r => !r.IsHighlighted), "the highlight ends");
            HeadlessPageTests.Pump();
            var after = grid.GetVisualDescendants().OfType<DataGridRow>().Count(r => r.Classes.Contains("highlight"));
            window.Close();
            return (headers, rows, after);
        }, CancellationToken.None);

        Assert.Equal(new[] { PolicyTableGroups.Issuing, PolicyTableGroups.Servers, PolicyTableGroups.Access, PolicyTableGroups.Enrollment, PolicyTableGroups.Expiry }.Order(StringComparer.Ordinal),
            checks.headers.Select(h => h.Name!).Order(StringComparer.Ordinal));
        Assert.All(checks.headers, h => Assert.True(h.IsEffectivelyVisible && h.Matches, h.Name));
        Assert.Contains(checks.rows, r => r.IsHighlighted);
        Assert.All(checks.rows, r => Assert.Equal(r.IsHighlighted, r.Item2));
        Assert.Equal(0, checks.after);
    }
}
