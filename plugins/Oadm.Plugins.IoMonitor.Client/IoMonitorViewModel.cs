using System.Collections.ObjectModel;
using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.IoMonitor.Client;

/// <summary>A device in the picker: "10.0.0.70 · A9210".</summary>
public sealed record IoDeviceOption(Guid Id, string Address, string Text)
{
    public override string ToString() => Text;

    public static IReadOnlyList<IoDeviceOption> From(IEnumerable<IDeviceInfo> devices) =>
    [
        .. devices.Where(d => d.Apis.Supports(IoMonitorPluginInfo.ApiId, "1.0"))
            .OrderBy(d => AddressKey(d.Address), StringComparer.Ordinal)
            .Select(d => new IoDeviceOption(d.Id, d.Address, string.IsNullOrEmpty(d.Model) ? d.Address : $"{d.Address} · {d.Model.Replace("AXIS ", string.Empty, StringComparison.Ordinal)}")),
    ];

    private static string AddressKey(string address)
    {
        var parts = address.Split(':')[0].Split('.');
        return parts.Length == 4 && parts.All(p => byte.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            ? string.Join('.', parts.Select(p => p.PadLeft(3, '0')))
            : "~" + address;
    }
}

/// <summary>One port tile, updated in place.</summary>
public sealed partial class PortTileViewModel(string port) : ObservableObject
{
    public string Port { get; } = port;

    [ObservableProperty] public partial string Name { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveText))]
    public partial bool Active { get; private set; }

    [ObservableProperty] public partial string Detail { get; private set; } = string.Empty;

    /// <summary>The circuit is closed (the switch symbol is drawn closed).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpen))]
    public partial bool IsClosed { get; private set; }

    public bool IsOpen => !IsClosed;

    public string ActiveText => Active ? "Active" : "Inactive";

    /// <summary>"Port 1" (port "0"), the number the event rules' "Set output" uses.</summary>
    public string Number => int.TryParse(Port, out var n) ? string.Create(CultureInfo.InvariantCulture, $"Port {n + 1}") : Port;

    public void Apply(IoPort port)
    {
        Name = port.Name.Length == 0 ? Number : port.Name;
        Active = port.Active;
        IsClosed = string.Equals(port.State, "closed", StringComparison.OrdinalIgnoreCase);
        Detail = $"{Cap(port.State)}, normally {port.NormalState}" + (port.Usage.Length > 0 ? " · " + port.Usage : string.Empty);
    }

    private static string Cap(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}

/// <summary>
/// Page "I/O Monitor": pick a device with inputs or outputs and see their states live. Choosing a device starts the
/// server's reading (every 200 ms) for this page; another device or leaving the page stops it. Keep-alives every 5 s.
/// </summary>
public sealed partial class IoMonitorViewModel : ObservableObject, IDisposable
{
    private readonly ICorePluginClientContext _ctx;
    private readonly Dictionary<string, PortTileViewModel> _tiles = [];
    private CancellationTokenSource? _active;
    private CancellationTokenSource? _stream;
    private string? _streamId;

