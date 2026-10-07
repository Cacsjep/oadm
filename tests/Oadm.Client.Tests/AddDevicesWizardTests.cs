using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Client.Api;
using Oadm.Client.Discovery;
using Oadm.Client.Infrastructure;
using Oadm.Contracts.V1;

namespace Oadm.Client.Tests;

public sealed class AddDevicesWizardTests : IDisposable
{
    private readonly FakeOadmApi _api = new(TimeSpan.FromMilliseconds(5));

    public void Dispose() => _api.Dispose();

    private AddDevicesWizardViewModel Create(AddDevicesMode mode) =>
        new(_api, new ImmediateUiDispatcher(), NullLogger<AddDevicesWizardViewModel>.Instance, mode);

    /// <summary>Opens a zero-conf wizard and waits for the 8 fake devices (3 managed, 2 factory default, 3 with password).</summary>
    private async Task<AddDevicesWizardViewModel> OpenZeroConfAsync()
    {
        AddDevicesWizardViewModel wizard = Create(AddDevicesMode.ZeroConf);
        await wizard.OpenAsync();
        await TestSupport.WaitUntilAsync(() => wizard.Discovered.Count == 8);
        return wizard;
    }

    [Fact]
    public async Task Zero_conf_starts_on_select_step_and_lists_discovered_devices()
    {
        await using AddDevicesWizardViewModel wizard = await OpenZeroConfAsync();

        Assert.Equal(WizardStep.Select, wizard.CurrentStep);
        Assert.False(wizard.CanGoBack);
        Assert.False(wizard.CanGoNext); // nothing selected yet
        Assert.Equal(3, wizard.Discovered.Count(d => d.IsAlreadyManaged));
        Assert.Equal("8 devices found, 0 selected", wizard.SelectionText);
        Assert.DoesNotContain(wizard.Steps, s => s.Step == WizardStep.IpRange);
    }

    [Fact]
    public async Task Already_managed_devices_cannot_be_selected()
    {
        await using AddDevicesWizardViewModel wizard = await OpenZeroConfAsync();
        DiscoveredRowViewModel managed = wizard.Discovered.First(d => d.IsAlreadyManaged);

        managed.IsSelected = true;
        wizard.SelectAllCommand.Execute(null);

        Assert.False(managed.IsSelected);
        Assert.Equal(5, wizard.SelectedCount);
        Assert.Equal("Already added", managed.StatusText);
    }

    [Fact]
    public async Task Full_flow_with_skipped_password_and_credentials_for_all()
    {
        await using AddDevicesWizardViewModel wizard = await OpenZeroConfAsync();
        bool? closed = null;
        wizard.CloseRequested += (_, added) => closed = added;

        wizard.SelectAllCommand.Execute(null);
        Assert.True(wizard.CanGoNext);
        await wizard.NextCommand.ExecuteAsync(null);

        Assert.Equal(WizardStep.HostName, wizard.CurrentStep);
        Assert.False(wizard.UseHostName);
        Assert.Equal(2, wizard.PasswordDevices.Count);
        Assert.Equal(3, wizard.CredentialDevices.Count);
        wizard.UseHostName = true;
        await wizard.NextCommand.ExecuteAsync(null);

        Assert.Equal(WizardStep.Password, wizard.CurrentStep);
        Assert.False(wizard.CanGoNext);
        wizard.SkipCommand.Execute(null);

        Assert.Equal(WizardStep.Credentials, wizard.CurrentStep);
        Assert.True(wizard.SkipPassword);
        Assert.True(wizard.UseForAll);
        Assert.Equal("root", wizard.UserName);
        wizard.Password = "secret";
        await wizard.NextCommand.ExecuteAsync(null);

        Assert.Equal(WizardStep.Review, wizard.CurrentStep);
        Assert.True(wizard.IsLastStep);
        Assert.Equal(5, wizard.ReviewRows.Count);
        Assert.Equal(2, wizard.ReviewRows.Count(r => r.Action == "Add without password (factory default)"));
        Assert.Equal(3, wizard.ReviewRows.Count(r => r.Action == "Add with credentials for root"));
        Assert.All(wizard.ReviewRows, r => Assert.EndsWith(".local", r.Address, StringComparison.Ordinal)); // host names

        CommitRequest request = wizard.BuildCommitRequest();
        Assert.Equal("", request.InitialRootPassword);
        Assert.True(request.UseHostName);
        DeviceCredentials forAll = Assert.Single(request.Credentials);
        Assert.Equal("", forAll.DiscoveredId);
        Assert.Equal("root", forAll.UserName);
        Assert.Equal("secret", forAll.Password);

        await wizard.NextCommand.ExecuteAsync(null);

        Assert.True(closed);
        Assert.NotNull(wizard.Result);
        Assert.Equal(5, wizard.Result!.DeviceIds.Count);
        IReadOnlyList<Device> devices = await _api.ListDevicesAsync(CancellationToken.None);
        Assert.Equal(2, devices.Count(d => d.Serial is "B8A44F7788AA" or "B8A44F99CC01" && d.Status == DeviceStatus.PasswordNotSet));
        Assert.Equal(3, devices.Count(d => d.Serial is "ACCC8E5F6071" or "ACCC8E8192A3" or "B8A44FB4C5D6" && d.Status == DeviceStatus.Ok));
    }

