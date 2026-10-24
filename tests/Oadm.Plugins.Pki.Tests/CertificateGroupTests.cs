using Oadm.Plugins.Pki.Ca;
using Oadm.Plugins.Pki.Certificates;
using Oadm.Plugins.Pki.Client;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>
/// The certificate list as the full management view (docs/spec/pki.md "Certificates"): Group by on the server (type, issuer,
/// none) with the groups' counts over every page, the badges and second lines of the rows, the OADM CA's statuses, Group by
/// kept on this computer and the CA actions of the rows.
/// </summary>
public sealed class CertificateGroupTests
{
    private static Task<CertificatesReply> PageAsync(PkiHarness pki, CertificatesRequest request) =>
        pki.InvokeAsync<CertificatesReply>(PkiMethods.GetCertificates, request);

    /// <summary>Three device certificates of the first OADM CA, a new OADM CA, two device certificates of it and a created certificate.</summary>
    private static async Task<PkiHarness> StartAsync()
    {
        var devices = Enumerable.Range(0, 5).Select(i => new PkiFakeDevice(Guid.NewGuid(), "10.0.0." + (40 + i).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "ACCC8E" + (0x123400 + i).ToString("X6", System.Globalization.CultureInfo.InvariantCulture))).ToList();
        var pki = await PkiHarness.StartAsync(devices: devices);
        IDeviceCertificateAuthority authority = new PkiCertificateAuthority(pki.Service);
        for (var i = 0; i < 3; i++)
        {
            await RevocationTests.SignAsync(authority, devices[i].Id, devices[i].Serial);
        }

        Assert.True((await pki.InvokeAsync<PkiReply>(PkiMethods.Generate, new GenerateRequest("OADM Root CA NEW", null, 10, true))).Ok);
        for (var i = 3; i < 5; i++)
        {
            await RevocationTests.SignAsync(authority, devices[i].Id, devices[i].Serial);
        }

        Assert.True((await CertificateListTests.CreateAsync(pki)).Ok);
        return pki;
    }

    [Fact]
    public async Task By_type_the_groups_come_in_the_fixed_order_with_their_counts_over_every_page()
    {
        await using var pki = await StartAsync();
        var all = await PageAsync(pki, new CertificatesRequest());
        var cas = all.Rows.Count(r => r.Kind == ListedCertificateKind.Authority);

        Assert.Equal(
            [(IssuedCertificateGroups.Authorities, "Certificate authorities", cas), (IssuedCertificateGroups.Local, "Created, signed and imported", 1), (IssuedCertificateGroups.Devices, "Device certificates", 5)],
            all.Groups.Select(g => (g.Key, g.Title, g.Count)));
        Assert.Equal(all.Total, all.Groups.Sum(g => g.Count));

        // A small page in the middle: the counts are still those of every page.
        var page = await PageAsync(pki, new CertificatesRequest { Offset = cas - 1, Limit = 3 });
        Assert.Equal([ListedCertificateKind.Authority, ListedCertificateKind.Created, ListedCertificateKind.Device], page.Rows.Select(r => r.Kind));
        Assert.Equal(all.Groups, page.Groups);

        // No groups: the same order without headers.
        var none = await PageAsync(pki, new CertificatesRequest { GroupBy = IssuedCertificateGrouping.None });
        Assert.Empty(none.Groups);
        Assert.Equal(all.Rows.Select(r => r.Id ?? r.SerialNumber), none.Rows.Select(r => r.Id ?? r.SerialNumber));
    }

