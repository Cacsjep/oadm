using System.Collections.ObjectModel;
using System.Globalization;
using System.IO.Compression;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.DeviceConfig.Client;

/// <summary>Lets the user choose a configuration file; null = cancelled.</summary>
public interface IConfigFileSource
{
    Task<string?> PickAsync();
}

/// <summary>One settings area of the file, checked = imported.</summary>
public sealed partial class ImportSectionOption : ObservableObject
{
    public ImportSectionOption(string key, string name, string? detail)
    {
        Key = key;
        Name = name;
        Detail = detail;
    }

    public string Key { get; }
    public string Name { get; }

    /// <summary>"183 values" (single file), null for a ZIP.</summary>
    public string? Detail { get; }

    public string Text => Detail is null ? Name : Name + " (" + Detail + ")";

    [ObservableProperty] public partial bool IsChecked { get; set; } = true;
}

/// <summary>One selected device of a ZIP import: the file it gets, or why it is left out.</summary>
public sealed class ImportDeviceRow
{
    public ImportDeviceRow(IDeviceInfo device, string? entry, string status, bool ready)
    {
        ArgumentNullException.ThrowIfNull(device);
        DeviceId = device.Id;
        Address = device.Address;
        Serial = device.Serial;
        Model = device.Model ?? string.Empty;
        Entry = entry;
        Status = status;
        IsReady = ready;
    }

    public Guid DeviceId { get; }
    public string Address { get; }
    public string Serial { get; }
    public string Model { get; }

    /// <summary>The ZIP entry, also shown for a file that cannot be used.</summary>
    public string? Entry { get; }

    public string FileText => Entry ?? string.Empty;

    public string Status { get; }

    public bool IsReady { get; }

    public bool IsWarning => !IsReady;
}

/// <summary>
/// "Import configuration": one <c>.json</c> (from Export configuration or the camera's web interface) goes to every selected
/// device of its model; the ZIP of an export gives each selected device its own file, matched by the MAC address in the file
/// name. The user picks the settings areas; Import confirms, uploads the file and returns the <see cref="ImportPayload"/>.
/// </summary>
public sealed partial class ImportConfigViewModel : ObservableObject
{
    public const string Title = DeviceConfigIds.ImportName;

    public const string IdentityNote = "Host name, IP address and device names stay as they are on each device.";

    public const string PasswordNote = "The file has no passwords. Event recipients that need one must get it again.";

    /// <summary>Warning below the settings areas when the file has connection settings (off by default).</summary>
    public const string ConnectionNote = "Connection settings (HTTPS, HTTP port, login method, IEEE 802.1X, IPv6, web interface) can make the devices unreachable.";

    /// <summary>Added to the confirmation when the connection settings are chosen.</summary>
    public const string UnreachableWarning = "The devices may become unreachable.";

    private readonly IReadOnlyList<IDeviceInfo> _devices;
    private readonly IConfigFileSource _files;
    private readonly Func<string, IProgress<double>, CancellationToken, Task<UploadedFile>> _upload;
    private readonly Func<string, string, string, Task<bool>> _confirm;
    private Dictionary<Guid, string> _targets = [];
    private Dictionary<Guid, string> _leftOut = [];
    private CancellationTokenSource? _uploadCts;

