using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Plugins.Provisioning.Model;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.Provisioning.Client;

/// <summary>One row of the camera table: a camera of the session by MAC address, updated in place when its row changes.</summary>
public sealed partial class CameraRowViewModel : ObservableObject
{
    public CameraRowViewModel(CameraRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        Row = row;
        Mac = row.Mac;
        Apply(row);
    }

    /// <summary>The server's row as last applied.</summary>
    public CameraRow Row { get; private set; }

    public string Mac { get; }

    /// <summary>When the camera got its row (its first address of the session), in local time.</summary>
    public string TimeText => Row.Seen == default ? string.Empty : Row.Seen.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The sort of the time column.</summary>
    public long TimeSortKey => Row.Seen.UtcTicks;

    [ObservableProperty]
    public partial string Model { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Serial { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Address { get; private set; } = string.Empty;

    /// <summary>The address as a number for the sort.</summary>
    [ObservableProperty]
    public partial long AddressSortKey { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOk), nameof(IsWarning), nameof(IsError), nameof(IsAccent), nameof(IsInfo))]
    public partial CameraState State { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAccent), nameof(IsInfo))]
    public partial bool IsWaiting { get; private set; }

    [ObservableProperty]
    public partial string StateText { get; private set; } = string.Empty;

    /// <summary>The node the camera is on, or ran last.</summary>
    [ObservableProperty]
    public partial string NodeText { get; private set; } = string.Empty;

    /// <summary>The first red (else yellow) result, or the row's own text.</summary>
    [ObservableProperty]
    public partial string Problem { get; private set; } = string.Empty;

    /// <summary>The sort of the State column: problems first.</summary>
    public int StateSortKey => State switch
    {
        CameraState.Failed => 0,
        CameraState.Running when IsWaiting => 1,
        CameraState.DoneWithWarnings => 2,
        CameraState.Running => 3,
        CameraState.Queued => 4,
        CameraState.Done => 5,
        _ => 6,
    };

    public bool IsOk => State == CameraState.Done;

    public bool IsWarning => State == CameraState.DoneWithWarnings;

    public bool IsError => State == CameraState.Failed;

    public bool IsAccent => State == CameraState.Running && !IsWaiting;

    public bool IsInfo => State == CameraState.Running && IsWaiting;

    /// <summary>A red node: Run failed again applies.</summary>
    public bool HasFailed => State == CameraState.Failed || Row.Results.Any(r => r?.State == NodeState.Error);

    /// <summary>Lowercase text the search looks in.</summary>
    public string SearchKey { get; private set; } = string.Empty;

    /// <summary>Takes a newer row (an older one, by its version, is ignored); true when it changed something.</summary>
    public bool Apply(CameraRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.Version < Row.Version)
        {
            return false;
        }

        Row = row;
        Model = row.Model ?? string.Empty;
        Serial = row.Serial;
        Address = row.Address;
        AddressSortKey = Network.Model.Ipv4.TryParse(row.Address, out var value) ? value : long.MaxValue;
        IsWaiting = row.WaitingForApproval;
        State = row.State;
        StateText = ProvisioningTexts.StateText(row.State, row.WaitingForApproval);
        NodeText = row.State is CameraState.Queued or CameraState.NotNew ? string.Empty : row.NodeName ?? string.Empty;
        Problem = ProblemOf(row);
        SearchKey = string.Join('\n', Mac, Model, Serial, Address).ToLowerInvariant();
        OnPropertyChanged(nameof(StateSortKey));
        OnPropertyChanged(nameof(HasFailed));
        return true;
    }

    private static string ProblemOf(CameraRow row)
    {
        var error = row.Results.FirstOrDefault(r => r?.State == NodeState.Error);
        var warning = row.Results.FirstOrDefault(r => r?.State == NodeState.Warning);
        return error?.Message ?? warning?.Message ?? (row.State == CameraState.NotNew ? null : row.Message) ?? string.Empty;
    }
}

/// <summary>
/// A camera's flow (spec "The page": the profile's canvas with the camera's result per node): every node of the session's
/// profile, green (Success, Not needed), yellow (Warning), red (Error, its message below), violet while it runs, grey when it
/// has not run, each with its message. Read only.
/// </summary>
public static class CameraFlow
{
    public static IReadOnlyList<NodeChainNode> Nodes(Profile? profile, IReadOnlyDictionary<NodeKind, string> names, CameraRow row)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(row);
        var nodes = profile?.Nodes ?? [];
        var list = new List<NodeChainNode>(nodes.Count);
        for (var i = 0; i < nodes.Count; i++)
        {
            var kind = nodes[i].Kind;
            var name = names.GetValueOrDefault(kind) ?? kind.ToString();
            var result = i < row.Results.Count ? row.Results[i] : null;
            var icon = NodeLook.IconKey(kind);
            list.Add(result switch
            {
                { State: NodeState.Success or NodeState.NotNeeded } => new NodeChainNode(name, icon, "Oadm.StatusOkBrush")
                {
                    Line = result.Message, Tip = result.Message, StatusIconKey = "status.ok", StatusBrushKey = "Oadm.StatusOkBrush",
                },
                { State: NodeState.Warning } => new NodeChainNode(name, icon, "Oadm.StatusWarningBrush")
                {
                    Line = result.Message, Tip = result.Message, StatusIconKey = "status.warning", StatusBrushKey = "Oadm.StatusWarningBrush",
                },
                { State: NodeState.Error } => new NodeChainNode(name, icon, "Oadm.StatusErrorBrush")
                {
                    Below = result.Message, Tip = result.Message, StatusIconKey = "status.error", StatusBrushKey = "Oadm.StatusErrorBrush",
                },
                _ when i == row.NodeIndex && row.State == CameraState.Running => new NodeChainNode(name, icon, "Oadm.AccentBrush")
                {
                    Line = row.WaitingForApproval ? ProvisioningTexts.WaitingForApproval : ProvisioningTexts.Running,
                    StatusIconKey = "status.running",
                    StatusBrushKey = "Oadm.AccentBrush",
                },
                _ => new NodeChainNode(name, icon, "Oadm.TextDisabledBrush") { Line = ProvisioningTexts.NotRun, IsDimmed = true },
            });
        }

        return list;
    }
}
