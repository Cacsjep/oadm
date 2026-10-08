using System.ComponentModel;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Oadm.Client.Tasks;

public partial class TaskDetailsWindow : Window
{
    public TaskDetailsWindow()
    {
        InitializeComponent();
        Footer.CancelButton.Click += OnClose;
        Footer.CancelButton.IsDefault = true;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is TaskDetailsViewModel vm)
        {
            vm.PropertyChanged += OnViewModelChanged;
            Dispatcher.UIThread.Post(ScrollToCurrentStep, DispatcherPriority.Background);
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TaskDetailsViewModel.CurrentStep))
        {
            ScrollToCurrentStep();
        }
    }

    /// <summary>Keeps the step the task is on in view (the running one, else the failed or last one).</summary>
    private void ScrollToCurrentStep()
    {
        if (DataContext is TaskDetailsViewModel { CurrentStep: { } step })
        {
            StepsGrid.ScrollIntoView(step, null);
        }
    }

    /// <summary>Stops the view model from following the task once the window is gone.</summary>
    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is TaskDetailsViewModel vm)
        {
            vm.PropertyChanged -= OnViewModelChanged;
        }

        (DataContext as IDisposable)?.Dispose();
        base.OnClosed(e);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
