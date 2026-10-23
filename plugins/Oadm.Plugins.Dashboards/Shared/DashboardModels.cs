using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

using Oadm.Sdk.Events;
using Oadm.Sdk.Layout;

namespace Oadm.Plugins.Dashboards;

/// <summary>Ids, limits, method names and topics shared by the server part, the page and the tests.</summary>
public static class DashboardsPluginInfo
{
    public const string PluginId = "oadm.dashboards";
    public const string DisplayName = "Dashboards";
    public const string IconKey = "gauge";

    /// <summary>The package whose events most dashboards show (the empty states point to it).</summary>
    public const string DeviceTriggersId = "oadm.device-triggers";

    /// <summary>Widgets in one dashboard; video widgets count too.</summary>
    public const int MaxWidgets = 50;

    /// <summary>Live videos in one dashboard (Live View's limit: decoding stays smooth on an average PC).</summary>
    public const int MaxVideos = 16;

    public const int MaxDashboards = 200;
    public const int MaxNameLength = 64;
    public const int MaxUnitLength = 10;
    public const int MaxThresholds = 10;
    public const int MaxDecimals = 6;
    public const int MinShare = 10;
    public const int MaxShare = 100;
    public const int DefaultShare = 60;

    /// <summary>The arc of a gauge: % of its radius.</summary>
    public const int MinLineWidth = 1;
    public const int MaxLineWidth = 20;
    public const int DefaultLineWidth = 2;

    /// <summary>The grid of a dashboard: 20 x 20 cells, a widget at least 2 x 2, a dropped value widget up to 4 x 4.</summary>
    public static readonly TileLayout Layout = new(20, 2, 4, MaxWidgets);

    /// <summary>The same grid for a dropped camera: up to 8 x 8.</summary>
    public static readonly TileLayout VideoLayout = new(20, 2, 8, MaxWidgets);

    /// <summary>Latest values kept in memory (event type and device); the least recently updated go first.</summary>
    public const int MaxKeys = 100_000;

    /// <summary>Values one page may watch: the widgets of 100 open dashboards of 50.</summary>
    public const int MaxWatchedKeys = 5_000;

    /// <summary>Values kept per event field for the type suggestion.</summary>
    public const int RecentValues = 20;

    /// <summary>A page renews its watch this often; the server forgets a watch it has not heard of for <see cref="WatchLifetime"/>.</summary>
    public static readonly TimeSpan WatchRenewal = TimeSpan.FromSeconds(10);

    public static readonly TimeSpan WatchLifetime = TimeSpan.FromSeconds(30);

    /// <summary>Changed values go to the pages at most this often (4 a second per widget).</summary>
    public static readonly TimeSpan PushInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>The field of a stateful event that holds its state (on / off); never a name a device uses.</summary>
    public const string ActiveField = "@active";

    public const string ActiveLabel = "Active";

    /// <summary>"Change dashboards": create, change and delete dashboards.</summary>
    public const string ManageKey = "manage";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };
}

/// <summary>Page backend methods and the live topics.</summary>
public static class DashboardsMethods
{
    /// <summary>-> <see cref="DashboardList"/>.</summary>
    public const string List = "list";

    /// <summary><see cref="SaveDashboardRequest"/> -> the stored <see cref="Dashboard"/> ("Change dashboards").</summary>
    public const string Save = "save";

    /// <summary><see cref="DeleteDashboardRequest"/> ("Change dashboards").</summary>
    public const string Delete = "delete";

    /// <summary>-> <see cref="CatalogReply"/>: the events with their fields seen so far.</summary>
    public const string Catalog = "catalog";

    /// <summary><see cref="DevicesRequest"/> -> <see cref="DevicesReply"/>: the devices of an event (the device pickers).</summary>
    public const string Devices = "devices";

    /// <summary><see cref="WatchRequest"/> -> <see cref="ValuesReply"/>: the values of the open tabs now, then pushed on change.</summary>
    public const string Watch = "watch";

    /// <summary><see cref="UnwatchRequest"/>: the page closed.</summary>
    public const string Unwatch = "unwatch";

    /// <summary>Event: the whole <see cref="DashboardList"/> after every change.</summary>
    public const string DashboardsTopic = "dashboards";

    /// <summary>Event: the whole <see cref="CatalogReply"/> when events or their fields changed (at most once a second).</summary>
    public const string CatalogTopic = "catalog";

