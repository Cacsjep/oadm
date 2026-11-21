using Avalonia.Controls;
using Avalonia.Data;

using Oadm.Sdk.Client.Controls;

namespace Oadm.Client.Logging;

/// <summary>
/// The progress of an export on a tab of the Logs page (DataContext a <see cref="LogExportViewModel"/>): the progress row
/// "Making the PDF file" and Cancel, only while the file is made. Shared by the Server, Client and Audit tab.
/// </summary>
public sealed class LogExportProgress : Grid
{
    public LogExportProgress()
    {
        ColumnDefinitions = new ColumnDefinitions("*,Auto");
        this.Bind(IsVisibleProperty, new Binding(nameof(LogExportViewModel.IsBusy)));
        var row = new ProgressRow();
        row.Bind(ProgressRow.ValueProperty, new Binding(nameof(LogExportViewModel.Progress)));
        row.Bind(ProgressRow.TextProperty, new Binding(nameof(LogExportViewModel.ProgressText)));
        row.Bind(ProgressRow.IsActiveProperty, new Binding(nameof(LogExportViewModel.IsBusy)));
        Children.Add(row);
        var cancel = new ToolbarButton { Text = "Cancel", IconKey = "cancel", Margin = new Avalonia.Thickness(12, 0, 0, 0) };
        cancel.Bind(Avalonia.Controls.Button.CommandProperty, new Binding(nameof(LogExportViewModel.CancelCommand)));
        SetColumn(cancel, 1);
        Children.Add(cancel);
    }
}
