using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Oadm.Plugins.Acap.Client;

/// <summary>Picks local license key files (.key, .xml or a ZIP of them).</summary>
public interface ILicenseKeyPicker
{
    Task<IReadOnlyList<string>> PickLicenseKeysAsync();
}

/// <summary>One selected device and the license key the chosen files have for it.</summary>
public sealed record LicenseKeyRow(DeviceChoice Device, string Verdict, string? Details, bool IsOk, bool IsWarning, bool IsError)
{
    public string DeviceLabel => Device.Label;
}

/// <summary>License keys: chosen with the package (optional) or on their own for the selected application.</summary>
public sealed partial class AcapDialogViewModel
{
    /// <summary>
    /// The start view a page asks for (<see cref="Oadm.Sdk.Client.ITaskDialogContext.StartView"/>): "license" opens the
    /// license part (Install licenses of the Applications page), "license:&lt;package name&gt;" with that application selected.
    /// </summary>
    public const string LicenseView = "license";

    private StartView? _startView;

    /// <summary>The dialog's start view, read once from the context.</summary>
    private sealed record StartView(string? Application)
    {
        public static StartView? Parse(string? view)
        {
            var text = view?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            if (string.Equals(text, LicenseView, StringComparison.OrdinalIgnoreCase))
            {
                return new StartView((string?)null);
            }

            return text.StartsWith(LicenseView + ":", StringComparison.OrdinalIgnoreCase) && text[(LicenseView.Length + 1)..].Trim() is { Length: > 0 } app
                ? new StartView(app)
                : null; // a view the dialog does not know: its usual start
        }
    }

    /// <summary>Opens the license part once the first device's applications are listed (only when the role may install licenses).</summary>
    private void OpenStartView()
    {
        if (_startView is not { } view)
        {
            return;
        }

        _startView = null;
        if (!MayLicense)
        {
            return;
        }

        if (view.Application is { } name && Applications.FirstOrDefault(a => string.Equals(a.PackageName, name, StringComparison.Ordinal)) is { } row)
        {
            SelectedApplication = row;
        }

        LicenseApplication = SelectedApplication is { App.NeedsNoLicense: false } selected ? selected : null;
        IsLicenseMode = true;
    }

    /// <summary>In the license part without an application yet: the one the user selects in the list.</summary>
    partial void OnSelectedApplicationChanged(ApplicationRow? value)
    {
        if (IsLicenseMode && LicenseApplication is null && value is { App.NeedsNoLicense: false })
        {
            LicenseApplication = value;
            if (HasLicenseFiles)
            {
                UpdateLicenseMatches();
            }
        }
    }

    /// <summary>What to do next in the license part: select the application, then choose its keys; null once both are done.</summary>
    public string? LicenseHint => !IsLicenseMode ? null
        : LicenseApplication is null ? "Select the application in the list."
        : !HasLicenseFiles ? "Choose the license keys of the application."
        : null;

    public bool HasLicenseHint => LicenseHint is not null;

    /// <summary>The license card's button: "Choose license keys" first, then "Choose other keys".</summary>
    public string ChooseKeysText => HasLicenseFiles ? "Choose other keys" : "Choose license keys";

    /// <summary>At most this many files are read at once (a ZIP counts as one).</summary>
    public const int MaxLicenseFiles = LicenseKeySet.MaxFiles;

    private ILicenseKeyPicker? _keyPicker;

    /// <summary>Today for the expiry check (tests set it).</summary>
    public Func<DateOnly> Today { get; init; } = () => DateOnly.FromDateTime(DateTime.UtcNow);

    /// <summary>The role may install license keys.</summary>
    public bool MayLicense => _ctx.Has(Key(AcapPermissions.License));

