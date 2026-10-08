using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Grpc.Core;

using NSubstitute;

using Oadm.Client.Api;
using Oadm.Client.Devices;
using Oadm.Client.Discovery;
using Oadm.Contracts.V1;

namespace Oadm.Client.Tests;

/// <summary>"Set password" for managed devices in factory default: context menu entry, dialog and fake mode.</summary>
public sealed class DeviceSetPasswordTests
{
    private const string Good = "Fresh-Pass-2026";

    private static DevicesFixture CreateWithDevices()
    {
        var f = new DevicesFixture();
        f.SeedDevices(
            TestSupport.Device("1", "B8A44F11AA22", "10.0.0.40", "AXIS F9111", DeviceStatus.PasswordNotSet),
            TestSupport.Device("2", "ACCC8E0A1B2C", "10.0.0.21", "AXIS M3106-L Mk II"),
            TestSupport.Device("3", "B8A44F2E7A90", "10.0.0.23", "AXIS P1468-LE", DeviceStatus.CredentialsRequired),
            TestSupport.Device("4", "00408CA1B2C3", "10.0.0.30", "AXIS Q1798-LE", DeviceStatus.PasswordNotSet));
        return f;
    }

    private static List<DeviceRowViewModel> Rows(int count) =>
        Enumerable.Range(1, count)
            .Select(i => new DeviceRowViewModel(TestSupport.Device(i.ToString(System.Globalization.CultureInfo.InvariantCulture), $"B8A44F11AA{i:D2}", $"10.0.0.{39 + i}", "AXIS F9111", DeviceStatus.PasswordNotSet)))
            .ToList();

    private static void Reply(IOadmApi api, params SetFirstPasswordResult[] results)
    {
        var reply = new SetFirstPasswordReply();
        reply.Results.AddRange(results);
        api.SetFirstPasswordAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(reply);
    }

    private static SetFirstPasswordResult Ok(string id) => new() { DeviceId = id, Ok = true };

    private static SetFirstPasswordResult Failed(string id, string message) => new() { DeviceId = id, Message = message };

    // ------------------------------------------------------------------ context menu

    [Fact]
    public void Set_password_appears_only_for_a_selection_with_a_factory_default_device()
    {
        using DevicesFixture f = CreateWithDevices();

        f.Select("2");
        Assert.DoesNotContain("Set password", f.Devices.ContextMenuEntries.Select(e => e.Header));
        f.Select("3");
        Assert.DoesNotContain("Set password", f.Devices.ContextMenuEntries.Select(e => e.Header));

        f.Select("1", "2", "3", "4");
        Assert.Equal(["Open web interface", "Refresh", "Log in", "Set password", "Tags"], f.Devices.ContextMenuEntries.Select(e => e.Header).Take(5).ToArray());
        MenuEntryViewModel entry = f.Devices.ContextMenuEntries[3];
        Assert.Equal("lock", entry.IconKey);
        Assert.Same(f.Devices.SetPasswordCommand, entry.Command);
        Assert.Equal(["1", "4"], f.Devices.SetPasswordTargets().Select(d => d.Id));
    }

    [Fact]
    public void Set_password_follows_the_status_of_the_selected_devices()
    {
        using DevicesFixture f = CreateWithDevices();
        f.Select("1", "2");
        Assert.Contains("Set password", f.Devices.ContextMenuEntries.Select(e => e.Header));

        f.Store.ApplyBatch([new DeviceChanged { Kind = DeviceChanged.Types.Kind.Updated, Device = TestSupport.Device("1", "B8A44F11AA22", "10.0.0.40", "AXIS F9111") }]);
        Assert.DoesNotContain("Set password", f.Devices.ContextMenuEntries.Select(e => e.Header));

        f.Store.ApplyBatch([new DeviceChanged { Kind = DeviceChanged.Types.Kind.Updated, Device = TestSupport.Device("2", "ACCC8E0A1B2C", "10.0.0.21", "AXIS M3106-L Mk II", DeviceStatus.PasswordNotSet) }]);
        Assert.Contains("Set password", f.Devices.ContextMenuEntries.Select(e => e.Header));
    }

