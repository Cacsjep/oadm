using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

using Oadm.Sdk.Devices;

namespace Oadm.Plugins.HardeningScan.Client;

/// <summary>How a device did at the shown level (summary line, status filter, device icon color).</summary>
public enum RowKind
{
    NotScanned = 0,
    Pass = 1,
    Warn = 2,
    Fail = 3,

    /// <summary>The device could not be scanned (refused status, not reachable): <see cref="HardeningRow.Status"/> says why.</summary>
    NotReachable = 4,
}

/// <summary>A column of the shown level: the check and its position in <see cref="DeviceResult.States"/>.</summary>
public sealed record LevelColumn(CheckInfo Check, int ResultIndex);

/// <summary>
/// One device of the grid. Keeps the last result of both levels; the cells of the shown level are one byte per column
/// (<see cref="States"/>, replaced as a whole when they change, so a cell binding re-reads it) and the tooltips are built only
/// when one opens.
/// </summary>
public sealed class HardeningRow : INotifyPropertyChanged
{
    private static readonly PropertyChangedEventArgs AllChanged = new(string.Empty);

    public HardeningRow(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        DeviceId = device.Id;
        UpdateDevice(device);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid DeviceId { get; }

    public string Address { get; private set; } = string.Empty;

    public string Model { get; private set; } = string.Empty;

    public string Firmware { get; private set; } = string.Empty;

    public string Serial { get; private set; } = string.Empty;

    public string IconKey { get; private set; } = "device.generic";

    /// <summary>Numeric IPv4 order (host names after all addresses, by name).</summary>
    public long AddressSortKey { get; private set; }

    public DeviceResult? Basic { get; private set; }

    public DeviceResult? Extended { get; private set; }

    /// <summary>One <see cref="CheckState"/> per column of the shown level.</summary>
    public byte[] States { get; private set; } = [];

    /// <summary>The value per column of the shown level (tooltips, CSV).</summary>
    public string?[] Values { get; private set; } = [];

    public RowKind Kind { get; private set; }

    /// <summary>Why the device could not be scanned, else null.</summary>
    public string? Status { get; private set; }

    public string ScoreText { get; private set; } = string.Empty;

    /// <summary>Passed / rated checks (sorting); -1 when not scanned.</summary>
    public double ScoreSortKey { get; private set; } = -1;

    public DateTimeOffset? ScannedUtc { get; private set; }

    public string LastScanText { get; private set; } = string.Empty;

    public bool IsPass => Kind == RowKind.Pass;

    public bool IsWarn => Kind == RowKind.Warn;

    public bool IsFail => Kind is RowKind.Fail or RowKind.NotReachable;

    /// <summary>
    /// The device icon is coloured only when that adds information: red for a device that could not be scanned. Pass,
    /// warnings and failed checks are already in the Score and check columns, so the icon stays neutral for them.
    /// </summary>
    public bool IsIconError => Kind == RowKind.NotReachable;

    /// <summary>Lower-case text the search looks in.</summary>
    public string SearchText { get; private set; } = string.Empty;

    /// <summary>Takes newer device facts (address, model, firmware); returns whether anything shown changed.</summary>
    public bool UpdateDevice(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        var address = string.IsNullOrEmpty(device.HostName) || !string.IsNullOrEmpty(device.Address) ? device.Address : device.HostName;
        var model = device.Model ?? string.Empty;
        var firmware = device.FirmwareVersion ?? string.Empty;
        var icon = IconFor(device.Category);
        if (address == Address && model == Model && firmware == Firmware && device.Serial == Serial && icon == IconKey)
        {
            return false;
        }

        Address = address;
        Model = model;
        Firmware = firmware;
        Serial = device.Serial;
        IconKey = icon;
        AddressSortKey = SortKey(address);
        SearchText = string.Join('\n', address, model, firmware, Serial).ToLowerInvariant();
        PropertyChanged?.Invoke(this, AllChanged);
        return true;
    }

    /// <summary>Stores a result of its level (an older one than stored is ignored).</summary>
    public void SetResult(DeviceResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Level == ScanLevel.Extended)
        {
            if (Extended is null || Extended.ScannedUtc <= result.ScannedUtc)
            {
                Extended = result;
            }
        }
        else if (Basic is null || Basic.ScannedUtc <= result.ScannedUtc)
        {
            Basic = result;
        }
    }

