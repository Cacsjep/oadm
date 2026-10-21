using CommunityToolkit.Mvvm.ComponentModel;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// One entry of a select field with check boxes (<c>ComboBox</c> with the class <c>multiSelect</c>): a check box with the
/// name and an optional grey second line. The theme draws it; a click ticks the entry and the list stays open. The closed
/// select shows its <c>PlaceholderText</c> (bind it to what is ticked, e.g. <see cref="CheckChoiceList.Summary"/>).
/// </summary>
public interface ICheckChoice
{
    /// <summary>The first line.</summary>
    string Name { get; }

    /// <summary>The grey second line; empty for none.</summary>
    string Detail { get; }

    /// <summary>Ticked.</summary>
    bool Chosen { get; set; }
}

/// <summary>A plain <see cref="ICheckChoice"/> with the item behind it.</summary>
/// <example>
/// <code>
/// &lt;ComboBox Classes="multiSelect" ItemsSource="{Binding Sites.Entries}" PlaceholderText="{Binding Sites.Summary}" /&gt;
/// </code>
/// </example>
public sealed partial class CheckChoice : ObservableObject, ICheckChoice
{
    private bool _silent;

    /// <summary>A choice for <paramref name="value"/>.</summary>
    public CheckChoice(object? value, string name, string detail = "")
    {
        Value = value;
        Name = name ?? "";
        Detail = detail ?? "";
    }

    /// <summary>The item behind the choice (a site id, a card); null for an "All" entry.</summary>
    public object? Value { get; }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public string Detail { get; }

    /// <inheritdoc />
    [ObservableProperty]
    public partial bool Chosen { get; set; }

    /// <summary>The user ticked or unticked it (not raised by <see cref="SetChosen"/>).</summary>
    public event EventHandler? ChosenChanged;

    /// <summary>Ticks or unticks it without raising <see cref="ChosenChanged"/> (the state of the data).</summary>
    public void SetChosen(bool chosen)
    {
        _silent = true;
        Chosen = chosen;
        _silent = false;
    }

    partial void OnChosenChanged(bool value)
    {
        if (!_silent)
            ChosenChanged?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// The entries of a select field with check boxes that offers "All" or chosen items, nothing ticked meaning none: ticking
/// All clears the items, ticking an item unticks All. <see cref="Summary"/> is the text of the closed select.
/// </summary>
public sealed partial class CheckChoiceList : ObservableObject
{
    private readonly string _noneText;

    /// <summary>A list with the entry <paramref name="allText"/> first ("All sites"); <paramref name="noneText"/> while nothing is ticked.</summary>
    public CheckChoiceList(string allText, string noneText)
    {
        _noneText = noneText ?? "";
        All = new CheckChoice(null, allText);
        All.ChosenChanged += (_, _) =>
        {
            if (All.Chosen)
            {
                foreach (var item in Items)
                    item.SetChosen(false);
            }

            Raise();
        };
        Entries = [All];
    }

    /// <summary>The "All" entry.</summary>
    public CheckChoice All { get; }

    /// <summary>The items after "All".</summary>
    public IReadOnlyList<CheckChoice> Items { get; private set; } = [];

    /// <summary>All, then the items: the select's ItemsSource.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<CheckChoice> Entries { get; private set; }

    /// <summary>"All sites", the ticked names ("Building B, Warehouse") or the none text.</summary>
    public string Summary => All.Chosen
        ? All.Name
        : string.Join(", ", Items.Where(i => i.Chosen).Select(i => i.Name)) is { Length: > 0 } names ? names : _noneText;

    /// <summary>All, or at least one item, is ticked.</summary>
    public bool AnyChosen => All.Chosen || Items.Any(i => i.Chosen);

    /// <summary>The values of the ticked items (empty while All is ticked).</summary>
    public IReadOnlyList<object?> ChosenValues => All.Chosen ? [] : [.. Items.Where(i => i.Chosen).Select(i => i.Value)];

    /// <summary>Raised after the user changed what is ticked.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Shows <paramref name="items"/> and ticks All (<paramref name="all"/>) or the items whose value is in
    /// <paramref name="chosen"/>. Raises nothing.
    /// </summary>
    public void Set(IEnumerable<CheckChoice> items, bool all, IEnumerable<object?> chosen)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(chosen);
        foreach (var old in Items)
            old.ChosenChanged -= OnItemChanged;
        var picked = chosen.ToHashSet();
        Items = [.. items];
        foreach (var item in Items)
        {
            item.SetChosen(!all && picked.Contains(item.Value));
            item.ChosenChanged += OnItemChanged;
        }

        All.SetChosen(all);
        Entries = [All, .. Items];
        OnPropertyChanged(nameof(Items));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(AnyChosen));
    }

    private void OnItemChanged(object? sender, EventArgs e)
    {
        if (sender is CheckChoice { Chosen: true })
            All.SetChosen(false);
        Raise();
    }

    private void Raise()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(AnyChosen));
        OnPropertyChanged(nameof(ChosenValues));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
