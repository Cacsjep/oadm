using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.DateAndTime.Model;
using Oadm.Plugins.DateAndTime.Vapix;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Validation;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.DateAndTime.Client;

/// <summary>
/// "Set date and time" dialog, a compact clone of the ADM / AXIS Camera Station dialog: Device time (first selected
/// device, read-only, ticking), Time zone (one drop-down of the IANA zones sorted by offset, "Automatically adjust for
/// daylight saving time changes") and Time mode (Synchronize with server computer time, Synchronize with NTP server:
/// Obtain from DHCP / servers / NTS, Set manually), each radio button with its input below it. Exactly like ADM there is no "keep": OK writes the time zone and the time mode to every selected device.
/// Defaults: the first device's time zone (the OADM server's when the device has no IANA zone) and its time mode (NTP
/// when enabled, otherwise manual). Field errors are reported below their input (<see cref="ValidatingViewModel"/>),
/// with the server's own rules (<see cref="PayloadValidator"/>). No per-device work: the device summaries are one
/// O(n) pass over the cached API lists.
/// </summary>
public sealed partial class DateTimeDialogViewModel : ValidatingViewModel
{
    private readonly IReadOnlyList<IDeviceInfo> _devices;
    private readonly Func<DateTimeOffset> _now;
    private string? _initialZoneId;
    private bool _initialDst = true;
    private int _maxYear = PayloadValidator.DefaultMaxYear;
    private bool _ready;
    private DateTimeOffset _readAt;

    public DateTimeDialogViewModel(IReadOnlyList<IDeviceInfo> devices, Func<DateTimeOffset>? now = null)
    {
        ArgumentNullException.ThrowIfNull(devices);
        if (devices.Count == 0)
        {
            throw new ArgumentException("At least one device is required.", nameof(devices));
        }

        _devices = devices;
        _now = now ?? (() => DateTimeOffset.Now);

        // One pass over the cached API lists (thousands of devices: no requests, no per-device UI).
        foreach (var device in devices)
        {
            var apis = device.Apis;
            if (TimeApis.SupportsNts(apis))
            {
                NtsDeviceCount++;
            }

            if (!TimeApis.HasTimeService(apis))
            {
                NoTimeApiCount++;
            }

            if (!TimeApis.HasNtpApi(apis))
            {
                SingleNtpServerCount++;
            }
        }

        var local = _now();
        ManualDate = local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        ManualTime = local.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        DeviceTimeStatus = "Reading the device time";
        Validation
            .Rule(nameof(SelectedZone), () => IsServerTime ? null
                : SelectedZone is null ? "Select a time zone." : PayloadValidator.ValidateTimeZone(SelectedZone.Id))
            .Rule(nameof(NtpServersText), () => ShowServerList ? PayloadValidator.ValidateNtpServers(ParseServers(NtpServersText), UseNts) : null)
            .Rule(nameof(ManualDate), ManualDateError)
            .Rule(nameof(ManualTime), () => IsManual && ParseTime(ManualTime) is null ? "Enter the time as hh:mm or hh:mm:ss." : null);
        _ready = true;
        Recompute();
    }

    /// <summary>Raised when the dialog should close: true = OK (see <see cref="ResultJson"/>), false = cancel.</summary>
    public event EventHandler<bool>? CloseRequested;

    public string Title => _devices.Count == 1 ? "Set date and time" : $"Set date and time for {_devices.Count.ToString("N0", CultureInfo.InvariantCulture)} devices";

    public bool IsMultiDevice => _devices.Count > 1;

    public int NtsDeviceCount { get; }

    public int NoTimeApiCount { get; }

    public int SingleNtpServerCount { get; }

    // ---- Device time (first selected device, read-only) ----

    /// <summary>"10.0.0.48 · P3265-V" or "10.0.0.48 · P3265-V, first of 12 devices" (next to the Device time heading).</summary>
    public string DeviceLabel => IsMultiDevice
        ? $"{Label(_devices[0])}, first of {Count(_devices.Count)} devices"
        : Label(_devices[0]);

