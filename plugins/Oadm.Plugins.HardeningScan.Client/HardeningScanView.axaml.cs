using System.Collections;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.HardeningScan.Client;

/// <summary>
/// Page view. View glue only: builds one icon column per check of the shown level (cells bound to one byte of the row,
/// tooltips made when they open), activates the view model while shown, remembers the detail height and the column order and
/// widths, and hands the save dialog and the launcher to the view model.
/// </summary>
public partial class HardeningScanView : UserControl
{
    private static readonly FilePickerFileType CsvFiles = new("CSV files") { Patterns = ["*.csv"], MimeTypes = ["text/csv"] };

    private readonly List<DataGridColumn> _checkColumns = [];
    private HardeningScanViewModel? _vm;

    public HardeningScanView()
    {
        InitializeComponent();
        DetailSplitter.DragCompleted += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.Settings.DetailHeight = Math.Clamp(Root.RowDefinitions[5].ActualHeight, 120, 2000);
                _vm.SettingsStore.Save(_vm.Settings);
            }
        };
        ResultGrid.ColumnReordered += (_, _) => SaveColumns();
    }

    /// <summary>The check columns currently in the grid (tests).</summary>
    public IReadOnlyList<DataGridColumn> CheckColumns => _checkColumns;

    /// <summary>Scrolls the grid so that a check column is visible (tests, screenshots).</summary>
    public void ScrollToColumn(int index)
    {
        if (index >= 0 && index < _checkColumns.Count && ResultGrid.ItemsSource is System.Collections.IList { Count: > 0 } rows)
        {
            ResultGrid.ScrollIntoView(rows[0], _checkColumns[index]);
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.ColumnsChanged -= OnColumnsChanged;
            _vm.SaveCsv = null;
        }

        _vm = DataContext as HardeningScanViewModel;
        if (_vm is not null)
        {
            _vm.ColumnsChanged += OnColumnsChanged;
            _vm.SaveCsv = SaveCsvAsync;
            Root.RowDefinitions[5].Height = new GridLength(_vm.Settings.DetailHeight);
            BuildCheckColumns();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _vm?.Activate();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        SaveColumns();
        _vm?.Deactivate();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnColumnsChanged(object? sender, EventArgs e)
    {
        SaveColumns();
        BuildCheckColumns();
    }

    private void BuildCheckColumns()
    {
        foreach (var column in _checkColumns)
        {
            ResultGrid.Columns.Remove(column);
        }

        _checkColumns.Clear();
        if (_vm is null)
        {
            return;
        }

        for (var i = 0; i < _vm.Columns.Count; i++)
        {
            var column = CreateColumn(_vm.Columns[i], i);
            _checkColumns.Add(column);
            ResultGrid.Columns.Add(column);
        }

        ApplySavedLayout();
    }

    private DataGridTemplateColumn CreateColumn(LevelColumn levelColumn, int index)
    {
        var header = new TextBlock { Text = levelColumn.Check.Header };
        ToolTip.SetTip(header, levelColumn.Check.Title);
        header.AddHandler(ToolTip.ToolTipOpeningEvent, (_, _) => ToolTip.SetTip(header, _vm?.HeaderTip(index) ?? levelColumn.Check.Title));

        var column = new DataGridTemplateColumn
        {
            Header = header,
            Tag = levelColumn.Check.Id,
            Width = new DataGridLength(Math.Max(64, 50 + Math.Round(levelColumn.Check.Header.Length * 7.2))),
            MinWidth = 44,
            CanUserSort = true,
            CustomSortComparer = new StateComparer(index),
            CellTemplate = new FuncDataTemplate<HardeningRow>((_, _) => CreateCell(index), supportsRecycling: true),
        };
        return column;
    }

    /// <summary>An icon-only status chip bound to one byte of the row; the tooltip is made when it opens.</summary>
    private StatusChip CreateCell(int index)
    {
        var chip = new StatusChip();
        chip.Bind(StatusChip.IsOkProperty, State(index, s => s == CheckState.Pass));
        chip.Bind(StatusChip.IsWarningProperty, State(index, s => s == CheckState.Warn));
        chip.Bind(StatusChip.IsErrorProperty, State(index, s => s is CheckState.Fail or CheckState.Error));
        chip.Bind(IsVisibleProperty, State(index, s => s != CheckState.NotScanned));
        ToolTip.SetTip(chip, string.Empty);
        chip.AddHandler(ToolTip.ToolTipOpeningEvent, (_, _) =>
        {
            if (chip.DataContext is HardeningRow row && _vm is { } vm && index < vm.Columns.Count)
            {
                ToolTip.SetTip(chip, row.CellTip(vm.Columns[index], index));
            }
        });
        return chip;
    }

    private static ReflectionBinding State(int index, Func<CheckState, bool> test) =>
        new(nameof(HardeningRow.States)) { Converter = new CellStateConverter(index, test) };

    /// <summary>Restores the remembered widths and order of the columns (keys: the Tag of each column).</summary>
    private void ApplySavedLayout()
    {
        if (_vm is null)
        {
            return;
        }

        var settings = _vm.Settings;
        foreach (var column in ResultGrid.Columns)
        {
            if (column.Tag is string key && settings.ColumnWidths.TryGetValue(key, out var width) && width >= 20)
            {
                column.Width = new DataGridLength(width);
            }
        }

        var order = settings.ColumnOrder;
        var position = 0;
        foreach (var key in order)
        {
            var column = ResultGrid.Columns.FirstOrDefault(c => Equals(c.Tag, key));
            if (column is not null && position < ResultGrid.Columns.Count)
            {
                column.DisplayIndex = position++;
            }
        }
    }

    private void SaveColumns()
    {
        if (_vm is null || ResultGrid.Columns.Count == 0)
        {
            return;
        }

        var settings = _vm.Settings;
        foreach (var column in ResultGrid.Columns)
        {
            if (column.Tag is string key && column.ActualWidth > 0)
            {
                settings.ColumnWidths[key] = Math.Round(column.ActualWidth);
            }
        }

        var shown = ResultGrid.Columns.OrderBy(c => c.DisplayIndex).Select(c => c.Tag as string).OfType<string>().ToList();
        // Keys of the other level keep their place in the remembered order.
        settings.ColumnOrder = [.. shown.Concat(settings.ColumnOrder.Where(k => !shown.Contains(k, StringComparer.Ordinal)))];
        _vm.SettingsStore.Save(settings);
    }

    private async Task<bool> SaveCsvAsync(string fileName, string content)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
        {
            return false;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export hardening scan",
            SuggestedFileName = fileName,
            DefaultExtension = "csv",
            FileTypeChoices = [CsvFiles],
        }).ConfigureAwait(true);
        if (file is null)
        {
            return false;
        }

        await using var stream = await file.OpenWriteAsync().ConfigureAwait(true);
        await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await writer.WriteAsync(content).ConfigureAwait(true);
        return true;
    }

    /// <summary>Sorts a check column by state: fail, error, warn, pass, n/a, info, not scanned.</summary>
    private sealed class StateComparer(int index) : IComparer
    {
        public int Compare(object? x, object? y) => Rank(x).CompareTo(Rank(y));

        private int Rank(object? item) => item is HardeningRow row ? row.StateAt(index) switch
        {
            CheckState.Fail => 0,
            CheckState.Error => 1,
            CheckState.Warn => 2,
            CheckState.Pass => 3,
            CheckState.NotApplicable => 4,
            CheckState.Info => 5,
            _ => 6,
        } : 7;
    }
}
