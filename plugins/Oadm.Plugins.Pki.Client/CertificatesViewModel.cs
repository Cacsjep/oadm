using System.Diagnostics;
using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Pki.Client;

/// <summary>One installed certificate of one device (a row of the view and delete dialogs).</summary>
public sealed partial class CertificateRow : ObservableObject
{
    private readonly Action<CertificateRow, bool>? _checkedChanged;

    public CertificateRow(IDeviceInfo device, InstalledCertificate certificate, Action<CertificateRow, bool>? checkedChanged = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(certificate);
        Device = device;
        Certificate = certificate;
        _checkedChanged = checkedChanged;
        Group = GroupName(certificate.Kind);
        ValidTo = certificate.NotAfterUtc == default ? string.Empty : certificate.NotAfterUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        InUse = string.Join(", ", certificate.InUse);
        Source = certificate.FromOadm ? "OADM" : "Other";
        BlockedReason = certificate.Factory ? "Axis factory certificate (device ID): it cannot be deleted."
            : certificate.InUse.Count > 0 ? $"In use by {string.Join(" and ", certificate.InUse)}: it cannot be deleted."
            : null;
        SearchText = string.Join('\n', device.Serial, device.Address, certificate.Alias, certificate.IssuedBy, certificate.IssuedTo, InUse, Source).ToUpperInvariant();
    }

    /// <summary>A device that could not be read: one row with the reason.</summary>
    public CertificateRow(IDeviceInfo device, string error)
        : this(device, new InstalledCertificate { Alias = string.Empty, Kind = UnreadableKind, IssuedTo = error })
    {
        Error = error;
        BlockedReason = error;
    }

    /// <summary>Group key of devices that could not be read.</summary>
    public const string UnreadableKind = "unreadable";

    public IDeviceInfo Device { get; }

    public InstalledCertificate Certificate { get; }

    /// <summary>"Server certificates", "Client certificates", "CA certificates", "Devices that could not be read".</summary>
    public string Group { get; }

    public string MacAddress => Device.Serial;

    public string Address => Device.Address;

    public string Alias => Certificate.Alias;

    public string IssuedBy => Certificate.IssuedBy;

    public string IssuedTo => Certificate.IssuedTo;

    public string ValidTo { get; }

    public string InUse { get; }

    public string Source { get; }

    public string? Error { get; }

    public bool IsError => Error is not null;

    /// <summary>Why the row cannot be selected for deletion (tooltip); null = selectable.</summary>
    public string? BlockedReason { get; }

    public bool IsSelectable => BlockedReason is null;

    /// <summary>Upper-case text of the searchable columns.</summary>
    public string SearchText { get; }

    private bool _isChecked;

    /// <summary>Checked for deletion; rows that are not selectable stay unchecked.</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (value && !IsSelectable)
            {
                OnPropertyChanged();
                return;
            }

            if (SetProperty(ref _isChecked, value))
            {
                _checkedChanged?.Invoke(this, value);
            }
        }
    }

    /// <summary>Sets the flag without the per-row callback (select all / none update the count once).</summary>
    internal void SetCheckedSilently(bool value) => SetProperty(ref _isChecked, value, nameof(IsChecked));

    public static string GroupName(string kind) => kind switch
    {
        CertificateKind.Server => "Server certificates",
        CertificateKind.Client => "Client certificates",
        CertificateKind.Ca => "CA certificates",
        _ => "Devices that could not be read",
    };

    /// <summary>Order of the groups: client, server, CA (like ADM), unreadable devices last.</summary>
    public int GroupOrder => Certificate.Kind switch
    {
        CertificateKind.Client => 0,
        CertificateKind.Server => 1,
        CertificateKind.Ca => 2,
        _ => 3,
    };
}

/// <summary>
/// "View installed certificates" and "Delete certificates": reads every selected device through the read-only query
/// <see cref="PkiQueries.ListCertificates"/>, at most 4 devices at a time, and shows the rows grouped Client / Server / CA as
/// they arrive (the visible list is replaced as a whole, at most every <see cref="RebuildInterval"/>). Search and select
/// all are O(n). The delete mode adds check boxes; certificates in use or from the factory cannot be checked.
/// </summary>
public sealed partial class CertificatesViewModel : ObservableObject
{
    public const int MaxParallel = 4;

