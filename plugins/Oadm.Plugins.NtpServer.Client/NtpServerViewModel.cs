using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.NtpServer.Serving;
using Oadm.Plugins.NtpServer.Upstream;
using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.NtpServer.Client;

/// <summary>One row of "Last requests".</summary>
public sealed class RequestRowViewModel(RequestEntry entry, string device)
{
    public long Seq { get; } = entry.Seq;

    public string Time { get; } = entry.TimeUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public string Client { get; } = entry.Client;

    public string Device { get; } = device;

    public string Offset { get; } = RequestLog.FormatOffset(entry.OffsetMilliseconds);

    public string Result { get; } = entry.Result;

    public bool IsAnswered => Result == RequestEntry.Answered;

    public bool IsLimited => !IsAnswered;

    internal RequestEntry Entry { get; } = entry;
}

/// <summary>
/// The NTP server page: enable, interface, optional upstream, Save; status line; the last 40 requests (pushed live).
/// <see cref="Activate"/> when the page is shown (reads the state, interfaces included, and watches the live events),
/// <see cref="Deactivate"/> when it is hidden. Without live events (older host, fake mode) it re-reads every 2 s.
/// </summary>
public sealed partial class NtpServerViewModel : ObservableObject, INotifyDataErrorInfo, IDisposable
{
    /// <summary>Wait before watching again after the event stream ended or failed.</summary>
    public static TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(2);

    private readonly ICorePluginClientContext _ctx;
    private readonly Dictionary<string, string> _errors = new(StringComparer.Ordinal);
    private Dictionary<string, string> _devicesByAddress = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _active;
    private long _lastSeq;
    private bool _formLoaded;