    /// <param name="upload">Uploads the chosen file to the server (<c>ITaskDialogContext.UploadAsync</c>).</param>
    /// <param name="confirm">The shared confirmation window (title, message, confirm text).</param>
    public ImportConfigViewModel(
        IReadOnlyList<IDeviceInfo> devices,
        IConfigFileSource files,
        Func<string, IProgress<double>, CancellationToken, Task<UploadedFile>> upload,
        Func<string, string, string, Task<bool>> confirm)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(upload);
        ArgumentNullException.ThrowIfNull(confirm);
        var seen = new HashSet<Guid>();
        _devices = [.. devices.Where(d => seen.Add(d.Id))];
        _files = files;
        _upload = upload;
        _confirm = confirm;
    }

    public string Scope => _devices.Count == 1
        ? $"Into {_devices[0].Address} ({ModelNames.Display(_devices[0].Model)})."
        : $"Into {DeviceConfigIds.Count(_devices.Count, "selected device")}.";

    public ObservableCollection<ImportSectionOption> Sections { get; } = [];

    /// <summary>Short lines below the settings areas, only those that apply.</summary>
    public ObservableCollection<string> Notes { get; } = [];

    /// <summary>ZIP only: one row per selected device.</summary>
    public RangeObservableCollection<ImportDeviceRow> Rows { get; } = new();

    [ObservableProperty] public partial string? FilePath { get; private set; }
    [ObservableProperty] public partial string? FileName { get; private set; }
    [ObservableProperty] public partial string? FileDetails { get; private set; }
    [ObservableProperty] public partial string? FileError { get; private set; }
    [ObservableProperty] public partial bool IsZip { get; private set; }
    [ObservableProperty] public partial bool IsLoading { get; private set; }
    [ObservableProperty] public partial string? DevicesSummary { get; private set; }
    [ObservableProperty] public partial bool IsUploading { get; private set; }
    [ObservableProperty] public partial double UploadProgress { get; private set; }
    [ObservableProperty] public partial string? UploadText { get; private set; }
    [ObservableProperty] public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    public bool HasSections => Sections.Count > 0;

    /// <summary>The file has connection settings: <see cref="ConnectionNote"/> shows.</summary>
    public bool HasConnectionSettings => Sections.Any(s => s.Key == ConfigSections.Connection);

    /// <summary>Devices that get a file.</summary>
    public int TargetCount => _targets.Count;

    /// <summary>The payload after Import, null before or after Cancel.</summary>
    public string? Result { get; private set; }

    /// <summary>Why Import is disabled, null when it can run.</summary>
    public string? ImportBlockedReason
    {
        get
        {
            if (IsLoading)
            {
                return "Reading the file";
            }

            if (FileError is not null)
            {
                return FileError;
            }

            if (FilePath is null)
            {
                return "Choose a configuration file";
            }

            if (_targets.Count == 0)
            {
                return IsZip ? "No selected device has a file in the ZIP" : "No selected device is of the file's model";
            }

            return Sections.Any(s => s.IsChecked) ? null : "Choose at least one settings area";
        }
    }

    public event EventHandler<string?>? CloseRequested;

    [RelayCommand(CanExecute = nameof(CanBrowse))]
    private async Task BrowseAsync()
    {
        var path = await _files.PickAsync().ConfigureAwait(true);
        if (path is not null)
        {
            await LoadAsync(path).ConfigureAwait(true);
        }
    }

    private bool CanBrowse() => !IsUploading && !IsLoading;

    /// <summary>Reads a <c>.json</c> or a <c>.zip</c> and plans which device gets what.</summary>
    public async Task LoadAsync(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        FilePath = path;
        FileName = Path.GetFileName(path);
        FileDetails = null;
        FileError = null;
        Error = null;
        IsZip = string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase);
        IsLoading = true;
        Clear();
        try
        {
            if (IsZip)
            {
                var plan = await Task.Run(() => ZipPlan.Read(path, _devices)).ConfigureAwait(true);
                Apply(plan);
            }
            else
            {
                var file = await Task.Run(() => ReadFileAsync(path)).ConfigureAwait(true);
                Apply(file);
            }
        }
        catch (ConfigFileException ex)
        {
            FileError = ex.Message;
        }
        catch (InvalidDataException)
        {
            FileError = "Not a ZIP file";
        }
        catch (IOException ex)
        {
            FileError = "The file could not be read: " + ex.Message;
        }
        catch (UnauthorizedAccessException ex)
        {
            FileError = "The file could not be read: " + ex.Message;
        }
        finally
        {
            IsLoading = false;
            Refresh();
        }
    }

    private static async Task<ConfigFile> ReadFileAsync(string path)
    {
        if (new FileInfo(path).Length > DeviceConfigIds.MaxFileBytes)
        {
            throw new ConfigFileException(DeviceConfigIds.NotAConfigFile + ": larger than 2 MB");
        }

        var stream = File.OpenRead(path);
        await using (stream.ConfigureAwait(false))
        {
            return await ConfigFile.ReadAsync(stream, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private void Clear()
    {
        _targets = [];
        _leftOut = [];
        Sections.Clear();
        Notes.Clear();
        Rows.ReplaceAll([]);
        DevicesSummary = null;
    }

    private void Apply(ConfigFile file)
    {
        FileDetails = file.SourceText;
        foreach (var device in _devices)
        {
            if (ModelNames.Same(file.Model, device.Model))
            {
                _targets[device.Id] = string.Empty;
            }
            else
            {
                _leftOut[device.Id] = DeviceConfigIds.OtherModel(file.Model);
            }
        }

        foreach (var section in file.Sections)
        {
            AddSection(new ImportSectionOption(section.Key, section.Name, DeviceConfigIds.Count(section.ValueCount, "value")));
        }

        AddNotes(file.Data.ContainsKey(ConfigSections.Param), file.HasRecipients);
        if (_leftOut.Count > 0)
        {
            Notes.Add(_leftOut.Count == _devices.Count
                ? $"None of the selected devices is an {ModelNames.Display(file.Model)}."
                : string.Create(CultureInfo.InvariantCulture, $"{_leftOut.Count:N0} of {_devices.Count:N0} devices are other models and are left out."));
        }
    }

    private void Apply(ZipPlan plan)
    {
        FileDetails = DeviceConfigIds.Count(plan.FileCount, "configuration file");
        foreach (var row in plan.Rows)
        {
            if (row.IsReady)
            {
                _targets[row.DeviceId] = row.Entry!;
            }
            else
            {
                _leftOut[row.DeviceId] = row.Status;
            }
        }

        Rows.ReplaceAll(plan.Rows);
        foreach (var key in plan.SectionKeys)
        {
            AddSection(new ImportSectionOption(key, ConfigSections.Name(key), null));
        }

        AddNotes(plan.SectionKeys.Contains(ConfigSections.Param), plan.HasRecipients);
        var parts = new List<string> { string.Create(CultureInfo.InvariantCulture, $"{_targets.Count:N0} ready") };
        if (_leftOut.Count > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{_leftOut.Count:N0} left out"));
        }

        if (plan.UnmatchedFiles > 0)
        {
            parts.Add(plan.UnmatchedFiles == 1 ? "1 file has no selected device" : string.Create(CultureInfo.InvariantCulture, $"{plan.UnmatchedFiles:N0} files have no selected device"));
        }

        DevicesSummary = string.Join(" · ", parts);
    }

    private void AddSection(ImportSectionOption option)
    {
        // The connection settings can cut OADM off: the user has to choose them.
        option.IsChecked = option.Key != ConfigSections.Connection;
        option.PropertyChanged += (_, _) => Refresh();
        Sections.Add(option);
    }

    private void AddNotes(bool hasParams, bool hasRecipients)
    {
        if (hasParams)
        {
            Notes.Add(IdentityNote);
        }

        if (hasRecipients)
        {
            Notes.Add(PasswordNote);
        }
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(ImportBlockedReason));
        OnPropertyChanged(nameof(HasSections));
        OnPropertyChanged(nameof(HasConnectionSettings));
        OnPropertyChanged(nameof(TargetCount));
        ImportCommand.NotifyCanExecuteChanged();
        BrowseCommand.NotifyCanExecuteChanged();
    }

    public bool CanImport() => !IsUploading && ImportBlockedReason is null;

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportAsync()
    {
        if (FilePath is not { } path || !CanImport())
        {
            return;
        }

        var devices = DeviceConfigIds.Count(_targets.Count, "device");
        var question = $"Import the configuration into {devices}? The settings in the file replace the current ones.";
        if (Sections.Any(s => s.Key == ConfigSections.Connection && s.IsChecked))
        {
            question += " " + UnreachableWarning;
        }

        if (!await _confirm(Title, question, "Import").ConfigureAwait(true))
        {
            return;
        }

        Error = null;
        IsUploading = true;
        UploadProgress = 0;
        UploadText = "Uploading the file to the server";
        var cts = new CancellationTokenSource();
        _uploadCts = cts;
        Refresh();
        try
        {
            var finished = false;
            var progress = new Progress<double>(p =>
            {
                if (!finished)
                {
                    UploadProgress = Math.Clamp(p <= 1.0 ? p * 100 : p, 0, 100);
                    UploadText = string.Create(CultureInfo.InvariantCulture, $"Uploading the file to the server ({UploadProgress:0} %)");
                }
            });
            var uploaded = await _upload(path, progress, cts.Token).ConfigureAwait(true);
            finished = true;
            Result = new ImportPayload
            {
                FileId = uploaded.Id,
                FileName = FileName ?? uploaded.Name,
                IsZip = IsZip,
                Sections = [.. Sections.Where(s => s.IsChecked).Select(s => s.Key)],
                Files = _targets,
                LeftOut = _leftOut,
            }.ToJson();
            CloseRequested?.Invoke(this, Result);
        }
        catch (OperationCanceledException)
        {
            UploadText = null;
            Error = "Upload cancelled.";
        }
#pragma warning disable CA1031 // Shown in the dialog; the user can try again.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            UploadText = null;
            Error = "The file could not be uploaded to the server: " + ExportConfigViewModel.ErrorText(ex);
        }
        finally
        {
            IsUploading = false;
            _uploadCts = null;
            cts.Dispose();
            Refresh();
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (_uploadCts is { } cts)
        {
            cts.Cancel();
            return;
        }

        Result = null;
        CloseRequested?.Invoke(this, null);
    }

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));
}

