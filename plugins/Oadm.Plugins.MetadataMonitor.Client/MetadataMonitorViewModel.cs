using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.MetadataMonitor.Client;

/// <summary>
/// The Metadata Monitor page: pick one camera, Start, watch its events live. The server opens the event stream and
/// pushes the messages (topic <c>messages</c>, batched) and the stream state (<c>state</c>); the page keeps the newest
/// <see cref="MetadataMonitorPluginInfo.MaxClientMessages"/>, filters them live and shows the selected message's XML.
/// <see cref="Activate"/> when the page is shown (watches the events), <see cref="Deactivate"/> when it is hidden
/// (stops the stream and the watch). While a stream runs the page confirms it every few seconds (keep-alive), so the
/// server ends streams of closed clients.
/// </summary>
public sealed partial class MetadataMonitorViewModel : ObservableObject, IDisposable
{
    private const int MaxEarlyEvents = 64;

    private readonly ICorePluginClientContext _ctx;
    private readonly MetadataClientSettingsStore _settingsStore;
    private readonly MetadataClientSettings _settings;
    private readonly List<MessageRow> _all = [];
    private readonly List<PluginEvent> _early = [];
    private List<CameraOption> _allCameras = [];
    private CancellationTokenSource? _active;
    private CancellationTokenSource? _keepAlive;
    private string? _streamId;
    private bool _starting;
    private bool _applyingBatch;
    private MessageRow? _selectedMessage;
    private string _filter = string.Empty;