    [Fact]
    public async Task Password_step_requires_matching_valid_password()
    {
        await using AddDevicesWizardViewModel wizard = await OpenZeroConfAsync();
        wizard.Discovered.First(d => d.Serial == "B8A44F7788AA").IsSelected = true;
        await wizard.NextCommand.ExecuteAsync(null);
        await wizard.NextCommand.ExecuteAsync(null);
        Assert.Equal(WizardStep.Password, wizard.CurrentStep);

        wizard.NewPassword = "pass1";
        wizard.ConfirmPassword = "pass2";
        Assert.False(wizard.CanGoNext);
        Assert.Equal("The passwords do not match.", wizard.PasswordError);

        wizard.ConfirmPassword = "pass1";
        Assert.True(wizard.CanGoNext);
        await wizard.NextCommand.ExecuteAsync(null);

        // no device needs credentials, so the credentials step is skipped
        Assert.Equal(WizardStep.Review, wizard.CurrentStep);
        Assert.True(wizard.Steps.Single(s => s.Step == WizardStep.Credentials).IsSkipped);
        Assert.Equal("Set root password and add", Assert.Single(wizard.ReviewRows).Action);
        Assert.Equal("pass1", wizard.BuildCommitRequest().InitialRootPassword);
        Assert.Empty(wizard.BuildCommitRequest().Credentials);
    }

    [Fact]
    public async Task Per_device_credentials_when_not_using_one_set_for_all()
    {
        await using AddDevicesWizardViewModel wizard = await OpenZeroConfAsync();
        foreach (DiscoveredRowViewModel row in wizard.Discovered.Where(d => d.Status == DeviceStatus.CredentialsRequired && d.IsSelectable))
        {
            row.IsSelected = true;
        }

        await wizard.NextCommand.ExecuteAsync(null);
        await wizard.NextCommand.ExecuteAsync(null);
        Assert.Equal(WizardStep.Credentials, wizard.CurrentStep);

        wizard.UseForAll = false;
        Assert.True(wizard.ShowPerDeviceCredentials);
        wizard.CredentialDevices[0].UserName = "operator";
        wizard.CredentialDevices[0].Password = "pw0";
        wizard.CredentialDevices[2].Password = "pw2";
        await wizard.NextCommand.ExecuteAsync(null);

        CommitRequest request = wizard.BuildCommitRequest();
        Assert.Equal(2, request.Credentials.Count);
        Assert.Equal("operator", request.Credentials[0].UserName);
        Assert.Equal(wizard.CredentialDevices[0].Item.DiscoveredId, request.Credentials[0].DiscoveredId);
        Assert.Equal("Add, credentials required later", wizard.ReviewRows[1].Action);
        Assert.True(wizard.ReviewRows[1].IsWarning);
    }

