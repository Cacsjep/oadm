using Avalonia.Controls;
using Avalonia.Platform.Storage;

using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.SystemReport.Client;

/// <summary>Platform parts of the flow (save dialog, progress window); tests replace them.</summary>
public interface ISystemReportUi
{
    /// <summary>Asks where to save the bundle; null = cancelled.</summary>
    Task<ReportTarget?> PickTargetAsync(string suggestedFileName);

    /// <summary>Shows the progress dialog, runs <paramref name="run"/> and returns when the dialog closed.</summary>
    Task ShowAsync(SystemReportViewModel dialog, Func<Task> run);
}

/// <summary>
/// "System report" on the Devices page toolbar (icon button, group Tasks after the task buttons), like ADM's "Get system report":
/// enabled with a selection; asks where to save the ZIP first (<c>oadm-system-reports-&lt;date&gt;.zip</c>), then shows the
/// progress dialog while the server downloads the server report of every selected device and bundles them. Hidden for
/// users with a device group (system reports are for users with all devices).
/// </summary>
public sealed class SystemReportToolbarPlugin : IToolbarPlugin
{
    public const string Text = "System report";

    public const string Tooltip = "Download the system reports of the selected devices as one ZIP";

    public string Id => "oadm.system-report.toolbar";

    /// <summary>The server checks the same permission on every call of the plugin.</summary>
    public string? RequiredPermission => "oadm.system-report:download";

    public int Order => 100;

    public ToolbarGroup Group => ToolbarGroup.Tasks;

    /// <summary>Tooltip and message when more devices are selected than one report holds.</summary>
    public static readonly string TooManyText = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Choose at most {SystemReportPluginInfo.MaxDevices} devices.");

    /// <summary>Replaces the save dialog and the window (tests).</summary>
    public ISystemReportUi? Ui { get; init; }

    public Control CreateControl(IToolbarContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        // Icon only (tooltip = name and purpose): the toolbar must fit the 1280 px minimum window with the rail expanded.
        var button = new ToolbarButton { Text = Text, IconKey = SystemReportPluginInfo.IconKey, Name = "SystemReportButton" };
        ToolTip.SetShowOnDisabled(button, true);
        void Update()
        {
            // System reports are for users with all devices only (the server refuses everyone else).
            button.IsVisible = ctx.HasAllDevices;
            var count = ctx.SelectedCount;
            button.IsEnabled = count is > 0 and <= SystemReportPluginInfo.MaxDevices;
            ToolTip.SetTip(button, count > SystemReportPluginInfo.MaxDevices ? TooManyText : Tooltip);
        }

        Update();
        ctx.SelectionChanged += (_, _) => Update();
        ctx.PermissionsChanged += (_, _) => Update();
        button.Click += async (_, _) => await RunAsync(ctx, Ui ?? new AvaloniaUi(ctx, button)).ConfigureAwait(true);
        return button;
    }

    /// <summary>The whole flow for the current selection. Never throws: problems show in the host's message window.</summary>
    public static async Task RunAsync(IToolbarContext ctx, ISystemReportUi ui)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(ui);
        var devices = ctx.SelectedDevices;
        if (devices.Count == 0)
        {
            return;
        }

        if (devices.Count > SystemReportPluginInfo.MaxDevices)
        {
            await ctx.ShowMessageAsync(Text, TooManyText).ConfigureAwait(true);
            return;
        }

        try
        {
            var target = await ui.PickTargetAsync(ReportBundle.DefaultBundleName(DateTime.Now)).ConfigureAwait(true);
            if (target is null)
            {
                return;
            }

            var dialog = new SystemReportViewModel(
                (method, payload, ct) => ctx.InvokePluginAsync(SystemReportPluginInfo.PluginId, method, payload, ct),
                devices);
            await ui.ShowAsync(dialog, () => dialog.RunAsync(target)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await ctx.ShowMessageAsync(Text, "The system reports could not be downloaded: " + SystemReportViewModel.ErrorText(ex)).ConfigureAwait(true);
        }
    }

    /// <summary>The platform save dialog of the main window and the progress window.</summary>
    private sealed class AvaloniaUi(IToolbarContext ctx, Control anchor) : ISystemReportUi
    {
        private static readonly FilePickerFileType ZipFiles = new("ZIP archives (*.zip)") { Patterns = ["*.zip"], MimeTypes = ["application/zip"] };

        public async Task<ReportTarget?> PickTargetAsync(string suggestedFileName)
        {
            var top = (TopLevel?)ctx.Owner ?? TopLevel.GetTopLevel(anchor) ?? throw new InvalidOperationException("No window to show the save dialog.");
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save system reports",
                SuggestedFileName = suggestedFileName,
                DefaultExtension = "zip",
                FileTypeChoices = [ZipFiles],
                ShowOverwritePrompt = true,
            }).ConfigureAwait(true);
            if (file is null)
            {
                return null;
            }

            var stream = await file.OpenWriteAsync().ConfigureAwait(true);
            return new ReportTarget(stream, file.TryGetLocalPath() ?? file.Name);
        }

        public async Task ShowAsync(SystemReportViewModel dialog, Func<Task> run)
        {
            var window = new SystemReportWindow();
            window.Attach(dialog);
            var owner = ctx.Owner ?? TopLevel.GetTopLevel(anchor) as Window;
            var closed = owner is null ? ShowAlone(window) : window.ShowDialog(owner);
            await run().ConfigureAwait(true);
            await closed.ConfigureAwait(true);
        }

        private static Task ShowAlone(Window window)
        {
            var closed = new TaskCompletionSource();
            window.Closed += (_, _) => closed.TrySetResult();
            window.Show();
            return closed.Task;
        }
    }
}
