using System.Text.Json;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Devices;

namespace Oadm.Samples.FirmwareOverview.Client;

/// <summary>
/// The page: reads the state from the server part, then follows its events. The devices come from the page's device
/// choice (the Devices page selection to start with); nothing is read before one. Refresh asks for a new read.
/// </summary>
public sealed partial class FirmwareOverviewViewModel : ObservableObject, IDisposable
{
    private readonly ICorePluginClientContext _ctx;
    private readonly CancellationTokenSource _stop = new();
    private Task? _run;
    private int _version = -1;

    public FirmwareOverviewViewModel(ICorePluginClientContext ctx)
    {
        _ctx = ctx;
        Devices = new PageDeviceChoice(ctx);
        Devices.PropertyChanged += (_, _) => RefreshCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The devices the page reads: the purple button "Choose devices" / "Selected devices: Tag Building A".</summary>
    public PageDeviceChoice Devices { get; }

    /// <summary>Opens the device picker; set by the view, which knows the window.</summary>
    public Func<Task<bool>>? PickDevices { get; set; }

    /// <summary>The table. One reset per update, also with thousands of devices.</summary>
    public RangeObservableCollection<FirmwareRow> Rows { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    public partial bool IsReading { get; private set; }

    [ObservableProperty]
    public partial string? ErrorText { get; private set; }

    /// <summary>"12 devices, 2 not read".</summary>
    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    /// <summary>Starts once, when the view is shown first.</summary>
    public void Start() => _run ??= RunAsync();

    /// <summary>Subscribe, read the whole state, apply the events; after a dropped connection wait and do it again.</summary>
    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var events = _ctx.WatchEventsAsync(_stop.Token);
                Apply(await _ctx.InvokeAsync(FirmwareOverview.GetState, null, _stop.Token));
                await foreach (var item in events)
                {
                    if (item.Topic == FirmwareOverview.StateTopic)
                    {
                        Apply(item.PayloadJson);
                    }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ErrorText = ServerErrors.Message(ex);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), _stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>The purple button: choose the devices, then read them.</summary>
    [RelayCommand]
    private async Task ChooseDevicesAsync()
    {
        if (PickDevices is { } pick && await pick() && CanRefresh())
        {
            await RefreshAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        try
        {
            ErrorText = null;
            // All devices go as "all", not as every id: one short request also with 30,000 devices.
            var request = Devices.Choice.Kind == DeviceChoiceKind.All
                ? new RefreshRequest(true, [])
                : new RefreshRequest(false, [.. Devices.Resolve().Select(d => d.Id)]);
            await _ctx.InvokeAsync(FirmwareOverview.Refresh, JsonSerializer.Serialize(request, FirmwareOverview.Json), _stop.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ErrorText = ServerErrors.Message(ex);
        }
    }

    private bool CanRefresh() => !IsReading && !Devices.IsEmpty;

    /// <summary>Shows a state from the server, unless an equal or newer one is shown already.</summary>
    public void Apply(string? json)
    {
        if (json is null || JsonSerializer.Deserialize<FirmwareState>(json, FirmwareOverview.Json) is not { } state || state.Version <= _version)
        {
            return;
        }

        _version = state.Version;
        IsReading = state.Reading;
        ErrorText = null;
        Rows.ReplaceAll(state.Rows);
        var failed = state.Rows.Count(r => r.Error is not null);
        Summary = state.Rows.Count switch
        {
            0 when Devices.IsEmpty => "Choose devices to read their AXIS OS.",
            0 => "Click Refresh to read the devices.",
            1 => "1 device",
            _ => $"{state.Rows.Count:N0} devices",
        } + (failed > 0 ? $", {failed:N0} not read" : "");
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
