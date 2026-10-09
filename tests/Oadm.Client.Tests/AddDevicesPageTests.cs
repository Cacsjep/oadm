using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Client.Api;
using Oadm.Client.Discovery;
using Oadm.Client.Infrastructure;
using Oadm.Contracts.V1;

namespace Oadm.Client.Tests;

/// <summary>The fast add page against the fake server (mixed automatic login results).</summary>
public sealed class AddDevicesPageTests : IDisposable
{
    private readonly FakeOadmApi _api = new(TimeSpan.FromMilliseconds(5));

    public void Dispose() => _api.Dispose();

    private AddDevicesViewModel Create(AddDevicesMode mode) =>
        new(_api, new ImmediateUiDispatcher(), NullLogger<AddDevicesViewModel>.Instance, mode);

    /// <summary>Scan: 3 managed, 3 authenticated, 2 login failed, 2 factory default, 1 unreachable.</summary>
    private async Task<AddDevicesViewModel> OpenScanAsync()
    {
        AddDevicesViewModel page = Create(AddDevicesMode.Scan);
        await page.OpenAsync();
        await TestSupport.WaitUntilAsync(() => page.Rows.Count == 11 && page.Rows.All(r => r.AuthState != AuthState.Pending));
        return page;
    }

    private static DiscoveredRowViewModel Row(AddDevicesViewModel page, string address) => page.Rows.Single(r => r.Address == address);

    [Fact]
    public async Task Scan_starts_immediately_and_shows_the_login_result_per_device()
    {
        await using AddDevicesViewModel page = await OpenScanAsync();

        Assert.True(page.IsScanning);
        Assert.Equal("Authenticated (root)", Row(page, "10.0.0.92").ChipText);
        Assert.True(Row(page, "10.0.0.92").IsStatusOk);
        Assert.Equal("Authenticated (operator)", Row(page, "10.0.0.96").ChipText);
        Assert.Equal("Login failed", Row(page, "10.0.0.93").ChipText);
        Assert.True(Row(page, "10.0.0.93").IsStatusError);
        Assert.True(Row(page, "10.0.0.93").ShowLogIn);
        Assert.Equal("None of the 2 known credentials worked.", Row(page, "10.0.0.93").ChipTooltip);
        Assert.Equal("Password not set", Row(page, "10.0.0.90").ChipText);
        Assert.True(Row(page, "10.0.0.90").IsStatusWarning);
        Assert.True(Row(page, "10.0.0.90").ShowSetPassword);
        Assert.Equal("Unreachable", Row(page, "10.0.0.95").ChipText);
        Assert.Equal(3, page.Rows.Count(r => r.ChipText == "Already added" && r.IsMuted));
        // Already added devices are not listed at all (user decision), only counted.
        Assert.DoesNotContain(page.FilteredRows, r => r.IsAlreadyManaged);
        Assert.Equal(8, page.FilteredRows.Count);
        Assert.Equal("8 found · 3 ready to add · 2 need a login · 2 need a password · 0 selected", page.SummaryText);
        Assert.False(page.AddCommand.CanExecute(null));
    }

    [Fact]
    public async Task Only_authenticated_devices_can_be_selected_and_are_added_in_one_click()
    {
        await using AddDevicesViewModel page = await OpenScanAsync();
        bool? closed = null;
        page.CloseRequested += (_, added) => closed = added;

        Row(page, "10.0.0.93").IsSelected = true;
        Row(page, "10.0.0.90").IsSelected = true;
        page.Rows.First(r => r.IsAlreadyManaged).IsSelected = true;
        Assert.Equal(0, page.SelectedCount);

        page.SelectAllAuthenticatedCommand.Execute(null);
        Assert.Equal(3, page.SelectedCount);
        Assert.Equal("Add 3 devices", page.AddButtonText);

        await page.AddCommand.ExecuteAsync(null);

        Assert.True(closed);
        Assert.Equal(3, page.AddedDeviceIds.Count);
        IReadOnlyList<Device> devices = await _api.ListDevicesAsync(CancellationToken.None);
        Assert.All(new[] { "10.0.0.92", "10.0.0.94", "10.0.0.96" }, a => Assert.Equal(DeviceStatus.Ok, devices.Single(d => d.Address == a).Status));
        Assert.All(new[] { "10.0.0.92", "10.0.0.94", "10.0.0.96" }, a => Assert.True(devices.Single(d => d.Address == a).HasCredentials));
    }

