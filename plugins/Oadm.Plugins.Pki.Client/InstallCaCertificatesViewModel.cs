using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Pki.Client;

/// <summary>One row of "Install CA certificates": a certificate (merged across files by fingerprint) or a file that cannot be used.</summary>
public sealed partial class CaCertificateRow : ObservableObject
{
    private readonly List<string> _files = [];

    internal CaCertificateRow(CaFileCertificate? certificate, string file, string? fileError)
    {
        Certificate = certificate;
        FileError = fileError;
        _files.Add(file);
        if (certificate is null)
        {
            Status = fileError == CaCertificateFiles.NoCertificate ? "No certificate" : "Cannot be read";
            StatusDetail = fileError;
            IsError = true;
        }
        else if (certificate.Problem is not null)
        {
            Status = certificate.ProblemLabel ?? "Cannot be used";
            StatusDetail = certificate.Problem;
            IsError = true;
        }
        else
        {
            Status = "Ready";
        }
    }

    /// <summary>Null for a file that cannot be used.</summary>
    public CaFileCertificate? Certificate { get; }

    /// <summary>Why the file cannot be used (rows without a certificate).</summary>
    public string? FileError { get; }

    public string Name => Certificate?.Name ?? string.Empty;

    public string IssuedBy => Certificate?.IssuedBy ?? string.Empty;

    public string ValidTo => Certificate is null ? string.Empty : Certificate.NotAfterUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Sort key of the Valid to column.</summary>
    public DateTime NotAfterUtc => Certificate?.NotAfterUtc ?? DateTime.MinValue;

    public string Fingerprint => Certificate is null ? string.Empty : CaCertificateFiles.ShortFingerprint(Certificate.Fingerprint);

    /// <summary>The whole SHA-256 fingerprint (tooltip).</summary>
    public string? FullFingerprint => Certificate is null ? null : "SHA-256 " + Pki.Ca.CaCertificates.ColonFingerprint(Certificate.Fingerprint);

    /// <summary>"ca.pem" or "ca.pem, bundle.crt" (a certificate found in several files is one row).</summary>
    public string Files => string.Join(", ", _files.Select(System.IO.Path.GetFileName));

    /// <summary>The full paths of the files (the row's file key and tooltip).</summary>
    public IReadOnlyList<string> Paths => _files;

    public string Status { get; }

    public string? StatusDetail { get; }

    public bool IsError { get; }

    public bool IsOk => !IsError;

    internal void AddFile(string path)
    {
        if (!_files.Contains(path, StringComparer.Ordinal))
        {
            _files.Add(path);
            OnPropertyChanged(nameof(Files));
        }
    }
}

/// <summary>
/// "Install CA certificates": choose one or more CA certificate files (PEM, also bundles, or DER), several at once, add more,
/// remove rows. One row per certificate (the same certificate in several files is one row), checked like the task: a CA
/// certificate and valid now; files that cannot be read or hold no certificate are a row with the reason. Install sends
/// the usable certificates (PEM, a few KB each: no upload) to every selected device, one task per device. Rows with a
/// problem are left out; Install is disabled while no usable certificate is listed.
/// </summary>
public sealed partial class InstallCaCertificatesViewModel : ObservableObject
{
    private readonly IReadOnlyList<IDeviceInfo> _devices;
    private readonly Func<string, byte[]> _readFile;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, CaCertificateRow> _byFingerprint = new(StringComparer.OrdinalIgnoreCase);
    private int _usable;

    public InstallCaCertificatesViewModel(IReadOnlyList<IDeviceInfo> devices, Func<string, byte[]>? readFile = null, TimeProvider? time = null)
    {
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _readFile = readFile ?? ReadLimited;
        _time = time ?? TimeProvider.System;
    }

    public const string Title = PkiTaskIds.InstallCaName;

    public ICertificateFilePicker? Picker { get; set; }

    /// <summary>The shared confirmation popup (title, message, confirm text); set by the window.</summary>
    public Func<string, string, string, Task<bool>>? Confirm { get; set; }

    public event EventHandler<string?>? CloseRequested;

    public string Scope => _devices.Count == 1 ? _devices[0].Address : string.Create(CultureInfo.InvariantCulture, $"{_devices.Count:N0} selected devices");

    /// <summary>The table (virtualized DataGrid; changed in one step per action).</summary>
    public RangeObservableCollection<CaCertificateRow> Rows { get; } = [];

    /// <summary>"3 CA certificates ready · 1 with a problem".</summary>
    [ObservableProperty]
    public partial string Summary { get; private set; } = "No files chosen.";

    /// <summary>Whole-table problem (too many certificates), shown directly below the table.</summary>
    public string? TableError => _usable > CaCertificateFiles.MaxCertificates
        ? string.Create(CultureInfo.InvariantCulture, $"At most {CaCertificateFiles.MaxCertificates} CA certificates at once; remove {_usable - CaCertificateFiles.MaxCertificates}.")
        : null;

