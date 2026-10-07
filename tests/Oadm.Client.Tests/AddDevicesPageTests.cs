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

    /// <summary>Scan: 3 managed, 3 authenticated, 1 login failed, 2 factory default, 1 unreachable.</summary>
    private async Task<AddDevicesViewModel> OpenScanAsync()
    {
        AddDevicesViewModel page = Create(AddDevicesMode.Scan);
        await page.OpenAsync();
        await TestSupport.WaitUntilAsync(() => page.Rows.Count == 10 && page.Rows.All(r => r.AuthState != AuthState.Pending));
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
        Assert.Equal("10 found · 3 ready to add · 1 need a login · 2 need a password · 0 selected", page.SummaryText);
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

        page.EditorPassword = "wrong";
        await page.RetryCommand.ExecuteAsync(null);
        Assert.Equal("The user name or password is wrong.", page.EditorError);
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
    }

    [Fact]
    public async Task Factory_default_devices_need_a_valid_password_which_is_set_on_add()
    {
        await using AddDevicesViewModel page = await OpenScanAsync();
        page.KeepOpen = true;
        DiscoveredRowViewModel simple = Row(page, "10.0.0.90");
        DiscoveredRowViewModel complex = Row(page, "10.0.0.91");
        Assert.False(simple.CanAdd);

        page.OpenEditorCommand.Execute(complex);
        Assert.True(page.IsPasswordEditorOpen);
        Assert.Contains("device policy \"complex\"", page.PolicyHint, StringComparison.Ordinal);
        page.NewPassword = "simple";
        page.ConfirmPassword = "simple";
        page.ApplyPasswordCommand.Execute(null);
        Assert.Equal("The device passphrase policy requires at least 12 characters.", page.EditorError);

        // One password for all factory-default devices: checked against every device's policy.
        page.NewPassword = "Str0ng-Passw0rd!";
        page.ConfirmPassword = "Str0ng-Passw0rd";
        page.ApplyPasswordCommand.Execute(null);
        Assert.Equal("The passwords do not match.", page.EditorError);
        page.ConfirmPassword = "Str0ng-Passw0rd!";
        page.UseForAllFactoryDefault = true;
        page.ApplyPasswordCommand.Execute(null);

        Assert.False(page.IsEditorOpen);
        Assert.True(simple.IsSelected && complex.IsSelected);
        Assert.True(simple.ShowPasswordReady);
        Assert.Equal(2, page.SelectedCount);

        await page.AddCommand.ExecuteAsync(null);

        // Keep open: the page stays and shows the added devices greyed out.
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

        page.RangeFrom = "10.0.1.20";
        page.RangeTo = "10.0.1.1";
        await page.StartRangeCommand.ExecuteAsync(null);
        Assert.Equal("The end address must not be lower than the start address.", page.ErrorText);

        page.RangeTo = "10.0.1.40";
        await page.StartRangeCommand.ExecuteAsync(null);
        Assert.Null(page.ErrorText);
        await TestSupport.WaitUntilAsync(() => !page.IsScanning && page.Rows.All(r => r.AuthState != AuthState.Pending) && page.Rows.Count == 6);

        Assert.Equal(100, page.ScanProgress);
        Assert.StartsWith("Done, 6 device(s) found", page.ScanStatusText, StringComparison.Ordinal);
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
        page.ManualAddress = "10.0.0.93";
        await page.ProbeAddressCommand.ExecuteAsync(null);
        await TestSupport.WaitUntilAsync(() => page.Rows.Count == 2 && page.Rows.All(r => r.AuthState != AuthState.Pending) && !page.IsScanning);

        Assert.Equal("Authenticated (root)", Row(page, "camera7.example.com:8443").ChipText);
        Assert.Equal("Login failed", Row(page, "10.0.0.93").ChipText);

        page.ManualAddress = "10.0.0.199";
        await page.ProbeAddressCommand.ExecuteAsync(null);
        await TestSupport.WaitUntilAsync(() => page.ErrorText is not null);
        Assert.Equal("No Axis device answered at 10.0.0.199.", page.ErrorText);

        page.ManualAddress = "ftp://x";
        await page.ProbeAddressCommand.ExecuteAsync(null);
        Assert.Equal("'ftp://x' is not a valid IP address or host name.", page.ErrorText);

        Row(page, "camera7.example.com:8443").IsSelected = true;
        await page.AddCommand.ExecuteAsync(null);
        IReadOnlyList<Device> devices = await _api.ListDevicesAsync(CancellationToken.None);
        Assert.Contains(devices, d => d.Address == "camera7.example.com:8443" && d.Status == DeviceStatus.Ok);
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