    [Fact]
    public async Task Login_failed_row_opens_the_inline_editor_and_retries_right_away()
    {
        await using AddDevicesViewModel page = await OpenScanAsync();
        DiscoveredRowViewModel failed = Row(page, "10.0.0.93");

        page.FocusedRow = failed; // a single click on the row
        Assert.True(page.IsLoginEditorOpen);
        Assert.Equal("Log in to 10.0.0.93 (ACCC8E8192A3)", page.EditorTitle);
        Assert.True(page.SaveToCredentialList);
        Assert.Null(page.ErrorOf(nameof(page.EditorPassword))); // untouched editor: no errors yet
        Assert.False(page.RetryCommand.CanExecute(null)); // ...but no password: Retry waits
        Assert.Equal("Enter the password.", page.RetryBlockedReason);

        page.EditorPassword = "x";
        page.EditorPassword = "";
        Assert.Equal("Enter the password.", page.ErrorOf(nameof(page.EditorPassword))); // below the password field
        page.EditorPassword = "wrong";
        Assert.True(page.RetryCommand.CanExecute(null));
        await page.RetryCommand.ExecuteAsync(null);
        Assert.Equal("The user name or password is wrong.", page.ErrorOf(nameof(page.EditorPassword)));
        Assert.Equal("Login failed", failed.ChipText);
        Assert.True(page.IsLoginEditorOpen);

        int listBefore = (await _api.ListCredentialsAsync(CancellationToken.None)).Count;
        page.EditorUserName = "admin";
        page.EditorPassword = "right-Pass1";
        await page.RetryCommand.ExecuteAsync(null);

        Assert.False(page.IsEditorOpen);
        Assert.Equal("Authenticated (admin)", failed.ChipText);
        Assert.True(failed.IsSelected);
        Assert.Equal(listBefore + 1, (await _api.ListCredentialsAsync(CancellationToken.None)).Count);

        // The server tries the working login on the other device whose login failed; the open scan stream reports it.
        await TestSupport.WaitUntilAsync(() => Row(page, "10.0.0.97").ChipText == "Authenticated (admin)");
        Assert.Equal("8 found · 5 ready to add · 2 need a password · 1 selected", page.SummaryText);
    }

    [Fact]
    public async Task Factory_default_devices_need_a_valid_password_which_is_set_on_add()
    {
        await using AddDevicesViewModel page = await OpenScanAsync();
        DiscoveredRowViewModel simple = Row(page, "10.0.0.90");
        DiscoveredRowViewModel complex = Row(page, "10.0.0.91");
        Assert.False(simple.CanAdd);

        page.OpenEditorCommand.Execute(complex);
        Assert.True(page.IsPasswordEditorOpen);
        Assert.Contains("At least 12 characters with upper and lower case", page.PolicyHint, StringComparison.Ordinal);
        page.NewPassword = "simple";
        page.ConfirmPassword = "simple";
        Assert.Equal("This device needs at least 12 characters.", page.ErrorOf(nameof(page.NewPassword)));
        Assert.Null(page.ErrorOf(nameof(page.ConfirmPassword)));
        Assert.False(page.ApplyPasswordCommand.CanExecute(null));

        // One password for all factory-default devices: checked against every device's policy.
        page.NewPassword = "Str0ng-Passw0rd!";
        page.ConfirmPassword = "Str0ng-Passw0rd";
        Assert.Null(page.ErrorOf(nameof(page.NewPassword)));
        Assert.Equal("The passwords do not match.", page.ErrorOf(nameof(page.ConfirmPassword)));
        Assert.Equal("The passwords do not match.", page.ApplyPasswordBlockedReason);
        page.ConfirmPassword = "Str0ng-Passw0rd!";
        page.UseForAllFactoryDefault = true;
        Assert.True(page.ApplyPasswordCommand.CanExecute(null));
        page.ApplyPasswordCommand.Execute(null);

        Assert.False(page.IsEditorOpen);
        Assert.True(simple.IsSelected && complex.IsSelected);
        Assert.True(simple.ShowPasswordReady);
        Assert.Equal(2, page.SelectedCount);

        bool? closed = null;
        page.CloseRequested += (_, added) => closed = added;
        await page.AddCommand.ExecuteAsync(null);

        // The page always closes after adding; the rows are marked added meanwhile.
        Assert.True(closed);
        Assert.Equal("Added", simple.ChipText);
        Assert.True(simple.IsMuted);
        Assert.False(simple.CanAdd);
        IReadOnlyList<Device> devices = await _api.ListDevicesAsync(CancellationToken.None);
        Assert.Equal(DeviceStatus.Ok, devices.Single(d => d.Address == "10.0.0.91").Status);
    }

