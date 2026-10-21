using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.SiteNode.Admin;

namespace Oadm.SiteNode.App.ViewModels;

/// <summary>
/// A network card in the network select: ticked when it is chosen (All cards off); a change goes to the service. While
/// All cards is on no card is ticked.
/// </summary>
public sealed partial class CardItem : ObservableObject
{
    private readonly Func<CardItem, bool, Task> _changed;
    private bool _applying;

    public CardItem(CardView card, bool allCards, Func<CardItem, bool, Task> changed)
    {
        ArgumentNullException.ThrowIfNull(card);
        _changed = changed;
        Id = card.Id;
        Apply(card, allCards);
    }

    public string Id { get; }

    [ObservableProperty]
    public partial string Name { get; private set; } = "";

    [ObservableProperty]
    public partial string AddressesText { get; private set; } = "";

    /// <summary>"Default router": usually the office side.</summary>
    [ObservableProperty]
    public partial string Hint { get; private set; } = "";

    /// <summary>The second line of the card in the select: "192.168.0.5/24 · Default router".</summary>
    public string Details => Hint.Length > 0 ? $"{AddressesText} · {Hint}" : AddressesText;

    /// <summary>Chosen: the node reaches devices on this card (and the other chosen ones only).</summary>
    [ObservableProperty]
    public partial bool Chosen { get; set; }

    /// <summary>The service's state (no change is sent).</summary>
    public void Apply(CardView card, bool allCards)
    {
        ArgumentNullException.ThrowIfNull(card);
        _applying = true;
        Name = card.Name;
        AddressesText = card.Addresses.Count == 0 ? "No address" : string.Join(", ", card.Addresses);
        Hint = card.HasDefaultRouter ? "Default router" : "";
        OnPropertyChanged(nameof(Details));
        Chosen = !allCards && card.Reached;
        _applying = false;
    }

    partial void OnChosenChanged(bool value)
    {
        if (!_applying)
            _ = _changed(this, value);
    }
}

/// <summary>"All cards", the first entry of the network select: bound to <see cref="NodeAppViewModel.AllCards"/>.</summary>
public sealed class AllCardsItem(Func<bool> get, Action<bool> set) : ObservableObject
{
    public string Name { get; } = "All cards";

    public bool Chosen
    {
        get => get();
        set => set(value);
    }

    /// <summary>The service's state changed: the check box shows it again.</summary>
    public void Refresh() => OnPropertyChanged(nameof(Chosen));
}

/// <summary>A row of the ports table: an allowed port with Remove, or a port the server asks for with Allow and Dismiss.</summary>
public sealed record PortItem(PortEntry Entry, bool IsRequested)
{
    public static PortItem Allowed(PortEntry entry) => new(entry, false);

    public static PortItem Requested(PortEntry entry) => new(entry, true);

    /// <summary>The node connects to devices on the port.</summary>
    public const string Out = "Out";

    /// <summary>The node listens on the port at the site (a relay of the server, e.g. NTP).</summary>
    public const string In = "In";

    /// <summary>"Out" or "In".</summary>
    public string Direction => Entry.Use == PortUse.Listen ? In : Out;

    public string Protocol => AdminTexts.Name(Entry.Protocol);

    public string Port => Entry.Port.ToString(CultureInfo.InvariantCulture);

    /// <summary>What the port is for: "NTP server", "camera 192.168.0.30"; empty for the default ports.</summary>
    public string Reason => Entry.Reason;

    /// <summary>"Requested" (the server asks for it) or "Allowed".</summary>
    public string StateText => IsRequested ? "Requested" : "Allowed";

    public bool IsAllowed => !IsRequested;
}

/// <summary>A relay the server opened that does not run: "NTP server, UDP 123" and why.</summary>
public sealed record RelayItem(string Text, string State, string Message)
{
    public static RelayItem From(RelayView relay)
    {
        ArgumentNullException.ThrowIfNull(relay);
        var name = $"{AdminTexts.Name(relay.Protocol)} {relay.Port}";
        return new RelayItem(relay.Reason.Length > 0 ? $"{relay.Reason}, {name}" : name, relay.State, relay.Message.Length > 0 ? relay.Message : relay.State);
    }
}

/// <summary>A choice of a select field.</summary>
public sealed record Choice<T>(T Value, string Text)
{
    public override string ToString() => Text;
}