    public bool HasTableError => TableError is not null;

    /// <summary>Why Install is disabled (tooltip), or null.</summary>
    public string? InstallBlockedReason =>
        _devices.Count == 0 ? "No devices are selected."
        : Rows.Count == 0 ? "Choose CA certificate files."
        : _usable == 0 ? "None of the certificates can be installed; see the Status column."
        : TableError;

    public bool CanInstall => InstallBlockedReason is null;

    /// <summary>The certificates Install sends (rows without a problem, in table order).</summary>
    public IReadOnlyList<CaFileCertificate> UsableCertificates => [.. Rows.Where(r => r.IsOk).Select(r => r.Certificate!)];

    [RelayCommand]
    private async Task AddFiles()
    {
        if (Picker is null)
        {
            return;
        }

        var paths = await Picker.PickAsync().ConfigureAwait(true);
        if (paths.Count > 0)
        {
            Add(paths);
        }
    }

    /// <summary>
    /// Reads the files (at most 1 MB each) and adds their certificates: a certificate already listed only gets the file name,
    /// a file chosen again replaces its error row. One collection change.
    /// </summary>
    public void Add(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var now = _time.GetUtcNow().UtcDateTime;
        var added = new List<CaCertificateRow>();
        var pathSet = paths.ToHashSet(StringComparer.Ordinal);
        Rows.RemoveAll(r => r.Certificate is null && pathSet.Contains(r.Paths[0]));
        foreach (var path in paths.Distinct(StringComparer.Ordinal))
        {
            CaFileContents contents;
            try
            {
                contents = CaCertificateFiles.Read(_readFile(path), now);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                contents = new CaFileContents([], "The file cannot be read: " + ex.Message);
            }

            if (contents.Error is not null)
            {
                added.Add(new CaCertificateRow(null, path, contents.Error));
                continue;
            }

            foreach (var certificate in contents.Certificates)
            {
                if (_byFingerprint.TryGetValue(certificate.Fingerprint, out var existing))
                {
                    existing.AddFile(path);
                    continue;
                }

                var row = new CaCertificateRow(certificate, path, null);
                _byFingerprint[certificate.Fingerprint] = row;
                added.Add(row);
            }
        }

        Rows.AddRange(added);
        Recount();
    }

    /// <summary>Removes one row (a certificate, or a file that cannot be used).</summary>
    [RelayCommand]
    private void Remove(CaCertificateRow? row)
    {
        if (row is null || !Rows.Remove(row))
        {
            return;
        }

        if (row.Certificate is not null)
        {
            _byFingerprint.Remove(row.Certificate.Fingerprint);
        }

        Recount();
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task Install()
    {
        var certificates = UsableCertificates;
        var skipped = Rows.Count - certificates.Count;
        var what = certificates.Count == 1 ? $"the CA certificate {certificates[0].Name}" : string.Create(CultureInfo.InvariantCulture, $"{certificates.Count} CA certificates");
        var message = $"Install {what} on {PkiConfirmations.DeviceCount(_devices.Count)}? The devices then trust certificates these CAs issued."
            + (skipped switch
            {
                0 => string.Empty,
                1 => " 1 row with a problem is left out.",
                _ => string.Create(CultureInfo.InvariantCulture, $" {skipped} rows with a problem are left out."),
            });
        if (Confirm is not null && !await Confirm(Title, message, "Install").ConfigureAwait(true))
        {
            return;
        }

        CloseRequested?.Invoke(this, BuildPayload());
    }

    /// <summary>The payload: name and PEM of every usable certificate (public data, kept in memory only).</summary>
    public string BuildPayload() =>
        PkiJson.Serialize(new InstallCaPayload { Certificates = [.. UsableCertificates.Select(c => new CaCertificatePayload(c.Name, c.Pem))] });

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, null);

    private void Recount()
    {
        _usable = 0;
        var problems = 0;
        foreach (var row in Rows)
        {
            if (row.IsOk)
            {
                _usable++;
            }
            else
            {
                problems++;
            }
        }

        Summary = Rows.Count == 0 ? "No files chosen."
            : string.Create(CultureInfo.InvariantCulture, $"{_usable} CA certificate{(_usable == 1 ? string.Empty : "s")} ready")
              + (problems == 0 ? string.Empty : string.Create(CultureInfo.InvariantCulture, $" · {problems} with a problem (left out)"));
        OnPropertyChanged(nameof(TableError));
        OnPropertyChanged(nameof(HasTableError));
        OnPropertyChanged(nameof(InstallBlockedReason));
        OnPropertyChanged(nameof(CanInstall));
        InstallCommand.NotifyCanExecuteChanged();
    }

    private static byte[] ReadLimited(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > CaCertificateFiles.MaxBytes)
        {
            return new byte[CaCertificateFiles.MaxBytes + 1]; // "larger than 1 MB" without reading it
        }

        return File.ReadAllBytes(path);
    }
}
