using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Oadm.Plugins.MetadataMonitor.Client;

/// <summary>
/// Page view. View glue only: activates the view model while shown, keeps the newest row visible while Autoscroll is
/// on (and turns Autoscroll off / on when the user scrolls up / to the end), remembers the detail height and hands the
/// clipboard to the copy command.
/// </summary>
public partial class MetadataMonitorView : UserControl
{
    private MetadataMonitorViewModel? _vm;
    private ScrollBar? _verticalBar;

    public MetadataMonitorView()
    {
        InitializeComponent();
        MessageGrid.AddHandler(PointerWheelChangedEvent, (_, _) => Dispatcher.UIThread.Post(UpdateAutoscrollFromPosition, DispatcherPriority.Background), RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        DetailSplitter.DragCompleted += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.DetailHeight = Root.RowDefinitions[3].ActualHeight;
            }
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.MessagesAppended -= OnMessagesAppended;
            _vm.CopyText = null;
        }

        _vm = DataContext as MetadataMonitorViewModel;
        if (_vm is not null)
        {
            _vm.MessagesAppended += OnMessagesAppended;
            _vm.CopyText = CopyAsync;
            Root.RowDefinitions[3].Height = new GridLength(_vm.DetailHeight);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _vm?.Activate();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _vm?.Deactivate();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        _verticalBar = MessageGrid.GetVisualDescendants().OfType<ScrollBar>().FirstOrDefault(b => b.Orientation == Avalonia.Layout.Orientation.Vertical);
        if (_verticalBar is not null)
        {
            _verticalBar.Scroll += (_, _) => Dispatcher.UIThread.Post(UpdateAutoscrollFromPosition, DispatcherPriority.Background);
        }
    }

    private void OnMessagesAppended(object? sender, EventArgs e)
    {
        if (_vm is { Autoscroll: true })
        {
            Dispatcher.UIThread.Post(ScrollToEnd, DispatcherPriority.Background);
        }
    }

    private void ScrollToEnd()
    {
        if (_vm is { Autoscroll: true, Messages.Count: > 0 } vm)
        {
            MessageGrid.ScrollIntoView(vm.Messages[^1], null);
        }
    }

    /// <summary>The user scrolled: at the end Autoscroll turns on, above it off.</summary>
    private void UpdateAutoscrollFromPosition()
    {
        if (_vm is null || _verticalBar is null)
        {
            return;
        }

        _vm.Autoscroll = !_verticalBar.IsVisible || _verticalBar.Value >= _verticalBar.Maximum - 2;
    }

    private async Task CopyAsync(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text).ConfigureAwait(true);
        }
    }
}
