using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Client.Validation;

namespace Oadm.Client.Tests;

/// <summary>A small form for the shared validation helper: user name required, password confirmed.</summary>
public sealed partial class SampleFormViewModel : ValidatingViewModel
{
    public SampleFormViewModel()
    {
        Validation
            .Rule(nameof(UserName), () => UserName.Trim().Length == 0 ? "Enter a user name." : null)
            .Rule(nameof(Password), () => Password.Length < 8 ? "Use at least 8 characters." : null)
            .Rule(nameof(Confirm), () => Confirm != Password ? "The passwords do not match." : null)
            .Rule(nameof(Mode), () => Mode == "bad" ? "Choose a mode." : null)
            .Rule(nameof(Count), () => Count is null ? "Enter a number from 1 to 10." : null);
        Validation.Validate();
    }

    [ObservableProperty] public partial string UserName { get; set; } = "";
    [ObservableProperty] public partial string Password { get; set; } = "";
    [ObservableProperty] public partial string Confirm { get; set; } = "";
    [ObservableProperty] public partial string Mode { get; set; } = "a";
    [ObservableProperty] public partial decimal? Count { get; set; } = 3;

    public int ValidationChanges { get; private set; }

    public void Submit() => Validation.ShowAll();

    public void ServerSays(string error) => Validation.SetServerError(nameof(Password), error);

    public void Restart() => Validation.Reset();

    protected override void OnValidationChanged() => ValidationChanges++;
}

public sealed class FormValidationTests
{
    [Fact]
    public void Untouched_form_shows_nothing_but_cannot_be_submitted()
    {
        var vm = new SampleFormViewModel();
        Assert.False(vm.HasErrors);
        Assert.Empty(vm.GetErrors(nameof(SampleFormViewModel.UserName)).Cast<object>());
        Assert.False(vm.IsFormValid);
        Assert.Equal("Enter a user name.", vm.FormError); // tooltip of the disabled submit button
    }

    [Fact]
    public void Error_appears_on_its_property_after_an_edit_and_clears_when_fixed()
    {
        var vm = new SampleFormViewModel();
        var changed = new List<string?>();
        vm.ErrorsChanged += (_, e) => changed.Add(e.PropertyName);

        vm.Password = "short";
        Assert.Equal("Use at least 8 characters.", vm.ErrorOf(nameof(vm.Password)));
        Assert.Null(vm.ErrorOf(nameof(vm.UserName))); // not edited yet
        Assert.Null(vm.ErrorOf(nameof(vm.Confirm)));
        Assert.Equal([nameof(vm.Password)], changed);
        Assert.True(vm.HasErrors);

        vm.Password = "long enough";
        Assert.Null(vm.ErrorOf(nameof(vm.Password)));
        Assert.Empty(vm.GetErrors(nameof(vm.Password)).Cast<object>());
        Assert.False(vm.HasErrors);
    }

    [Fact]
    public void Submit_shows_every_error_and_fixing_all_enables_submit()
    {
        var vm = new SampleFormViewModel();
        vm.Submit();
        Assert.Equal("Enter a user name.", vm.ErrorOf(nameof(vm.UserName)));
        Assert.Equal("Use at least 8 characters.", vm.ErrorOf(nameof(vm.Password)));
        Assert.Null(vm.ErrorOf(nameof(vm.Confirm))); // "" == "": fine

        vm.UserName = "joe";
        vm.Password = "long enough";
        Assert.Equal("The passwords do not match.", vm.ErrorOf(nameof(vm.Confirm))); // depends on Password
        Assert.False(vm.IsFormValid);
        vm.Confirm = "long enough";
        Assert.True(vm.IsFormValid);
        Assert.Null(vm.FormError);
        Assert.False(vm.HasErrors);
        Assert.True(vm.ValidationChanges > 0);
    }