    public IoMonitorViewModel(ICorePluginClientContext ctx)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _ctx.DevicesChanged += (_, _) => RefreshDevices();
        RefreshDevices();
    }

    public static TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(5);

    public static TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(2);

    public RangeObservableCollection<IoDeviceOption> Devices { get; } = [];

    public ObservableCollection<PortTileViewModel> Inputs { get; } = [];

    public ObservableCollection<PortTileViewModel> Outputs { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PanelTitle))]
    public partial IoDeviceOption? SelectedDevice { get; set; }

    /// <summary>The faceplate's label: the model, else the address.</summary>
    public string PanelTitle => SelectedDevice?.Text.Split(" · ", 2)[^1] ?? string.Empty;

    public bool ShowPanel => Inputs.Count > 0 || Outputs.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLive), nameof(IsConnecting), nameof(HasError), nameof(ShowEmpty), nameof(EmptyText))]
    public partial string State { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; private set; }

    public bool IsLive => State == "Live";

    public bool IsConnecting => State == "Connecting";

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    public bool HasInputs => Inputs.Count > 0;

    public bool HasOutputs => Outputs.Count > 0;

    public bool ShowEmpty => Inputs.Count == 0 && Outputs.Count == 0 && !IsConnecting;

    public string EmptyText => Devices.Count == 0
        ? "No device with inputs or outputs."
        : SelectedDevice is null ? "Choose a device." : HasError ? string.Empty : "The device reports no ports.";

    partial void OnSelectedDeviceChanged(IoDeviceOption? value)
    {
        if (!_silent)
        {
            _ = SwitchAsync(value);
        }
    }

    /// <summary>The page is shown: watch the pushed ports and read the chosen device again.</summary>
    public void Activate()
    {
        if (_active is not null)
        {
            return;
        }

        _active = new CancellationTokenSource();
        _ = WatchAsync(_active.Token);
        _ = SwitchAsync(SelectedDevice);
    }

    /// <summary>The page is hidden: nothing is read any more.</summary>
    public void Deactivate()
    {
        if (_active is { } active)
        {
            _active = null;
            active.Cancel();
            active.Dispose();
        }

        _ = StopStreamAsync();
    }

    public void Dispose() => Deactivate();

    /// <summary>Applies one pushed event (tests call it directly).</summary>
    public void HandleEvent(PluginEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Topic != IoMonitorMethods.PortsTopic)
        {
            return;
        }

        var ports = IoMonitorJson.Deserialize<PortsEvent>(item.PayloadJson);
        if (ports.StreamId != _streamId)
        {
            return;
        }

        ApplyPorts(ports.Ports);
        ErrorText = ports.Error;
        State = ports.Error is null ? "Live" : "Error";
    }

    private void ApplyPorts(IReadOnlyList<IoPort> ports)
    {
        var seen = new HashSet<string>();
        foreach (var port in ports)
        {
            seen.Add(port.Port);
            if (!_tiles.TryGetValue(port.Port, out var tile))
            {
                tile = new PortTileViewModel(port.Port);
                _tiles[port.Port] = tile;
            }

            var list = port.IsOutput ? Outputs : Inputs;
            var other = port.IsOutput ? Inputs : Outputs;
            other.Remove(tile); // a configurable port changed its direction
            if (!list.Contains(tile))
            {
                list.Insert(list.Count(t => int.Parse(t.Port, CultureInfo.InvariantCulture) < int.Parse(port.Port, CultureInfo.InvariantCulture)), tile);
            }

            tile.Apply(port);
        }

        foreach (var gone in _tiles.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            Inputs.Remove(_tiles[gone]);
            Outputs.Remove(_tiles[gone]);
            _tiles.Remove(gone);
        }

        OnPropertyChanged(nameof(HasInputs));
        OnPropertyChanged(nameof(HasOutputs));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowPanel));
    }

    private void ClearPorts()
    {
        _tiles.Clear();
        Inputs.Clear();
        Outputs.Clear();
        OnPropertyChanged(nameof(HasInputs));
        OnPropertyChanged(nameof(HasOutputs));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowPanel));
    }

    private async Task SwitchAsync(IoDeviceOption? device)
    {
        await StopStreamAsync().ConfigureAwait(true);
        ClearPorts();
        ErrorText = null;
        State = string.Empty;
        if (device is null || _active is null)
        {
            OnPropertyChanged(nameof(EmptyText));
            return;
        }

        State = "Connecting";
        try
        {
            var json = await _ctx.InvokeAsync(IoMonitorMethods.Start, IoMonitorJson.Serialize(new StartRequest(device.Id)), CancellationToken.None).ConfigureAwait(true);
            var reply = IoMonitorJson.Deserialize<StartReply>(json);
            if (reply.StreamId is null || SelectedDevice?.Id != device.Id || _active is null)
            {
                if (reply.StreamId is not null)
                {
                    await _ctx.InvokeAsync(IoMonitorMethods.Stop, IoMonitorJson.Serialize(new StreamRequest(reply.StreamId)), CancellationToken.None).ConfigureAwait(true);
                }

                if (SelectedDevice?.Id == device.Id)
                {
                    ErrorText = reply.Error;
                    State = reply.Error is null ? string.Empty : "Error";
                }

                return;
            }

            _streamId = reply.StreamId;
            _stream = new CancellationTokenSource();
            _ = KeepAliveAsync(reply.StreamId, _stream.Token);
        }
#pragma warning disable CA1031 // Shown on the page.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorText = Message(ex);
            State = "Error";
        }
    }

    private async Task StopStreamAsync()
    {
        var id = _streamId;
        _streamId = null;
        if (_stream is { } stream)
        {
            _stream = null;
            stream.Cancel();
            stream.Dispose();
        }

        if (id is null)
        {
            return;
        }

        try
        {
            await _ctx.InvokeAsync(IoMonitorMethods.Stop, IoMonitorJson.Serialize(new StreamRequest(id)), CancellationToken.None).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // The server stops it on its own when the keep-alives end.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private async Task KeepAliveAsync(string streamId, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(KeepAliveInterval, ct).ConfigureAwait(true);
                string? json;
                try
                {
                    json = await _ctx.InvokeAsync(IoMonitorMethods.KeepAlive, IoMonitorJson.Serialize(new StreamRequest(streamId)), ct).ConfigureAwait(true);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
#pragma warning disable CA1031 // A missed keep-alive is retried with the next one.
                catch (Exception)
#pragma warning restore CA1031
                {
                    continue;
                }

                // the server no longer reads for this stream (it expired or the server restarted): start again, so the
                // page never shows old states; an empty answer (older server, fake mode) means it still runs
                if (!string.IsNullOrEmpty(json) && !IoMonitorJson.Deserialize<KeepAliveReply>(json).Running && !ct.IsCancellationRequested && _streamId == streamId)
                {
                    _ = SwitchAsync(SelectedDevice);
                    return;
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
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await foreach (var item in _ctx.WatchEventsAsync(ct).ConfigureAwait(true))
                {
                    HandleEvent(item);
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

            try
            {
                await Task.Delay(ReconnectDelay, ct).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void RefreshDevices()
    {
        var options = IoDeviceOption.From(_ctx.Devices);
        var selected = SelectedDevice;

        // one reset; the picker may clear its selection on it, which must not stop the stream
        _silent = true;
        try
        {
            Devices.ReplaceAll(options);
        }
        finally
        {
            _silent = false;
        }

        if (selected is null)
        {
            if (SelectedDevice is not null)
            {
                SetSelectedSilently(null);
            }
        }
        else if (options.FirstOrDefault(d => d.Id == selected.Id) is { } again)
        {
            if (!ReferenceEquals(again, SelectedDevice))
            {
                SetSelectedSilently(again); // the same device: keep the stream, only swap the list entry
            }
        }
        else
        {
            SetSelectedSilently(null); // removed from OADM: stop reading it
            _ = SwitchAsync(null);
        }

        OnPropertyChanged(nameof(EmptyText));
    }

    private bool _silent;

    private void SetSelectedSilently(IoDeviceOption? option)
    {
        _silent = true;
        SelectedDevice = option;
        _silent = false;
    }

    private static string Message(Exception ex)
    {
        var detail = ex.GetType().GetProperty("Status")?.GetValue(ex) is { } status
            ? status.GetType().GetProperty("Detail")?.GetValue(status) as string
            : null;
        return string.IsNullOrEmpty(detail) ? ex.Message : detail;
    }
}