    [Fact]
    public async Task Ip_range_scans_on_enter_and_reports_progress()
    {
        await using AddDevicesViewModel page = Create(AddDevicesMode.IpRange);
        await page.OpenAsync();
        Assert.Empty(page.Rows);

        Assert.False(page.StartRangeCommand.CanExecute(null));
        Assert.Null(page.ErrorOf(nameof(page.RangeFrom))); // nothing shown on the untouched form
        page.RangeFrom = "10.0.1";
        Assert.Equal("Enter a valid IPv4 address, e.g. 192.168.0.1.", page.ErrorOf(nameof(page.RangeFrom)));
        Assert.Null(page.ErrorOf(nameof(page.RangeTo)));
        page.RangeFrom = "10.0.1.20";
        page.RangeTo = "10.0.1.1";
        Assert.Null(page.ErrorOf(nameof(page.RangeFrom)));
        Assert.Equal("The last address must not be lower than the first.", page.ErrorOf(nameof(page.RangeTo)));
        Assert.False(page.StartRangeCommand.CanExecute(null));

        page.RangeTo = "10.0.1.40";
        Assert.True(page.StartRangeCommand.CanExecute(null));
        await page.StartRangeCommand.ExecuteAsync(null);
        Assert.Null(page.ErrorText);
        Assert.False(page.HasErrors);
        await TestSupport.WaitUntilAsync(() => !page.IsScanning && page.Rows.All(r => r.AuthState != AuthState.Pending) && page.Rows.Count == 7);

        Assert.Equal(100, page.ScanProgress);
        Assert.Equal("Scan finished, 7 devices found", page.ScanStatusText);
        Assert.True(page.ShowScanAgain);
        Assert.False(page.ShowStop);
        Assert.All(page.Rows, r => Assert.StartsWith("10.0.1.", r.Address, StringComparison.Ordinal));
        Assert.Equal(3, page.Rows.Count(r => r.CanAdd));
    }

    [Fact]
    public async Task Add_manually_probes_each_entered_address()
    {
        await using AddDevicesViewModel page = Create(AddDevicesMode.Manual);
        await page.OpenAsync();

        page.ManualAddress = "camera7.example.com:8443";
        await page.ProbeAddressCommand.ExecuteAsync(null);
        Assert.Equal("", page.ManualAddress);
        Assert.Null(page.ErrorOf(nameof(page.ManualAddress))); // cleared for the next address, no error
        page.ManualAddress = "10.0.0.93";
        await page.ProbeAddressCommand.ExecuteAsync(null);
        await TestSupport.WaitUntilAsync(() => page.Rows.Count == 2 && page.Rows.All(r => r.AuthState != AuthState.Pending) && !page.IsScanning);

        Assert.Equal("Authenticated (root)", Row(page, "camera7.example.com:8443").ChipText);
        Assert.Equal("Login failed", Row(page, "10.0.0.93").ChipText);

        page.ManualAddress = "10.0.0.199";
        await page.ProbeAddressCommand.ExecuteAsync(null);
        await TestSupport.WaitUntilAsync(() => page.ErrorOf(nameof(page.ManualAddress)) is not null);
        Assert.Equal("No Axis device answered at 10.0.0.199.", page.ErrorOf(nameof(page.ManualAddress))); // below the address
        Assert.Null(page.ErrorText);

        page.ManualAddress = "ftp://x";
        Assert.Null(page.ErrorOf(nameof(page.ManualAddress))); // editing clears the server's answer
        await page.ProbeAddressCommand.ExecuteAsync(null);
        Assert.Equal("'ftp://x' is not a valid IP address or host name.", page.ErrorOf(nameof(page.ManualAddress)));

        Row(page, "camera7.example.com:8443").IsSelected = true;
        await page.AddCommand.ExecuteAsync(null);
        IReadOnlyList<Device> devices = await _api.ListDevicesAsync(CancellationToken.None);
        Assert.Contains(devices, d => d.Address == "camera7.example.com:8443" && d.Status == DeviceStatus.Ok);
    }