    [Fact]
    public void Server_error_shows_at_once_and_clears_on_the_next_edit()
    {
        var vm = new SampleFormViewModel { UserName = "joe", Password = "long enough", Confirm = "long enough" };
        vm.ServerSays("The login failed.");
        Assert.Equal("The login failed.", vm.ErrorOf(nameof(vm.Password)));
        Assert.False(vm.IsFormValid);

        vm.Password = "another one";
        Assert.Null(vm.ErrorOf(nameof(vm.Password)));
    }

    [Fact]
    public void Reset_hides_errors_until_the_next_edit()
    {
        var vm = new SampleFormViewModel { Password = "x" };
        Assert.NotNull(vm.ErrorOf(nameof(vm.Password)));
        vm.Restart();
        Assert.Null(vm.ErrorOf(nameof(vm.Password)));
        Assert.False(vm.IsFormValid); // still invalid, just not shown
        vm.Password = "y";
        Assert.NotNull(vm.ErrorOf(nameof(vm.Password)));
    }

    [Fact]
    public void Rule_sets_report_several_properties_and_the_first_rule_wins()
    {
        string a = "", b = "";
        var raised = new List<string>();
        var validator = new FormValidator(raised.Add);
        validator.Rule("A", () => a.Length == 0 ? "A is empty." : null);
        validator.Rules(["A", "B"], () => new Dictionary<string, string?>
        {
            ["A"] = a == "x" ? "A must not be x." : "ignored when the first rule fails",
            ["B"] = b.Length == 0 ? "B is empty." : null,
        });
        validator.Validate();
        Assert.Equal("A is empty.", validator.FirstError);
        Assert.Equal(["A", "B"], validator.Fields);
        Assert.Empty(raised);

        validator.ShowAll("B");
        Assert.Equal("B is empty.", validator["B"]);
        Assert.Null(validator["A"]);
        Assert.False(validator.IsValidFor("B"));
        Assert.Equal("B is empty.", validator.FirstErrorOf(["B"]));

        a = "x";
        b = "y";
        validator.Touch("A");
        Assert.Equal("A must not be x.", validator["A"]);
        Assert.True(validator.IsValidFor("B"));
        Assert.Contains("A", raised);
        Assert.Contains("B", raised);
    }