/// <summary>What a ZIP of exported configurations gives each selected device (read off the UI thread).</summary>
public sealed class ZipPlan
{
    public required IReadOnlyList<ImportDeviceRow> Rows { get; init; }

    /// <summary>Settings areas of the usable files, in order of first appearance.</summary>
    public required IReadOnlyList<string> SectionKeys { get; init; }

    public required int FileCount { get; init; }

    /// <summary>Configuration files whose name has no selected device's MAC address.</summary>
    public required int UnmatchedFiles { get; init; }

    public required bool HasRecipients { get; init; }

    /// <summary>
    /// Matches every <c>*.json</c> entry to a selected device by the MAC address in its name and reads the matched ones
    /// (one at a time). O(entries + devices).
    /// </summary>
    public static ZipPlan Read(string path, IReadOnlyList<IDeviceInfo> devices)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(devices);
        if (new FileInfo(path).Length > DeviceConfigIds.MaxZipBytes)
        {
            throw new ConfigFileException("The ZIP is larger than 1 GB");
        }

        using var zip = ZipFile.OpenRead(path);
        var entries = zip.Entries
            .Where(e => e.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && !e.FullName.EndsWith('/'))
            .ToList();
        if (entries.Count == 0)
        {
            throw new ConfigFileException("The ZIP has no configuration files");
        }