    [Fact]
    public async Task Zero_conf_scan_ends_after_the_time_limit_and_can_run_again()
    {
        _api.ZeroConfDuration = TimeSpan.FromMilliseconds(300);
        await using AddDevicesViewModel page = await OpenScanAsync();

        await TestSupport.WaitUntilAsync(() => !page.IsScanning);
        Assert.Equal("Scan finished, 8 devices found, 3 already added", page.ScanStatusText);
        Assert.Equal(100, page.ScanProgress);
        Assert.False(page.ShowStop);
        Assert.True(page.ShowScanAgain);
        Assert.True(page.ScanAgainCommand.CanExecute(null));

        // Scan again: a new zero-conf session; the devices already found stay (deduplicated by serial).
        Row(page, "10.0.0.92").IsSelected = true;
        await page.ScanAgainCommand.ExecuteAsync(null);
        Assert.True(page.IsScanning);
        Assert.True(page.ShowStop);
        Assert.False(page.ShowScanAgain);
        Assert.Equal("Searching the network...", page.ScanStatusText);
        Assert.Equal(11, page.Rows.Count);
        await TestSupport.WaitUntilAsync(() => !page.IsScanning);
        Assert.Equal("Scan finished, 8 devices found, 3 already added", page.ScanStatusText);
        Assert.Equal(11, page.Rows.Count);
        Assert.True(Row(page, "10.0.0.92").IsSelected); // the new search did not reset the known login or the selection
        Assert.Equal("Authenticated (root)", Row(page, "10.0.0.92").ChipText);
    }

    [Fact]
    public async Task Stop_ends_a_zero_conf_scan_and_keeps_the_devices()
    {
        await using AddDevicesViewModel page = await OpenScanAsync(); // fake limit: 30 s, like the server default
        Assert.True(page.IsScanning);
        Assert.True(page.ShowStop);
        Assert.True(page.StopScanCommand.CanExecute(null));

        await page.StopScanCommand.ExecuteAsync(null);

        await TestSupport.WaitUntilAsync(() => !page.IsScanning);
        Assert.Equal("Scan stopped, 8 devices found, 3 already added", page.ScanStatusText);
        Assert.Equal(11, page.Rows.Count);
        Assert.True(page.ShowScanAgain);
        Assert.False(page.ShowStop);
    }

    [Fact]
    public async Task Stop_ends_an_ip_range_scan()
    {
        await using AddDevicesViewModel page = Create(AddDevicesMode.IpRange);
        await page.OpenAsync();
        Assert.False(page.ShowScanAgain); // nothing scanned yet
        page.RangeFrom = "10.0.1.1";
        page.RangeTo = "10.0.1.254";

        await page.StartRangeCommand.ExecuteAsync(null);
        Assert.True(page.ShowStop);
        await page.StopScanCommand.ExecuteAsync(null);

        await TestSupport.WaitUntilAsync(() => !page.IsScanning);
        Assert.StartsWith("Scan stopped, ", page.ScanStatusText, StringComparison.Ordinal);
        Assert.True(page.ShowScanAgain);

        // Scan again repeats the range.
        await page.ScanAgainCommand.ExecuteAsync(null);
        await TestSupport.WaitUntilAsync(() => !page.IsScanning && page.Rows.Count == 7);
        Assert.Equal("Scan finished, 7 devices found", page.ScanStatusText);
    }

    [Fact]
    public async Task A_working_login_is_tried_on_failed_devices_of_the_other_searches()
    {
        await using AddDevicesViewModel page = Create(AddDevicesMode.Manual);
        await page.OpenAsync();
        foreach (string address in new[] { "10.0.0.93", "10.0.0.97" })
        {
            page.ManualAddress = address;
            await page.ProbeAddressCommand.ExecuteAsync(null);
        }

        await TestSupport.WaitUntilAsync(() => !page.IsScanning && page.Rows.Count == 2 && page.Rows.All(r => r.ChipText == "Login failed"));
        Assert.False(page.ShowStop);
        Assert.False(page.ShowScanAgain); // manual search: nothing to repeat
        await Task.Delay(100); // both watch streams have ended

        page.OpenEditorCommand.Execute(Row(page, "10.0.0.93"));
        page.EditorUserName = "admin";
        page.EditorPassword = "right-Pass1";
        page.SaveToCredentialList = false;
        await page.RetryCommand.ExecuteAsync(null);

        Assert.Equal("Authenticated (admin)", Row(page, "10.0.0.93").ChipText);
        await TestSupport.WaitUntilAsync(() => Row(page, "10.0.0.97").ChipText == "Authenticated (admin)");
        Assert.Equal(2, page.Rows.Count(r => r.CanAdd));
    }

