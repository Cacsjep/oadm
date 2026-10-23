using Avalonia;
using Avalonia.Controls;

namespace Oadm.Client.Settings;

public partial class UsersPageView : UserControl
{
    /// <summary>
    /// Below this width of the page the device groups card moves under the users card (small or scaled screens: the users
    /// table needs about 800 px for its columns in each half; a Full HD screen at 100 % keeps them side by side).
    /// </summary>
    public const double TwoColumnWidth = 1640;

    private bool? _twoColumns;

    public UsersPageView()
    {
        InitializeComponent();
        Columns.SizeChanged += (_, e) => Arrange(e.NewSize.Width);
    }

    /// <summary>Whether the users and the device groups stand side by side now (tests).</summary>
    public bool HasTwoColumns => _twoColumns ?? true;

    private void Arrange(double width)
    {
        bool two = width >= TwoColumnWidth;
        if (two == _twoColumns)
        {
            return;
        }

        _twoColumns = two;
        Columns.ColumnDefinitions[1].Width = new GridLength(two ? 16 : 0);
        Columns.ColumnDefinitions[2].Width = two ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(GroupsCard, two ? 2 : 0);
        Grid.SetRow(GroupsCard, two ? 0 : 1);
        GroupsCard.Margin = two ? default : new Thickness(0, 16, 0, 0);

        // Stacked, the cards would push the roles card off the page: they scroll in half of its height instead.
        Page.RowDefinitions[1].Height = two ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
    }
}