    [ObservableProperty]
    public partial bool IsLoading { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDeviceTimeStatus))]
    public partial string? DeviceTimeStatus { get; set; }

    [ObservableProperty]
    public partial bool DeviceTimeFailed { get; set; }

    public bool HasDeviceTimeStatus => !string.IsNullOrEmpty(DeviceTimeStatus);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDeviceTime))]
    public partial CurrentTimeSettings? Current { get; set; }

    public bool HasDeviceTime => Current is not null;

    /// <summary>The device's local time, "2026-10-07 18:24:01 (UTC+02:00)", advanced by <see cref="Tick"/>.</summary>
    [ObservableProperty]
    public partial string DeviceTimeText { get; set; } = string.Empty;

    /// <summary>The OADM server's time in this computer's zone and the difference to the device (below "Synchronize with server computer time").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasServerTime), nameof(HasServerTimeDetail))]
    public partial string ServerTimeText { get; set; } = string.Empty;

    public bool HasServerTime => ServerTimeText.Length > 0;

    /// <summary>IANA id of the OADM server's time zone (server time mode).</summary>
    [ObservableProperty]
    public partial string? ServerTimeZone { get; set; }

    // ---- Time zone ----

    /// <summary>Every zone of the drop-down, sorted by UTC offset (zones the OS does not know last).</summary>
    public IReadOnlyList<TimeZoneEntry> Zones { get; } = TimeZoneCatalog.All;

    [ObservableProperty]
    public partial TimeZoneEntry? SelectedZone { get; set; }

    [ObservableProperty]
    public partial bool AdjustForDst { get; set; } = true;

    /// <summary>The selected zone and DST equal what the (single) device has: the task is named after the time mode.</summary>
    public bool TimeZoneUnchanged => !IsMultiDevice && !IsServerTime && SelectedZone is not null && _initialZoneId is not null
        && string.Equals(SelectedZone.Id, _initialZoneId, StringComparison.Ordinal) && AdjustForDst == _initialDst;

    public bool CanEditTimeZone => !IsServerTime;

    public bool CanAdjustDst => CanEditTimeZone && SelectedZone is { ObservesDaylightSaving: true };

    // ---- Time mode ----

    /// <summary>Always written. Starts at NTP; the first device's mode once it is read (NTP if enabled, otherwise manual).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsServerTime), nameof(IsNtp), nameof(IsManual))]
    public partial TimeMode Mode { get; set; } = TimeMode.Ntp;

    public bool IsServerTime
    {
        get => Mode == TimeMode.ServerTime;
        set => SetMode(value, TimeMode.ServerTime);
    }

    public bool IsNtp
    {
        get => Mode == TimeMode.Ntp;
        set => SetMode(value, TimeMode.Ntp);
    }

    public bool IsManual
    {
        get => Mode == TimeMode.Manual;
        set => SetMode(value, TimeMode.Manual);
    }

    /// <summary>"Obtain from DHCP"; NTS always uses the entered servers.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowServerList))]
    public partial bool UseDhcp { get; set; }

    /// <summary>The server field is in use (enabled and validated): NTP without DHCP, or NTS.</summary>
    public bool ShowServerList => IsNtp && (!UseDhcp || UseNts);

    /// <summary>Host names or IP addresses separated by commas (new lines, spaces and semicolons also separate).</summary>
    [ObservableProperty]
    public partial string NtpServersText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowServerList), nameof(ServersPlaceholder), nameof(HasNtsHint))]
    public partial bool UseNts { get; set; }

    public bool ShowNts => NtsDeviceCount > 0;

    public string ServersPlaceholder => UseNts ? "Up to 5 NTS KE servers, separated by commas" : "Up to 5 NTP servers, separated by commas";

    /// <summary>Only when some of the selected devices cannot use NTS.</summary>
    public string? NtsHint => NtsDeviceCount == _devices.Count
        ? null
        : $"NTS works on {Count(NtsDeviceCount)} of {Count(_devices.Count)} devices; the others are not changed.";

    public bool HasNtsHint => UseNts && NtsHint is not null;

    [ObservableProperty]
    public partial string ManualDate { get; set; }

    [ObservableProperty]
    public partial string ManualTime { get; set; }

    /// <summary>Device notes that apply to the chosen mode (legacy firmware).</summary>
    public string? ModeNote
    {
        get
        {
            var note = Mode switch
            {
                TimeMode.ServerTime => ServerTimeZone is { } zone
                    ? $"The devices get this time and the server's time zone ({zone}) once, when each task runs. NTP is turned off."
                    : "The devices get this time once, when each task runs, and keep their time zone. NTP is turned off.",
                TimeMode.Manual => "Date and time in the time zone of each device. NTP is turned off.",
                _ => null,
            };
            var devices = Mode switch
            {
                TimeMode.ServerTime or TimeMode.Manual when NoTimeApiCount > 0 =>
                    $"{Count(NoTimeApiCount)} of the selected devices {(NoTimeApiCount == 1 ? "has" : "have")} no Time API (AXIS OS 9.30 or later): their tasks fail without changes.",
                TimeMode.Ntp when SingleNtpServerCount > 0 && ServerCount > 1 =>
                    $"{Count(SingleNtpServerCount)} of the selected devices {(SingleNtpServerCount == 1 ? "takes" : "take")} only one NTP server: their tasks fail without changes.",
                _ => null,
            };
            return note is null ? devices : devices is null ? note : note + " " + devices;
        }
    }

    public bool HasModeNote => ModeNote is not null;

    /// <summary>The note below the chosen time mode's input (null for the other two).</summary>
    public string? ServerTimeNote => IsServerTime ? ModeNote : null;

    /// <summary>Something to show below "Synchronize with server computer time" (no empty gap while reading).</summary>
    public bool HasServerTimeDetail => HasServerTime || ServerTimeNote is not null;

    public string? NtpNote => IsNtp ? ModeNote : null;

    public string? ManualNote => IsManual ? ModeNote : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ApplyBlockedReason))]
    public partial bool CanApply { get; set; }

    /// <summary>Why OK is disabled (its tooltip): the first field error.</summary>
    public string? ApplyBlockedReason => CanApply ? null : FormError ?? "Check the input.";

    /// <summary>The payload of the last successful <see cref="Apply"/>.</summary>
    public string? ResultJson { get; private set; }

    /// <summary>The payload for the current input (time zone and time mode, always both), or null when an input is invalid.</summary>
    public DateTimePayload? BuildPayload()
    {
        var zone = IsServerTime ? null : SelectedZone;
        var payload = new DateTimePayload(
            TimeZone: zone?.Id,
            Mode: Mode,
            Ntp: Mode == TimeMode.Ntp ? new NtpSettings(UseNts ? NtpSource.Static : UseDhcp ? NtpSource.Dhcp : NtpSource.Static, ShowServerList ? ParseServers(NtpServersText) : [], UseNts) : null,
            ManualDateTime: Mode == TimeMode.Manual ? CombineManual() : null,
            DaylightSaving: zone is null || AdjustForDst || !zone.ObservesDaylightSaving,
            TimeZoneUnchanged: TimeZoneUnchanged);
        return PayloadValidator.Validate(payload, _maxYear).Count == 0 ? payload : null;
    }

    /// <summary>
    /// Shows the first device's settings and prefills the inputs like ADM: its time zone (the OADM server's when it has
    /// no IANA zone), its time mode (NTP if enabled, otherwise manual), servers, date.
    /// </summary>
    public void ApplyCurrent(CurrentTimeSettings current)
    {
        ArgumentNullException.ThrowIfNull(current);
        Current = current;
        IsLoading = false;
        DeviceTimeFailed = false;
        DeviceTimeStatus = null;
        ServerTimeZone = current.ServerTimeZone;
        _maxYear = current.MaxYear ?? PayloadValidator.DefaultMaxYear;
        _readAt = _now();
        Tick();

        _initialZoneId = current.TimeZone;
        _initialDst = current.DstEnabled ?? true;
        var defaultZone = current.TimeZone is { } zone && TimeZoneCatalog.Find(zone) is { } entry
            ? entry
            : current.ServerTimeZone is { } serverZone ? TimeZoneCatalog.Find(serverZone) : null;
        if (defaultZone is not null)
        {
            SelectedZone = defaultZone;
            AdjustForDst = current.TimeZone is null || _initialDst;
        }

        Mode = current.NtpEnabled == false ? TimeMode.Manual : TimeMode.Ntp;

        var servers = current.NtsEnabled == true ? current.NtsServers : current.NtpServers;
        if (string.IsNullOrWhiteSpace(NtpServersText) && servers.Count > 0)
        {
            NtpServersText = string.Join(", ", servers);
        }

        UseDhcp = current.NtpSource == NtpSource.Dhcp && current.NtsEnabled != true;
        UseNts = current.NtsEnabled == true && ShowNts;
        if (current.DeviceLocal is { } deviceLocal)
        {
            ManualDate = deviceLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            ManualTime = deviceLocal.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        }

        Validation.Reset(); // prefilled, not edited: errors show once the user edits a field or tries OK
        Recompute();
    }

    public void ShowLoadError(string message)
    {
        IsLoading = false;
        DeviceTimeFailed = true;
        DeviceTimeStatus = "The device time could not be read: " + message;
    }

    /// <summary>
    /// Advances the device and server time by the time since they were read (the window calls it every second), so
    /// both tick like a clock without another device request.
    /// </summary>
    public void Tick()
    {
        if (Current is not { } current)
        {
            return;
        }

        var elapsed = _now() - _readAt;
        DeviceTimeText = current.DeviceLocal is { } local
            ? FormatTime(local + elapsed) + (current.DeviceUtc is null ? string.Empty : " " + TimeZoneCatalog.FormatOffset(local.Offset))
            : "Not reported";
        ServerTimeText = DescribeServerTime(current, elapsed);
    }

    /// <summary>Reads the first device's time settings (read-only query) for Device time.</summary>
    public async Task LoadCurrentAsync(ITaskDialogContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        try
        {
            var json = await ctx.QueryAsync(_devices[0].Id, DateTimeTaskPlugin.QueryGetTimeSettings, null, ct).ConfigureAwait(true);
            if (string.IsNullOrEmpty(json))
            {
                ShowLoadError("the device sent nothing.");
                return;
            }

            ApplyCurrent(CurrentTimeSettings.FromJson(json));
        }
        catch (OperationCanceledException)
        {
            IsLoading = false;
        }
#pragma warning disable CA1031 // A failed read must never break the dialog; the user can still enter everything.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ShowLoadError(RpcDetail(ex));
        }
    }

    [RelayCommand]
    private void UseComputerTime()
    {
        var local = _now();
        ManualDate = local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        ManualTime = local.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    }

    [RelayCommand]
    private void Apply()
    {
        Recompute();
        var payload = BuildPayload();
        if (payload is null)
        {
            Validation.ShowAll(); // every problem below its field
            return;
        }

        ResultJson = payload.ToJson();
        CloseRequested?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    partial void OnSelectedZoneChanged(TimeZoneEntry? value) => Recompute();

    partial void OnAdjustForDstChanged(bool value) => Recompute();

    partial void OnModeChanged(TimeMode value) => Recompute();

    partial void OnUseDhcpChanged(bool value) => Recompute();

    partial void OnUseNtsChanged(bool value) => Recompute();

    partial void OnNtpServersTextChanged(string value) => Recompute();

    partial void OnManualDateChanged(string value) => Recompute();

    partial void OnManualTimeChanged(string value) => Recompute();

    partial void OnServerTimeZoneChanged(string? value) => Recompute();

    private int ServerCount => ParseServers(NtpServersText).Count;

    private void SetMode(bool isChecked, TimeMode mode)
    {
        if (isChecked)
        {
            Mode = mode;
        }
    }

    /// <summary>Re-evaluates field errors, derived texts and the OK button.</summary>
    private void Recompute()
    {
        if (!_ready)
        {
            return;
        }

        Validation.Validate();
        foreach (var name in new[]
        {
            nameof(TimeZoneUnchanged), nameof(CanEditTimeZone), nameof(CanAdjustDst),
            nameof(ShowServerList), nameof(ModeNote), nameof(HasModeNote), nameof(ServerTimeNote), nameof(HasServerTimeDetail), nameof(NtpNote), nameof(ManualNote),
        })
        {
            OnPropertyChanged(name);
        }

        CanApply = IsFormValid && BuildPayload() is not null;
        OnPropertyChanged(nameof(ApplyBlockedReason));
    }

    protected override void OnValidationChanged()
    {
        if (_ready)
        {
            CanApply = IsFormValid && BuildPayload() is not null;
            OnPropertyChanged(nameof(ApplyBlockedReason));
        }
    }

    /// <summary>Format of the date, then the year range of the combined date and time.</summary>
    private string? ManualDateError()
    {
        if (!IsManual)
        {
            return null;
        }

        if (!System.DateTime.TryParseExact(ManualDate?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            return "Enter the date as yyyy-mm-dd.";
        }

        return ParseTime(ManualTime) is null ? null : PayloadValidator.ValidateManualDateTime(CombineManual(), _maxYear);
    }

    private string? CombineManual()
    {
        if (!System.DateTime.TryParseExact(ManualDate?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            || ParseTime(ManualTime) is not { } time)
        {
            return null;
        }

        return (date + time).ToString(DateTimePayload.ManualFormat, CultureInfo.InvariantCulture);
    }

    private static TimeSpan? ParseTime(string? text) =>
        TimeSpan.TryParseExact(text?.Trim(), [@"hh\:mm\:ss", @"hh\:mm", @"h\:mm\:ss", @"h\:mm"], CultureInfo.InvariantCulture, out var t) && t < TimeSpan.FromDays(1)
            ? t
            : null;

    /// <summary>Lines, commas or spaces separate servers; empty entries are dropped.</summary>
    internal static List<string> ParseServers(string? text) =>
        [.. (text ?? string.Empty).Split(['\r', '\n', ',', ' ', ';', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>"2026-10-07 18:24:00 (UTC+02:00) · device is 3.2 s ahead": the server's time in this computer's zone.</summary>
    internal static string DescribeServerTime(CurrentTimeSettings s, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (s.ServerUtc is not { } server)
        {
            return string.Empty;
        }

        var local = (server + elapsed).ToLocalTime();
        var text = FormatTime(local) + " " + TimeZoneCatalog.FormatOffset(local.Offset);
        if (s.Difference is { } diff)
        {
            var seconds = Math.Abs(diff.TotalSeconds);
            text += seconds < 1
                ? " · device and server agree"
                : string.Create(CultureInfo.InvariantCulture, $" · device is {seconds:0.#} s {(diff > TimeSpan.Zero ? "ahead" : "behind")}");
        }

        return text;
    }

    private static string FormatTime(DateTimeOffset time) => time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string Label(IDeviceInfo device) =>
        string.IsNullOrEmpty(device.Model) ? device.Address : $"{device.Address} · {device.Model}";

    private static string Count(int n) => n.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>gRPC errors carry the user message in Status.Detail; read it without referencing Grpc.</summary>
    private static string RpcDetail(Exception ex)
    {
        var status = ex.GetType().GetProperty("Status")?.GetValue(ex);
        return status?.GetType().GetProperty("Detail")?.GetValue(status) as string is { Length: > 0 } detail ? detail : ex.Message;
    }
}
