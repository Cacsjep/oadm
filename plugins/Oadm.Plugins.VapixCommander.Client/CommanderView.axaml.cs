using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;

namespace Oadm.Plugins.VapixCommander.Client;

/// <summary>The Commander page view. Code-behind only wires the file pickers, library clicks and keyboard shortcuts to view model commands.</summary>
public partial class CommanderView : UserControl, ICommanderFiles
{
    private static readonly FilePickerFileType JsonFiles = new("VAPIX Commander commands") { Patterns = ["*.json"], MimeTypes = ["application/json"] };

    public CommanderView()
    {
        InitializeComponent();
        LibraryTreeView.AddHandler(KeyDownEvent, OnLibraryKeyDown, RoutingStrategies.Tunnel);
        RolloutGrid.AddHandler(KeyDownEvent, OnRolloutKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (DataContext is CommanderViewModel vm)
        {
            vm.Files = this;
        }

        base.OnDataContextChanged(e);
    }

    public async Task<string?> OpenJsonAsync()
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
        {
            return null;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Import commands", AllowMultiple = false, FileTypeFilter = [JsonFiles] }).ConfigureAwait(true);
        if (files.Count == 0)
        {
            return null;
        }

        await using var stream = await files[0].OpenReadAsync().ConfigureAwait(true);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync().ConfigureAwait(true);
    }

    public async Task<bool> SaveJsonAsync(string fileName, string content)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
        {
            return false;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export commands",
            SuggestedFileName = fileName,
            DefaultExtension = "json",
            FileTypeChoices = [JsonFiles],
        }).ConfigureAwait(true);
        if (file is null)
        {
            return false;
        }

        await using var stream = await file.OpenWriteAsync().ConfigureAwait(true);
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(content).ConfigureAwait(true);
        return true;
    }

    /// <summary>A click on a library row: a command goes to the rollout set, a group expands or collapses. Row icon buttons are ignored.</summary>
    private void OnLibraryNodeTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: LibraryNodeViewModel node } row || IsOnButton(row, e))
        {
            return;
        }

        if (DataContext is CommanderViewModel vm)
        {
            vm.ActivateCommand.Execute(node);
            e.Handled = true;
        }
    }

    /// <summary>The second click of a double click is not a second action (and does not toggle a group back).</summary>
    private void OnLibraryNodeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control row && !IsOnButton(row, e))
        {
            e.Handled = true;
        }
    }

    private static bool IsOnButton(Control row, RoutedEventArgs e) =>
        e.Source is Visual source && (source as Button ?? source.FindAncestorOfType<Button>()) is { } button && row.IsVisualAncestorOf(button);

    /// <summary>Library: Enter adds the selected command (or toggles the group), Delete deletes a selected saved command.</summary>
    private void OnLibraryKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not CommanderViewModel vm || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            vm.ActivateCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && vm.DeleteSavedCommand.CanExecute(null))
        {
            vm.DeleteSavedCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>Rollout set: Delete removes the selected row, Ctrl+Up / Ctrl+Down move it (before the grid moves the selection).</summary>
    private void OnRolloutKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not CommanderViewModel vm)
        {
            return;
        }

        var command = (e.Key, e.KeyModifiers) switch
        {
            (Key.Delete, KeyModifiers.None) => vm.RemoveCommand,
            (Key.Up, KeyModifiers.Control) => vm.MoveUpCommand,
            (Key.Down, KeyModifiers.Control) => vm.MoveDownCommand,
            _ => null,
        };
        if (command is null)
        {
            return;
        }

        if (command.CanExecute(null))
        {
            command.Execute(null);
        }

        e.Handled = true;
    }
}
