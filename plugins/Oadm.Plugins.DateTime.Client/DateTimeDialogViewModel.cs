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
/// "Set date and time" dialog, a clone of the ADM / AXIS Camera Station dialog: Device time (first selected device,
/// read-only), Time zone (searchable IANA list, "Automatically adjust for daylight saving time changes") and Time mode
/// (Synchronize with server computer time, Synchronize with NTP server: Obtain from DHCP / Use servers / NTS, Set
/// manually). Exactly like ADM there is no "keep": OK writes the time zone and the time mode to every selected device.
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

        FilteredZones = TimeZoneCatalog.All;
        var local = _now();
        ManualDate = local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        ManualTime = local.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        DeviceTimeStatus = $"Reading the time of {Label(devices[0])}...";
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

    /// <summary>"10.0.0.48 · P3265-V" or "10.0.0.48 · P3265-V (first of 12 selected devices)".</summary>
    public string DeviceCardDescription => IsMultiDevice
        ? $"{Label(_devices[0])}, the first of {_devices.Count.ToString("N0", CultureInfo.InvariantCulture)} selected devices"
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

    [ObservableProperty]
    public partial string DeviceTimeText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DeviceTimeZoneText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DeviceTimeModeText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ServerTimeText { get; set; } = string.Empty;

    /// <summary>IANA id of the OADM server's time zone (server time mode).</summary>
    [ObservableProperty]
    public partial string? ServerTimeZone { get; set; }

    // ---- Time zone ----

    [ObservableProperty]
    public partial IReadOnlyList<TimeZoneEntry> FilteredZones { get; set; }

    [ObservableProperty]
    public partial string ZoneSearch { get; set; } = string.Empty;

    [ObservableProperty]
    public partial TimeZoneEntry? SelectedZone { get; set; }

    [ObservableProperty]
    public partial bool AdjustForDst { get; set; } = true;

    /// <summary>The selected zone and DST equal what the (single) device has: the task is named after the time mode.</summary>
    public bool TimeZoneUnchanged => !IsMultiDevice && !IsServerTime && SelectedZone is not null && _initialZoneId is not null
        && string.Equals(SelectedZone.Id, _initialZoneId, StringComparison.Ordinal) && AdjustForDst == _initialDst;

    public bool CanEditTimeZone => !IsServerTime;

    public bool CanAdjustDst => CanEditTimeZone && SelectedZone is { ObservesDaylightSaving: true };

    /// <summary>The time zone error, shown directly below the time zone list.</summary>
    public string? TimeZoneError => ErrorOf(nameof(SelectedZone));

    public bool HasTimeZoneError => TimeZoneError is not null;

    /// <summary>Card description: what happens with the time zone.</summary>
    public string TimeZoneDescription => IsServerTime
        ? ServerTimeZone is { } server
            ? $"The devices get the time zone of the OADM server: {server}."
            : "The OADM server's time zone is not known to AXIS devices; the devices keep their time zone."
        : SelectedZone is { } zone
            ? $"Set to {zone.Label}" + (AdjustForDst || !zone.ObservesDaylightSaving ? "." : ", without daylight saving time.")
            : "Select the time zone of the devices.";

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseServers), nameof(ShowServerList))]
    public partial bool UseDhcp { get; set; }

    public bool UseServers
    {
        get => !UseDhcp;
        set => UseDhcp = !value;
    }

    public bool ShowServerList => IsNtp && (!UseDhcp || UseNts);

    /// <summary>One host name or IP address per line (commas and spaces also separate).</summary>
    [ObservableProperty]
    public partial string NtpServersText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowServerList), nameof(ServersLabel))]
    public partial bool UseNts { get; set; }

    public bool ShowNts => NtsDeviceCount > 0;

    public string ServersLabel => UseNts ? "NTS KE servers" : "NTP servers";

    public string NtsHint => NtsDeviceCount == _devices.Count
        ? "Network Time Security: the servers must be NTS KE servers."
        : $"Network Time Security: supported by {Count(NtsDeviceCount)} of {Count(_devices.Count)} devices (ntp 1.5 or later); the others fail without changes.";

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
                TimeMode.ServerTime => "The devices get the date and time of the OADM server once, when each task runs, and NTP is turned off.",
                TimeMode.Manual => "Date and time in the time zone of each device. NTP is turned off.",
                _ => null,
            };
            var devices = Mode switch
            {
                TimeMode.ServerTime or TimeMode.Manual when NoTimeApiCount > 0 =>
                    $"{Count(NoTimeApiCount)} of the selected devices {(NoTimeApiCount == 1 ? "has" : "have")} no Time API (AXIS OS 9.30 or later) and cannot take a date and time; their tasks fail without changes.",
                TimeMode.Ntp when SingleNtpServerCount > 0 && ServerCount > 1 =>
                    $"{Count(SingleNtpServerCount)} of the selected devices {(SingleNtpServerCount == 1 ? "takes" : "take")} only one NTP server (no NTP API); their tasks fail without changes.",
                _ => null,
            };
            return note is null ? devices : devices is null ? note : note + " " + devices;
        }
    }

    public bool HasModeNote => ModeNote is not null;

    public string ModeDescription => DeviceTimeModeText.Length == 0
        ? "Written to every selected device."
        : "Written to every selected device. Device: " + DeviceTimeModeText;

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

        DeviceTimeText = current.DeviceLocal is { } local
            ? local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + (current.DeviceUtc is null ? string.Empty : " " + TimeZoneCatalog.FormatOffset(local.Offset))
            : "Not reported";
        DeviceTimeZoneText = current.TimeZone is { } tz
            ? TimeZoneCatalog.Find(tz)?.Label ?? tz
            : current.PosixTimeZone is { } posix
                ? $"Custom (POSIX {posix}), daylight saving {(current.DstEnabled == false ? "off" : "on")}"
                : "Not reported";
        DeviceTimeModeText = DescribeMode(current);
        ServerTimeText = DescribeServerTime(current);

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
            NtpServersText = string.Join(Environment.NewLine, servers);
        }

        UseDhcp = current.NtpSource == NtpSource.Dhcp && current.NtsEnabled != true;
        UseNts = current.NtsEnabled == true && ShowNts;
        if (current.DeviceLocal is { } deviceLocal)
        {
            ManualDate = deviceLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            ManualTime = deviceLocal.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        }

        OnPropertyChanged(nameof(ModeDescription));
        Validation.Reset(); // prefilled, not edited: errors show once the user edits a field or tries OK
        Recompute();
    }

    public void ShowLoadError(string message)
    {
        IsLoading = false;
        DeviceTimeFailed = true;
        DeviceTimeStatus = "The device time could not be read: " + message;
    }

    /// <summary>Reads the first device's time settings (read-only query) for the Device time card.</summary>
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

    partial void OnZoneSearchChanged(string value)
    {
        FilteredZones = [.. TimeZoneCatalog.Search(TimeZoneCatalog.All, value)];
    }

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
            nameof(TimeZoneUnchanged), nameof(CanEditTimeZone), nameof(CanAdjustDst), nameof(TimeZoneDescription),
            nameof(ShowServerList), nameof(ModeNote), nameof(HasModeNote),
        })
        {
            OnPropertyChanged(name);
        }

        CanApply = IsFormValid && BuildPayload() is not null;
        OnPropertyChanged(nameof(ApplyBlockedReason));
    }

    protected override void OnValidationChanged()
    {
        OnPropertyChanged(nameof(TimeZoneError));
        OnPropertyChanged(nameof(HasTimeZoneError));
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

    internal static string DescribeMode(CurrentTimeSettings s)
    {
        if (s.NtpEnabled == false)
        {
            return "Set manually (NTP off)";
        }

        if (s.NtpEnabled is null)
        {
            return "Not reported";
        }

        var text = s.NtsEnabled == true
            ? "Synchronize with NTS KE servers " + Join(s.NtsServers)
            : s.NtpSource == NtpSource.Dhcp
                ? "Synchronize with NTP server, obtained from DHCP" + (s.AdvertisedServers.Count > 0 ? " (" + Join(s.AdvertisedServers) + ")" : string.Empty)
                : "Synchronize with NTP server " + Join(s.NtpServers);
        return s.Synced switch
        {
            true => text + " · synchronized" + (s.NtpOffsetMilliseconds is { } o ? string.Create(CultureInfo.InvariantCulture, $", offset {o:0.###} ms") : string.Empty),
            false => text + " · not synchronized",
            _ => text,
        };

        static string Join(IReadOnlyList<string> list) => list.Count == 0 ? "(none)" : string.Join(", ", list);
    }

    internal static string DescribeServerTime(CurrentTimeSettings s)
    {
        if (s.ServerUtc is not { } server)
        {
            return "Not available";
        }

        var text = server.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " (this computer's time zone)";
        if (s.Difference is { } diff)
        {
            var seconds = Math.Abs(diff.TotalSeconds);
            text += seconds < 1
                ? " · device and server agree"
                : string.Create(CultureInfo.InvariantCulture, $" · device is {seconds:0.#} s {(diff > TimeSpan.Zero ? "ahead" : "behind")}");
        }

        return text;
    }

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