    [Fact]
    public async Task By_issuer_each_ca_holds_its_certificates_in_the_order_of_the_cas_across_pages()
    {
        await using var pki = await StartAsync();
        var reply = await PageAsync(pki, new CertificatesRequest { GroupBy = IssuedCertificateGrouping.Issuer });
        var keys = reply.Groups.Select(g => g.Key).ToList();

        Assert.Equal(CertificateAuthorityInfo.OadmId, keys[0]); // the OADM CA first, then its previous one
        Assert.StartsWith("oadm-previous:", keys[1], StringComparison.Ordinal);
        Assert.Equal(reply.Total, reply.Groups.Sum(g => g.Count));
        var current = reply.Rows.Where(r => r.IssuerId == CertificateAuthorityInfo.OadmId).ToList();
        Assert.Equal(2, current.Count(r => r.Kind == ListedCertificateKind.Device));
        Assert.Single(current, r => r.Kind == ListedCertificateKind.Created);
        Assert.Equal(3, reply.Rows.Count(r => r.IssuerId == keys[1] && r.Kind == ListedCertificateKind.Device));

        // The rows come group by group, also when a page cuts through them.
        var order = reply.Rows.Select(r => keys.IndexOf(r.IssuerId!)).ToList();
        Assert.Equal(order.Order(), order);
        var paged = new List<IssuedCertificateInfo>();
        for (var offset = 0; offset < reply.Total; offset += 4)
        {
            paged.AddRange((await PageAsync(pki, new CertificatesRequest { GroupBy = IssuedCertificateGrouping.Issuer, Offset = offset, Limit = 4 })).Rows);
        }

        Assert.Equal(reply.Rows.Select(r => r.Id ?? r.SerialNumber), paged.Select(r => r.Id ?? r.SerialNumber));

        // The filter and the search still apply inside the groups.
        var https = await PageAsync(pki, new CertificatesRequest { GroupBy = IssuedCertificateGrouping.Issuer, Search = "10.0.0.44" });
        Assert.Equal((CertificateAuthorityInfo.OadmId, 1), (Assert.Single(https.Groups).Key, https.Total));
    }

    [Fact]
    public async Task Ca_rows_carry_their_kind_key_and_whether_they_are_roots()
    {
        await using var pki = await StartAsync();
        var rows = (await PageAsync(pki, new CertificatesRequest())).Rows;
        var oadm = rows.Single(r => r.AuthorityKind == CertificateAuthorityKind.Oadm);

        Assert.Equal((CertificateAuthorityInfo.OadmId, true), (oadm.AuthorityId, oadm.IsRootCa));
        Assert.StartsWith("RSA ", oadm.KeyText, StringComparison.Ordinal);
        Assert.Contains(rows, r => r.AuthorityKind == CertificateAuthorityKind.AxisDeviceId && !r.IsRootCa); // the intermediates
        Assert.NotNull(rows.Single(r => r.Kind == ListedCertificateKind.Created).KeyText);
        Assert.All(rows.Where(r => r.Kind == ListedCertificateKind.Device), r => Assert.Null(r.KeyText));

        // Used by in short: one badge per group of a CA, every use in the full text (the tooltip and View).
        Assert.Equal(["Issuing"], oadm.UsedByBadges);
        Assert.StartsWith("Issuing: HTTPS", oadm.UsedBy, StringComparison.Ordinal);
    }

