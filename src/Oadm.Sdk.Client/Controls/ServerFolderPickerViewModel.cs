using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Client.Validation;
using Oadm.Sdk.Plugins;

namespace Oadm.Sdk.Client.Controls;

/// <summary>
/// The view model of the server folder dialog (<see cref="ServerFolderWindow"/>): the open folder in the path bar, its
/// subfolders (double click opens), Up, Drives, New folder with its name checked below the field, and Choose for the open
/// folder once the server's check passed (<see cref="IServerFolders.CheckAsync"/>; the reason below the path bar
/// otherwise). Testable without Avalonia; members run on the UI thread.
/// </summary>
public sealed partial class ServerFolderPickerViewModel : ValidatingViewModel
{
    /// <summary>The loading text of the list.</summary>
    public const string LoadingText = "Reading folders";

    /// <summary>The empty list of a folder.</summary>
    public const string NoFoldersText = "No folders here. Create one with New folder.";

    /// <summary>The empty list of the drives.</summary>
    public const string NoDrivesText = "The server shows no drives.";

    private readonly IServerFolders _folders;
    private readonly string? _start;
    private CancellationTokenSource? _load;
    private string _opened = string.Empty;
    private string? _parent;
    private string? _openedProblem = ServerFolderTexts.ChooseFolder;

    /// <param name="folders">The server's folders (<see cref="ICorePluginClientContext.ServerFolders"/>).</param>
    /// <param name="startPath">The folder to open first (the field's path); null or empty: the drives.</param>
    public ServerFolderPickerViewModel(IServerFolders folders, string? startPath)
    {
        ArgumentNullException.ThrowIfNull(folders);
        _folders = folders;
        _start = string.IsNullOrWhiteSpace(startPath) ? null : startPath.Trim();
        Validation.Rule(nameof(PathText), () => null); // the server's answers only (SetServerError)
        Validation.Rule(nameof(NewFolderName), () => IsCreating ? ServerFolderTexts.CheckName(NewFolderName) : null);
        Validation.Validate();
    }

    /// <summary>The subfolders of the open folder (the drives at the top).</summary>
    public RangeObservableCollection<ServerFolder> Folders { get; } = [];

    /// <summary>The path bar: the open folder, or what the user typed (Enter opens it).</summary>
    [ObservableProperty]
    public partial string PathText { get; set; } = string.Empty;

    /// <summary>The row the user clicked.</summary>
    [ObservableProperty]
    public partial ServerFolder? SelectedFolder { get; set; }

    /// <summary>A listing is being read: the list shows the loading overlay.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(CanChoose), nameof(ChooseTip))]
    public partial bool IsLoading { get; private set; }

    /// <summary>The list shows the drives / top folders.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyText), nameof(CanChoose), nameof(ChooseTip))]
    [NotifyCanExecuteChangedFor(nameof(UpCommand), nameof(NewFolderCommand))]
    public partial bool IsAtRoots { get; private set; } = true;

    /// <summary>The folder has more subfolders than the list holds.</summary>
    [ObservableProperty]
    public partial bool More { get; private set; }

    /// <summary>The inline New folder editor is open: its Create is the primary button, Choose turns secondary.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChoose), nameof(ChooseTip))]
    [NotifyCanExecuteChangedFor(nameof(NewFolderCommand))]
    public partial bool IsCreating { get; private set; }

    /// <summary>The name of the new folder.</summary>
    [ObservableProperty]
    public partial string NewFolderName { get; set; } = string.Empty;

    /// <summary>The new folder is being made.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    public partial bool IsSaving { get; private set; }

    /// <summary>The folder Choose returned (the open folder).</summary>
    public string? ChosenPath { get; private set; }

    /// <summary>Nothing to list (after the read).</summary>
    public bool IsEmpty => !IsLoading && Folders.Count == 0;

    /// <summary>The line of an empty list.</summary>
    public string EmptyText => IsAtRoots ? NoDrivesText : NoFoldersText;

    /// <summary>The line below a list that was cut off.</summary>
    public static string MoreText => $"Only the first {ServerFolderTexts.MaxEntries:N0} folders are shown. Type a path to open another.";

    /// <summary>Choose may run: nothing loads, no editor is open, and the open folder passed the check or another path was typed.</summary>
    public bool CanChoose => ChooseTip is null;

    /// <summary>Why Choose is off (its tooltip); null when it is on.</summary>
    public string? ChooseTip
    {
        get
        {
            if (IsLoading)
            {
                return LoadingText;
            }

            if (IsCreating)
            {
                return "Create the folder or cancel it first.";
            }

            if (Validation.ErrorOf(nameof(PathText)) is { } pathError)
            {
                return pathError;
            }

            if (PathText.Trim().Length == 0)
            {
                return ServerFolderTexts.ChooseFolder;
            }

            return IsTyped ? null : _openedProblem;
        }
    }

    /// <summary>The path bar holds another path than the open folder.</summary>
    private bool IsTyped => !string.Equals(PathText.Trim(), _opened, StringComparison.Ordinal);

