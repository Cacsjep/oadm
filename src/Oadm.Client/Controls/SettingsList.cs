using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;

namespace Oadm.Client.Controls;

/// <summary>
/// The Settings page's column of <see cref="SettingsGroup"/>s. <see cref="Filter"/> (the search box) shows only the
/// rows whose title or hint contains the text, also the rows of an expander or group whose title matches, hides groups
/// without a match and opens expanders with one. A group whose rows are all hidden by the view (<c>IsShown</c>) hides too.
/// </summary>
public sealed class SettingsList : StackPanel
{
    public static readonly StyledProperty<string?> FilterProperty =
        AvaloniaProperty.Register<SettingsList, string?>(nameof(Filter));

    public static readonly DirectProperty<SettingsList, bool> HasMatchesProperty =
        AvaloniaProperty.RegisterDirect<SettingsList, bool>(nameof(HasMatches), o => o.HasMatches);

    private bool _hasMatches = true;

    public SettingsList()
    {
        Classes.Add("settingsList");
    }

    public string? Filter
    {
        get => GetValue(FilterProperty);
        set => SetValue(FilterProperty, value);
    }

    /// <summary>False while the search finds nothing (the view shows its "no match" line).</summary>
    public bool HasMatches
    {
        get => _hasMatches;
        private set => SetAndRaise(HasMatchesProperty, ref _hasMatches, value);
    }

    internal static bool Contains(string? text, string filter) =>
        filter.Length == 0 || (text is not null && text.Contains(filter, StringComparison.OrdinalIgnoreCase));

    /// <summary>A row's <c>IsShown</c> changed: its group may hide or show again.</summary>
    internal static void RefreshFor(ILogical row) => row.FindLogicalAncestorOfType<SettingsList>()?.Apply();

    protected override void OnAttachedToLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        base.OnAttachedToLogicalTree(e);
        Apply();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FilterProperty)
        {
            Apply();
        }
    }

    private void Apply()
    {
        string filter = (Filter ?? string.Empty).Trim();
        bool any = false;
        foreach (var group in Children.OfType<SettingsGroup>())
        {
            any |= group.ApplyFilter(filter);
        }

        HasMatches = any || filter.Length == 0;
    }
}

/// <summary>A heading with its settings below it, 4 px apart (Client, General, Server, Support).</summary>
public sealed class SettingsGroup : StackPanel
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<SettingsGroup, string?>(nameof(Title));

    private readonly TextBlock _title = new();

    public SettingsGroup()
    {
        Classes.Add("settingsGroup");
        _title.Classes.Add("sectionTitle");
        _title.Classes.Add("settingsGroupTitle");
        Children.Add(_title);
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    internal bool ApplyFilter(string filter)
    {
        bool groupMatches = filter.Length > 0 && SettingsList.Contains(Title, filter);
        bool any = false;
        foreach (var child in Children)
        {
            switch (child)
            {
                case SettingsCard card:
                    card.SetFilterMatch(filter.Length == 0 || groupMatches || card.Matches(filter));
                    any |= card.IsShownAndMatching;
                    break;
                case SettingsExpander expander:
                    any |= expander.ApplyFilter(filter, groupMatches);
                    break;
            }
        }

        IsVisible = any;
        return any;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TitleProperty)
        {
            _title.Text = Title;
        }
    }
}