    [Fact]
    public async Task Set_password_opens_the_dialog_for_the_factory_default_devices_only()
    {
        using DevicesFixture f = CreateWithDevices();
        DeviceSetPasswordViewModel? shown = null;
        f.Dialogs.ShowDeviceSetPasswordAsync(Arg.Do<DeviceSetPasswordViewModel>(vm => shown = vm)).Returns(false);
        f.Select("1", "2", "3", "4");

        await f.Devices.SetPasswordCommand.ExecuteAsync(null);

        Assert.NotNull(shown);
        Assert.Equal(["1", "4"], shown.Devices.Select(d => d.Id));
        Assert.Equal("2 devices have no password yet (factory default).", shown.Intro);
        Assert.Equal("root", shown.UserName);
    }

    // ------------------------------------------------------------------ dialog view model

    [Fact]
    public void The_form_checks_like_the_add_page_and_untouched_fields_show_nothing()
    {
        var vm = new DeviceSetPasswordViewModel(Substitute.For<IOadmApi>(), Rows(1));

        Assert.Equal("10.0.0.40 (AXIS F9111) has no password yet (factory default).", vm.Intro);
        Assert.False(vm.SetPasswordCommand.CanExecute(null));
        Assert.Equal("Enter a password.", vm.FormError);
        Assert.Null(vm.ErrorOf(nameof(vm.Password)));
        Assert.Equal(PasswordRules.Hint((string?)null), vm.PolicyHint);

        vm.Password = "pässword";
        Assert.Equal("Use only letters, digits, spaces and standard symbols.", vm.ErrorOf(nameof(vm.Password)));
        vm.Password = Good;
        vm.ConfirmPassword = "other";
        Assert.Equal("The passwords do not match.", vm.ErrorOf(nameof(vm.ConfirmPassword)));
        vm.ConfirmPassword = Good;
        Assert.True(vm.SetPasswordCommand.CanExecute(null));
        Assert.Null(vm.FormError);
    }

    [Fact]
    public async Task The_device_policies_set_the_hint_and_the_strictest_rules()
    {
        var api = Substitute.For<IOadmApi>();
        api.GetPassphrasePoliciesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string> { ["1"] = "length", ["2"] = "complex", ["3"] = "" });
        var vm = new DeviceSetPasswordViewModel(api, Rows(3));
        await vm.LoadAsync(CancellationToken.None);

        Assert.Contains("At least 15 characters with upper and lower case", vm.PolicyHint, StringComparison.Ordinal);
        vm.Password = "Abcdefgh1234!"; // complex, but 13 < 15
        Assert.Equal("This device needs at least 15 characters.", vm.ErrorOf(nameof(vm.Password)));
        vm.Password = "abcdefghijklmnopq"; // long enough, not complex
        Assert.Equal("This device needs upper and lower case, a digit and a symbol.", vm.ErrorOf(nameof(vm.Password)));
        vm.Password = Good;
        Assert.Null(vm.ErrorOf(nameof(vm.Password)));

