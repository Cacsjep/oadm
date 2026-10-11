using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Collections;

namespace Oadm.Plugins.VapixCommander.Client;

/// <summary>A command the request builder can start from: a library or saved command.</summary>
public sealed class CommandChoice(CommandListItem item)
{
    public CommandListItem Item { get; } = item;

    public string Title => Item.Command.Name;

    /// <summary>"Built-in · Network" / "Saved · Custom".</summary>
    public string Detail => (Item.Source == CommandSources.Saved ? "Saved · " : "Built-in · ") + Item.Command.Category;

    internal bool Matches(string search) =>
        Item.Command.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Item.Command.Category.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Item.Command.Request.Path.Contains(search, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The request builder dialog (own window instead of a tab of the page). A new command starts
/// with a choice: from scratch, or from a library or saved command (always saved as a new command). The pen icon of a
/// saved command opens it directly; Save then replaces it. Send tries the request on one device; Save stores the command
/// for all clients and closes the dialog.
/// </summary>
public sealed partial class RequestBuilderViewModel : ObservableObject
{
    private readonly ICommanderBackend _backend;
    private readonly ICorePluginClientContext _host;
    private readonly IReadOnlyList<CommandChoice> _choices;
    private readonly string? _editId;

    /// <summary>A new command: the first step asks where to start.</summary>
    public RequestBuilderViewModel(ICommanderBackend backend, ICorePluginClientContext host, IReadOnlyList<CommandListItem> commands,
        IReadOnlyList<TargetDeviceViewModel> devices, TargetDeviceViewModel? device)
        : this(backend, host, commands, devices, device, edit: null)
    {
    }

    private RequestBuilderViewModel(ICommanderBackend backend, ICorePluginClientContext host, IReadOnlyList<CommandListItem> commands,
        IReadOnlyList<TargetDeviceViewModel> devices, TargetDeviceViewModel? device, CommandListItem? edit)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(devices);
        _backend = backend;
        _host = host;
        _choices = [.. commands.Select(c => new CommandChoice(c))];
        Devices = devices;
        TryDevice = device ?? (devices.Count > 0 ? devices[0] : null);
        Raw.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RawEditorViewModel.IsFormValid))
            {
                SaveCommand.NotifyCanExecuteChanged();
            }
        };
        if (edit is not null)
        {
            _editId = edit.Command.Id;
            Raw.Load(edit);
            IsChoosing = false;
        }
        else
        {
            ApplySearch();
        }
    }

    /// <summary>Opens a saved command to change it: no first step, Save replaces it.</summary>
    public static RequestBuilderViewModel ForEdit(ICommanderBackend backend, ICorePluginClientContext host, CommandListItem saved,
        IReadOnlyList<TargetDeviceViewModel> devices, TargetDeviceViewModel? device)
    {
        ArgumentNullException.ThrowIfNull(saved);
        return new RequestBuilderViewModel(backend, host, [], devices, device, saved);
    }

    public RawEditorViewModel Raw { get; } = new();

    public bool IsEditing => _editId is not null;

    public string Title => IsEditing ? "Edit command" : "New command";

    // ---------------------------------------------------------------- step 1: where to start

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBuilding), nameof(CanGoBack))]
    public partial bool IsChoosing { get; private set; } = true;

    public bool IsBuilding => !IsChoosing;

    /// <summary>Back to the first step (a new command only).</summary>
    public bool CanGoBack => IsBuilding && !IsEditing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartFromExisting))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    public partial bool StartFromScratch { get; set; } = true;

    public bool StartFromExisting
    {
        get => !StartFromScratch;
        set => StartFromScratch = !value;
    }

    [ObservableProperty]
    public partial string? Search { get; set; }

    /// <summary>The commands the search shows (replaced as a whole: one reset).</summary>
    public RangeObservableCollection<CommandChoice> VisibleCommands { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    [NotifyPropertyChangedFor(nameof(NextBlockedReason))]
    public partial CommandChoice? SelectedCommand { get; set; }

    public string? NextBlockedReason => CanNext() ? null : "Choose a command to start from.";

    partial void OnSearchChanged(string? value) => ApplySearch();

    partial void OnSelectedCommandChanged(CommandChoice? value)
    {
        if (value is not null)
        {
            StartFromExisting = true;
        }
    }

    private void ApplySearch()
    {
        var search = Search?.Trim();
        VisibleCommands.ReplaceAll(string.IsNullOrEmpty(search) ? _choices : _choices.Where(c => c.Matches(search)));
    }

    /// <summary>Opens the builder: empty, or filled from the chosen command (name with " (copy)" for a saved one).</summary>
    [RelayCommand(CanExecute = nameof(CanNext))]
    private void Next()
    {
        if (StartFromExisting && SelectedCommand is { } choice)
        {
            Raw.Load(choice.Item);
            if (choice.Item.Source == CommandSources.Saved)
            {
                var name = choice.Item.Command.Name + " (copy)";
                Raw.SaveName = name.Length > 80 ? name[..80] : name;
            }
        }

        IsChoosing = false;
    }

    private bool CanNext() => StartFromScratch || SelectedCommand is not null;

    [RelayCommand]
    private void Back()
    {
        if (CanGoBack)
        {
            IsChoosing = true;
        }
    }

    // ---------------------------------------------------------------- step 2: build, send, save

    public IReadOnlyList<TargetDeviceViewModel> Devices { get; }

    [ObservableProperty]
    public partial TargetDeviceViewModel? TryDevice { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTryResult))]
    public partial TryResultViewModel? TryResult { get; private set; }

    public bool HasTryResult => TryResult is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(SaveCommand))]
    public partial bool IsBusy { get; private set; }

    /// <summary>A refused save (server answer), left of Save.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSaveError))]
    public partial string? SaveError { get; private set; }

    public bool HasSaveError => SaveError is not null;

    /// <summary>The saved command after Save (the page reloads its library); null when cancelled.</summary>
    public CommandListItem? Saved { get; private set; }

    public event EventHandler? CloseRequested;

    /// <summary>Sends the request as it is now to <see cref="TryDevice"/>; a request that changes the device asks first.</summary>
    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task SendAsync()
    {
        if (!Raw.ShowProblems(forSend: true))
        {
            return; // every problem below its input
        }

        if (TryDevice is not { } device)
        {
            TryResult = new TryResultViewModel("Request", "-", null, "Choose a device to send to.");
            return;
        }

        var (command, _) = Raw.Build(forSave: false);
        var name = command.Request.Method + " " + command.Request.Path;
        if (command.Writes && !await _host.ConfirmAsync("Send command", $"\"{name}\" changes {device.Address}. Send it now?", "Send").ConfigureAwait(true))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var reply = await _backend.TryAsync(
                new TryCommandRequest { DeviceId = device.Id, Command = new CommandRef { Source = CommandSources.Inline, Command = command }, Values = Raw.Values(), Confirmed = command.Writes },
                CancellationToken.None).ConfigureAwait(true);
            TryResult = new TryResultViewModel(name, device.Address, reply.Outcome, reply.Error ?? (reply.NeedsConfirmation ? "The command changes the device and was not confirmed." : null));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TryResult = new TryResultViewModel(name, device.Address, null, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CloseTryResult() => TryResult = null;

    /// <summary>Saves the command (an edited one in place, else a new one) for all clients and closes the dialog.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (!Raw.ShowProblems())
        {
            return;
        }

        var (command, problems) = Raw.Build(forSave: true);
        if (problems.Count > 0)
        {
            return;
        }

        command.Id = _editId ?? string.Empty;
        IsBusy = true;
        try
        {
            var reply = await _backend.SaveAsync(new SaveCommandRequest { Command = command, PreviousId = _editId, Owner = _host.OwnerName }, CancellationToken.None).ConfigureAwait(true);
            if (reply.Error is not null || reply.Saved is null)
            {
                SaveError = "Not saved: " + (reply.Error ?? "no answer");
                return;
            }

            Saved = reply.Saved;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SaveError = "Not saved: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSave() => !IsBusy && IsBuilding && Raw.IsFormValid;

    private bool NotBusy() => !IsBusy;

    partial void OnIsChoosingChanged(bool value) => SaveCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