    private async Task<AddDevicesViewModel> OpenImportAsync(string csv)
    {
        AddDevicesViewModel page = Create(AddDevicesMode.Import);
        page.SetImport(DeviceImportFile.Parse("site.csv", csv));
        await page.OpenAsync();
        return page;
    }

    [Fact]
    public async Task Import_lists_every_line_and_probes_the_addresses_with_their_credentials()
    {
        string managed = (await _api.ListDevicesAsync(CancellationToken.None))[0].Address;
        string csv = string.Join("\n",
            "Address,User name,Password,Notes",
            "10.0.0.93,admin,right-Pass1,login failed with the list: the file's credentials work",
            "10.0.0.97,,,login failed",
            "camera7.example.com:8443,,,new device",
            "10.0.0.199,,,nothing answers",
            "not an address,,,invalid",
            "10.0.0.97,,,listed twice",
            managed + ",,,already managed");

        await using AddDevicesViewModel page = await OpenImportAsync(csv);

        Assert.True(page.IsImportMode);
        Assert.Equal("Import from file", page.HeaderTitle);
        Assert.Equal("Addresses from site.csv. Logins in the file are tried first.", page.HeaderDescription);
        Assert.Equal(7, page.Rows.Count); // every line is a row from the start, in file order
        Assert.True(page.IsScanning);
        Assert.True(page.ShowStop);
        await page.ImportCompletion!;
        await TestSupport.WaitUntilAsync(() => !page.IsScanning && page.Rows.All(r => r.IsImportPlaceholder || r.AuthState != AuthState.Pending));

        Assert.Equal("Authenticated (admin)", Row(page, "10.0.0.93").ChipText);
        Assert.Equal("Login failed", page.Rows[1].ChipText);
        Assert.Equal("Authenticated (root)", Row(page, "camera7.example.com:8443").ChipText);
        DiscoveredRowViewModel nothing = Row(page, "10.0.0.199");
        Assert.Equal(("Not found", "No Axis device answered at 10.0.0.199."), (nothing.ChipText, nothing.StatusDetail));
        Assert.True(nothing.IsStatusError);
        Assert.Equal(("Not added", "\"not an address\" is not an IP address or host name."), (page.Rows[4].ChipText, page.Rows[4].StatusDetail));
        Assert.Equal("Listed before in line 3.", page.Rows[5].StatusDetail);
        Assert.True(page.Rows[6].IsAlreadyManaged);
        Assert.DoesNotContain(page.Rows[6], page.FilteredRows); // already added devices are not listed
        Assert.Equal(["10.0.0.93", "10.0.0.97", "camera7.example.com:8443", "10.0.0.199", "not an address", "10.0.0.97"], page.FilteredRows.Select(r => r.Address));
        Assert.Equal("3 found · 2 ready to add · 1 need a login · 3 not added · 0 selected", page.SummaryText);
        Assert.Equal("Import finished, 3 devices found, 1 already added", page.ScanStatusText);
        Assert.False(page.ShowScanAgain);

        // Rows with a problem cannot be selected; the found ones are added as usual, with the file's credentials.
        page.Rows[4].IsSelected = true;
        page.SelectAllAuthenticatedCommand.Execute(null);
        Assert.Equal("Add 2 devices", page.AddButtonText);
        await page.AddCommand.ExecuteAsync(null);
        IReadOnlyList<Device> devices = await _api.ListDevicesAsync(CancellationToken.None);
        Assert.True(devices.Single(d => d.Address == "10.0.0.93").HasCredentials);
        Assert.Contains(devices, d => d.Address == "camera7.example.com:8443");
    }

