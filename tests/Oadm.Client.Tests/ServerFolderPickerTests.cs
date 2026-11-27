using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Client.Api;
using Oadm.Client.Folders;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Client.Validation;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Client.Tests;

/// <summary>
/// The server folder picker (<see cref="ServerFolderPicker"/>, <see cref="ServerFolderWindow"/>,
/// <see cref="ServerFolderPickerViewModel"/>) against the fake server's folders (a Windows server with C: and D:):
/// browsing, the checks below the path bar and the field, New folder with its name error, Choose. Screenshots with
/// OADM_SCREENSHOT_DIR (OADM_TEST_APPEARANCE=Light for the light ones).
/// </summary>
public sealed partial class ServerFolderPickerTests
{
    private static ApiServerFolders Folders(FakeOadmApi api) => new ApiServerFolders(api);

    [Fact]
    public async Task It_starts_at_the_drives_and_opens_a_folder_that_may_be_chosen()
    {
        using var api = new FakeOadmApi(seedSampleData: false);
        var vm = new ServerFolderPickerViewModel(Folders(api), null);
        await vm.LoadAsync();

        Assert.True(vm.IsAtRoots);
        Assert.Equal(["C:", "Data (D:)"], vm.Folders.Select(f => f.Name));
        Assert.All(vm.Folders, f => Assert.True(f.IsRoot));
        Assert.False(vm.CanChoose);
        Assert.Equal(ServerFolderTexts.ChooseFolder, vm.ChooseTip);
        Assert.Null(vm.ErrorOf(nameof(vm.PathText))); // the drives need no line below the path bar
        Assert.False(vm.UpCommand.CanExecute(null));
        Assert.False(vm.NewFolderCommand.CanExecute(null));

        await vm.OpenFolderCommand.ExecuteAsync(vm.Folders[1]);
        Assert.Equal(@"D:\", vm.PathText);
        Assert.Equal(["Exports", "Plates", "Recordings"], vm.Folders.Select(f => f.Name));
        Assert.Equal(ServerFolderTexts.WholeDrive, vm.ErrorOf(nameof(vm.PathText)));
        Assert.False(vm.CanChoose);
        Assert.True(vm.NewFolderCommand.CanExecute(null)); // a new folder on the drive is the way to a good one

        await vm.OpenFolderCommand.ExecuteAsync(vm.Folders.Single(f => f.Name == "Plates"));
        Assert.Equal((@"D:\Plates", true, null), (vm.PathText, vm.CanChoose, vm.ErrorOf(nameof(vm.PathText))));
        Assert.Equal(["Gate North", "Gate South"], vm.Folders.Select(f => f.Name));
        Assert.True(await vm.ChooseAsync());
        Assert.Equal(@"D:\Plates", vm.ChosenPath);

        await vm.UpCommand.ExecuteAsync(null);
        await vm.UpCommand.ExecuteAsync(null);
        Assert.True(vm.IsAtRoots);
    }

    [Fact]
    public async Task A_refused_or_missing_folder_says_why_below_the_path_bar_and_cannot_be_chosen()
    {
        using var api = new FakeOadmApi(seedSampleData: false);
        var vm = new ServerFolderPickerViewModel(Folders(api), @"D:\Exports");
        await vm.LoadAsync();

        Assert.Equal(@"D:\Exports", vm.PathText);
        Assert.Equal(ServerFolderTexts.OthersCanChange, vm.ErrorOf(nameof(vm.PathText)));
        Assert.Equal(ServerFolderTexts.OthersCanChange, vm.ChooseTip);
        Assert.False(await vm.ChooseAsync());

        // A typed path that is not there: the listing before stays, the reason below the path bar, Choose off until edited.
        vm.PathText = @"D:\Nothing";
        await vm.GoCommand.ExecuteAsync(null);
        Assert.Equal(ServerFolderTexts.NotFound, vm.ErrorOf(nameof(vm.PathText)));
        Assert.Equal(@"D:\Nothing", vm.PathText);
        Assert.Empty(vm.Folders); // D:\Exports has none
        Assert.False(vm.CanChoose);

        // Typing a good path and Choose opens it first.
        vm.PathText = @"d:/plates/gate north";
        Assert.True(vm.CanChoose);
        Assert.True(await vm.ChooseAsync());
        Assert.Equal(@"D:\plates\gate north", vm.ChosenPath);
        Assert.Equal(ServerFolderPickerViewModel.NoFoldersText, vm.EmptyText);
        Assert.True(vm.IsEmpty);

        // A start folder that is gone: the drives, with the reason below the path bar.
        var gone = new ServerFolderPickerViewModel(Folders(api), @"D:\Old");
        await gone.LoadAsync();
        Assert.True(gone.IsAtRoots);
        Assert.Equal((@"D:\Old", ServerFolderTexts.NotFound), (gone.PathText, gone.ErrorOf(nameof(gone.PathText))));
    }

    [Fact]
    public async Task New_folder_checks_the_name_below_the_field_and_opens_the_new_folder()
    {
        using var api = new FakeOadmApi(seedSampleData: false);
        var vm = new ServerFolderPickerViewModel(Folders(api), @"D:\Plates");
        await vm.LoadAsync();

        vm.NewFolderCommand.Execute(null);
        Assert.True(vm.IsCreating);
        Assert.Null(vm.ErrorOf(nameof(vm.NewFolderName))); // untouched: no error yet
        Assert.False(vm.CanChoose);

        vm.NewFolderName = "a/b";
        Assert.Equal(ServerFolderTexts.NameInvalid, vm.ErrorOf(nameof(vm.NewFolderName)));
        vm.NewFolderName = "Gate North";
        await vm.CreateCommand.ExecuteAsync(null);
        Assert.Equal(ServerFolderTexts.NameExists, vm.ErrorOf(nameof(vm.NewFolderName))); // the server's refusal, below the name
        Assert.True(vm.IsCreating);

        vm.NewFolderName = "Gate East";
        Assert.Null(vm.ErrorOf(nameof(vm.NewFolderName)));
        await vm.CreateCommand.ExecuteAsync(null);
        Assert.False(vm.IsCreating);
        Assert.Equal(@"D:\Plates\Gate East", vm.PathText);
        Assert.True(await vm.ChooseAsync());
        Assert.Equal(@"D:\Plates\Gate East", vm.ChosenPath);

        // Cancel closes the editor and forgets its error.
        vm.NewFolderCommand.Execute(null);
        vm.NewFolderName = "?";
        vm.CancelNewFolderCommand.Execute(null);
        Assert.False(vm.IsCreating);
        Assert.True(vm.IsFormValid);
    }

    [Fact]
    public async Task While_a_folder_is_read_the_list_shows_that_it_loads_and_a_newer_open_wins()
    {
        var folders = new GatedFolders();
        var vm = new ServerFolderPickerViewModel(folders, null);
        var first = vm.OpenAsync(@"D:\Slow");
        Assert.True(vm.IsLoading);
        Assert.False(vm.CanChoose);
        Assert.Equal(ServerFolderPickerViewModel.LoadingText, vm.ChooseTip);
        var second = vm.OpenAsync(@"D:\Fast");
        folders.Release(@"D:\Fast");
        Assert.True(await second);
        Assert.False(await first); // cancelled by the newer open
        Assert.False(vm.IsLoading);
        Assert.Equal(@"D:\Fast", vm.PathText);
    }

    [Fact]
    public async Task The_field_shows_its_own_error_or_the_server_check_below_the_text_box_and_takes_a_choice()
    {
        var done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            using var api = new FakeOadmApi(seedSampleData: false);
            var form = new FolderForm();
            var picker = new ServerFolderPicker { Folders = Folders(api) };
            picker.Bind(ServerFolderPicker.PathProperty, new Avalonia.Data.Binding(nameof(FolderForm.LogFolder)) { Mode = Avalonia.Data.BindingMode.TwoWay });
            picker.Bind(ServerFolderPicker.ProblemProperty, new Avalonia.Data.Binding(nameof(FolderForm.FolderProblem)) { Mode = Avalonia.Data.BindingMode.OneWayToSource });
            var field = new FormField { Label = "Folder on the server", Input = picker };
            field.Classes.Add("wide");
            var window = FieldWindow(form, field);
            window.Show();
            Pump();

            Assert.Equal(ServerFolderPicker.ButtonText, picker.Button.GetVisualDescendants().OfType<TextBlock>().Single(t => !string.IsNullOrEmpty(t.Text)).Text);
            Assert.True(picker.Button.IsEnabled);
            Assert.False(field.HasError); // untouched form

            // The view model's own rule: shows below the text box once edited.
            picker.TextBox.Text = "x";
            picker.TextBox.Text = string.Empty;
            Pump();
            Assert.Equal(string.Empty, form.LogFolder);
            await Eventually.TrueAsync(() => field.HasError && DataValidationErrors.GetHasErrors(picker.TextBox), "the rule's error shows");
            Assert.Equal(FolderForm.Required, ErrorText(picker));

            // A typed path the server refuses: its check below the text box after the user leaves it.
            picker.TextBox.Text = @"C:\Windows";
            await picker.CheckAsync();
            Pump();
            Assert.Equal(ServerFolderTexts.SystemFolder, picker.Problem);
            Assert.Equal(ServerFolderTexts.SystemFolder, form.FolderProblem);
            Assert.Equal(ServerFolderTexts.SystemFolder, ErrorText(picker));
            Save(window, "server-folder-field-error.png");

            // A good path: no error, the view model has it.
            form.LogFolder = @"D:\Plates";
            await picker.CheckAsync();
            Pump();
            Assert.Null(picker.Problem);
            Assert.Null(form.FolderProblem);
            Assert.False(field.HasError);
            Assert.Equal(@"D:\Plates", picker.TextBox.Text);
            Save(window, "server-folder-field.png");

            // Without the server's folders (an older host) Choose folder is off.
            picker.Folders = null;
            Assert.False(picker.Button.IsEnabled);
            window.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Fact]
    public async Task The_dialog_lists_the_folders_and_new_folder_turns_choose_secondary()
    {
        var done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            using var api = new FakeOadmApi(seedSampleData: false);
            var window = new ServerFolderWindow(new ServerFolderPickerViewModel(Folders(api), @"D:\Plates")) { Heading = "Folder for the plate log" };
            window.Show();
            var vm = window.ViewModel;
            await Eventually.TrueAsync(() => !vm.IsLoading && vm.Folders.Count == 2, "the folder is listed");
            Pump();

            Assert.False(window.Loading.IsVisible);
            Assert.Equal(@"D:\Plates", window.PathBox.Text);
            Assert.True(window.ChooseButton.IsEnabled);
            Assert.Contains("primary", window.ChooseButton.Classes);
            var names = window.List.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Contains("Gate North", names);
            Assert.Contains("Gate South", names);
            Save(window, "server-folder-dialog.png");

            window.NewFolderButton.Command!.Execute(null);
            Pump();
            Assert.True(window.NameBox.IsEffectivelyVisible);
            Assert.Contains("secondary", window.ChooseButton.Classes);
            Assert.DoesNotContain("primary", window.ChooseButton.Classes);
            Assert.False(window.ChooseButton.IsEnabled);
            window.NameBox.Text = "Gate:North";
            Pump();
            await Eventually.TrueAsync(() => DataValidationErrors.GetHasErrors(window.NameBox), "the name's error shows");
            Assert.Equal(ServerFolderTexts.NameInvalid, DataValidationErrors.GetErrors(window.NameBox)?.OfType<object>().Select(e => e.ToString()).FirstOrDefault());
            Save(window, "server-folder-new-folder-error.png");

            vm.CancelNewFolderCommand.Execute(null);
            Pump();
            Assert.Contains("primary", window.ChooseButton.Classes);

            // Drives: the list of drives with their icons.
            await vm.DrivesCommand.ExecuteAsync(null);
            Pump();
            Assert.False(window.ChooseButton.IsEnabled);
            Save(window, "server-folder-drives.png");
            window.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    private static string? ErrorText(ServerFolderPicker picker) =>
        DataValidationErrors.GetErrors(picker.TextBox)?.OfType<object>().Select(e => e is Exception ex ? ex.Message : e.ToString()).FirstOrDefault();

    private static Window FieldWindow(object dataContext, Control field)
    {
        var card = new Border { Child = field, Margin = new Avalonia.Thickness(16) };
        card.Classes.Add("card");
        return new Window { Width = 820, Height = 160, DataContext = dataContext, Content = card };
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Save(Window window, string name)
    {
        WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        if (!string.IsNullOrEmpty(outDir))
        {
            var appearance = string.Equals(Environment.GetEnvironmentVariable("OADM_TEST_APPEARANCE"), "Light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark";
            Directory.CreateDirectory(outDir);
            frame.Save(Path.Combine(outDir, name.Replace(".png", "-" + appearance + ".png", StringComparison.Ordinal)), PngBitmapEncoderOptions.Default);
        }
    }

    /// <summary>A plugin's settings form: the folder is required, the picker's check is kept for Save.</summary>
    private sealed partial class FolderForm : ValidatingViewModel
    {
        public const string Required = "Choose a folder for the log.";

        public FolderForm()
        {
            Validation.Rule(nameof(LogFolder), () => string.IsNullOrWhiteSpace(LogFolder) ? Required : null);
            Validation.Validate();
        }

        [ObservableProperty]
        public partial string LogFolder { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string? FolderProblem { get; set; }
    }

    /// <summary>Listings that wait until the test releases them; a cancelled one ends with the cancellation.</summary>
    private sealed class GatedFolders : IServerFolders
    {
        private readonly Dictionary<string, TaskCompletionSource> _gates = new(StringComparer.Ordinal);

        public void Release(string path) => Gate(path).TrySetResult();

        private TaskCompletionSource Gate(string path)
        {
            lock (_gates)
            {
                if (!_gates.TryGetValue(path, out var gate))
                {
                    gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _gates[path] = gate;
                }

                return gate;
            }
        }

        public async Task<ServerFolderListing> ListAsync(string? path, CancellationToken ct)
        {
            await Gate(path ?? string.Empty).Task.WaitAsync(ct);
            return new ServerFolderListing(path ?? string.Empty, @"D:\", [], false, null);
        }

        public Task<ServerFolder> CreateAsync(string parentPath, string name, CancellationToken ct) => throw new NotSupportedException();

        public Task<string?> CheckAsync(string path, CancellationToken ct) => Task.FromResult<string?>(null);
    }
}