    [Fact]
    public void Rows_show_badges_a_second_line_and_the_days_left()
    {
        var now = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
        IssuedCertificateRow Row(IssuedCertificateInfo info) => new(info) { NowUtc = now };

        var root = Row(new IssuedCertificateInfo { Kind = ListedCertificateKind.Authority, AuthorityKind = CertificateAuthorityKind.Oadm, IsRootCa = true, KeyText = "RSA 4096",
            Name = "OADM Root CA SERVER01", IssuedBy = "OADM Root CA SERVER01", Purpose = ListedCertificateUses.Ca, NotAfterUtc = now.AddDays(245.5) });
        Assert.Equal(["Root CA", "OADM CA"], root.Badges);
        Assert.Equal("RSA 4096", root.Detail);
        Assert.Equal((false, (string?)null), (root.HasStateBadge, root.StateTip)); // a valid certificate: its date only
        Assert.True(root.IsOadmCa);
        Assert.False(root.IsRemovableCa);

        var intermediate = Row(new IssuedCertificateInfo { Kind = ListedCertificateKind.Authority, AuthorityKind = CertificateAuthorityKind.AxisDeviceId, KeyText = "ECDSA P-256",
            IssuedBy = "Axis device ID", Purpose = ListedCertificateUses.Ca, NotAfterUtc = now.AddDays(1.2) });
        Assert.Equal(["Intermediate CA", "Axis device ID"], intermediate.Badges);
        Assert.Equal("ECDSA P-256 · issued by Axis device ID", intermediate.Detail);
        Assert.False(intermediate.HasStateBadge);

        var imported = Row(new IssuedCertificateInfo { Kind = ListedCertificateKind.Authority, AuthorityKind = CertificateAuthorityKind.Imported, IsRootCa = true, Purpose = ListedCertificateUses.Ca });
        Assert.Equal(["Root CA", "Imported"], imported.Badges);
        Assert.True(imported.IsRemovableCa);

        var device = Row(new IssuedCertificateInfo { Kind = ListedCertificateKind.Device, Purpose = DeviceCertificatePurposes.Dot1x, IssuedBy = "OADM Root CA SERVER01",
            State = IssuedCertificateState.ExpiresSoon, NotAfterUtc = now.AddDays(12.5), Device = "10.0.0.48", DeviceManaged = true });
        Assert.Equal(["IEEE 802.1X"], device.Badges);
        Assert.Equal("Issued by OADM Root CA SERVER01", device.Detail);
        Assert.Equal(("Expires in 12 days", true, true), (device.StateText, device.HasStateBadge, device.IsWarning));

        Assert.Equal(["Server"], Row(new IssuedCertificateInfo { Kind = ListedCertificateKind.Server, Purpose = ListedCertificateUses.Server }).Badges);
        var used = Row(new IssuedCertificateInfo { Kind = ListedCertificateKind.Authority, UsedBy = "802.1X: OADM CA (only devices OADM manages); Issuing: HTTPS", UsedByBadges = ["802.1X", "Issuing"] });
        Assert.Equal((null, "802.1X: OADM CA (only devices OADM manages)" + Environment.NewLine + "Issuing: HTTPS"), (used.UsedByText, used.UsedByTip));
        Assert.Equal(("10.0.0.48", (string?)null), (Row(new IssuedCertificateInfo { UsedBy = "10.0.0.48" }).UsedByText, Row(new IssuedCertificateInfo()).UsedByTip));
        Assert.Equal(["802.1X", "Issuing", "EST"], new[] { "802.1X: OADM CA (only devices OADM manages)", "Issuing: HTTPS", "EST server" }.Select(PkiService.UsedByGroup));
        Assert.Equal(["HTTPS", "Created"], Row(new IssuedCertificateInfo { Kind = ListedCertificateKind.Created, Purpose = DeviceCertificatePurposes.Https }).Badges);
        Assert.Equal(["IEEE 802.1X", "Signed"], Row(new IssuedCertificateInfo { Kind = ListedCertificateKind.SignedRequest, Purpose = DeviceCertificatePurposes.Dot1x }).Badges);
        Assert.Equal(["Imported"], Row(new IssuedCertificateInfo { Kind = ListedCertificateKind.Imported, Purpose = ListedCertificateUses.Other }).Badges);
        var expired = Row(new IssuedCertificateInfo { State = IssuedCertificateState.Expired, NotAfterUtc = now.AddDays(-1) });
        Assert.Equal(("Expired", true, true), (expired.StateText, expired.HasStateBadge, expired.IsError));
    }