    [Fact]
    public async Task Import_probes_at_most_sixteen_addresses_at_a_time()
    {
        string csv = string.Join("\n", Enumerable.Range(1, 80).Select(i => $"10.0.2.{i}"));

        await using AddDevicesViewModel page = await OpenImportAsync(csv);
        await page.ImportCompletion!;
        await TestSupport.WaitUntilAsync(() => !page.IsScanning && page.Rows.All(r => r.IsImportPlaceholder || r.AuthState != AuthState.Pending), 20000);

        Assert.InRange(_api.MaxOpenProbeWatches, 2, AddDevicesViewModel.MaxImportProbes);
        Assert.Equal(80, page.Rows.Count(r => r.ChipText == "Authenticated (root)"));
        Assert.Equal("Import finished, 80 devices found", page.ScanStatusText);
        Assert.Equal(100, page.ScanProgress);
    }

    [Fact]
    [Trait("Category", "Timing")]
    public async Task An_import_of_ten_thousand_lines_lists_and_filters_fast()
    {
        string csv = "Address,User name,Password\n" + string.Join("\n", Enumerable.Range(0, DeviceImportFile.MaxLines).Select(i => $"10.1.{i / 256}.{i % 256},root,pass{i}"));
        DeviceImportFile file = DeviceImportFile.Parse("big.csv", csv);
        await using AddDevicesViewModel page = Create(AddDevicesMode.Import);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        page.SetImport(file);
        long listed = watch.ElapsedMilliseconds;
        page.SearchText = "10.1.3.";
        long filtered = watch.ElapsedMilliseconds - listed;
        page.SelectAllAuthenticatedCommand.Execute(null);
        long all = watch.ElapsedMilliseconds;

        Assert.Equal(DeviceImportFile.MaxLines, page.Rows.Count);
        Assert.Equal(256, page.FilteredRows.Count);
        Assert.Equal("0 found · 0 ready to add · 0 selected", page.SummaryText);
        Assert.True(all < 1500, $"10,000 lines: listed in {listed} ms, filtered in {filtered} ms, total {all} ms");
    }

    [Fact]
    public async Task Stop_ends_an_import_and_leaves_the_rest_unchecked()
    {
        string csv = string.Join("\n", Enumerable.Range(1, 200).Select(i => $"10.0.3.{i}"));
        await using AddDevicesViewModel page = await OpenImportAsync(csv);
        await TestSupport.WaitUntilAsync(() => page.Rows.Any(r => r.ChipText == "Authenticated (root)"));

        await page.StopScanCommand.ExecuteAsync(null);
        await page.ImportCompletion!;
        await TestSupport.WaitUntilAsync(() => !page.IsScanning, 20000);

        Assert.StartsWith("Import stopped, ", page.ScanStatusText, StringComparison.Ordinal);
        DiscoveredRowViewModel unchecked_ = page.Rows[^1];
        Assert.Equal(("Not checked", "Stopped before this address was checked."), (unchecked_.ChipText, unchecked_.StatusDetail));
        Assert.False(unchecked_.IsStatusError);
        Assert.False(page.ShowStop);
    }

    [Fact]
    public async Task The_page_always_closes_after_adding()
    {
        await using AddDevicesViewModel page = await OpenScanAsync();
        bool? closed = null;
        page.CloseRequested += (_, added) => closed = added;
        Row(page, "10.0.0.92").IsSelected = true;

        await page.AddCommand.ExecuteAsync(null);

        Assert.True(closed);
        Assert.Single(page.AddedDeviceIds);
    }

    [Theory]
    [InlineData("", "", null, "")]
    [InlineData("abc", "", null, "")]
    [InlineData("abc", "abd", null, "The passwords do not match.")]
    [InlineData("abc", "abc", null, null)]
    [InlineData("short-password", "short-password", "length", "This device needs at least 15 characters.")]
    [InlineData("longenough-pass", "longenough-pass", "length", null)]
    [InlineData("alllowercase-1", "alllowercase-1", "complex", "This device needs upper and lower case, a digit and a symbol.")]
    [InlineData("Mixed-Case-12", "Mixed-Case-12", "complex", null)]
    [InlineData("café", "café", null, "Use only letters, digits, spaces and standard symbols.")]
    public void Password_rules_follow_the_device_policy(string password, string confirm, string? policy, string? expected) =>
        Assert.Equal(expected, PasswordRules.Validate(password, confirm, policy));
}
