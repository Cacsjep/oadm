using System.Windows.Input;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// The one Export command of a list that saves as CSV or PDF (SDK 0.15.0; the tabs of the Logs page): a command button
/// "Export" with the export icon and a menu with CSV and PDF. Disable it while the file is made and show the progress in
/// the list's progress row.
/// </summary>
public sealed class ExportButton : ToolbarButton
{
    public static readonly StyledProperty<ICommand?> CsvCommandProperty =
        AvaloniaProperty.Register<ExportButton, ICommand?>(nameof(CsvCommand));

    public static readonly StyledProperty<ICommand?> PdfCommandProperty =
        AvaloniaProperty.Register<ExportButton, ICommand?>(nameof(PdfCommand));

    private readonly MenuItem _csv = new() { Header = "CSV" };
    private readonly MenuItem _pdf = new() { Header = "PDF" };

    public ExportButton()
    {
        Text = "Export";
        IconKey = "export";
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        menu.Items.Add(_csv);
        menu.Items.Add(_pdf);
        Flyout = menu;
    }

    /// <summary>Saves the list as a CSV file.</summary>
    public ICommand? CsvCommand
    {
        get => GetValue(CsvCommandProperty);
        set => SetValue(CsvCommandProperty, value);
    }

    /// <summary>Saves the list as a PDF file.</summary>
    public ICommand? PdfCommand
    {
        get => GetValue(PdfCommandProperty);
        set => SetValue(PdfCommandProperty, value);
    }

    /// <summary>The menu entries CSV and PDF (tests).</summary>
    public IReadOnlyList<MenuItem> Entries => [_csv, _pdf];

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CsvCommandProperty)
        {
            _csv.Command = CsvCommand;
        }
        else if (change.Property == PdfCommandProperty)
        {
            _pdf.Command = PdfCommand;
        }
    }
}
