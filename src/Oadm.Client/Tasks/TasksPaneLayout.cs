using System.ComponentModel;

using Avalonia;
using Avalonia.Controls;

namespace Oadm.Client.Tasks;

/// <summary>
/// The one "page content above, tasks pane below" layout: <see cref="MainContent"/> on top, the
/// draggable pane splitter, and the shared tasks pane with its persisted height. Used by the
/// Devices page and by core plugin pages that run tasks (ICorePluginPage.ShowTasksPane), so a
/// technician sees the tasks of a rollout without switching pages.
/// </summary>
public sealed class TasksPaneLayout : Grid
{
    public static readonly StyledProperty<object?> MainContentProperty =
        AvaloniaProperty.Register<TasksPaneLayout, object?>(nameof(MainContent));

    public static readonly StyledProperty<TasksViewModel?> TasksProperty =
        AvaloniaProperty.Register<TasksPaneLayout, TasksViewModel?>(nameof(Tasks));

    public static readonly StyledProperty<bool> ShowTasksProperty =
        AvaloniaProperty.Register<TasksPaneLayout, bool>(nameof(ShowTasks), true);

    public static readonly StyledProperty<double> MainMinHeightProperty =
        AvaloniaProperty.Register<TasksPaneLayout, double>(nameof(MainMinHeight));

    private const int MainRow = 0;
    private const int TasksRow = 2;

    private readonly ContentControl _main = new();
    private readonly GridSplitter _splitter = new();
    private readonly Border _gap = new();
    private readonly TasksPaneView _pane = new();
    private TasksViewModel? _attached;

    public TasksPaneLayout()
    {
        RowDefinitions = new RowDefinitions("*,Auto,Auto");
        _splitter.Classes.Add("paneSplitter");
        _splitter.DragCompleted += (_, _) => _attached?.CommitPaneHeight(RowDefinitions[TasksRow].ActualHeight);
        SetRow(_main, MainRow);
        SetRow(_splitter, 1);
        SetRow(_gap, 1);
        SetRow(_pane, TasksRow);
        Children.Add(_main);
        Children.Add(_splitter);
        Children.Add(_gap);
        Children.Add(_pane);
        Apply();
    }

    public object? MainContent
    {
        get => GetValue(MainContentProperty);
        set => SetValue(MainContentProperty, value);
    }

    public TasksViewModel? Tasks
    {
        get => GetValue(TasksProperty);
        set => SetValue(TasksProperty, value);
    }

    /// <summary>False hides the splitter and the tasks pane; the main content takes the whole height.</summary>
    public bool ShowTasks
    {
        get => GetValue(ShowTasksProperty);
        set => SetValue(ShowTasksProperty, value);
    }

    public double MainMinHeight
    {
        get => GetValue(MainMinHeightProperty);
        set => SetValue(MainMinHeightProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (this.TryFindResource("Oadm.SplitterHeight", ActualThemeVariant, out object? h) && h is double height)
        {
            _gap.Height = height;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MainContentProperty)
        {
            _main.Content = MainContent;
        }
        else if (change.Property == TasksProperty)
        {
            if (_attached is not null)
            {
                _attached.PropertyChanged -= OnTasksPropertyChanged;
            }

            _attached = Tasks;
            _pane.DataContext = _attached;
            if (_attached is not null)
            {
                _attached.PropertyChanged += OnTasksPropertyChanged;
            }

            Apply();
        }
        else if (change.Property == ShowTasksProperty)
        {
            Apply();
        }
        else if (change.Property == MainMinHeightProperty)
        {
            RowDefinitions[MainRow].MinHeight = MainMinHeight;
        }
    }

    private void OnTasksPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TasksViewModel.IsExpanded) or nameof(TasksViewModel.PaneHeight))
        {
            Apply();
        }
    }

    /// <summary>
    /// Expanded: the tasks row has the persisted pixel height the splitter changes. Collapsed:
    /// header only, a plain gap instead of the splitter. Hidden: no pane at all.
    /// </summary>
    private void Apply()
    {
        bool show = ShowTasks && _attached is not null;
        bool expanded = show && _attached!.IsExpanded;
        _pane.IsVisible = show;
        _splitter.IsVisible = expanded;
        _gap.IsVisible = show && !expanded;

        RowDefinition row = RowDefinitions[TasksRow];
        if (expanded)
        {
            row.MinHeight = TasksViewModel.MinPaneHeight;
            row.Height = new GridLength(_attached!.PaneHeight, GridUnitType.Pixel);
        }
        else
        {
            row.MinHeight = 0;
            row.Height = GridLength.Auto;
        }
    }
}