    private readonly ITaskDialogContext _ctx;
    private readonly IReadOnlyList<IDeviceInfo> _devices;
    private readonly List<CertificateRow> _all = [];
    private readonly Lock _pendingLock = new();
    private List<CertificateRow> _pending = [];
    private readonly Stopwatch _sinceRebuild = Stopwatch.StartNew();
    private int _checked;
    private int _devicesRead;
    private int _failed;
    private CancellationTokenSource? _loading;

    public CertificatesViewModel(ITaskDialogContext ctx, IReadOnlyList<IDeviceInfo> devices, bool deleteMode)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        DeleteMode = deleteMode;
    }

    /// <summary>The list shown after a batch arrived at most this often while loading.</summary>
    public static TimeSpan RebuildInterval { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Runs work on the UI thread (the window sets the dispatcher; tests run inline).</summary>
    public Func<Action, Task> UiThread { get; set; } = action =>
    {
        action();
        return Task.CompletedTask;
    };

    /// <summary>The shared confirmation popup over the dialog (title, message, confirm text); set by the window.</summary>
    public Func<string, string, string, Task<bool>>? Confirm { get; set; }

    public event EventHandler<string?>? CloseRequested;

    public bool DeleteMode { get; }

    public string Title => DeleteMode ? "Delete certificates" : "Installed certificates";

    public string Scope => _devices.Count == 1 ? _devices[0].Address : string.Create(CultureInfo.InvariantCulture, $"{_devices.Count:N0} devices");

    /// <summary>The rows matching the search, grouped order (client, server, CA), replaced as a whole.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<CertificateRow> VisibleRows { get; private set; } = [];

    [ObservableProperty]
    public partial string? SearchText { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial double Progress { get; private set; }

    [ObservableProperty]
    public partial string ProgressText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial int CheckedCount { get; private set; }

    public IReadOnlyList<CertificateRow> AllRows => _all;

    public string DeleteText => CheckedCount == 1 ? "Delete 1 certificate" : string.Create(CultureInfo.InvariantCulture, $"Delete {CheckedCount:N0} certificates");

    public bool CanDelete => CheckedCount > 0 && !IsLoading;

    public string? DeleteBlockedReason => IsLoading ? "Wait until every device was read." : CheckedCount == 0 ? "Check the certificates to delete." : null;

    partial void OnSearchTextChanged(string? value) => Rebuild();

    partial void OnCheckedCountChanged(int value) => NotifyDelete();

    partial void OnIsLoadingChanged(bool value) => NotifyDelete();

    private void NotifyDelete()
    {
        OnPropertyChanged(nameof(DeleteText));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(DeleteBlockedReason));
        DeleteCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Reads every device (4 at a time); rows appear progressively.</summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        if (_loading is not null)
        {
            await _loading.CancelAsync().ConfigureAwait(true);
        }

        var cts = new CancellationTokenSource();
        _loading = cts;
        _all.Clear();
        lock (_pendingLock)
        {
            _pending = [];
        }

        _checked = 0;
        _devicesRead = 0;
        _failed = 0;
        CheckedCount = 0;
        IsLoading = true;
        UpdateProgress();
        Rebuild();
        try
        {
            using var gate = new SemaphoreSlim(MaxParallel);
            var work = _devices.Select(async device =>
            {
                await gate.WaitAsync(cts.Token).ConfigureAwait(false);
                try
                {
                    var rows = await ReadAsync(device, cts.Token).ConfigureAwait(false);
                    bool rebuild;
                    lock (_pendingLock)
                    {
                        _pending.AddRange(rows);
                        _devicesRead++;
                        rebuild = _sinceRebuild.Elapsed >= RebuildInterval;
                        if (rebuild)
                        {
                            _sinceRebuild.Restart();
                        }
                    }

                    if (rebuild)
                    {
                        await UiThread(() => ApplyPending()).ConfigureAwait(false);
                    }
                }
                finally
                {
                    gate.Release();
                }
            }).ToList();
            await Task.WhenAll(work).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return;
        }

        await UiThread(() =>
        {
            ApplyPending();
            IsLoading = false;
            UpdateProgress();
        }).ConfigureAwait(false);
    }

    [RelayCommand]
    private Task Refresh() => LoadAsync();

    /// <summary>Checks every selectable visible row (one count update).</summary>
    [RelayCommand]
    public void SelectAll() => SetChecked(VisibleRows, true);

    [RelayCommand]
    public void SelectNone() => SetChecked(_all, false);

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task Delete()
    {
        var chosen = _all.Where(r => r.IsChecked && r.IsSelectable).ToList();
        if (chosen.Count == 0)
        {
            return;
        }

        var devices = chosen.Select(r => r.Device.Id).Distinct().Count();
        var message = (chosen.Count == 1 ? $"Delete \"{chosen[0].Alias}\"" : string.Create(CultureInfo.InvariantCulture, $"Delete {chosen.Count:N0} certificates"))
            + (devices == 1 ? $" from {chosen[0].Address}?" : string.Create(CultureInfo.InvariantCulture, $" from {devices:N0} devices?"))
            + " A deleted certificate and its key cannot be restored.";
        if (Confirm is null || !await Confirm("Delete certificates", message, "Delete").ConfigureAwait(true))
        {
            return;
        }

        var payload = new DeletePayload
        {
            Devices = chosen.GroupBy(r => r.Device.Id).ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<CertificateRef>)[.. g.Select(r => new CertificateRef(r.Alias, r.Certificate.Kind == CertificateKind.Ca))]),
        };
        CloseRequested?.Invoke(this, PkiJson.Serialize(payload));
    }

    [RelayCommand]
    private void Cancel()
    {
        _loading?.Cancel();
        CloseRequested?.Invoke(this, null);
    }

    private async Task<IReadOnlyList<CertificateRow>> ReadAsync(IDeviceInfo device, CancellationToken ct)
    {
        try
        {
            var json = await _ctx.QueryAsync(device.Id, PkiQueries.ListCertificates, null, ct).ConfigureAwait(false);
            var reply = PkiJson.Deserialize<CertificateListReply>(json);
            if (reply.Error is { } error)
            {
                Interlocked.Increment(ref _failed);
                return [new CertificateRow(device, error)];
            }

            return [.. reply.Certificates.Select(c => new CertificateRow(device, c, OnRowChecked))];
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // A device that cannot be read is one row with the reason; the others go on.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Interlocked.Increment(ref _failed);
            return [new CertificateRow(device, PkiDialogViewModel.Message(ex))];
        }
    }

    private void ApplyPending()
    {
        List<CertificateRow> batch;
        lock (_pendingLock)
        {
            batch = _pending;
            _pending = [];
        }

        _all.AddRange(batch);
        UpdateProgress();
        Rebuild();
    }

    private void UpdateProgress()
    {
        var read = Volatile.Read(ref _devicesRead);
        Progress = _devices.Count == 0 ? 100 : read * 100.0 / _devices.Count;
        ProgressText = string.Create(CultureInfo.InvariantCulture, $"Reading certificates of {read:N0} of {_devices.Count:N0} devices");
        var server = 0;
        var client = 0;
        var ca = 0;
        foreach (var row in _all)
        {
            switch (row.Certificate.Kind)
            {
                case CertificateKind.Server: server++; break;
                case CertificateKind.Client: client++; break;
                case CertificateKind.Ca: ca++; break;
            }
        }

        var failed = Volatile.Read(ref _failed);
        Summary = string.Create(CultureInfo.InvariantCulture, $"{client + server + ca:N0} certificates on {read - failed:N0} devices · {client:N0} client · {server:N0} server · {ca:N0} CA")
            + (failed == 0 ? string.Empty : string.Create(CultureInfo.InvariantCulture, $" · {failed:N0} devices could not be read"))
            + (DeleteMode && CheckedCount > 0 ? string.Create(CultureInfo.InvariantCulture, $" · {CheckedCount:N0} selected") : string.Empty);
    }

    /// <summary>Filters and sorts O(n) into a new list (one reset of the grid).</summary>
    private void Rebuild()
    {
        var search = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim().ToUpperInvariant();
        var rows = new List<CertificateRow>(search is null ? _all.Count : 64);
        foreach (var row in _all)
        {
            if (search is null || row.SearchText.Contains(search, StringComparison.Ordinal))
            {
                rows.Add(row);
            }
        }

        // Stable group order (client, server, CA, unreadable), devices in reading order inside a group.
        VisibleRows = [.. rows.OrderBy(r => r.GroupOrder)];
    }

    private void OnRowChecked(CertificateRow row, bool value)
    {
        _checked += value ? 1 : -1;
        CheckedCount = _checked;
        UpdateProgress();
    }

    private void SetChecked(IReadOnlyList<CertificateRow> rows, bool value)
    {
        foreach (var row in rows)
        {
            if (!row.IsSelectable || row.IsChecked == value)
            {
                continue;
            }

            row.SetCheckedSilently(value);
            _checked += value ? 1 : -1;
        }

        CheckedCount = _checked;
        UpdateProgress();
    }
}