    public MetadataMonitorViewModel(ICorePluginClientContext ctx, MetadataClientSettingsStore? settingsStore = null)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _settingsStore = settingsStore ?? new MetadataClientSettingsStore();
        _settings = _settingsStore.Load();
        _ctx.DevicesChanged += (_, _) => RefreshCameras();
        Messages.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowEmptyHint));
        RefreshCameras();
    }

    /// <summary>Wait before watching again after the event stream ended or failed.</summary>
    public static TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How often a running stream is confirmed to the server.</summary>
    public static TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Raised after a batch was added (the view keeps the newest row visible while <see cref="Autoscroll"/> is on).</summary>
    public event EventHandler? MessagesAppended;

    /// <summary>Cameras matching <see cref="CameraSearch"/> (the selected one always stays).</summary>
    public RangeObservableCollection<CameraOption> Cameras { get; } = [];

    /// <summary>The visible (filtered) messages, oldest first.</summary>
    public RangeObservableCollection<MessageRow> Messages { get; } = [];

    /// <summary>Copies text to the clipboard (set by the view: the TopLevel clipboard).</summary>
    public Func<string, Task>? CopyText { get; set; }

    [ObservableProperty]
    public partial string CameraSearch { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartStopCommand))]
    public partial CameraOption? SelectedCamera { get; set; }

    [ObservableProperty]
    public partial string FilterText { get; set; } = string.Empty;

    /// <summary>On: the newest row stays visible. The view turns it off when the user scrolls up and on at the end.</summary>
    [ObservableProperty]
    public partial bool Autoscroll { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartStopText))]
    [NotifyPropertyChangedFor(nameof(StartStopTip))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyHint))]
    [NotifyCanExecuteChangedFor(nameof(StartStopCommand))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartStopCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? StatusDetail { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStatusOk))]
    [NotifyPropertyChangedFor(nameof(IsStatusWarning))]
    [NotifyPropertyChangedFor(nameof(IsStatusError))]
    [NotifyPropertyChangedFor(nameof(IsStatusAccent))]
    public partial string StatusKind { get; set; } = "neutral";

    [ObservableProperty]
    public partial string SummaryText { get; set; } = "No messages";

    public bool IsStatusOk => StatusKind == "ok";

    public bool IsStatusWarning => StatusKind == "warning";

    public bool IsStatusError => StatusKind == "error";

    public bool IsStatusAccent => StatusKind == "accent";

    public bool HasStatus => StatusText.Length > 0;

    public string StartStopText => IsRunning ? "Stop" : "Start";

    public string StartStopTip => IsRunning
        ? "End the event stream (the list stays)"
        : SelectedCamera is null ? "Choose a camera first" : "Open the event stream of the camera and show its events live";

    /// <summary>The selected message; survives batches, filtering and trimming while the row exists.</summary>
    public MessageRow? SelectedMessage
    {
        get => _selectedMessage;
        set
        {
            // A collection reset of the grid pushes null while a batch is applied: keep the selection.
            if (_applyingBatch && value is null)
            {
                return;
            }

            if (SetProperty(ref _selectedMessage, value))
            {
                OnPropertyChanged(nameof(HasDetail));
                OnPropertyChanged(nameof(DetailText));
                OnPropertyChanged(nameof(DetailTitle));
                CopyCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool HasDetail => SelectedMessage is not null;

    /// <summary>The hint over the empty list: no messages and no stream running.</summary>
    public bool ShowEmptyHint => Messages.Count == 0 && !IsRunning;

    /// <summary>The XML of the selected message (the CodeView pretty-prints it unless Raw is chosen).</summary>
    public string? DetailText => SelectedMessage?.Xml;

    public string DetailTitle => SelectedMessage is { } row
        ? string.Create(CultureInfo.InvariantCulture, $"#{row.Seq} · {(row.Topic.Length > 0 ? row.Topic : row.Category)}")
        : "Select a message to see its XML";

    /// <summary>Messages kept (all of the stream, not only the visible ones).</summary>
    public int TotalCount => _all.Count;

    /// <summary>Height of the detail pane, remembered per client.</summary>
    public double DetailHeight
    {
        get => _settings.DetailHeight;
        set
        {
            var clamped = Math.Clamp(value, 120, 2000);
            if (Math.Abs(clamped - _settings.DetailHeight) < 0.5)
            {
                return;
            }

            _settings.DetailHeight = clamped;
            _settingsStore.Save(_settings);
            OnPropertyChanged();
        }
    }

    /// <summary>Page shown: watch the live events.</summary>
    public void Activate()
    {
        if (_active is not null)
        {
            return;
        }

        _active = new CancellationTokenSource();
        _ = WatchAsync(_active.Token);
    }

    /// <summary>Page hidden: end the running stream and the watch.</summary>
    public void Deactivate()
    {
        if (IsRunning || _starting)
        {
            _ = StopStreamAsync();
        }

        if (_active is { } active)
        {
            _active = null;
            active.Cancel();
            active.Dispose();
        }
    }

    public void Dispose() => Deactivate();

    /// <summary>Tests: accept the events of <paramref name="streamId"/> as if Start had opened it.</summary>
    internal void UseStreamForTests(string streamId)
    {
        _streamId = streamId;
        IsRunning = true;
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
            case MetadataMethods.MessagesTopic:
                var batch = MetadataJson.Deserialize<MessagesEvent>(item.PayloadJson);
                if (Accept(batch.StreamId, item))
                {
                    ApplyMessages(batch.Messages);
                }

                break;
            case MetadataMethods.StateTopic:
                var state = MetadataJson.Deserialize<MonitorState>(item.PayloadJson);
                if (Accept(state.StreamId, item))
                {
                    ApplyState(state);
                }

                break;
        }
    }

    /// <summary>Adds a batch: appended in one step, the oldest beyond the cap removed in one step, selection kept.</summary>
    public void ApplyMessages(IReadOnlyList<MetadataMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0)
        {
            return;
        }

        var rows = new List<MessageRow>(messages.Count);
        foreach (var message in messages)
        {
            rows.Add(new MessageRow(message));
        }

        _all.AddRange(rows);
        var overflow = _all.Count - MetadataMonitorPluginInfo.MaxClientMessages;
        var removedVisible = 0;
        if (overflow > 0)
        {
            for (var i = 0; i < overflow; i++)
            {
                if (_all[i].Matches(_filter))
                {
                    removedVisible++;
                }
            }

            _all.RemoveRange(0, overflow);
        }

        var selected = _selectedMessage;
        _applyingBatch = true;
        try
        {
            if (removedVisible > 0)
            {
                Messages.RemoveFirst(Math.Min(removedVisible, Messages.Count));
            }

            Messages.AddRange(_filter.Length == 0 ? rows : rows.Where(r => r.Matches(_filter)).ToList());
        }
        finally
        {
            _applyingBatch = false;
        }

        if (selected is not null && overflow > 0 && _all.Count > 0 && selected.Seq < _all[0].Seq)
        {
            SelectedMessage = null; // its row was trimmed
        }
        else
        {
            OnPropertyChanged(nameof(SelectedMessage)); // the grid lost it in the reset: select it again
        }

        UpdateSummary();
        MessagesAppended?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand(CanExecute = nameof(CanStartStop))]
    private async Task StartStopAsync()
    {
        if (IsRunning)
        {
            await StopStreamAsync().ConfigureAwait(true);
        }
        else
        {
            await StartStreamAsync().ConfigureAwait(true);
        }
    }

    private bool CanStartStop() => !IsBusy && (IsRunning || SelectedCamera is not null);

    [RelayCommand]
    private void Clear()
    {
        _all.Clear();
        Messages.ReplaceAll([]);
        SelectedMessage = null;
        UpdateSummary();
    }

    [RelayCommand(CanExecute = nameof(HasDetail))]
    private async Task CopyAsync()
    {
        if (SelectedMessage is not { } row || CopyText is not { } copy)
        {
            return;
        }

        var text = CodeText.FormatXml(row.Xml) ?? row.Xml;
        await copy(text).ConfigureAwait(true);
    }

    partial void OnSelectedCameraChanged(CameraOption? oldValue, CameraOption? newValue)
    {
        OnPropertyChanged(nameof(StartStopTip));
        if (oldValue is not null && newValue?.Id != oldValue.Id && (IsRunning || _starting))
        {
            _ = StopStreamAsync(); // one camera at a time: switching stops the running stream
        }
    }

    partial void OnCameraSearchChanged(string value) => ApplyCameraFilter();

    partial void OnFilterTextChanged(string value)
    {
        _filter = value?.Trim() ?? string.Empty;
        var selected = _selectedMessage;
        _applyingBatch = true;
        try
        {
            Messages.ReplaceAll(_filter.Length == 0 ? _all : _all.Where(r => r.Matches(_filter)).ToList());
        }
        finally
        {
            _applyingBatch = false;
        }

        if (selected is not null && !selected.Matches(_filter))
        {
            SelectedMessage = null;
        }
        else
        {
            OnPropertyChanged(nameof(SelectedMessage));
        }

        UpdateSummary();
    }

    private async Task StartStreamAsync()
    {
        if (SelectedCamera is not { } camera)
        {
            return;
        }

        IsBusy = true;
        _starting = true;
        _early.Clear();
        Clear();
        _streamId = null;
        SetStatus("accent", "Connecting", null);
        try
        {
            var json = await _ctx.InvokeAsync(MetadataMethods.Start, MetadataJson.Serialize(new StartRequest(camera.Id)), CancellationToken.None).ConfigureAwait(true);
            var reply = MetadataJson.Deserialize<StartReply>(json);
            if (reply.StreamId is null)
            {
                SetStatus("error", reply.Error ?? "The event stream could not be opened", reply.Error);
                return;
            }

            _streamId = reply.StreamId;
            IsRunning = true;
            if (StatusKind == "accent")
            {
                SetStatus("ok", "Live", null);
            }

            StartKeepAlive(reply.StreamId);
            foreach (var early in _early.ToList())
            {
                HandleEvent(early);
            }
        }
#pragma warning disable CA1031 // Shown to the user.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            SetStatus("error", Message(ex), null);
        }
        finally
        {
            _starting = false;
            _early.Clear();
            IsBusy = false;
        }
    }

    private async Task StopStreamAsync()
    {
        var id = _streamId;
        StopKeepAlive();
        IsRunning = false;
        if (id is null)
        {
            return;
        }

        try
        {
            await _ctx.InvokeAsync(MetadataMethods.Stop, MetadataJson.Serialize(new StreamRequest(id)), CancellationToken.None).ConfigureAwait(true);
            if (StatusKind != "error")
            {
                SetStatus("neutral", "Stopped", null);
            }
        }
#pragma warning disable CA1031 // The server ends the stream itself when the keep-alives stop.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            SetStatus("error", Message(ex), null);
        }
    }

    /// <summary>Events of the current stream; while a start is pending, unknown ones wait for its reply.</summary>
    private bool Accept(string streamId, PluginEvent item)
    {
        if (_streamId is not null && string.Equals(streamId, _streamId, StringComparison.Ordinal))
        {
            return true;
        }

        if (_starting && _streamId is null && _early.Count < MaxEarlyEvents)
        {
            _early.Add(item);
        }

        return false;
    }

    private void ApplyState(MonitorState state)
    {
        var lost = state.Lost > 0
            ? string.Create(CultureInfo.InvariantCulture, $" · {state.Lost:N0} {(state.Lost == 1 ? "message" : "messages")} lost")
            : string.Empty;
        switch (state.State)
        {
            case MonitorStates.Connecting:
                SetStatus("accent", "Connecting", null);
                break;
            case MonitorStates.Live:
                SetStatus("ok", string.Create(CultureInfo.InvariantCulture, $"Live{lost}"), null);
                break;
            case MonitorStates.Reconnecting:
                SetStatus("warning", "Reconnecting" + lost, state.Text);
                break;
            case MonitorStates.Error:
                StopKeepAlive();
                IsRunning = false;
                SetStatus("error", state.Text ?? "The event stream failed", state.Text);
                break;
            case MonitorStates.Stopped:
                StopKeepAlive();
                IsRunning = false;
                SetStatus("neutral", "Stopped" + lost, null);
                break;
        }
    }

    private void SetStatus(string kind, string text, string? detail)
    {
        StatusKind = kind;
        StatusText = text;
        StatusDetail = detail;
    }

    private void UpdateSummary()
    {
        SummaryText = _all.Count == 0
            ? "No messages"
            : _filter.Length == 0
                ? string.Create(CultureInfo.InvariantCulture, $"{_all.Count:N0} {(_all.Count == 1 ? "message" : "messages")}")
                : string.Create(CultureInfo.InvariantCulture, $"{Messages.Count:N0} of {_all.Count:N0} messages");
        OnPropertyChanged(nameof(TotalCount));
    }

    private void RefreshCameras()
    {
        _allCameras = CameraOption.From(_ctx.Devices);
        var selected = SelectedCamera is { } current ? _allCameras.FirstOrDefault(c => c.Id == current.Id) : null;
        if (SelectedCamera is not null && selected is null && (IsRunning || _starting))
        {
            _ = StopStreamAsync(); // the camera was removed
        }

        ApplyCameraFilter(selected, keepSelection: true);
    }

    private void ApplyCameraFilter() => ApplyCameraFilter(SelectedCamera, keepSelection: true);

    private void ApplyCameraFilter(CameraOption? selected, bool keepSelection)
    {
        var search = CameraSearch.Trim();
        var visible = search.Length == 0
            ? _allCameras
            : _allCameras.Where(c => c.Label.Contains(search, StringComparison.OrdinalIgnoreCase) || (selected is not null && c.Id == selected.Id)).ToList();
        Cameras.ReplaceAll(visible);
        if (keepSelection)
        {
            SelectedCamera = selected;
        }

        if (SelectedCamera is null && search.Length > 0 && visible.Count > 0)
        {
            SelectedCamera = visible[0];
        }
    }

    private void StartKeepAlive(string streamId)
    {
        StopKeepAlive();
        var cts = new CancellationTokenSource();
        _keepAlive = cts;
        _ = KeepAliveAsync(streamId, cts.Token);
    }

    private void StopKeepAlive()
    {
        if (_keepAlive is { } cts)
        {
            _keepAlive = null;
            cts.Cancel();
            cts.Dispose();
        }
    }

    private async Task KeepAliveAsync(string streamId, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(KeepAliveInterval, ct).ConfigureAwait(true);
                try
                {
                    await _ctx.InvokeAsync(MetadataMethods.KeepAlive, MetadataJson.Serialize(new StreamRequest(streamId)), ct).ConfigureAwait(true);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
#pragma warning disable CA1031 // A missed keep-alive is retried; the stream state comes from the events.
                catch (Exception)
#pragma warning restore CA1031
                {
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stopped
        }
    }

    private async Task WatchAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var events = _ctx.WatchEventsAsync(ct).GetAsyncEnumerator(ct);
                try
                {
                    while (await events.MoveNextAsync().ConfigureAwait(true))
                    {
                        HandleEvent(events.Current);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
#pragma warning disable CA1031 // The connection dropped: watch again after a short delay.
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

    /// <summary>gRPC errors carry the user message in Status.Detail; read it without a Grpc reference.</summary>
    private static string Message(Exception ex)
    {
        var detail = ex.GetType().GetProperty("Status")?.GetValue(ex) is { } status
            ? status.GetType().GetProperty("Detail")?.GetValue(status) as string
            : null;
        return string.IsNullOrEmpty(detail) ? ex.Message : detail;
    }
}