    [Fact]
    public async Task The_page_groups_its_rows_keeps_the_choice_and_shows_the_trust_of_the_oadm_ca()
    {
        await using var pki = await StartAsync();
        var ctx = new PluginPageContext(pki.Plugin, pki.Hub);
        var trust = new FakeClientTrust();
        using var vm = new PkiViewModel(ctx, trust);
        await vm.LoadAsync();
        await vm.Authorities.LoadAsync();
        await vm.Certificates.LoadAsync();

        // The CAs are their own list without groups; the certificates group without them.
        Assert.False(vm.Authorities.CanGroup);
        Assert.False(vm.Authorities.IsGrouped);
        Assert.All(vm.Authorities.Rows, r => Assert.Equal(ListedCertificateKind.Authority, r.Source.Kind));
        Assert.True(vm.Certificates.IsGrouped);
        Assert.Equal(["Created, signed and imported", "Device certificates"], vm.Certificates.GroupTitles.Select(t => t[..t.IndexOf(" · ", StringComparison.Ordinal)]));
        Assert.Equal("Device certificates · 5", vm.Certificates.Rows[^1].GroupTitle);
        Assert.DoesNotContain(vm.Certificates.Rows, r => r.Source.Kind == ListedCertificateKind.Authority);
        Assert.Empty(vm.Authorities.Rows.Single(r => r.IsOadmCa).MoreStates); // not installed: no trust badge

        vm.Certificates.GroupBy = vm.Certificates.GroupChoices.Single(c => c.Value == IssuedCertificateGrouping.Issuer);
        await Wait.UntilAsync(() => vm.Certificates.GroupTitles.Count > 0 && vm.Certificates.GroupTitles[0].StartsWith("OADM Root CA NEW · ", StringComparison.Ordinal));
        Assert.Equal(nameof(IssuedCertificateGrouping.Issuer), ctx.GetLocalSetting(IssuedCertificatesViewModel.GroupBySetting));
        using (var again = new PkiViewModel(ctx, new FakeClientTrust()))
        {
            Assert.Equal(IssuedCertificateGrouping.Issuer, again.Certificates.GroupBy.Value); // kept on this computer
        }

        vm.Certificates.GroupBy = vm.Certificates.GroupChoices.Single(c => c.Value == IssuedCertificateGrouping.None);
        await Wait.UntilAsync(() => !vm.Certificates.IsGrouped);
        Assert.All(vm.Certificates.Rows, r => Assert.Null(r.GroupTitle));

        // Install in trusted root store: the OADM CA's row says where it is trusted now.
        await vm.InstallTrustCommand.ExecuteAsync(null);
        var states = vm.Authorities.Rows.Single(r => r.IsOadmCa).MoreStates;
        Assert.Contains(states, s => s.Text == "OS root store: client" && s.IsOk && s.Tip == Oadm.Plugins.Pki.Client.PkiViewModel.TrustedHereTip);
        Assert.All(vm.Authorities.Rows.Where(r => !r.IsOadmCa), r => Assert.Empty(r.MoreStates));
        Assert.All(vm.Certificates.Rows, r => Assert.Empty(r.MoreStates));
    }

    [Fact]
    public async Task The_oadm_ca_row_exports_the_public_certificate_and_backs_up_and_other_ca_rows_export_their_certificate()
    {
        await using var pki = await StartAsync();
        var ctx = new PluginPageContext(pki.Plugin, pki.Hub);
        var settingsFile = Path.Combine(Path.GetTempPath(), "oadm-pki-ca-rows-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var dialogs = new FakeDialogs();
            using var vm = new PkiViewModel(ctx, new FakeClientTrust(), new PkiClientSettingsStore(settingsFile)) { Dialogs = dialogs };
            var certificateDialogs = new FakeCertificateDialogs();
            vm.Authorities.Dialogs = certificateDialogs;
            vm.Certificates.Dialogs = certificateDialogs;
            await vm.LoadAsync();
            await vm.Authorities.LoadAsync();
            await vm.Certificates.LoadAsync();
            var oadm = vm.Authorities.Rows.Single(r => r.IsOadmCa);

            // Export of the OADM CA's row is Export public certificate (PEM or DER), saved with the page's file picker.
            await vm.Authorities.ExportDerCommand.ExecuteAsync(oadm);
            Assert.Equal("OADM Root CA NEW.cer", Assert.Single(dialogs.FakeFiles.Saved).FileName);
            Assert.Empty(certificateDialogs.Files.Saved);

            // An Axis device ID row: its own certificate.
            var axis = vm.Authorities.Rows.First(r => r.AuthorityKind == CertificateAuthorityKind.AxisDeviceId);
            await vm.Authorities.ExportPemCommand.ExecuteAsync(axis);
            Assert.EndsWith(".crt", Assert.Single(certificateDialogs.Files.Saved).FileName, StringComparison.Ordinal);
            Assert.False(axis.IsRemovableCa);
            Assert.False(vm.RemoveCaCommand.CanExecute(axis));

            // Several chosen rows: the menu of any of them acts on all of them (red); a row outside the choice keeps its own.
            var devices = vm.Certificates.Rows.Where(r => r.Source.Kind == ListedCertificateKind.Device).Take(2).ToList();
            vm.Certificates.SetSelection(devices);
            Assert.Equal([("Revoke 2 certificates", true), ("Delete 2 certificates", true)], vm.Certificates.MenuOf(devices[1]).Select(a => (a.Text, a.IsDanger)));
            Assert.Equal("View", vm.Authorities.MenuOf(oadm)[0].Text); // the CA list's own menu

            // Back up of the OADM CA's row: the backup dialog.
            await vm.BackupCommand.ExecuteAsync(null);
            Assert.IsType<BackupViewModel>(dialogs.Last);
        }
        finally
        {
            File.Delete(settingsFile);
        }
    }
}
