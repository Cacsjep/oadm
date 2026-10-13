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
    /// <summary>At most this many files are read at once (a ZIP counts as one).</summary>
    public const int MaxLicenseFiles = 10_000;

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
    [NotifyPropertyChangedFor(nameof(HasLicenseFiles), nameof(LicenseFilesText))]
    public partial int LicenseFileCount { get; private set; }

    /// <summary>"Install license" mode: the license card instead of the package card, for <see cref="LicenseApplication"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPackageCard), nameof(LicenseTitle), nameof(ShowInstallButton))]
    [NotifyCanExecuteChangedFor(nameof(InstallLicenseCommand))]
    public partial bool IsLicenseMode { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LicenseTitle))]
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
    public string? LicenseProblemText => LicenseKeys.Problems.Count == 0
        ? null
        : string.Join(" ", LicenseKeys.Problems.Take(5).Select(p => $"{p.FileName}: {p.Reason}"))
          + (LicenseKeys.Problems.Count > 5 ? string.Create(CultureInfo.InvariantCulture, $" +{LicenseKeys.Problems.Count - 5:N0} more") : string.Empty);

    public string? InstallLicenseBlockedReason => LicensedCount == 0 ? "None of the selected devices has a usable key in the chosen files." : null;

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
        var set = await Task.Run(() => LicenseKeySet.Read(chosen.Select(ReadFile))).ConfigureAwait(true);
        LicenseFileCount = chosen.Count;
        LicenseKeys = set;
        UpdateLicenseMatches();
    }

    private static (string Name, byte[] Content) ReadFile(string path)
    {
        var name = Path.GetFileName(path);
        try
        {
            var info = new FileInfo(path);
            // Larger than a ZIP of keys may be: not read (Read reports it as not a key).
            return info.Length > LicenseKeySet.MaxZipBytes ? (name, new byte[LicenseKey.MaxBytes + 1]) : (name, File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (name, []);
        }
    }

    partial void OnPackageChanged(EapManifest? value) => UpdateLicenseMatches();

    /// <summary>The application id the keys must have: the selected application's (license mode) or the package's; null = any.</summary>
    private string? LicenseApplicationId()
    {
        var id = IsLicenseMode ? LicenseApplication?.App.ApplicationId : Package?.AppId;
        return id is { Length: > 0 } and not "0" ? id : null;
    }

    /// <summary>Matches the keys to every selected device in one pass (dictionary by MAC address).</summary>
    private void UpdateLicenseMatches()
    {
        var appId = LicenseApplicationId();
        var byDevice = LicenseKeys.ByDevice(null);
        var forApp = UsableKeys(appId);
        var ambiguous = appId is null ? LicenseKeys.DevicesWithSeveralApplications() : [];
        var today = Today();
        var rows = new LicenseKeyRow[Devices.Count];
        var usable = 0;
        for (var i = 0; i < rows.Length; i++)
        {
            var device = Devices[i];
            var mac = LicenseKey.NormalizeMac(device.Device.Serial) ?? string.Empty;
            if (ambiguous.Contains(mac))
            {
                rows[i] = new(device, "Several keys", "The files have keys of several applications for this device. Choose the keys of one application.", false, false, true);
            }
            else if (forApp.TryGetValue(mac, out var key))
            {
                if (key.IsExpired(today))
                {
                    rows[i] = new(device, "Key expired", $"The key expired on {key.ExpirationDate!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.", false, false, true);
                }
                else
                {
                    usable++;
                    rows[i] = new(device, "Key found", "Key " + key.ValidityText + ".", true, false, false);
                }
            }
            else if (byDevice.TryGetValue(mac, out var other))
            {
                rows[i] = new(device, "Other application", $"The key is for application ID {other.ApplicationId}.", false, false, true);
            }
            else
            {
                rows[i] = new(device, "No key", AcapTaskPlugin.Steps.NoKey, false, true, false);
            }
        }

        LicenseRows = rows;
        LicensedCount = usable;
        LicenseSummary = LicenseFileCount == 0
            ? string.Empty
            : string.Create(CultureInfo.InvariantCulture, $"Keys for {usable:N0} of {Devices.Count:N0} {(Devices.Count == 1 ? "device" : "devices")}.");
    }

    /// <summary>The keys of the selected devices that are usable, one per device (the payload).</summary>
    private List<LicenseKeyEntry> PayloadKeys()
    {
        var forApp = UsableKeys(LicenseApplicationId());
        var today = Today();
        var keys = new List<LicenseKeyEntry>();
        foreach (var device in Devices)
        {
            if (LicenseKey.NormalizeMac(device.Device.Serial) is { } mac && forApp.TryGetValue(mac, out var key) && !key.IsExpired(today))
            {
                keys.Add(new LicenseKeyEntry(mac, key.Xml));
            }
        }

        return keys;
    }

    /// <summary>The keys by device for the application (any application when the id is unknown: then only devices with one).</summary>
    private Dictionary<string, LicenseKey> UsableKeys(string? appId)
    {
        var keys = LicenseKeys.ByDevice(appId);
        if (appId is null)
        {
            foreach (var mac in LicenseKeys.DevicesWithSeveralApplications())
            {
                keys.Remove(mac);
            }
        }

        return keys;
    }

    private bool CanInstallLicense() => IsLicenseMode && LicensedCount > 0 && !IsBusy;

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
        });
    }
}