    /// <summary>Event: <see cref="ValuesReply"/> with the watched values that changed.</summary>
    public const string ValuesTopic = "values";

    public static bool IsChange(string method) => method is Save or Delete;
}

/// <summary>What a widget shows.</summary>
public enum WidgetKind
{
    /// <summary>A yes / no value as an icon per state.</summary>
    OnOff = 0,

    /// <summary>The value as received.</summary>
    Text = 1,

    /// <summary>A number with decimals, unit and thresholds.</summary>
    Number = 2,

    /// <summary>The live video of a camera (no event).</summary>
    Video = 3,

    /// <summary>A number on an arc from <see cref="Widget.Min"/> to <see cref="Widget.Max"/>, with the Number widget's settings.</summary>
    Gauge = 4,
}

/// <summary>The type the user chose for a field; Automatic takes the declared or suggested one.</summary>
public enum FieldType
{
    Automatic = 0,
    YesNo = 1,
    Number = 2,
    Text = 3,
}

/// <summary>
/// A threshold of a Number or Gauge widget: from <see cref="Value"/> on the number takes <see cref="Color"/> (a gauge also
/// colors its arc from there up to the next threshold).
/// </summary>
public sealed record Threshold(double Value, string Color);

/// <summary>
/// One widget of a dashboard: a rectangle of grid cells and what it shows. Value widgets name an event type, a device (none
/// for a server event) and a field; video widgets a camera and channel. Settings of the other kinds are kept but unused.
/// </summary>
public sealed record Widget
{
    public string Id { get; init; } = string.Empty;

    public WidgetKind Kind { get; init; }

    public int X { get; init; }

    public int Y { get; init; }

    public int Width { get; init; } = 4;

    public int Height { get; init; } = 4;

    /// <summary>Shown at the top of the widget.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The name shows at the top of a value widget; off, the value takes the room (the edit frame still names it).</summary>
    public bool ShowName { get; init; } = true;

    public string? TypeId { get; init; }

    /// <summary>The device (also of a video widget); null for an event of the server.</summary>
    public Guid? DeviceId { get; init; }

    public string? Field { get; init; }

    public FieldType Type { get; init; }

    /// <summary>The channel of a video widget (1-based; 0 = the camera's first).</summary>
    public int Camera { get; init; }

    public string OnIcon { get; init; } = "status.ok";

    public string OnColor { get; init; } = "Green";

    public string OnText { get; init; } = string.Empty;

    public string OffIcon { get; init; } = "circle";

    public string OffColor { get; init; } = "Neutral";

    public string OffText { get; init; } = string.Empty;

    /// <summary>Icon size of an On / Off widget, % of the widget.</summary>
    public int IconSize { get; init; } = DashboardsPluginInfo.DefaultShare;

    /// <summary>Font size of a Text or Number widget, % of the widget.</summary>
    public int FontSize { get; init; } = DashboardsPluginInfo.DefaultShare;

    public string Color { get; init; } = "Text";

    /// <summary>Decimal places of a Number widget; null = as received.</summary>
    public int? Decimals { get; init; }

    public string Unit { get; init; } = string.Empty;

    public IReadOnlyList<Threshold> Thresholds { get; init; } = [];

    /// <summary>The start of a gauge's arc.</summary>
    public double Min { get; init; }

    /// <summary>The end of a gauge's arc; more than <see cref="Min"/>.</summary>
    public double Max { get; init; } = 100;

    /// <summary>The thickness of a gauge's arc, % of its radius.</summary>
    public int LineWidth { get; init; } = DashboardsPluginInfo.DefaultLineWidth;

    [JsonIgnore]
    public TileRect Rect => new(X, Y, Width, Height);

    [JsonIgnore]
    public bool IsValue => Kind != WidgetKind.Video;
}

/// <summary>A saved dashboard. <see cref="Version"/> grows with every save; a save of an older version is refused.</summary>
public sealed record Dashboard(string Id, string Name, long Version, IReadOnlyList<Widget> Widgets)
{
    /// <summary>
    /// The device group that owns it (its users see and change it, with the group's devices only); null = all devices (also
    /// every dashboard saved before device groups). Set by the server, never taken from a page.
    /// </summary>
    public string? Owner { get; init; }

    /// <summary>
    /// Only in the answer of a save: widgets the save dropped because their devices are no longer in the owner's devices (a
    /// user with all devices changed a group's dashboard). Never stored.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public int Dropped { get; init; }
}

