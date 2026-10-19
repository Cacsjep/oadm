using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.SiteNode.Admin;

namespace Oadm.SiteNode.App.ViewModels;

/// <summary>A network card with "Reach devices on this network"; a change goes to the service.</summary>
public sealed partial class CardItem : ObservableObject
{
    private readonly Func<CardItem, bool, Task> _changed;
    private bool _applying;

    public CardItem(CardView card, Func<CardItem, bool, Task> changed)
    {
        ArgumentNullException.ThrowIfNull(card);
        _changed = changed;
        Id = card.Id;
        Apply(card);
    }

    public string Id { get; }

    [ObservableProperty]
    public partial string Name { get; private set; } = "";

    [ObservableProperty]
    public partial string AddressesText { get; private set; } = "";

    /// <summary>"Default router" for the office side (not reached by default).</summary>
    [ObservableProperty]
    public partial string Hint { get; private set; } = "";

    [ObservableProperty]
    public partial bool Reached { get; set; }

    /// <summary>The service's state (no change is sent).</summary>
    public void Apply(CardView card)
    {
        ArgumentNullException.ThrowIfNull(card);
        _applying = true;
        Name = card.Name;
        AddressesText = card.Addresses.Count == 0 ? "No address" : string.Join(", ", card.Addresses);
        Hint = card.HasDefaultRouter ? "Default router" : "";
        Reached = card.Reached;
        _applying = false;
    }

    partial void OnReachedChanged(bool value)
    {
        if (!_applying)
            _ = _changed(this, value);
    }
}

/// <summary>An allowed port with Remove, or a requested one with Allow and Dismiss.</summary>
public sealed record PortItem(PortEntry Entry, string Text, string Reason)
{
    public static PortItem Allowed(PortEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new PortItem(entry, entry.Name, entry.Reason);
    }

    /// <summary>"Connect to TCP 8443: camera 192.168.0.30", "Listen on UDP 123: NTP server".</summary>
    public static PortItem Requested(PortEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var text = (entry.Use == PortUse.Listen ? "Listen on " : "Connect to ") + entry.Name;
        return new PortItem(entry, entry.Reason.Length > 0 ? text + ": " + entry.Reason : text, entry.Reason);
    }
}

/// <summary>An extra subnet with Remove.</summary>
public sealed record SubnetItem(string Subnet);

/// <summary>A relay the server opened: "NTP server, UDP 123" and its state.</summary>
public sealed record RelayItem(string Text, string State, string Message, bool IsRunning)
{
    public static RelayItem From(RelayView relay)
    {
        ArgumentNullException.ThrowIfNull(relay);
        var name = $"{AdminTexts.Name(relay.Protocol)} {relay.Port}";
        return new RelayItem(relay.Reason.Length > 0 ? $"{relay.Reason}, {name}" : name, relay.State, relay.Message, relay.State == "Running");
    }
}

/// <summary>A line of the connection log as the grid shows it (local time).</summary>
public sealed record LogRow(string Time, string Server, string Target, string Kind, string Result)
{
    /// <summary>A connection that worked ("Connected", a relay's "Accepted").</summary>
    public bool IsOk => Result is "Connected" or "Accepted";

    /// <summary>Refused by the node's own rules ("Port not allowed", "Outside the networks", "Busy"): something to allow here.</summary>
    public bool IsWarning => Result is "Port not allowed" or "Outside the networks" or "Busy";

    /// <summary>A range scan's count ("12 answered"): information only.</summary>
    public bool IsInfo => Result.EndsWith(" answered", StringComparison.Ordinal);

    /// <summary>The device did not answer (refused, timed out, unreachable, reset, name not found).</summary>
    public bool IsError => !IsOk && !IsWarning && !IsInfo;

    public static LogRow From(ConnectionLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new LogRow(entry.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), entry.Server, entry.Target, entry.Kind, entry.Result);
    }
}

/// <summary>A choice of a select field.</summary>
public sealed record Choice<T>(T Value, string Text)
{
    public override string ToString() => Text;
}