    [Fact]
    public async Task Back_returns_to_previous_applicable_step()
    {
        await using AddDevicesWizardViewModel wizard = await OpenZeroConfAsync();
        wizard.Discovered.First(d => d.Serial == "ACCC8E5F6071").IsSelected = true;
        await wizard.NextCommand.ExecuteAsync(null); // host name
        await wizard.NextCommand.ExecuteAsync(null); // credentials (password step not needed)
        Assert.Equal(WizardStep.Credentials, wizard.CurrentStep);

        wizard.BackCommand.Execute(null);
        Assert.Equal(WizardStep.HostName, wizard.CurrentStep);
        wizard.BackCommand.Execute(null);
        Assert.Equal(WizardStep.Select, wizard.CurrentStep);
        Assert.True(wizard.Discovered.First(d => d.Serial == "ACCC8E5F6071").IsSelected);
    }

    [Fact]
    public async Task Search_filters_the_discovered_list()
    {
        await using AddDevicesWizardViewModel wizard = await OpenZeroConfAsync();

        wizard.SearchText = "q6075";

        DiscoveredRowViewModel row = Assert.Single(wizard.FilteredDiscovered);
        Assert.Equal("ACCC8E5F6071", row.Serial);

        wizard.SelectAllCommand.Execute(null); // select all applies to the visible rows only
        Assert.Equal(1, wizard.SelectedCount);

        wizard.SearchText = "";
        Assert.Equal(8, wizard.FilteredDiscovered.Count);
    }

    [Fact]
    public async Task Ip_range_mode_asks_for_range_first_and_reports_scan_progress()
    {
        await using AddDevicesWizardViewModel wizard = Create(AddDevicesMode.IpRange);
        await wizard.OpenAsync();

        Assert.Equal(WizardStep.IpRange, wizard.CurrentStep);
        Assert.Equal(WizardStep.IpRange, wizard.Steps[0].Step);
        Assert.False(wizard.CanGoNext);

        wizard.RangeFrom = "10.0.1.20";
        wizard.RangeTo = "10.0.1.1";
        await wizard.NextCommand.ExecuteAsync(null);
        Assert.Equal(WizardStep.IpRange, wizard.CurrentStep);
        Assert.Equal("The end address must not be lower than the start address.", wizard.ErrorText);

        wizard.RangeFrom = "10.0.1.1";
        wizard.RangeTo = "10.0.1.254";
        await wizard.NextCommand.ExecuteAsync(null);

        Assert.Equal(WizardStep.Select, wizard.CurrentStep);
        Assert.Null(wizard.ErrorText);
        await TestSupport.WaitUntilAsync(() => !wizard.IsScanning);
        Assert.Equal(100, wizard.ScanProgress);
        Assert.Equal(5, wizard.Discovered.Count);
        Assert.All(wizard.Discovered, d => Assert.StartsWith("10.0.1.", d.Address, StringComparison.Ordinal));
        Assert.StartsWith("Scan finished", wizard.ScanStatusText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "", "")]
    [InlineData("abc", "", "")]
    [InlineData("abc", "abd", "The passwords do not match.")]
    [InlineData("abc", "abc", null)]
    [InlineData("pass word!", "pass word!", null)]
    [InlineData("pässword", "pässword", "The password may only contain printable ASCII characters.")]
    public void Password_rules(string password, string confirm, string? expected)
    {
        Assert.Equal(expected, AddDevicesWizardViewModel.ValidatePassword(password, confirm));
    }

    [Fact]
    public void Password_longer_than_64_characters_is_rejected()
    {
        string longPassword = new('a', 65);
        Assert.Equal("The password can be at most 64 characters.", AddDevicesWizardViewModel.ValidatePassword(longPassword, longPassword));
        Assert.Null(AddDevicesWizardViewModel.ValidatePassword(new string('a', 64), new string('a', 64)));
    }

    [Theory]
    [InlineData("192.168.0.1", "192.168.0.254", true)]
    [InlineData("192.168.0.1", "192.168.0.1", true)]
    [InlineData("192.168.0", "192.168.0.10", false)]
    [InlineData("fe80::1", "fe80::2", false)]
    [InlineData("10.0.0.0", "10.0.255.255", true)]
    [InlineData("10.0.0.0", "10.1.0.0", false)]
    public void Range_validation(string from, string to, bool valid)
    {
        Assert.Equal(valid, AddDevicesWizardViewModel.TryParseRange(from, to, out _));
    }
}