/// <summary>Every dashboard, sorted by name (method "list" and the event "dashboards").</summary>
public sealed record DashboardList(IReadOnlyList<Dashboard> Dashboards);

/// <summary>Method "save": <see cref="Id"/> empty creates one; <see cref="Version"/> is the one the change was made on.</summary>
public sealed record SaveDashboardRequest(string? Id, string Name, long Version, IReadOnlyList<Widget> Widgets);

public sealed record DeleteDashboardRequest(string Id);

/// <summary>A value of an event: its kind is the declared one, or for text the one the received values suggest.</summary>
public sealed record CatalogField(string Name, string Label, EventValueKind Kind);

/// <summary>An event type with its values: Active for an on / off event, the declared ones, then any others received.</summary>
public sealed record CatalogEvent(
    string TypeId,
    string Name,
    string Group,
    string SourceId,
    string SourceName,
    bool Stateful,
    bool DeviceScope,
    bool Available,
    IReadOnlyList<CatalogField> Fields)
{
    /// <summary>
    /// Only some devices send the event (a device trigger's devices): the picker asks for them (<c>devices</c>) when it opens,
    /// so the catalog stays small also for a trigger on 10,000 devices. False: any device.
    /// </summary>
    public bool HasDeviceList { get; init; }
}

/// <summary>Method "devices": the devices of an event type.</summary>
public sealed record DevicesRequest(string TypeId);

/// <summary>The devices that can send the event; null = any device.</summary>
public sealed record DevicesReply(IReadOnlyList<Guid>? DeviceIds);

public sealed record CatalogReply(IReadOnlyList<CatalogEvent> Events);

/// <summary>A value of a widget: an event type on a device (none for a server event).</summary>
public sealed record ValueKey(string TypeId, Guid? DeviceId);

/// <summary>The keys of every widget in the open tabs of one page; renewed every 10 s.</summary>
public sealed record WatchRequest(string WatchId, IReadOnlyList<ValueKey> Keys);

public sealed record UnwatchRequest(string WatchId);

/// <summary>
/// The latest of an event on a device: <see cref="On"/> for an on / off event (off when no event came: the server knows
/// every event that is on), the fields of the events received (each field its last value), when the last one came, and
/// why the value may be old (<see cref="Stale"/>: "Connection lost", "Unreachable", ...).
/// </summary>
public sealed record ValueSnapshot(string TypeId, Guid? DeviceId, bool On, IReadOnlyDictionary<string, string> Data, DateTimeOffset? Time, string? Stale);

public sealed record ValuesReply(IReadOnlyList<ValueSnapshot> Values);

