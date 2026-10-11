using System.Globalization;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// One step of a <see cref="WizardSteps"/> row: number, name, current / done, and whether it can be opened (every step
/// before it complete). The owner calls <see cref="Refresh"/> after the step or a problem changed.
/// </summary>
public sealed partial class WizardStepItem(int index, string name, Func<int> current, Func<int, bool> canOpen, Action<int> open) : ObservableObject
{
    public int Index { get; } = index;

    public string Name { get; } = name;

    public string Number { get; } = (index + 1).ToString(CultureInfo.InvariantCulture);

    public bool IsCurrent => current() == Index;

    public bool IsDone => current() > Index;

    public bool IsLater => current() < Index;

    public bool IsFirst => Index == 0;

    public bool CanOpen => !IsCurrent && canOpen(Index);

    [RelayCommand]
    private void Open() => open(Index);

    public void Refresh()
    {
        OnPropertyChanged(nameof(IsCurrent));
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(IsLater));
        OnPropertyChanged(nameof(CanOpen));
    }
}

/// <summary>
/// The step row of every wizard (user decision 2026-10-11): a numbered circle and the name per step, a thin line between
/// steps; the current step accent, done steps with a check, later steps grey. A step that can be opened is a button.
/// Bind <see cref="ItemsControl.ItemsSource"/> to <see cref="WizardStepItem"/>s. Look from the theme
/// (<c>Button.wizardStep</c>, <c>Border.wizardDot</c>, <c>TextBlock.wizardLabel</c>, <c>Border.wizardLine</c>).
/// </summary>
public sealed class WizardSteps : ItemsControl
{
    public WizardSteps()
    {
        ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Orientation = Orientation.Horizontal });
        ItemTemplate = new FuncDataTemplate<WizardStepItem>((_, _) => new WizardStepView(), supportsRecycling: false);
    }

    protected override Type StyleKeyOverride => typeof(ItemsControl);

    /// <summary>One step: the line from the previous step, then the dot and the name as one button.</summary>
    private sealed class WizardStepView : StackPanel
    {
        private readonly Border _dot = new();
        private readonly TextBlock _number = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        private readonly OadmIcon _check = new() { Data = ToolbarButton.ResolveIcon("check"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _label = new() { VerticalAlignment = VerticalAlignment.Center };
        private readonly Border _line = new() { VerticalAlignment = VerticalAlignment.Center };
        private readonly Button _button = new();
        private WizardStepItem? _item;

        public WizardStepView()
        {
            Orientation = Orientation.Horizontal;
            _number.Classes.Add("wizardNumber");
            _check.Classes.Add("wizardCheck");
            _dot.Classes.Add("wizardDot");
            _dot.Child = new Panel { Children = { _number, _check } };
            _label.Classes.Add("wizardLabel");
            _line.Classes.Add("wizardLine");
            _button.Classes.Add("wizardStep");
            _button.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _dot, _label } };
            _button.Click += (_, _) => _item?.OpenCommand.Execute(null);
            Children.AddRange([_line, _button]);
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (_item is not null)
            {
                _item.PropertyChanged -= OnItemChanged;
            }

            _item = DataContext as WizardStepItem;
            if (_item is not null)
            {
                _item.PropertyChanged += OnItemChanged;
                _number.Text = _item.Number;
                _label.Text = _item.Name;
                _line.IsVisible = !_item.IsFirst;
            }

            Apply();
        }

        private void OnItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Apply();

        private void Apply()
        {
            if (_item is null)
            {
                return;
            }

            _number.IsVisible = !_item.IsDone;
            _check.IsVisible = _item.IsDone;
            _dot.Classes.Set("current", _item.IsCurrent);
            _dot.Classes.Set("done", _item.IsDone);
            _label.Classes.Set("current", _item.IsCurrent);
            _label.Classes.Set("later", _item.IsLater);
            _line.Classes.Set("done", !_item.IsLater);
            _button.IsHitTestVisible = _item.CanOpen;
        }
    }
}
