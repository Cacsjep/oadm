using Avalonia;
using Avalonia.Controls;

namespace Oadm.Client.Settings;

public partial class SettingsView : UserControl
{
    /// <summary>Below this width the backup card moves under the server settings (small or scaled screens).</summary>
    public const double TwoColumnWidth = 1100;

    private bool? _twoColumns;

    public SettingsView()
    {
        InitializeComponent();
        Columns.SizeChanged += (_, e) => Arrange(e.NewSize.Width);
    }

    /// <summary>Whether the page shows two columns now (tests).</summary>
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
        Grid.SetColumn(BackupCard, two ? 2 : 0);
        Grid.SetRow(BackupCard, two ? 0 : 1);
        BackupCard.Margin = two ? default : new Thickness(0, 16, 0, 0);
    }
}