    /// <summary>
    /// Recomputes the cells of <paramref name="level"/>: per column the newest result that scanned it (an Extended scan covers
    /// the Basic columns too). Raises one change for the row.
    /// </summary>
    public void Show(ScanLevel level, IReadOnlyList<LevelColumn> columns, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(columns);
        var newest = Newest(Basic, Extended);
        var older = ReferenceEquals(newest, Basic) ? Extended : Basic;
        if (level == ScanLevel.Basic)
        {
            older = null; // both contain the Basic columns: the newest wins
        }

        var states = new byte[columns.Count];
        var values = new string?[columns.Count];
        int passed = 0, rated = 0;
        bool warn = false, fail = false;
        for (var i = 0; i < columns.Count; i++)
        {
            var index = columns[i].ResultIndex;
            var state = Cell(newest, index, out var value);
            if (state == CheckState.NotScanned)
            {
                state = Cell(older, index, out value);
            }

            states[i] = (byte)state;
            values[i] = value;
            if (!columns[i].Check.IsRated)
            {
                continue;
            }

            switch (state)
            {
                case CheckState.Pass:
                    passed++;
                    rated++;
                    break;
                case CheckState.Warn:
                    warn = true;
                    rated++;
                    break;
                case CheckState.Fail:
                    fail = true;
                    rated++;
                    break;
                case CheckState.Error:
                    rated++;
                    break;
            }
        }

        States = states;
        Values = values;
        Status = newest?.Status;
        ScannedUtc = newest?.ScannedUtc;
        Kind = newest is null ? RowKind.NotScanned
            : newest.Status is not null ? RowKind.NotReachable
            : fail ? RowKind.Fail
            : warn ? RowKind.Warn
            : RowKind.Pass;
        ScoreText = newest is null || newest.Status is not null ? string.Empty : string.Create(CultureInfo.InvariantCulture, $"{passed}/{rated}");
        ScoreSortKey = newest is null ? -1 : rated == 0 ? 0 : passed / (double)rated;
        LastScanText = newest is null ? "Not scanned" : Ago(newest.ScannedUtc, now);
        PropertyChanged?.Invoke(this, AllChanged);
    }

    public CheckState StateAt(int column) => column < States.Length ? (CheckState)States[column] : CheckState.NotScanned;

    /// <summary>Tooltip of one cell: the state and value found, the rule and the guide's recommendation.</summary>
    public string CellTip(LevelColumn column, int index)
    {
        ArgumentNullException.ThrowIfNull(column);
        var check = column.Check;
        var state = StateAt(index);
        var text = new StringBuilder();
        text.Append(check.Title).Append(": ").Append(CheckStateCodes.ToLabel(state));
        if (index < Values.Length && !string.IsNullOrEmpty(Values[index]))
        {
            text.Append('\n').Append(Values[index]);
        }

        if (check.Rule.Length > 0)
        {
            text.Append("\n\n").Append(check.Rule);
        }

        text.Append("\n\n").Append(check.Recommendation).Append(" (").Append(check.Section).Append(')');
        if (check.Note is { } note)
        {
            text.Append("\n\n").Append(note);
        }

        return text.ToString();
    }

    public static string IconFor(DeviceCategory category) => category switch
    {
        DeviceCategory.Camera => "device.camera",
        DeviceCategory.Encoder => "device.encoder",
        DeviceCategory.Speaker => "device.speaker",
        DeviceCategory.Audio => "device.audio",
        DeviceCategory.Intercom => "device.intercom",
        DeviceCategory.Radar => "device.radar",
        DeviceCategory.IoModule => "device.io",
        DeviceCategory.DoorController => "device.door",
        _ => "device.generic",
    };

    /// <summary>"just now", "5 min ago", "3 h ago", "2 days ago", then the date.</summary>
    public static string Ago(DateTimeOffset when, DateTimeOffset now)
    {
        var age = now - when;
        return age.TotalMinutes < 1 ? "just now"
            : age.TotalHours < 1 ? string.Create(CultureInfo.InvariantCulture, $"{(int)age.TotalMinutes} min ago")
            : age.TotalDays < 1 ? string.Create(CultureInfo.InvariantCulture, $"{(int)age.TotalHours} h ago")
            : age.TotalDays < 2 ? "1 day ago"
            : age.TotalDays < 14 ? string.Create(CultureInfo.InvariantCulture, $"{(int)age.TotalDays} days ago")
            : when.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    public static long SortKey(string address)
    {
        if (IPAddress.TryParse(address, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            return ((long)bytes[0] << 24) | ((long)bytes[1] << 16) | ((long)bytes[2] << 8) | bytes[3];
        }

        return 1L << 33; // host names and IPv6 after the IPv4 addresses
    }

    private static DeviceResult? Newest(DeviceResult? a, DeviceResult? b) =>
        a is null ? b : b is null ? a : b.ScannedUtc >= a.ScannedUtc ? b : a;

    private static CheckState Cell(DeviceResult? result, int index, out string? value)
    {
        value = null;
        if (result is null || index >= result.States.Length)
        {
            return CheckState.NotScanned;
        }

        value = index < result.Values.Count ? result.Values[index] : null;
        return CheckStateCodes.FromCode(result.States[index]);
    }
}
