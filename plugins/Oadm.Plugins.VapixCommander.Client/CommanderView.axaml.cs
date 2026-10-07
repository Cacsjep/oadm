using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;

namespace Oadm.Plugins.VapixCommander.Client;

/// <summary>The Commander page view. Code-behind only wires the file pickers and the library double-click.</summary>
public partial class CommanderView : UserControl, ICommanderFiles
{
    private static readonly FilePickerFileType JsonFiles = new("VAPIX Commander commands") { Patterns = ["*.json"], MimeTypes = ["application/json"] };

    public CommanderView()
    {
        InitializeComponent();
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

    private void OnLibraryDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is CommanderViewModel vm && vm.AddCommandCommand.CanExecute(null))
        {
            vm.AddCommandCommand.Execute(null);
        }
    }
}