    /// <summary>Opens the start folder; when it cannot be listed, the drives with the reason below the path bar.</summary>
    public async Task LoadAsync()
    {
        if (_start is null || !await OpenAsync(_start).ConfigureAwait(true))
        {
            var error = _start is null ? null : Validation.ErrorOf(nameof(PathText));
            await OpenAsync(null).ConfigureAwait(true);
            if (error is not null)
            {
                PathText = _start!;
                Validation.SetServerError(nameof(PathText), error);
            }
        }
    }

    /// <summary>
    /// Lists <paramref name="path"/> (null or empty: the drives); true when it was listed. A listing that fails keeps the
    /// one before and shows why below the path bar. A newer open cancels an older one.
    /// </summary>
    public async Task<bool> OpenAsync(string? path)
    {
        // The newer open owns the state before the older one is cancelled: the older one then changes nothing.
        var previous = _load;
        var load = new CancellationTokenSource();
        _load = load;
        previous?.Cancel();
        IsLoading = true;
        try
        {
            var listing = await _folders.ListAsync(string.IsNullOrWhiteSpace(path) ? null : path.Trim(), load.Token).ConfigureAwait(true);
            if (!ReferenceEquals(_load, load))
            {
                return false;
            }

            _opened = listing.Path;
            _parent = listing.Parent;
            _openedProblem = listing.Problem;
            IsCreating = false;
            IsAtRoots = listing.Path.Length == 0;
            More = listing.More;
            SelectedFolder = null;
            Folders.ReplaceAll(listing.Folders);
            PathText = listing.Path;
            // The open folder's check below the path bar; the drives need no line, Choose's tooltip says it.
            Validation.SetServerError(nameof(PathText), IsAtRoots ? null : listing.Problem);
            return true;
        }
        catch (OperationCanceledException) when (load.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ReferenceEquals(_load, load))
            {
                if (path is not null)
                {
                    PathText = path.Trim();
                }

                Validation.SetServerError(nameof(PathText), ServerErrors.Message(ex));
            }

            return false;
        }
        finally
        {
            if (ReferenceEquals(_load, load))
            {
                _load = null;
                IsLoading = false;
                OnPropertyChanged(nameof(IsEmpty));
                OnPropertyChanged(nameof(ChooseTip));
                OnPropertyChanged(nameof(CanChoose));
                UpCommand.NotifyCanExecuteChanged();
            }

            load.Dispose();
        }
    }

    /// <summary>Opens a folder of the list (double click, Enter).</summary>
    [RelayCommand]
    private Task OpenFolderAsync(ServerFolder? folder) => folder is null ? Task.CompletedTask : OpenAsync(folder.Path);

    /// <summary>Opens what the path bar holds (Enter in the path bar).</summary>
    [RelayCommand]
    private Task<bool> GoAsync() => OpenAsync(PathText);

    /// <summary>The parent folder; from a drive the list of drives.</summary>
    [RelayCommand(CanExecute = nameof(CanGoUp))]
    private Task<bool> UpAsync() => OpenAsync(_parent);

    private bool CanGoUp() => !IsAtRoots;

    /// <summary>The drives / top folders of the server.</summary>
    [RelayCommand]
    private Task<bool> DrivesAsync() => OpenAsync(null);

    /// <summary>Opens the inline editor for a new folder in the open folder.</summary>
    [RelayCommand(CanExecute = nameof(CanAddFolder))]
    private void NewFolder()
    {
        NewFolderName = string.Empty;
        IsCreating = true;
        Validation.Reset(nameof(NewFolderName));
    }

    private bool CanAddFolder() => !IsAtRoots && !IsCreating;

    /// <summary>Closes the inline editor without a folder.</summary>
    [RelayCommand]
    private void CancelNewFolder()
    {
        IsCreating = false;
        Validation.Reset(nameof(NewFolderName));
    }

    /// <summary>Makes the new folder and opens it; a refusal shows below the name.</summary>
    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateAsync()
    {
        Validation.ShowAll(nameof(NewFolderName));
        if (Validation.ErrorOf(nameof(NewFolderName)) is not null)
        {
            return;
        }

        IsSaving = true;
        try
        {
            var created = await _folders.CreateAsync(_opened, NewFolderName.TrimStart(), CancellationToken.None).ConfigureAwait(true);
            IsCreating = false;
            Validation.Reset(nameof(NewFolderName));
            await OpenAsync(created.Path).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Validation.SetServerError(nameof(NewFolderName), ServerErrors.Message(ex));
        }
        finally
        {
            IsSaving = false;
        }
    }

    private bool CanCreate() => !IsSaving;

    /// <summary>
    /// Choose: a typed path is opened (and checked) first; true with <see cref="ChosenPath"/> when the open folder passed the
    /// server's check, else false with the reason below the path bar.
    /// </summary>
    public async Task<bool> ChooseAsync()
    {
        if (IsTyped && !await OpenAsync(PathText).ConfigureAwait(true))
        {
            return false;
        }

        if (!CanChoose)
        {
            return false;
        }

        ChosenPath = _opened;
        return true;
    }

    partial void OnPathTextChanged(string value)
    {
        OnPropertyChanged(nameof(ChooseTip));
        OnPropertyChanged(nameof(CanChoose));
    }

    protected override void OnValidationChanged()
    {
        OnPropertyChanged(nameof(ChooseTip));
        OnPropertyChanged(nameof(CanChoose));
    }
}
