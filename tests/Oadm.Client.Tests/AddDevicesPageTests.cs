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
        Assert.Equal("11 found · 3 ready to add · 2 need a login · 2 need a password · 0 selected", page.SummaryText);
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
        Assert.Equal("11 found · 5 ready to add · 2 need a password · 1 selected", page.SummaryText);
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
        Assert.Contains("device policy \"complex\"", page.PolicyHint, StringComparison.Ordinal);
        page.NewPassword = "simple";
        page.ConfirmPassword = "simple";
        Assert.Equal("The device passphrase policy requires at least 12 characters.", page.ErrorOf(nameof(page.NewPassword)));
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
        Assert.Equal("Scan finished, 11 devices found", page.ScanStatusText);
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
        Assert.Equal("Scan finished, 11 devices found", page.ScanStatusText);
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
        Assert.Equal("Scan stopped, 11 devices found", page.ScanStatusText);
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
    [InlineData("short-password", "short-password", "length", "The device passphrase policy requires at least 15 characters.")]
    [InlineData("longenough-pass", "longenough-pass", "length", null)]
    [InlineData("alllowercase-1", "alllowercase-1", "complex", "The device passphrase policy requires an upper-case letter, a lower-case letter, a digit and a special character.")]
    [InlineData("Mixed-Case-12", "Mixed-Case-12", "complex", null)]
    [InlineData("café", "café", null, "The password may only contain printable ASCII characters.")]
    public void Password_rules_follow_the_device_policy(string password, string confirm, string? policy, string? expected) =>
        Assert.Equal(expected, PasswordRules.Validate(password, confirm, policy));
}
