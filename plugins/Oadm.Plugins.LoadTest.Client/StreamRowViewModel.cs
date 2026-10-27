using CommunityToolkit.Mvvm.ComponentModel;

namespace Oadm.Plugins.LoadTest.Client;

/// <summary>One phase column of the camera table: the chip and its sort rank (worst first).</summary>
public sealed record PhaseCell(PhaseResult Result)
{
    public static readonly PhaseCell NotApplicable = new(PhaseResult.NotApplicable);

    public string Text => CheckTexts.Result(Result);

    public bool IsOk => Result == PhaseResult.Passed;

    public bool IsError => Result is PhaseResult.Failed or PhaseResult.Interrupted;

    public bool IsAccent => Result == PhaseResult.Running;

    /// <summary>0 = worst: Interrupted, Failed, Running, Waiting, Passed, N/A.</summary>
    public int SortKey => Result switch
    {
        PhaseResult.Interrupted => 0,
        PhaseResult.Failed => 1,
        PhaseResult.Running => 2,
        PhaseResult.Pending => 3,
        PhaseResult.Passed => 4,
        _ => 5,
    };

    /// <summary>The ramp column: the worst of the ramp steps the stream ran in; N/A when it ran in none.</summary>
    public static PhaseCell Combine(IEnumerable<PhaseResult> results)
    {
        PhaseCell? worst = null;
        foreach (var result in results)
        {
            var cell = new PhaseCell(result);
            if (result != PhaseResult.NotApplicable && (worst is null || cell.SortKey < worst.SortKey))
            {
                worst = cell;
            }
        }

        return worst ?? NotApplicable;
    }
}

/// <summary>One camera stream of the table, updated in place from the live rows.</summary>
public sealed partial class StreamRowViewModel : ObservableObject
{
    public StreamRowViewModel(StreamRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        Row = row;
        Apply(row);
    }

    public string StreamId => Row.StreamId;

    [ObservableProperty] public partial StreamRow Row { get; private set; }

    [ObservableProperty] public partial string Name { get; private set; } = string.Empty;

    [ObservableProperty] public partial string Address { get; private set; } = string.Empty;

    [ObservableProperty] public partial string Model { get; private set; } = string.Empty;

    [ObservableProperty] public partial PhaseCell Ramp { get; private set; } = PhaseCell.NotApplicable;

    [ObservableProperty] public partial PhaseCell Normal { get; private set; } = PhaseCell.NotApplicable;

    [ObservableProperty] public partial PhaseCell LowLight { get; private set; } = PhaseCell.NotApplicable;

    [ObservableProperty] public partial string PacketLossText { get; private set; } = string.Empty;

    [ObservableProperty] public partial string FrameLossText { get; private set; } = string.Empty;

    [ObservableProperty] public partial string MbpsText { get; private set; } = string.Empty;

    [ObservableProperty] public partial string JitterText { get; private set; } = string.Empty;

    [ObservableProperty] public partial string ProblemText { get; private set; } = string.Empty;

    [ObservableProperty] public partial bool HasProblem { get; private set; }

    /// <summary>Packet loss sort key; TCP streams (not measured) sort last.</summary>
    public double PacketLossSortKey => Row.PacketLossPercent ?? -1;

    public double FrameLossSortKey => Row.FrameLossPercent;

    public double MbpsSortKey => Row.AverageMbps;

    public double JitterSortKey => Row.JitterMs;

    public string AddressSortKey => Oadm.Plugins.Shared.AddressSortKeys.Of(Row.Address);

    /// <summary>Worst first: phases failed or interrupted, streaming now, then the worst loss; waiting last. Larger = worse.</summary>
    public double Badness { get; private set; }

    public void Apply(StreamRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        Row = row;
        Name = string.IsNullOrEmpty(row.SourceName) ? row.Name : row.Name + " · " + row.SourceName;
        Address = row.Address;
        Model = row.Model ?? string.Empty;
        Ramp = PhaseCell.Combine(row.Phases.Where(p => p.Phase <= CheckPhase.Ramp75).Select(p => p.Result));
        Normal = Of(row, CheckPhase.Normal);
        LowLight = Of(row, CheckPhase.LowLight);
        var tcp = string.Equals(row.Transport, "TCP", StringComparison.OrdinalIgnoreCase);
        PacketLossText = row.PacketLossPercent is { } loss ? CheckTexts.Percent(loss) : tcp ? CheckTexts.NotMeasured : string.Empty;
        FrameLossText = CheckTexts.Percent(row.FrameLossPercent);
        MbpsText = CheckTexts.Mbps(row.AverageMbps);
        JitterText = CheckTexts.Ms(row.JitterMs);
        ProblemText = row.Problem ?? row.Note ?? (tcp ? CheckTexts.SitesNoPacketLoss : string.Empty);
        HasProblem = row.Problem is not null;
        var failed = row.Phases.Count(p => p.Result is PhaseResult.Failed or PhaseResult.Interrupted);
        // Failed and interrupted first, then streaming now, then the finished ones by loss, the waiting ones last.
        var running = row.Phases.Any(p => p.Result == PhaseResult.Running);
        var waiting = !running && row.Phases.All(p => p.Result is PhaseResult.Pending or PhaseResult.NotApplicable);
        Badness = (failed * 1000) + (row.Phases.Any(p => p.Result == PhaseResult.Interrupted) ? 500 : 0) + (running ? 100 : 0) - (waiting ? 1 : 0)
            + Math.Max(row.PacketLossPercent ?? 0, row.FrameLossPercent);
        OnPropertyChanged(nameof(PacketLossSortKey));
        OnPropertyChanged(nameof(FrameLossSortKey));
        OnPropertyChanged(nameof(MbpsSortKey));
        OnPropertyChanged(nameof(JitterSortKey));
    }

    private static PhaseCell Of(StreamRow row, CheckPhase phase)
    {
        foreach (var p in row.Phases)
        {
            if (p.Phase == phase)
            {
                return new PhaseCell(p.Result);
            }
        }

        return PhaseCell.NotApplicable;
    }

    /// <summary>Worst first, then by name: the table's order.</summary>
    public static int WorstFirst(StreamRowViewModel a, StreamRowViewModel b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var c = b.Badness.CompareTo(a.Badness);
        return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase) is var n && n != 0 ? n
            : string.CompareOrdinal(a.StreamId, b.StreamId);
    }
}
