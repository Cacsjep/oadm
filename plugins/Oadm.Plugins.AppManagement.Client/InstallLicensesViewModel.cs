using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.Acap;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Collections;

namespace Oadm.Plugins.AppManagement.Client;

/// <summary>One selected application on its device and the key the chosen files have for it.</summary>
public sealed class LicenseTargetRow
{
    /// <summary>The row of an application that needs no license (its License column says "Not required"): skipped.</summary>
    public const string NoLicenseNeeded = "This application needs no license.";

    public LicenseTargetRow(AppRow row, LicenseKeyMatch? match)
    {
        ArgumentNullException.ThrowIfNull(row);
        Row = row;
        Match = match;
        if (row.App is { NeedsNoLicense: true })
        {
            Verdict = NoLicenseNeeded;
        }
        else if (match is not null)
        {
            Verdict = match.Text;
            Details = match.Details;
            IsOk = match.IsUsable;
            IsWarning = match.IsWarning;
            IsError = match.IsError;
        }
    }

    public AppRow Row { get; }

    public LicenseKeyMatch? Match { get; }

    public Guid DeviceId => Row.DeviceId;

    public string Address => Row.Address;

    public string AddressSortKey => Row.AddressSortKey;

    public string Model => Row.Model;

    public string Application => Row.AppName;

    /// <summary>The license as the page shows it ("2027-09-18", "Missing", "Not required").</summary>
    public string License => Row.LicenseText;

    /// <summary>"Key found", "No key", "Key expired", ...; empty before files are chosen.</summary>
    public string Verdict { get; } = string.Empty;

    public string? Details { get; }

    public bool IsOk { get; }

    public bool IsWarning { get; }

    public bool IsError { get; }

    /// <summary>A result to show (none before files are chosen).</summary>
    public bool HasVerdict => Verdict.Length > 0;

    /// <summary>The row gets a task: an application that needs a license and a usable key.</summary>
    public bool CanInstall => Match is { IsUsable: true } && Row.App is { NeedsNoLicense: false };

    public string ResultSortKey => (IsError ? "0" : IsWarning ? "1" : IsOk ? "2" : "3") + Verdict;
}

/// <summary>
/// The page's Install licenses dialog (docs/spec/app-management.md): the selected application rows, license key files (one,
/// many or ZIPs), the key of each row matched by MAC address and application id with the Applications plugin's own rules
/// (<see cref="LicenseKeyMatcher"/>, linked source), then the license tasks through the page's server part, one run per
/// application with the keys of its devices.
/// </summary>
public sealed partial class InstallLicensesViewModel : ObservableObject
{
    public const string Title = "Install licenses";
    public const string ChooseKeysFirst = "Choose the license key files.";
    public const string NoKeyFits = "No key fits the selected applications.";

    private readonly ICorePluginClientContext _ctx;
    private readonly Func<DateOnly> _today;
    private LicenseKeySet _keys = LicenseKeySet.Empty;
    private int _fileCount;