        if (entries.Count > DeviceConfigIds.MaxZipEntries)
        {
            throw new ConfigFileException(string.Create(CultureInfo.InvariantCulture, $"The ZIP has more than {DeviceConfigIds.MaxZipEntries:N0} files"));
        }

        var bySerial = new Dictionary<string, IDeviceInfo>(StringComparer.Ordinal);
        foreach (var device in devices)
        {
            bySerial.TryAdd(ConfigFileNames.NormalizeSerial(device.Serial), device);
        }

        var perDevice = new Dictionary<Guid, List<ZipArchiveEntry>>();
        var unmatched = 0;
        foreach (var entry in entries)
        {
            var device = ConfigFileNames.SerialsIn(entry.FullName).Select(s => bySerial.GetValueOrDefault(s)).FirstOrDefault(d => d is not null);
            if (device is null)
            {
                unmatched++;
                continue;
            }

            if (!perDevice.TryGetValue(device.Id, out var list))
            {
                perDevice[device.Id] = list = [];
            }

            list.Add(entry);
        }

        var rows = new List<ImportDeviceRow>(devices.Count);
        var keys = new List<string>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var recipients = false;
        foreach (var device in devices)
        {
            if (!perDevice.TryGetValue(device.Id, out var list))
            {
                rows.Add(new ImportDeviceRow(device, null, DeviceConfigIds.NoFile, ready: false));
                continue;
            }

            if (list.Count > 1)
            {
                rows.Add(new ImportDeviceRow(device, string.Join(", ", list.Select(e => e.FullName)), DeviceConfigIds.TwoFiles, ready: false));
                continue;
            }

            var entry = list[0];
            ConfigFile file;
            try
            {
                if (entry.Length > DeviceConfigIds.MaxFileBytes)
                {
                    throw new ConfigFileException(DeviceConfigIds.NotAConfigFile + ": larger than 2 MB");
                }

                using var stream = entry.Open();
                using var buffer = new MemoryStream((int)entry.Length);
                stream.CopyTo(buffer);
                file = ConfigFile.Parse(buffer.ToArray());
            }
            catch (Exception ex) when (ex is ConfigFileException or InvalidDataException)
            {
                rows.Add(new ImportDeviceRow(device, entry.FullName, ex is ConfigFileException ? ex.Message : DeviceConfigIds.NotAConfigFile, ready: false));
                continue;
            }

            if (!ModelNames.Same(file.Model, device.Model))
            {
                rows.Add(new ImportDeviceRow(device, entry.FullName, DeviceConfigIds.OtherModel(file.Model), ready: false));
                continue;
            }

            rows.Add(new ImportDeviceRow(device, entry.FullName, "Ready", ready: true));
            recipients |= file.HasRecipients;
            foreach (var section in file.Sections)
            {
                if (seenKeys.Add(section.Key))
                {
                    keys.Add(section.Key);
                }
            }
        }

        // The connection settings come last, as in a single file and in the task.
        if (keys.Remove(ConfigSections.Connection))
        {
            keys.Add(ConfigSections.Connection);
        }

        return new ZipPlan { Rows = rows, SectionKeys = keys, FileCount = entries.Count, UnmatchedFiles = unmatched, HasRecipients = recipients };
    }
}