    public NtpServerViewModel(ICorePluginClientContext ctx)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _ctx.DevicesChanged += (_, _) => OnDevicesChanged();
        BuildDeviceMap();
    }

    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

    public ObservableCollection<InterfaceOption> Interfaces { get; } = [];

    public ObservableCollection<RequestRowViewModel> Requests { get; } = [];

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    [ObservableProperty]
    public partial InterfaceOption? SelectedInterface { get; set; }

    [ObservableProperty]
    public partial string Upstream { get; set; } = string.Empty;

    /// <summary>Result of the last upstream check under the field ("Stratum 2, offset +3 ms, round trip 12 ms").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpstreamResult))]
    public partial string? UpstreamResult { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCheckingUpstream))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsSaving { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Loading";

    [ObservableProperty]
    public partial string? StatusDetail { get; set; }

    [ObservableProperty]
    public partial string StatusKind { get; set; } = "accent";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpstreamInfo))]
    public partial string? UpstreamInfoText { get; set; }

    public bool IsStatusOk => StatusKind == NtpStatusInfo.Ok;

    public bool IsStatusWarning => StatusKind == NtpStatusInfo.Warning;

    public bool IsStatusError => StatusKind == NtpStatusInfo.Error;

    public bool IsStatusAccent => StatusKind == "accent";

    public bool HasUpstreamResult => !string.IsNullOrEmpty(UpstreamResult) && !IsSaving;

    public bool HasUpstreamInfo => !string.IsNullOrEmpty(UpstreamInfoText);

    public bool IsCheckingUpstream => IsSaving && !string.IsNullOrWhiteSpace(Upstream);

    public string CheckingText => $"Checking {Upstream.Trim()}";

    public bool HasRequests => Requests.Count > 0;

    public bool HasErrors => _errors.Count > 0;

    public bool IsActive => _active is not null;

    public IEnumerable GetErrors(string? propertyName) =>
        propertyName is not null && _errors.TryGetValue(propertyName, out var error) ? new[] { error } : Array.Empty<string>();

    /// <summary>Page shown: read the state (interfaces refreshed) and watch the live events.</summary>
    public void Activate()
    {
        if (_active is not null)
        {
            return;
        }

        _active = new CancellationTokenSource();
        _ = RunAsync(_active.Token);
    }

    /// <summary>Page hidden: stop watching.</summary>
    public void Deactivate()
    {
        if (_active is { } active)
        {
            _active = null;
            active.Cancel();
            active.Dispose();
        }
    }

    public void Dispose() => Deactivate();

    /// <summary>Reads the whole state; the form is filled the first time (later reads keep what the user typed).</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await _ctx.InvokeAsync(NtpServerMethods.GetState, null, ct).ConfigureAwait(true);
            if (json is null)
            {
                return;
            }

            var state = NtpJson.Deserialize<NtpState>(json);
            ApplyInterfaces(state.Interfaces, state.Config);
            ApplyStatus(state);
            ApplyRequests(state.Requests, replace: true);
            if (!_formLoaded)
            {
                ApplyForm(state.Config);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Any server error is shown in the status line.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            SetStatus("error", "Cannot read the NTP server state", Message(ex));
        }
    }

    /// <summary>Applies one live event (also used by tests).</summary>
    public void HandleEvent(PluginEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrEmpty(item.PayloadJson))
        {
            return;
        }

        switch (item.Topic)
        {
            case NtpServerMethods.StateTopic:
                ApplyStatus(NtpJson.Deserialize<NtpState>(item.PayloadJson));
                break;
            case NtpServerMethods.RequestsTopic:
                ApplyRequests(NtpJson.Deserialize<RequestsEvent>(item.PayloadJson).Entries, replace: false);
                break;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        ValidateUpstream();
        if (_errors.ContainsKey(nameof(Upstream)))
        {
            return;
        }

        IsSaving = true;
        OnPropertyChanged(nameof(CheckingText));
        UpstreamResult = null;
        try
        {
            var request = new SaveRequest(IsEnabled, SelectedInterface?.Id ?? NtpServerPluginInfo.AllInterfaces, string.IsNullOrWhiteSpace(Upstream) ? null : Upstream.Trim());
            var json = await _ctx.InvokeAsync(NtpServerMethods.Save, NtpJson.Serialize(request), CancellationToken.None).ConfigureAwait(true);
            var reply = NtpJson.Deserialize<SaveReply>(json);
            if (!reply.Saved)
            {
                SetError(nameof(Upstream), reply.UpstreamError ?? "The upstream server could not be checked.");
            }
            else
            {
                ApplyForm(reply.State.Config);
                SetError(nameof(Upstream), null);
                UpstreamResult = reply.UpstreamResult is null ? null : "Answered: " + reply.UpstreamResult;
            }

            ApplyStatus(reply.State);
        }
#pragma warning disable CA1031 // Shown to the user.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            SetStatus(NtpStatusInfo.Error, "Saving failed", Message(ex));
        }
        finally
        {
            IsSaving = false;
            OnPropertyChanged(nameof(HasUpstreamResult));
        }
    }

    private bool CanSave() => !IsSaving;

    partial void OnUpstreamChanged(string value)
    {
        UpstreamResult = null;
        OnPropertyChanged(nameof(CheckingText));
        ValidateUpstream();
    }

    partial void OnStatusKindChanged(string value)
    {
        OnPropertyChanged(nameof(IsStatusOk));
        OnPropertyChanged(nameof(IsStatusWarning));
        OnPropertyChanged(nameof(IsStatusError));
        OnPropertyChanged(nameof(IsStatusAccent));
    }

    private void ValidateUpstream()
    {
        var text = Upstream?.Trim() ?? string.Empty;
        string? error = null;
        if (text.Length > 0)
        {
            UpstreamHost.TryParse(text, out error);
        }

        SetError(nameof(Upstream), error);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var events = _ctx.WatchEventsAsync(ct).GetAsyncEnumerator(ct);
                try
                {
                    // Subscribe first, then read the state: nothing published in between is lost (entries deduplicated by seq).
                    var next = events.MoveNextAsync();
                    await LoadAsync(ct).ConfigureAwait(true);
                    while (await next.ConfigureAwait(true))
                    {
                        HandleEvent(events.Current);
                        next = events.MoveNextAsync();
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
#pragma warning disable CA1031 // The connection dropped: read again and watch again after a short delay.
                catch (Exception)
#pragma warning restore CA1031
                {
                }
                finally
                {
                    await events.DisposeAsync().ConfigureAwait(true);
                }

                await Task.Delay(ReconnectDelay, ct).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            // page hidden
        }
    }

    private void ApplyForm(NtpConfig config)
    {
        _formLoaded = true;
        IsEnabled = config.Enabled;
        Upstream = config.Upstream ?? string.Empty;
        UpstreamResult = null;
        SelectedInterface = Interfaces.FirstOrDefault(i => string.Equals(i.Id, config.InterfaceId, StringComparison.Ordinal)) ?? Interfaces.FirstOrDefault();
    }

    private void ApplyInterfaces(IReadOnlyList<InterfaceOption> options, NtpConfig config)
    {
        if (options.Count == 0)
        {
            return;
        }

        var selectedId = SelectedInterface?.Id ?? config.InterfaceId;
        Interfaces.Clear();
        foreach (var option in options)
        {
            Interfaces.Add(option);
        }

        // A stored interface that is gone stays selectable so the user sees what is configured.
        if (!options.Any(o => string.Equals(o.Id, selectedId, StringComparison.Ordinal)) && string.Equals(selectedId, config.InterfaceId, StringComparison.Ordinal))
        {
            Interfaces.Add(new InterfaceOption(config.InterfaceId, $"{config.InterfaceName ?? config.InterfaceId} (not available)", config.InterfaceName ?? config.InterfaceId, []));
        }

        SelectedInterface = Interfaces.FirstOrDefault(i => string.Equals(i.Id, selectedId, StringComparison.Ordinal)) ?? Interfaces[0];
    }

    private void ApplyStatus(NtpState state)
    {
        SetStatus(state.Status.Kind, state.Status.Text, state.Status.Detail);
        UpstreamInfoText = state.Upstream is { } up && state.Config.Enabled ? DescribeUpstream(up, state.Stratum) : null;
    }

    private void SetStatus(string kind, string text, string? detail)
    {
        StatusKind = kind;
        StatusText = text;
        StatusDetail = detail;
    }

    private void ApplyRequests(IReadOnlyList<RequestEntry> entries, bool replace)
    {
        if (replace)
        {
            Requests.Clear();
            _lastSeq = 0;
            foreach (var entry in entries.OrderByDescending(e => e.Seq).Take(NtpServerPluginInfo.MaxRequests))
            {
                Requests.Add(new RequestRowViewModel(entry, DeviceFor(entry.Client)));
                _lastSeq = Math.Max(_lastSeq, entry.Seq);
            }
        }
        else
        {
            foreach (var entry in entries.Where(e => e.Seq > _lastSeq).OrderBy(e => e.Seq))
            {
                Requests.Insert(0, new RequestRowViewModel(entry, DeviceFor(entry.Client)));
                _lastSeq = entry.Seq;
            }

            while (Requests.Count > NtpServerPluginInfo.MaxRequests)
            {
                Requests.RemoveAt(Requests.Count - 1);
            }
        }

        OnPropertyChanged(nameof(HasRequests));
    }

    private void OnDevicesChanged()
    {
        BuildDeviceMap();
        for (var i = 0; i < Requests.Count; i++)
        {
            var row = Requests[i];
            var device = DeviceFor(row.Client);
            if (!string.Equals(device, row.Device, StringComparison.Ordinal))
            {
                Requests[i] = new RequestRowViewModel(row.Entry, device);
            }
        }
    }

    /// <summary>Address -> "P3265-V (10.0.0.48)"; O(n) once per device change, the 40 rows look up in O(1).</summary>
    private void BuildDeviceMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (IDeviceInfo device in _ctx.Devices)
        {
            map.TryAdd(device.Address, string.IsNullOrEmpty(device.Model) ? device.Address : $"{device.Model} ({device.Address})");
        }

        _devicesByAddress = map;
    }

    private string DeviceFor(string client) => _devicesByAddress.TryGetValue(client, out var label) ? label : "-";

    private void SetError(string property, string? error)
    {
        var changed = error is null ? _errors.Remove(property) : !_errors.TryGetValue(property, out var old) || old != error;
        if (error is not null)
        {
            _errors[property] = error;
        }

        if (changed)
        {
            ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(property));
            OnPropertyChanged(nameof(HasErrors));
        }
    }

    private static string DescribeUpstream(UpstreamInfo upstream, int stratum) => upstream.Reachable switch
    {
        true => string.Create(CultureInfo.InvariantCulture,
            $"Serving stratum {stratum} from {upstream.Host} (stratum {upstream.Stratum}, offset {RequestLog.FormatOffset(upstream.OffsetMilliseconds)})"),
        false => $"Serving the server clock (stratum {stratum}); {upstream.Host}: {upstream.Message}",
        null => $"Serving the server clock (stratum {stratum}) until {upstream.Host} answers",
    };

    /// <summary>gRPC errors carry the user message in Status.Detail; read it without a Grpc reference.</summary>
    private static string Message(Exception ex)
    {
        var detail = ex.GetType().GetProperty("Status")?.GetValue(ex) is { } status
            ? status.GetType().GetProperty("Detail")?.GetValue(status) as string
            : null;
        return string.IsNullOrEmpty(detail) ? ex.Message : detail;
    }
}