    [Fact]
    public async Task Form_field_shows_the_error_below_the_input_with_the_label_level_with_the_input()
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        HeadlessUnitTestSession session = HeadlessSession.Shared;
        await session.Dispatch(() =>
        {
            var vm = new SampleFormViewModel();
            TextBox user = new() { [!TextBox.TextProperty] = new Avalonia.Data.Binding(nameof(vm.UserName)) };
            PasswordBox password = new() { [!TextBox.TextProperty] = new Avalonia.Data.Binding(nameof(vm.Password)) };
            PasswordBox confirm = new() { [!TextBox.TextProperty] = new Avalonia.Data.Binding(nameof(vm.Confirm)) };
            ComboBox mode = new() { ItemsSource = new[] { "a", "bad" }, [!ComboBox.SelectedItemProperty] = new Avalonia.Data.Binding(nameof(vm.Mode)) };
            NumericUpDown count = new() { Minimum = 1, Maximum = 10, FormatString = "0", [!NumericUpDown.ValueProperty] = new Avalonia.Data.Binding(nameof(vm.Count)) };
            var userField = new FormField { Label = "User name", Hint = "The account on the device", Input = user };
            var passwordField = new FormField { Label = "Password", Input = password };
            var confirmField = new FormField { Label = "Confirm password", Input = confirm };
            var modeField = new FormField { Label = "Mode", Input = mode };
            var countField = new FormField { Label = "Count", Input = count };
            var fileField = new FormField { Label = "File", Input = new TextBlock { Text = "firmware.bin" }, Error = "The file is not an AXIS OS file." };
            var checkField = new FormField { Input = new CheckBox { Content = "Save to credential list" } };
            var form = new StackPanel { Classes = { "form" }, Margin = new Thickness(20), Children = { userField, passwordField, confirmField, modeField, countField, fileField, checkField } };
            var window = new Window { Width = 520, Height = 520, DataContext = vm, Content = new Border { Classes = { "card" }, Child = form } };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            double labelTop = LabelTop(passwordField);
            double inputTop = password.TranslatePoint(default, window)!.Value.Y;
            double inputHeight = password.Bounds.Height;
            Assert.Equal("The account on the device", userField.MessageText); // hint while there is no error
            Assert.Equal("The file is not an AXIS OS file.", fileField.MessageText);
            Assert.True(fileField.HasError);

            vm.Submit();
            vm.Password = "short";
            vm.Confirm = "other";
            vm.Mode = "bad";
            vm.Count = null;
            Dispatcher.UIThread.RunJobs();

            Assert.True(userField.HasError);
            Assert.Null(userField.MessageText); // the hint gives way to the error
            Assert.True(DataValidationErrors.GetHasErrors(user));
            Assert.True(DataValidationErrors.GetHasErrors(mode));
            Assert.True(passwordField.HasError);
            Assert.True(countField.HasError);
            Assert.Equal(32, mode.GetVisualDescendants().OfType<Border>().First(b => b.Name == "Background").Bounds.Height, 0.5);

            // The error sits directly below the input, left aligned with it; the label stays level with the input.
            TextBlock error = password.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("fieldMessage"));
            Assert.Equal("Use at least 8 characters.", error.Text);
            Point errorPos = error.TranslatePoint(default, window)!.Value;
            Point passwordPos = password.TranslatePoint(default, window)!.Value;
            Assert.Equal(passwordPos.X, errorPos.X, 0.5);
            Assert.InRange(errorPos.Y - (passwordPos.Y + inputHeight), 0, 8);
            Assert.Equal(labelTop - inputTop, LabelTop(passwordField) - password.TranslatePoint(default, window)!.Value.Y, 0.5);

            Capture(window, outDir, "form-field-errors.png");

            vm.UserName = "joe";
            vm.Password = "long enough";
            vm.Confirm = "long enough";
            vm.Mode = "a";
            vm.Count = 4;
            Dispatcher.UIThread.RunJobs();
            Assert.False(userField.HasError);
            Assert.Equal("The account on the device", userField.MessageText);
            Assert.Equal(inputHeight, password.Bounds.Height, 0.5); // no space reserved without an error
            Assert.True(vm.IsFormValid);
            window.Close();
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Settings_fields_report_their_errors_below_themselves_and_block_save()
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        HeadlessUnitTestSession session = HeadlessSession.Shared;
        await session.Dispatch(async () =>
        {
            var api = new Oadm.Client.Api.FakeOadmApi(TimeSpan.FromMilliseconds(5));
            using var f = new DevicesFixture(api);
            using var connection = new Oadm.Client.Shell.ServerConnection(f.Api, f.Store, f.Tasks, f.Ui,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<Oadm.Client.Shell.ServerConnection>.Instance);
            var vm = new Oadm.Client.Settings.SettingsViewModel(f.Api, connection, f.Clipboard,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<Oadm.Client.Settings.SettingsViewModel>.Instance);
            await vm.LoadAsync();
            Assert.False(vm.HasErrors); // loaded values: nothing shown
            Assert.True(vm.SaveCommand.CanExecute(null));
            Assert.False(vm.AddCredentialCommand.CanExecute(null)); // no password yet, not shown either
            Assert.Null(vm.ErrorOf(nameof(vm.NewCredentialPassword)));

            var window = new Window { Width = 900, Height = 1100, Content = new Oadm.Client.Settings.SettingsView { DataContext = vm } };
            window.Show();
            vm.PollingIntervalSeconds = null;
            vm.ServerName = " ";
            vm.ListenUrl = "ftp://server";
            vm.NewCredentialPassword = "x";
            vm.NewCredentialPassword = "";
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("Enter a whole number from 5 to 3600.", vm.ErrorOf(nameof(vm.PollingIntervalSeconds)));
            Assert.Equal("Enter a server name.", vm.ErrorOf(nameof(vm.ServerName)));
            Assert.Equal("Enter a URL like http://0.0.0.0:5080.", vm.ErrorOf(nameof(vm.ListenUrl)));
            Assert.Equal("Enter the password.", vm.ErrorOf(nameof(vm.NewCredentialPassword)));
            Assert.False(vm.SaveCommand.CanExecute(null));
            Assert.Equal("Enter a whole number from 5 to 3600.", vm.SaveBlockedReason);

            List<FormField> fields = window.GetVisualDescendants().OfType<FormField>().ToList();
            Assert.True(fields.Single(x => x.Label == "Listen URL").HasError);
            Assert.False(fields.Single(x => x.Label == "Full refresh interval (min)").HasError);
            Capture(window, outDir, "client-settings-errors.png");

            vm.PollingIntervalSeconds = 60;
            vm.ServerName = "acs";
            vm.ListenUrl = "http://*:5080";
            Assert.True(vm.SaveCommand.CanExecute(null));
            Assert.Null(vm.SaveBlockedReason);
            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Parallel_tasks_per_plugin_loads_validates_below_itself_and_saves()
    {
        HeadlessUnitTestSession session = HeadlessSession.Shared;
        await session.Dispatch(async () =>
        {
            var api = new Oadm.Client.Api.FakeOadmApi(TimeSpan.FromMilliseconds(5));
            using var f = new DevicesFixture(api);
            using var connection = new Oadm.Client.Shell.ServerConnection(f.Api, f.Store, f.Tasks, f.Ui,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<Oadm.Client.Shell.ServerConnection>.Instance);
            var vm = new Oadm.Client.Settings.SettingsViewModel(f.Api, connection, f.Clipboard,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<Oadm.Client.Settings.SettingsViewModel>.Instance);
            await vm.LoadAsync();
            Assert.Equal(16m, vm.MaxParallelTasksPerPlugin);

            var window = new Window { Width = 900, Height = 1100, Content = new Oadm.Client.Settings.SettingsView { DataContext = vm } };
            window.Show();
            FormField field = window.GetVisualDescendants().OfType<FormField>().Single(x => x.Label == "Parallel tasks per plugin");
            Assert.Equal("How many devices a task runs on at the same time, e.g. restarts or firmware updates.", field.Hint);

            foreach (decimal? bad in new decimal?[] { null, 0m, 257m, 2.5m })
            {
                vm.MaxParallelTasksPerPlugin = bad;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("Enter a whole number from 1 to 256.", vm.ErrorOf(nameof(vm.MaxParallelTasksPerPlugin)));
                Assert.True(field.HasError);
                Assert.False(vm.SaveCommand.CanExecute(null));
            }

            vm.MaxParallelTasksPerPlugin = 32;
            Dispatcher.UIThread.RunJobs();
            Assert.Null(vm.ErrorOf(nameof(vm.MaxParallelTasksPerPlugin)));
            Assert.False(field.HasError);
            await vm.SaveCommand.ExecuteAsync(null);
            Assert.Equal(32, (await api.GetSettingsAsync(CancellationToken.None)).MaxParallelTasksPerPlugin);

            // A partial update without the field keeps it, like the server.
            await api.SetSettingsAsync(new Oadm.Contracts.V1.ServerSettings { PollingIntervalSeconds = 60, FullRefreshMinutes = 10 }, CancellationToken.None);
            await vm.LoadAsync();
            Assert.Equal(32m, vm.MaxParallelTasksPerPlugin);
            window.Close();
        }, CancellationToken.None);
    }

    private static double LabelTop(FormField field)
    {
        TextBlock label = field.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("fieldLabel"));
        Window window = field.FindAncestorOfType<Window>()!;
        return label.TranslatePoint(default, window)!.Value.Y;
    }

    private static void Capture(Window window, string? outDir, string name)
    {
        WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
            frame.Save(Path.Combine(outDir, name));
        }
    }
}