    public InstallLicensesViewModel(ICorePluginClientContext ctx, IReadOnlyList<AppRow> rows, Func<DateOnly> today)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        ArgumentNullException.ThrowIfNull(rows);
        _today = today ?? throw new ArgumentNullException(nameof(today));
        Targets = [.. rows.Where(r => r.App is not null)];
        Rows.ReplaceAll(Targets.Select(r => new LicenseTargetRow(r, null)));
    }

    /// <summary>Raised when the dialog closes: true when the tasks were started.</summary>
    public event EventHandler<bool>? CloseRequested;

    /// <summary>Opens the file picker (set by the window).</summary>
    public IAppFilePicker? Picker { get; set; }

    public IReadOnlyList<AppRow> Targets { get; }

    public RangeObservableCollection<LicenseTargetRow> Rows { get; } = [];

    [ObservableProperty]
    public partial string? FileName { get; private set; }

    /// <summary>"3 files, 5 keys".</summary>
    [ObservableProperty]
    public partial string? FileDetails { get; private set; }

    /// <summary>The chosen files that are not usable keys (at most five named).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblems))]
    public partial string? ProblemText { get; private set; }

    public bool HasProblems => !string.IsNullOrEmpty(ProblemText);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallBlockedReason))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(ChooseFilesCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string? BusyText { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallBlockedReason))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    public partial int FitCount { get; private set; }

    /// <summary>"Keys for 3 of 5 applications." once files are chosen.</summary>
    [ObservableProperty]
    public partial string? Summary { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; private set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    public string? InstallBlockedReason =>
        IsBusy ? null
        : _fileCount == 0 ? ChooseKeysFirst
        : FitCount == 0 ? NoKeyFits
        : null;

    private bool CanChoose => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanChoose))]
    private async Task ChooseFilesAsync()
    {
        if (Picker is { } picker && await picker.PickLicenseKeysAsync().ConfigureAwait(true) is { Count: > 0 } paths)
        {
            await LoadFilesAsync(paths).ConfigureAwait(true);
        }
    }

    /// <summary>Reads the key files off the UI thread (the Applications plugin's reader), then matches every row.</summary>
    public async Task LoadFilesAsync(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0)
        {
            return;
        }

        IsBusy = true;
        BusyText = "Reading the files";
        try
        {
            var chosen = paths.Take(LicenseKeySet.MaxFiles).ToList();
            var keys = await Task.Run(() => LicenseKeySet.ReadFiles(chosen)).ConfigureAwait(true);
            ApplyKeys(keys, chosen.Count, chosen.Count == 1 ? Path.GetFileName(chosen[0]) : null);
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
        }
    }

    /// <summary>The keys of the chosen files; every row is matched in one pass (tests call it directly).</summary>
    internal void ApplyKeys(LicenseKeySet keys, int fileCount, string? fileName = null)
    {
        ArgumentNullException.ThrowIfNull(keys);
        _keys = keys;
        _fileCount = fileCount;
        FileName = fileName ?? string.Create(CultureInfo.CurrentCulture, $"{fileCount:N0} {(fileCount == 1 ? "file" : "files")}");
        FileDetails = string.Create(CultureInfo.CurrentCulture, $"{fileCount:N0} {(fileCount == 1 ? "file" : "files")}, {keys.Keys.Count:N0} {(keys.Keys.Count == 1 ? "key" : "keys")}");
        ProblemText = keys.ProblemText;
        var matcher = new LicenseKeyMatcher(keys);
        var today = _today();
        var rows = new LicenseTargetRow[Targets.Count];
        var fit = 0;
        for (var i = 0; i < rows.Length; i++)
        {
            var target = Targets[i];
            rows[i] = new LicenseTargetRow(target, matcher.Match(target.Mac, target.App!.ApplicationId, today));
            fit += rows[i].CanInstall ? 1 : 0;
        }

        Rows.ReplaceAll(rows);
        FitCount = fit;
        Summary = string.Create(CultureInfo.CurrentCulture, $"Keys for {fit:N0} of {rows.Length:N0} {(rows.Length == 1 ? "application" : "applications")}.");
        OnPropertyChanged(nameof(InstallBlockedReason));
    }

    /// <summary>One run per application: the devices with a usable key and those keys (the task matches them again).</summary>
    internal RunRequest BuildRequest()
    {
        var matcher = new LicenseKeyMatcher(_keys);
        var today = _today();
        var items = new List<RunItem>();
        foreach (var group in Rows.Where(r => r.CanInstall).GroupBy(r => r.Row.AppKey!, StringComparer.Ordinal))
        {
            var ids = new List<Guid>();
            var seen = new HashSet<Guid>();
            var keys = new List<LicenseKeyEntry>();
            string? appId = null;
            foreach (var row in group)
            {
                if (!seen.Add(row.DeviceId))
                {
                    continue;
                }

                ids.Add(row.DeviceId);
                var id = LicenseKeyMatcher.ApplicationIdOf(row.Row.App!.ApplicationId);
                appId ??= id;
                keys.AddRange(matcher.PayloadKeys(row.Row.Mac, id, today));
            }

            items.Add(new RunItem { Application = group.Key, DisplayName = group.First().Application, DeviceIds = ids, LicenseKeys = keys, LicenseApplicationId = appId });
        }

        return new RunRequest { Action = AcapAction.InstallLicense, Items = items, Owner = _ctx.OwnerName };
    }

    private bool CanInstall => !IsBusy && FitCount > 0;

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        IsBusy = true;
        BusyText = "Starting the tasks";
        ErrorText = null;
        try
        {
            await _ctx.InvokeAsync(AppManagementMethods.Run, AppManagementJson.Serialize(BuildRequest()), CancellationToken.None).ConfigureAwait(true);
            CloseRequested?.Invoke(this, true);
        }
#pragma warning disable CA1031 // Shown in the dialog.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorText = ServerErrors.Message(ex);
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);
}
