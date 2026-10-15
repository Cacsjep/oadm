using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Oadm.Plugins.ActionRules.Client;

/// <summary>A change of the last 30 days in the undo list.</summary>
public sealed class UndoRunRow(UndoRun run)
{
    public UndoRun Run { get; } = run;

    public string TimeText => Run.CreatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public string Description => Run.Description;

    public string StateText => Run.Undone ? "Undone" : string.Empty;

    public bool CanUndo => !Run.Undone;
}

/// <summary>"Undo": the changes of the last 30 days, newest first; Undo puts the rules of the chosen one back.</summary>
public sealed partial class UndoViewModel : ObservableObject
{
    public UndoViewModel(IEnumerable<UndoRun> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        Runs = [.. runs.OrderByDescending(r => r.CreatedUtc).Select(r => new UndoRunRow(r))];
    }

    public event EventHandler<bool>? CloseRequested;

    public static string Title => "Undo";

    public IReadOnlyList<UndoRunRow> Runs { get; }

    public bool ShowEmpty => Runs.Count == 0;

    public static string EmptyText => "No changes in the last 30 days.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    [NotifyPropertyChangedFor(nameof(UndoBlockedReason))]
    public partial UndoRunRow? Selected { get; set; }

    public string? UndoBlockedReason => Selected is null ? "Select a change." : Selected.CanUndo ? null : "This change was undone already.";

    private bool CanUndo => UndoBlockedReason is null;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => CloseRequested?.Invoke(this, true);

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);
}