        vm.ApplyPolicies(["complex"]);
        Assert.Equal(PasswordRules.Hint("complex"), vm.PolicyHint);
        vm.ApplyPolicies(["none"]);
        Assert.Equal(PasswordRules.Hint("none"), vm.PolicyHint);
    }

    [Fact]
    public async Task All_devices_take_the_password_in_one_call_and_the_dialog_closes()
    {
        var api = Substitute.For<IOadmApi>();
        Reply(api, Ok("1"), Ok("2"));
        var vm = new DeviceSetPasswordViewModel(api, Rows(2)) { Password = Good, ConfirmPassword = Good };
        bool? closed = null;
        vm.CloseRequested += (_, ok) => closed = ok;

        await vm.SetPasswordCommand.ExecuteAsync(null);

        Assert.True(closed);
        Assert.Equal(2, vm.SetCount);
        await api.Received(1).SetFirstPasswordAsync(Arg.Is<IReadOnlyCollection<string>>(ids => ids.SequenceEqual(new[] { "1", "2" })), Good, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refusals_stay_below_the_password_and_only_those_devices_stay()
    {
        var api = Substitute.For<IOadmApi>();
        Reply(api, Failed("1", "The device rejected the password: Error: too short."));
        var one = new DeviceSetPasswordViewModel(api, Rows(1)) { Password = Good, ConfirmPassword = Good };
        await one.SetPasswordCommand.ExecuteAsync(null);
        Assert.Equal("The device rejected the password: Error: too short.", one.ErrorOf(nameof(one.Password)));
        Assert.False(one.SetPasswordCommand.CanExecute(null));
        one.Password = Good + "x";
        Assert.Null(one.ErrorOf(nameof(one.Password)));

        Reply(api, Ok("1"), Failed("2", "The device already has a password. Nothing was changed."), Ok("3"));
        var several = new DeviceSetPasswordViewModel(api, Rows(3)) { Password = Good, ConfirmPassword = Good };
        await several.SetPasswordCommand.ExecuteAsync(null);
        Assert.Equal("1 of 3 devices failed: 10.0.0.41 (The device already has a password. Nothing was changed)", several.ErrorOf(nameof(several.Password)));
        Assert.Equal(["2"], several.Devices.Select(d => d.Id));
        Assert.Equal(2, several.SetCount);
    }

    [Fact]
    public async Task A_server_error_shows_below_the_password()
    {
        var api = Substitute.For<IOadmApi>();
        api.SetFirstPasswordAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<SetFirstPasswordReply>>(_ => throw new RpcException(new Status(StatusCode.Unavailable, "Server not reachable")));
        var vm = new DeviceSetPasswordViewModel(api, Rows(1)) { Password = Good, ConfirmPassword = Good };

        await vm.SetPasswordCommand.ExecuteAsync(null);

        Assert.Equal("The password could not be set: Server not reachable", vm.ErrorOf(nameof(vm.Password)));
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Fake_mode_sets_the_password_of_the_factory_default_camera_in_memory()
    {
        using var fake = new FakeOadmApi();
        var devices = new List<Device>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (DeviceChanged change in fake.WatchDevicesAsync(cts.Token))
        {
            if (change.Kind == DeviceChanged.Types.Kind.SnapshotEnd)
            {
                break;
            }

            devices.Add(change.Device);
        }

        Device camera = devices.Single(d => d.Address == "10.0.0.40");
        Device other = devices.First(d => d.Status == DeviceStatus.Ok);
        Assert.Equal(DeviceStatus.PasswordNotSet, camera.Status);
        Assert.Equal(FakeOadmApi.FakePassphrasePolicy, (await fake.GetPassphrasePoliciesAsync([camera.Id], CancellationToken.None))[camera.Id]);

        SetFirstPasswordReply reply = await fake.SetFirstPasswordAsync([camera.Id, other.Id], Good, CancellationToken.None);

        Assert.True(reply.Results[0].Ok);
        Assert.Equal("The device already has a password. Nothing was changed.", reply.Results[1].Message);
    }

    // ------------------------------------------------------------------ headless screenshot

    [Fact]
    public async Task Set_password_dialog_renders()
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        HeadlessUnitTestSession session = HeadlessSession.Shared;
        bool done = await session.Dispatch(async () =>
        {
            var api = Substitute.For<IOadmApi>();
            api.GetPassphrasePoliciesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
                .Returns(new Dictionary<string, string> { ["1"] = "complex" });
            var vm = new DeviceSetPasswordViewModel(api, Rows(1));
            var window = new DeviceSetPasswordWindow();
            window.Attach(vm);
            window.Show();
            await vm.LoadAsync(CancellationToken.None);
            vm.Password = Good;
            vm.ConfirmPassword = "Fresh-Pass";
            Dispatcher.UIThread.RunJobs();

            List<string> texts = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "").ToList();
            Assert.Contains("10.0.0.40 (AXIS F9111) has no password yet (factory default).", texts);
            Assert.Contains(PasswordRules.Hint("complex"), texts);
            Assert.Contains("The passwords do not match.", texts);
            Assert.DoesNotContain(texts, t => t.EndsWith("...", StringComparison.Ordinal));
            Assert.Equal(2, window.GetVisualDescendants().OfType<Oadm.Sdk.Client.Controls.PasswordBox>().Count());

            Dispatcher.UIThread.RunJobs();
            WriteableBitmap? frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            if (!string.IsNullOrEmpty(outDir))
            {
                Directory.CreateDirectory(outDir);
                frame.Save(Path.Combine(outDir, "client-set-password.png"));
            }

            window.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }
}
