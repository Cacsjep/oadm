using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Pki.Client;

/// <summary>Picks local PKCS#12 files; implemented by the window with the platform storage provider.</summary>
public interface ICertificateFilePicker
{
    /// <summary>Local paths of the chosen files (empty when cancelled).</summary>
    Task<IReadOnlyList<string>> PickAsync();
}

/// <summary>A purpose choice of the select field.</summary>
public sealed record PurposeChoice(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One chosen file: what it holds and which selected device it matches.</summary>
public sealed partial class InstallFileRow(string path, byte[] data) : ObservableObject
{
    public string Path { get; } = path;

    public byte[] Data { get; } = data;

    public string FileName => System.IO.Path.GetFileName(Path);

    [ObservableProperty]
    public partial string Certificate { get; set; } = string.Empty;

    /// <summary>The matched device ("B8A44F631339 · 10.0.0.48"), empty when none.</summary>
    [ObservableProperty]
    public partial string DeviceText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? StatusDetail { get; set; }

    [ObservableProperty]
    public partial bool IsOk { get; set; }

    [ObservableProperty]
    public partial bool IsError { get; set; }

    /// <summary>The device this file goes to (null when none or not unique).</summary>
    public IDeviceInfo? Device { get; set; }

    public Pkcs12Contents? Contents { get; set; }
}

/// <summary>
/// "Install certificates manually" (like ADM): choose .pfx / .p12 files (one password for all) and the purpose. Each file is
/// matched to one selected device by MAC address, IP address or host name / FQDN in its common name or alternative names; a
/// file must match exactly one device and a device gets at most one file. "CA certificates only" sends every file to every
/// selected device (no matching). Rows that cannot be installed show the reason in
/// the Status column and block Install. Install uploads the matched files (<see cref="ITaskDialogContext.UploadAsync"/>) and
/// returns the payload; the password stays in memory.
/// </summary>
public sealed partial class InstallCertificatesViewModel : ObservableObject
{
    private readonly ITaskDialogContext _ctx;
    private readonly IReadOnlyList<IDeviceInfo> _devices;
    private readonly Func<string, byte[]> _readFile;
    private readonly TimeProvider _time;

    public InstallCertificatesViewModel(ITaskDialogContext ctx, IReadOnlyList<IDeviceInfo> devices, Func<string, byte[]>? readFile = null, TimeProvider? time = null)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _readFile = readFile ?? ReadLimited;
        _time = time ?? TimeProvider.System;
        SelectedPurpose = Purposes[0];
    }

    public const string Title = "Install certificates manually";

    public IReadOnlyList<PurposeChoice> Purposes { get; } =
    [
        new(InstallPurpose.Https, "HTTPS (server certificate)"),
        new(InstallPurpose.Dot1x, "IEEE 802.1X (client certificate)"),
        new(InstallPurpose.CaOnly, "CA certificates only"),
    ];

    public ICertificateFilePicker? Picker { get; set; }

    /// <summary>The shared confirmation popup (title, message, confirm text); set by the window.</summary>
    public Func<string, string, string, Task<bool>>? Confirm { get; set; }

    public event EventHandler<string?>? CloseRequested;

    public string Scope => _devices.Count == 1 ? _devices[0].Address : string.Create(CultureInfo.InvariantCulture, $"{_devices.Count:N0} selected devices");

    [ObservableProperty]
    public partial PurposeChoice SelectedPurpose { get; set; }

    [ObservableProperty]
    public partial string? Password { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<InstallFileRow> Files { get; private set; } = [];

    [ObservableProperty]
    public partial string? PasswordError { get; private set; }

    [ObservableProperty]
    public partial string? FilesError { get; private set; }

    [ObservableProperty]
    public partial string Summary { get; private set; } = "No files chosen.";

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial double UploadProgress { get; private set; }

    [ObservableProperty]
    public partial string UploadStatus { get; private set; } = string.Empty;

    public string FilesText => Files.Count switch
    {
        0 => "No file chosen",
        1 => Files[0].FileName,
        _ => string.Create(CultureInfo.InvariantCulture, $"{Files.Count} files"),
    };

    public bool HasFiles => Files.Count > 0;

    /// <summary>Why Install is disabled (tooltip), or null.</summary>
    public string? InstallBlockedReason =>
        IsBusy ? "Uploading."
        : Files.Count == 0 ? "Choose the certificate files."
        : Files.Any(f => f.IsError) ? "Some files cannot be installed; see the Status column."
        : null;

    public bool CanInstall => InstallBlockedReason is null;

    partial void OnPasswordChanged(string? value) => Evaluate();

    partial void OnSelectedPurposeChanged(PurposeChoice value) => Evaluate();

    partial void OnFilesChanged(IReadOnlyList<InstallFileRow> value)
    {
        OnPropertyChanged(nameof(FilesText));
        OnPropertyChanged(nameof(HasFiles));
    }

    partial void OnIsBusyChanged(bool value) => NotifyInstall();

    [RelayCommand]
    private async Task ChooseFiles()
    {
        if (Picker is null)
        {
            return;
        }

        var paths = await Picker.PickAsync().ConfigureAwait(true);
        if (paths.Count > 0)
        {
            SetFiles(paths);
        }
    }

    /// <summary>Reads the files (at most 1 MB each) and matches them.</summary>
    public void SetFiles(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var rows = new List<InstallFileRow>();
        string? error = null;
        foreach (var path in paths)
        {
            try
            {
                rows.Add(new InstallFileRow(path, _readFile(path)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = $"{System.IO.Path.GetFileName(path)} cannot be read: {ex.Message}";
            }
        }

        FilesError = error;
        Files = rows;
        Evaluate();
    }

    /// <summary>Reads every file with the password and matches it to the selected devices (O(files x devices)).</summary>
    public void Evaluate()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var caOnly = SelectedPurpose.Value == InstallPurpose.CaOnly;
        var wrongPassword = false;
        foreach (var row in Files)
        {
            var contents = CertificateFiles.Read(row.Data, Password, now);
            row.Contents = contents;
            row.Device = null;
            row.DeviceText = string.Empty;
            row.StatusDetail = null;
            if (contents.Error == CertificateFiles.WrongPassword)
            {
                wrongPassword = true;
                SetRow(row, "Cannot be read", error: true, contents.Error);
                row.Certificate = string.Empty;
                continue;
            }

            row.Certificate = contents.LeafSubject ?? string.Create(CultureInfo.InvariantCulture, $"{contents.CertificatePems.Count} certificates");
            if (contents.Error is not null || (!caOnly && !contents.HasLeaf))
            {
                SetRow(row, caOnly ? "Cannot be used" : "No certificate with key", error: true, contents.Error ?? "The file holds no certificate with a private key.");
                continue;
            }

            if (caOnly)
            {
                row.DeviceText = "All selected devices";
                SetRow(row, "Ready", error: false, null);
                continue;
            }

            var matches = _devices.Where(d => CertificateFiles.Matches(contents.Names, d.Serial, d.Address, d.HostName)).Take(2).ToList();
            if (matches.Count == 0)
            {
                SetRow(row, "No matching device", error: true, "No selected device has the MAC address, IP address or host name of this certificate.");
            }
            else if (matches.Count > 1)
            {
                SetRow(row, "Matches several devices", error: true, $"{matches[0].Address} and {matches[1].Address} both match.");
            }
            else
            {
                row.Device = matches[0];
                row.DeviceText = $"{matches[0].Serial} · {matches[0].Address}";
                SetRow(row, "Matched", error: false, null);
            }
        }

        // A device gets at most one file.
        foreach (var group in Files.Where(f => f.Device is not null).GroupBy(f => f.Device!.Id).Where(g => g.Count() > 1))
        {
            foreach (var row in group.Skip(1))
            {
                SetRow(row, "Same device as another file", error: true, $"{group.First().FileName} is installed on {row.Device!.Address}.");
                row.Device = null;
            }
        }

        PasswordError = wrongPassword ? CertificateFiles.WrongPassword : null;
        var matched = Files.Count(f => f.IsOk);
        var withFile = Files.Where(f => f.IsOk && f.Device is not null).Select(f => f.Device!.Id).ToHashSet();
        var without = caOnly || matched == 0 && Files.Count == 0 ? 0 : _devices.Count(d => !withFile.Contains(d.Id));
        Summary = Files.Count == 0 ? "No files chosen."
            : string.Create(CultureInfo.InvariantCulture, $"{matched} of {Files.Count} files matched")
              + (without == 0 ? string.Empty : string.Create(CultureInfo.InvariantCulture, $" · {without:N0} selected devices get no certificate"));
        NotifyInstall();
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task Install()
    {
        var rows = Files.Where(f => f.IsOk).ToList();
        if (SelectedPurpose.Value != InstallPurpose.CaOnly)
        {
            var what = SelectedPurpose.Value == InstallPurpose.Https ? "The web server of each device switches to its certificate." : PkiTaskIds.Dot1xWarning;
            if (Confirm is not null && !await Confirm("Install certificates", $"Install {rows.Count} certificates? {what}", "Install").ConfigureAwait(true))
            {
                return;
            }
        }

        IsBusy = true;
        try
        {
            var files = new List<InstallFile>();
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var index = i;
                UploadStatus = $"Uploading {row.FileName} ({i + 1} of {rows.Count})";
                var progress = new Progress<double>(p => UploadProgress = (index + p) * 100 / rows.Count);
                var uploaded = await _ctx.UploadAsync(row.Path, progress, CancellationToken.None).ConfigureAwait(true);
                files.Add(new InstallFile(row.Device?.Id ?? Guid.Empty, uploaded.Id, row.FileName)); // CA only: for all devices
            }

            CloseRequested?.Invoke(this, PkiJson.Serialize(new InstallPayload { Purpose = SelectedPurpose.Value, Password = Password, Files = files }));
        }
#pragma warning disable CA1031 // Shown in the dialog; the user can retry.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            FilesError = "Upload failed: " + PkiDialogViewModel.Message(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, null);

    private static void SetRow(InstallFileRow row, string status, bool error, string? detail)
    {
        row.Status = status;
        row.StatusDetail = detail;
        row.IsError = error;
        row.IsOk = !error;
    }

    private void NotifyInstall()
    {
        OnPropertyChanged(nameof(InstallBlockedReason));
        OnPropertyChanged(nameof(CanInstall));
        InstallCommand.NotifyCanExecuteChanged();
    }

    private static byte[] ReadLimited(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > CertificateFiles.MaxBytes)
        {
            return new byte[CertificateFiles.MaxBytes + 1]; // "larger than 1 MB" without reading it
        }

        return File.ReadAllBytes(path);
    }
}