    /// <summary>Keys and problems of the chosen files.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLicenseFiles), nameof(LicenseFilesText), nameof(LicenseProblemText), nameof(HasLicenseProblems))]
    public partial LicenseKeySet LicenseKeys { get; private set; } = LicenseKeySet.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLicenseFiles), nameof(LicenseFilesText), nameof(LicenseHint), nameof(HasLicenseHint), nameof(ChooseKeysText))]
    public partial int LicenseFileCount { get; private set; }

    /// <summary>"Install license" mode: the license card instead of the package card, for <see cref="LicenseApplication"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPackageCard), nameof(LicenseTitle), nameof(ShowInstallButton), nameof(LicenseHint), nameof(HasLicenseHint))]
    [NotifyCanExecuteChangedFor(nameof(InstallLicenseCommand))]
    public partial bool IsLicenseMode { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LicenseTitle), nameof(LicenseHint), nameof(HasLicenseHint), nameof(InstallLicenseBlockedReason))]
    [NotifyCanExecuteChangedFor(nameof(InstallLicenseCommand))]
    public partial ApplicationRow? LicenseApplication { get; private set; }

    /// <summary>One row per selected device (license mode), replaced as a whole.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<LicenseKeyRow> LicenseRows { get; private set; } = [];

    /// <summary>"Keys for 3 of 5 devices" (with the package or in license mode).</summary>
    [ObservableProperty]
    public partial string LicenseSummary { get; private set; } = string.Empty;

    /// <summary>Devices of the selection with a usable key.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallLicenseCommand))]
    [NotifyPropertyChangedFor(nameof(InstallLicenseBlockedReason))]
    public partial int LicensedCount { get; private set; }

    public bool HasLicenseFiles => LicenseFileCount > 0;

    public bool ShowPackageCard => HasPackageFile && !IsLicenseMode;

    public string LicenseTitle => LicenseApplication is { } app ? $"License of {app.Name}" : "License";

    public string LicenseFilesText => LicenseFileCount == 0
        ? string.Empty
        : string.Create(CultureInfo.InvariantCulture, $"{LicenseFileCount:N0} {(LicenseFileCount == 1 ? "file" : "files")}, {LicenseKeys.Keys.Count:N0} {(LicenseKeys.Keys.Count == 1 ? "key" : "keys")}");

    public bool HasLicenseProblems => LicenseKeys.Problems.Count > 0;

    /// <summary>The files that are not usable keys, at most five named.</summary>
    public string? LicenseProblemText => LicenseKeys.ProblemText;

    public string? InstallLicenseBlockedReason =>
        LicenseApplication is null ? "Select the application in the list."
        : LicensedCount == 0 ? "None of the selected devices has a usable key in the chosen files."
        : null;

    /// <summary>Called by the window: the same object as the package picker.</summary>
    public void UseKeyPicker(ILicenseKeyPicker picker) => _keyPicker = picker;

    private bool CanLicenseSelection() => SelectedApplication is { App.NeedsNoLicense: false } && !IsBusy && _keyPicker is not null;

    /// <summary>The tooltip of a disabled Install license.</summary>
    public string? LicenseSelectionBlockedReason =>
        SelectedApplication is null ? "Select an application."
        : SelectedApplication.App.NeedsNoLicense ? "This application needs no license."
        : null;

    /// <summary>Toolbar "Install license": choose key files for the selected application.</summary>
    [RelayCommand(CanExecute = nameof(CanLicenseSelection))]
    private async Task LicenseSelectionAsync()
    {
        var app = SelectedApplication!;
        var paths = await _keyPicker!.PickLicenseKeysAsync().ConfigureAwait(true);
        if (paths.Count == 0)
        {
            return;
        }

        LicenseApplication = app;
        IsLicenseMode = true;
        await LoadLicenseFilesAsync(paths).ConfigureAwait(true);
    }

    /// <summary>The package card's "Choose license keys".</summary>
    [RelayCommand]
    private async Task PickLicenseKeysAsync()
    {
        if (_keyPicker is null)
        {
            return;
        }

        var paths = await _keyPicker.PickLicenseKeysAsync().ConfigureAwait(true);
        if (paths.Count > 0)
        {
            await LoadLicenseFilesAsync(paths).ConfigureAwait(true);
        }
    }

    /// <summary>Forgets the chosen keys (package card).</summary>
    [RelayCommand]
    private void ClearLicenseKeys()
    {
        LicenseKeys = LicenseKeySet.Empty;
        LicenseFileCount = 0;
        UpdateLicenseMatches();
    }

    [RelayCommand]
    private void CloseLicense()
    {
        IsLicenseMode = false;
        LicenseApplication = null;
        ClearLicenseKeys();
    }

    /// <summary>Reads the files off the UI thread, then matches the keys to the selected devices (one pass).</summary>
    public async Task LoadLicenseFilesAsync(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var chosen = paths.Take(MaxLicenseFiles).ToList();
        var set = await Task.Run(() => LicenseKeySet.ReadFiles(chosen)).ConfigureAwait(true);
        LicenseFileCount = chosen.Count;
        LicenseKeys = set;
        UpdateLicenseMatches();
    }

    partial void OnPackageChanged(EapManifest? value) => UpdateLicenseMatches();

    /// <summary>The application id the keys must have: the selected application's (license mode) or the package's; null = any.</summary>
    private string? LicenseApplicationId() =>
        LicenseKeyMatcher.ApplicationIdOf(IsLicenseMode ? LicenseApplication?.App.ApplicationId : Package?.AppId);

    /// <summary>Matches the keys to every selected device in one pass (the shared rules, a dictionary by MAC address).</summary>
    private void UpdateLicenseMatches()
    {
        var appId = LicenseApplicationId();
        var matcher = new LicenseKeyMatcher(LicenseKeys);
        var today = Today();
        var rows = new LicenseKeyRow[Devices.Count];
        var usable = 0;
        for (var i = 0; i < rows.Length; i++)
        {
            var device = Devices[i];
            var match = matcher.Match(device.Device.Serial, appId, today);
            usable += match.IsUsable ? 1 : 0;
            rows[i] = new(device, match.Text, match.Details, match.IsUsable, match.IsWarning, match.IsError);
        }

        LicenseRows = rows;
        LicensedCount = usable;
        LicenseSummary = LicenseFileCount == 0
            ? string.Empty
            : string.Create(CultureInfo.InvariantCulture, $"Keys for {usable:N0} of {Devices.Count:N0} {(Devices.Count == 1 ? "device" : "devices")}.");
    }

    /// <summary>
    /// The usable keys of the selected devices (the payload): every key of the device for the application that has not
    /// expired, one per version range; the task installs the one that fits the installed version.
    /// </summary>
    private List<LicenseKeyEntry> PayloadKeys()
    {
        var appId = LicenseApplicationId();
        var matcher = new LicenseKeyMatcher(LicenseKeys);
        var today = Today();
        var keys = new List<LicenseKeyEntry>();
        foreach (var device in Devices)
        {
            keys.AddRange(matcher.PayloadKeys(device.Device.Serial, appId, today));
        }

        return keys;
    }

    /// <summary>The application id of the keys for the payload (the task checks it when the device reports none).</summary>
    private string? PayloadApplicationId() => LicenseApplicationId();

    private bool CanInstallLicense() => IsLicenseMode && LicenseApplication is not null && LicensedCount > 0 && !IsBusy;

    /// <summary>Completes with the license payload: one task per selected device; devices without a key end with a warning.</summary>
    [RelayCommand(CanExecute = nameof(CanInstallLicense))]
    private void InstallLicense()
    {
        var app = LicenseApplication!;
        Complete(new AcapPayload
        {
            Action = AcapAction.InstallLicense,
            Application = app.PackageName,
            AppDisplayName = app.Name,
            LicenseKeys = PayloadKeys(),
            LicenseApplicationId = PayloadApplicationId(),
        });
    }
}