/// <summary>Reading received values: numbers in invariant culture, yes / no words, the type suggestion.</summary>
public static class FieldValues
{
    private static readonly HashSet<string> YesWords = new(["true", "1", "yes", "on"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> YesNoWords = new(["true", "false", "yes", "no", "on", "off"], StringComparer.OrdinalIgnoreCase);

    /// <summary>On for "true", "1", "yes", "on" (any case).</summary>
    public static bool IsOn(string? value) => value is not null && YesWords.Contains(value.Trim());

    public static bool TryNumber(string? value, out double number) =>
        double.TryParse(value?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number);

    /// <summary>
    /// The kind of a field: the declared one unless it is text; text fields whose recent values are all numbers suggest
    /// Number, all true / false / yes / no / on / off Yes / no ("0" and "1" alone stay Number).
    /// </summary>
    public static EventValueKind Suggest(EventValueKind declared, IEnumerable<string> recent)
    {
        ArgumentNullException.ThrowIfNull(recent);
        if (declared != EventValueKind.Text)
        {
            return declared;
        }

        var values = recent.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        if (values.Count == 0)
        {
            return EventValueKind.Text;
        }

        if (values.All(v => TryNumber(v, out _)))
        {
            return EventValueKind.Number;
        }

        return values.All(v => YesNoWords.Contains(v.Trim())) ? EventValueKind.YesNo : EventValueKind.Text;
    }

    /// <summary>The kind a widget reads: the user's choice, else the field's.</summary>
    public static EventValueKind Effective(FieldType chosen, EventValueKind field) => chosen switch
    {
        FieldType.YesNo => EventValueKind.YesNo,
        FieldType.Number => EventValueKind.Number,
        FieldType.Text => EventValueKind.Text,
        _ => field,
    };

    /// <summary>The widgets that can show a value of <paramref name="kind"/>, the best first.</summary>
    public static IReadOnlyList<WidgetKind> WidgetsFor(EventValueKind kind) => kind switch
    {
        EventValueKind.YesNo => [WidgetKind.OnOff, WidgetKind.Text],
        EventValueKind.Number => [WidgetKind.Number, WidgetKind.Gauge, WidgetKind.Text],
        _ => [WidgetKind.Text],
    };

    /// <summary>A number with <paramref name="decimals"/> places (null: as received), in the current culture.</summary>
    public static string FormatNumber(string received, double number, int? decimals) =>
        decimals is { } places
            ? number.ToString("N" + places.ToString(CultureInfo.InvariantCulture), CultureInfo.CurrentCulture)
            : DecimalsOf(received) is { } own ? number.ToString("N" + own.ToString(CultureInfo.InvariantCulture), CultureInfo.CurrentCulture) : number.ToString(CultureInfo.CurrentCulture);

    /// <summary>The color of a number: the highest threshold it reaches, else <paramref name="normal"/>.</summary>
    public static string ColorOf(double number, IReadOnlyList<Threshold> thresholds, string normal)
    {
        ArgumentNullException.ThrowIfNull(thresholds);
        var color = normal;
        foreach (var threshold in thresholds.OrderBy(t => t.Value))
        {
            if (number >= threshold.Value)
            {
                color = threshold.Color;
            }
        }

        return color;
    }

    private static int? DecimalsOf(string received)
    {
        var text = received.Trim();
        if (text.Contains('e', StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var dot = text.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? 0 : Math.Min(text.Length - dot - 1, DashboardsPluginInfo.MaxDecimals);
    }
}

/// <summary>The labelled and the small ticks of a gauge, and the decimal places its labels need.</summary>
public sealed record GaugeTicks(double Step, IReadOnlyList<double> Major, IReadOnlyList<double> Minor, int Decimals);

/// <summary>A stretch of a gauge's arc in one color.</summary>
public sealed record GaugeBand(double From, double To, string Color);

/// <summary>
/// The scale of a gauge, without UI: "nice" steps from Min and Max (1, 2 or 5 times a power of ten, 4 to 10 labelled
/// ticks; 0 to 100 gives 0, 10, 20 ..., 0 to 3,000 gives 0, 500, 1,000 ...), small ticks between them, where a value sits on
/// the arc and the colored bands of the thresholds.
/// </summary>
public static class GaugeScale
{
    /// <summary>Most labelled ticks (intervals between them).</summary>
    public const int MaxIntervals = 10;

    public static GaugeTicks Ticks(double min, double max)
    {
        if (DashboardRules.RangeProblem(min, max) is not null)
        {
            return new GaugeTicks(1, [], [], 0);
        }

        var rough = (max - min) / MaxIntervals;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        var factor = rough / magnitude;
        var nice = factor <= 1 ? 1 : factor <= 2 ? 2 : factor <= 5 ? 5 : 10;
        var step = nice * magnitude;
        var decimals = DecimalsOf(step);
        var minorStep = step / (nice == 2 ? 4 : 5);

        var major = new List<double>();
        var first = Math.Ceiling(min / step - 1e-9);
        for (var i = 0; ; i++)
        {
            var value = Math.Round((first + i) * step, 9);
            if (value > max + step * 1e-9)
            {
                break;
            }

            major.Add(value);
        }

        var minor = new List<double>();
        var firstMinor = Math.Ceiling(min / minorStep - 1e-9);
        for (var i = 0; ; i++)
        {
            var value = Math.Round((firstMinor + i) * minorStep, 9);
            if (value > max + minorStep * 1e-9)
            {
                break;
            }

            if (major.All(m => Math.Abs(m - value) > minorStep * 1e-6))
            {
                minor.Add(value);
            }
        }

        return new GaugeTicks(step, major, minor, decimals);
    }

    /// <summary>Where <paramref name="value"/> sits on the arc, 0 (Min) to 1 (Max); outside the range it stops at the end.</summary>
    public static double Fraction(double value, double min, double max) =>
        max > min && double.IsFinite(value) ? Math.Clamp((value - min) / (max - min), 0, 1) : 0;

    /// <summary>
    /// The arc in colors: <paramref name="normal"/> up to the first threshold, then each threshold's color up to the next one.
    /// Thresholds outside Min and Max only color from the edge on.
    /// </summary>
    public static IReadOnlyList<GaugeBand> Bands(double min, double max, IReadOnlyList<Threshold> thresholds, string normal)
    {
        ArgumentNullException.ThrowIfNull(thresholds);
        var bands = new List<GaugeBand>();
        var from = min;
        var color = normal;
        foreach (var threshold in thresholds.Where(t => t is not null).OrderBy(t => t.Value))
        {
            var at = Math.Clamp(threshold.Value, min, max);
            if (at > from)
            {
                bands.Add(new GaugeBand(from, at, color));
                from = at;
            }

            color = threshold.Color;
        }

        if (max > from)
        {
            bands.Add(new GaugeBand(from, max, color));
        }

        return bands;
    }

    /// <summary>A tick label: as many decimals as the step needs, grouped ("1,000").</summary>
    public static string Label(double value, int decimals) =>
        (Math.Abs(value) < 1e-12 ? 0 : value).ToString("N" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.CurrentCulture);

    private static int DecimalsOf(double step)
    {
        for (var places = 0; places < DashboardsPluginInfo.MaxDecimals; places++)
        {
            if (Math.Abs(Math.Round(step, places) - step) < step * 1e-6)
            {
                return places;
            }
        }

        return DashboardsPluginInfo.MaxDecimals;
    }
}

/// <summary>The colors a widget can use: theme brushes by name, so dark and light both work.</summary>
public static class WidgetColors
{
    /// <summary>Name -> label, in the order of the color select.</summary>
    public static readonly IReadOnlyList<(string Name, string Label)> All =
    [
        ("Text", "Text"),
        ("Secondary", "Grey text"),
        ("Neutral", "Grey"),
        ("Accent", "Accent"),
        ("Green", "Green"),
        ("Blue", "Blue"),
        ("Teal", "Teal"),
        ("Amber", "Amber"),
        ("Orange", "Orange"),
        ("Red", "Red"),
        ("Pink", "Pink"),
        ("Violet", "Violet"),
        ("Ok", "Status OK"),
        ("Warning", "Status warning"),
        ("Error", "Status error"),
    ];

    public static bool IsKnown(string? name) => name is not null && All.Any(c => c.Name == name);

    /// <summary>The theme brush of a color ("Oadm.Tag.GreenBrush").</summary>
    public static string BrushKey(string? name) => name switch
    {
        "Secondary" => "Oadm.TextSecondaryBrush",
        "Neutral" => "Oadm.StatusNeutralBrush",
        "Accent" => "Oadm.AccentBrush",
        "Ok" => "Oadm.StatusOkBrush",
        "Warning" => "Oadm.StatusWarningBrush",
        "Error" => "Oadm.StatusErrorBrush",
        "Green" or "Blue" or "Teal" or "Amber" or "Orange" or "Red" or "Pink" or "Violet" => "Oadm.Tag." + name + "Brush",
        _ => "Oadm.TextPrimaryBrush",
    };
}

/// <summary>The icons an On / Off widget can show (keys of the OADM icon set, without "Icon.").</summary>
public static class WidgetIcons
{
    public static readonly IReadOnlyList<(string Key, string Label)> All =
    [
        ("status.ok", "Check circle"),
        ("circle", "Circle"),
        ("status.neutral", "Minus circle"),
        ("status.error", "X circle"),
        ("status.warning", "Warning"),
        ("status.info", "Information"),
        ("check", "Check"),
        ("close", "X"),
        ("toggle", "Switch"),
        ("lightbulb", "Light bulb"),
        ("bell", "Bell"),
        ("zap", "Flash"),
        ("flame", "Flame"),
        ("thermometer", "Thermometer"),
        ("lock", "Locked"),
        ("lockOpen", "Unlocked"),
        ("eye", "Eye"),
        ("eyeOff", "Eye off"),
        ("shield", "Shield"),
        ("video", "Video"),
        ("activity", "Activity"),
        ("clock", "Clock"),
        ("device.door", "Door"),
        ("device.io", "I/O"),
        ("device.camera", "Camera"),
        ("device.speaker", "Speaker"),
        ("network", "Network"),
        ("user", "Person"),
        ("key", "Key"),
        ("server", "Server"),
    ];

    public static bool IsKnown(string? key) => key is not null && All.Any(i => i.Key == key);
}

/// <summary>The rules of a dashboard, checked by the page while editing and by the server before it stores.</summary>
public static class DashboardRules
{
    public const string NameMissing = "Enter a name.";

    /// <summary>The answer of a save that dropped widgets (<see cref="Dashboard.Dropped"/>).</summary>
    public static string DroppedText(int widgets) => widgets == 1
        ? "Saved without 1 widget of a device that is no longer in the device group."
        : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Saved without {widgets:N0} widgets of devices that are no longer in the device group.");

    public static readonly string NameTooLong = $"Use at most {DashboardsPluginInfo.MaxNameLength} characters on one line.";

    public static readonly string TooManyWidgets = $"A dashboard holds at most {DashboardsPluginInfo.MaxWidgets} widgets.";

    public static readonly string TooManyVideos = $"A dashboard holds at most {DashboardsPluginInfo.MaxVideos} live videos.";

    public const string Outside = "A widget lies outside the dashboard.";

    public const string Overlap = "Two widgets overlap.";

    public const string NoRoom = "There is no free space for another widget. Make a widget smaller or remove one.";

    public const string Gone = "The dashboard no longer exists. Another user deleted it.";

    public static readonly string TooManyDashboards = $"OADM keeps at most {DashboardsPluginInfo.MaxDashboards} dashboards. Delete one first.";

    public const string NoField = "Choose a value.";

    public const string NoCamera = "Choose a camera.";

    /// <summary>A widget of a device event without a device (the page checks it before a save).</summary>
    public const string NoDevice = "Choose a device for every widget.";

    public static readonly string UnitTooLong = $"Use at most {DashboardsPluginInfo.MaxUnitLength} characters.";

    public static readonly string TooManyThresholds = $"Use at most {DashboardsPluginInfo.MaxThresholds} thresholds.";

    public const string BadSetting = "A widget has a setting OADM does not know.";

    public const string RangeWrong = "Min must be less than Max.";

    public const string NotANumber = "Enter a number.";

    /// <summary>The largest Min or Max (a gauge of a temperature, a count, a level; never astronomic).</summary>
    public const double MaxRange = 1e12;

    /// <summary>The problem of a gauge's Min and Max, or null.</summary>
    public static string? RangeProblem(double min, double max) =>
        !double.IsFinite(min) || !double.IsFinite(max) || Math.Abs(min) > MaxRange || Math.Abs(max) > MaxRange ? NotANumber
        : min < max ? null : RangeWrong;

    public static string NameTaken(string name) => $"A dashboard named {name} exists already.";

    /// <summary>Not every camera fitted: "12 of 20 cameras added. A dashboard holds at most 16 live videos."</summary>
    public static string CamerasAdded(int added, int wanted, string reason) =>
        string.Create(CultureInfo.CurrentCulture, $"{added:N0} of {wanted:N0} cameras added. {reason}");

    /// <summary>The group's new size did not fit every widget.</summary>
    public static string Resized(int done, int all) =>
        string.Create(CultureInfo.CurrentCulture, $"{done:N0} of {all:N0} widgets have the new size. The others have no room for it.");

    /// <summary>Not every copy fitted: "12 of 20 copies added: There is no free space ..."</summary>
    public static string CopiesAdded(int added, int wanted, string reason) =>
        string.Create(CultureInfo.CurrentCulture, $"{added:N0} of {wanted:N0} copies added. {reason}");

    public static string ChangedMeanwhile(string name) =>
        $"Another user changed the dashboard {name} meanwhile. Click Cancel and make your changes again.";

    /// <summary>The problem of a dashboard name, or null.</summary>
    public static string? NameProblem(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return NameMissing;
        }

        return trimmed.Length > DashboardsPluginInfo.MaxNameLength || trimmed.Any(char.IsControl) ? NameTooLong : null;
    }

    /// <summary>The longest id, event type id or value name a widget may carry.</summary>
    public const int MaxIdLength = 256;

    /// <summary>The problem of a widget's name (empty is allowed: the widget shows no name), or null.</summary>
    public static string? WidgetNameProblem(string? name) =>
        (name?.Length ?? 0) > DashboardsPluginInfo.MaxNameLength || (name?.Any(char.IsControl) ?? false) ? NameTooLong : null;

    /// <summary>The first problem of the widgets, or null: the grid's rules, the limits and every widget's settings.</summary>
    public static string? WidgetsProblem(IReadOnlyList<Widget>? widgets)
    {
        if (widgets is null)
        {
            return null;
        }

        if (widgets.Count > DashboardsPluginInfo.MaxWidgets)
        {
            return TooManyWidgets;
        }

        if (widgets.Any(w => w is null))
        {
            return Outside;
        }

        if (widgets.Count(w => w.Kind == WidgetKind.Video) > DashboardsPluginInfo.MaxVideos)
        {
            return TooManyVideos;
        }

        if (widgets.Any(w => !DashboardsPluginInfo.Layout.Fits(w.Rect)))
        {
            return Outside;
        }

        if (TileLayout.FirstOverlap([.. widgets.Select(w => w.Rect)]) >= 0)
        {
            return Overlap;
        }

        if (widgets.Select(w => w.Id).Distinct(StringComparer.Ordinal).Count() != widgets.Count || widgets.Any(w => string.IsNullOrWhiteSpace(w.Id)))
        {
            return BadSetting;
        }

        foreach (var widget in widgets)
        {
            if (WidgetProblem(widget) is { } problem)
            {
                return problem;
            }
        }

        return null;
    }

    /// <summary>The problem of one widget's settings, or null.</summary>
    public static string? WidgetProblem(Widget widget)
    {
        ArgumentNullException.ThrowIfNull(widget);
        if (!Enum.IsDefined(widget.Kind) || !Enum.IsDefined(widget.Type))
        {
            return BadSetting;
        }

        if (WidgetNameProblem(widget.Name) is { } name)
        {
            return name;
        }

        // Settings a request leaves out (null) or makes too long are refused for every kind, also those it does not use.
        if (widget.Unit is null || widget.OnText is null || widget.OffText is null || widget.Thresholds is null
            || widget.Color is null || widget.OnColor is null || widget.OffColor is null || widget.OnIcon is null || widget.OffIcon is null
            || Bad(widget.Id) || Bad(widget.TypeId) || Bad(widget.Field) || Bad(widget.OnText) || Bad(widget.OffText))
        {
            return BadSetting;
        }

        if (widget.Kind == WidgetKind.Video)
        {
            if (widget.DeviceId is null || widget.DeviceId == Guid.Empty)
            {
                return NoCamera;
            }

            if (widget.Camera is < 0 or > 64)
            {
                return BadSetting;
            }
        }
        else if (string.IsNullOrWhiteSpace(widget.TypeId) || string.IsNullOrWhiteSpace(widget.Field))
        {
            return NoField;
        }

        if (widget.Unit.Length > DashboardsPluginInfo.MaxUnitLength || widget.Unit.Any(char.IsControl))
        {
            return UnitTooLong;
        }

        if (widget.Thresholds.Count > DashboardsPluginInfo.MaxThresholds)
        {
            return TooManyThresholds;
        }

        // Min and Max are kept for every kind: always numbers, and in order where a gauge uses them.
        if (!double.IsFinite(widget.Min) || !double.IsFinite(widget.Max))
        {
            return BadSetting;
        }

        if (widget.Kind == WidgetKind.Gauge && RangeProblem(widget.Min, widget.Max) is { } range)
        {
            return range;
        }

        var shares = new[] { widget.IconSize, widget.FontSize };
        var known = WidgetColors.IsKnown(widget.Color) && WidgetColors.IsKnown(widget.OnColor) && WidgetColors.IsKnown(widget.OffColor)
            && WidgetIcons.IsKnown(widget.OnIcon) && WidgetIcons.IsKnown(widget.OffIcon)
            && widget.Thresholds.All(t => t is not null && double.IsFinite(t.Value) && WidgetColors.IsKnown(t.Color))
            && shares.All(s => s is >= DashboardsPluginInfo.MinShare and <= DashboardsPluginInfo.MaxShare)
            && widget.LineWidth is >= DashboardsPluginInfo.MinLineWidth and <= DashboardsPluginInfo.MaxLineWidth
            && widget.Decimals is null or (>= 0 and <= DashboardsPluginInfo.MaxDecimals)
            && widget.OnText.Length <= DashboardsPluginInfo.MaxNameLength && widget.OffText.Length <= DashboardsPluginInfo.MaxNameLength;
        return known ? null : BadSetting;
    }

    /// <summary>Too long, or a control character (a line break) inside.</summary>
    private static bool Bad(string? text) => text is not null && (text.Length > MaxIdLength || text.Any(char.IsControl));

    /// <summary>"3 widgets".</summary>
    public static string WidgetCount(int count) =>
        count == 1 ? "1 widget" : string.Create(CultureInfo.CurrentCulture, $"{count:N0} widgets");
}
