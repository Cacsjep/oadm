using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.VapixCommander.Client;

/// <summary>
/// The VAPIX Commander page: library tree (left), rollout set or raw editor with the Try result (middle), target
/// devices with compatibility per command (right) and the Run bar (bottom). Everything runs on the server.
/// </summary>
#pragma warning disable CA1001 // The token source only cancels superseded compatibility checks; the page lives as long as the client.
public sealed partial class CommanderViewModel : ObservableObject
#pragma warning restore CA1001
{
    private readonly ICommanderBackend _backend;
    private readonly ICorePluginClientContext _host;
    private List<CommandListItem> _library = [];
    private List<CommandListItem> _saved = [];
    private CancellationTokenSource? _compatibilityCts;

    public CommanderViewModel(ICommanderBackend backend, ICorePluginClientContext host)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(host);
        _backend = backend;
        _host = host;
        Raw.SaveRequested += (_, command) => _ = SaveRawAsync(command);
        RolloutItems.CollectionChanged += (_, _) => OnRolloutChanged();
        host.DevicesChanged += (_, _) => SyncDevices();
        SyncDevices();
        foreach (var device in host.SelectedDevices)
        {
            if (Targets.FirstOrDefault(t => t.Id == device.Id) is { } target)
            {
                target.IsSelected = true;
            }
        }
    }

    /// <summary>Set by the view: export/import file pickers.</summary>
    public ICommanderFiles? Files { get; set; }

    // ---------------------------------------------------------------- library

    public ObservableCollection<LibraryNodeViewModel> LibraryTree { get; } = [];

    [ObservableProperty]
    public partial string? LibrarySearch { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommandCommand), nameof(DeleteSavedCommand), nameof(ExportSelectedCommand))]
    public partial LibraryNodeViewModel? SelectedNode { get; set; }

    [ObservableProperty]
    public partial string LibrarySummary { get; private set; } = "Loading…";

    /// <summary>Library files the server could not load (shown under the tree).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLibraryProblems))]
    public partial string? LibraryProblems { get; private set; }

    public bool HasLibraryProblems => LibraryProblems is not null;

    partial void OnLibrarySearchChanged(string? value) => RebuildTree();

    // ---------------------------------------------------------------- rollout set and raw editor

    public ObservableCollection<RolloutCommandViewModel> RolloutItems { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedRolloutItem))]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand), nameof(MoveDownCommand), nameof(RemoveCommand), nameof(SavePresetCommand))]
    public partial RolloutCommandViewModel? SelectedRolloutItem { get; set; }

    public bool HasSelectedRolloutItem => SelectedRolloutItem is not null;

    public bool HasRolloutItems => RolloutItems.Count > 0;

    public RawEditorViewModel Raw { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRolloutTab))]
    public partial bool IsRawTab { get; set; }

    public bool IsRolloutTab => !IsRawTab;

    // "Save preset" of the selected rollout command
    [ObservableProperty]
    public partial bool IsPresetOpen { get; set; }

    [ObservableProperty]
    public partial string PresetName { get; set; } = string.Empty;

    // ---------------------------------------------------------------- targets

    public ObservableCollection<TargetDeviceViewModel> Targets { get; } = [];

    public ObservableCollection<TargetDeviceViewModel> VisibleTargets { get; } = [];

    [ObservableProperty]
    public partial string? TargetSearch { get; set; }

    public string TargetSummary => string.Create(CultureInfo.InvariantCulture, $"{SelectedTargets.Count} of {Targets.Count} selected");

    public IReadOnlyList<TargetDeviceViewModel> SelectedTargets => [.. Targets.Where(t => t.IsSelected)];

    /// <summary>Device for "Send" (Try); defaults to the first selected target.</summary>
    [ObservableProperty]
    public partial TargetDeviceViewModel? TryDevice { get; set; }

    partial void OnTargetSearchChanged(string? value) => RefreshVisibleTargets();

    // ---------------------------------------------------------------- try and run

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTryResult))]
    public partial TryResultViewModel? TryResult { get; private set; }

    public bool HasTryResult => TryResult is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TryCommand), nameof(RunCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial bool StopOnFirstError { get; set; } = true;

    /// <summary>"3 commands × 2 devices · 2 write · 1 dangerous".</summary>
    public string RunSummary
    {
        get
        {
            var commands = RolloutItems.Count;
            var devices = SelectedTargets.Count;
            var text = string.Create(CultureInfo.InvariantCulture, $"{commands} {(commands == 1 ? "command" : "commands")} × {devices} {(devices == 1 ? "device" : "devices")}");
            var writes = RolloutItems.Count(i => i.Writes);
            var dangerous = RolloutItems.Count(i => i.Dangerous);
            if (writes > 0)
            {
                text += string.Create(CultureInfo.InvariantCulture, $" · {writes} {(writes == 1 ? "write" : "writes")}");
            }

            if (dangerous > 0)
            {
                text += string.Create(CultureInfo.InvariantCulture, $" · {dangerous} dangerous");
            }

            return text;
        }
    }

    public string RunText => string.Create(CultureInfo.InvariantCulture, $"Run on {SelectedTargets.Count} {(SelectedTargets.Count == 1 ? "device" : "devices")}");

    /// <summary>Result of the last action (run started, saved, error); <see cref="IsStatusError"/> colors the chip.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string? Status { get; private set; }

    [ObservableProperty]
    public partial bool IsStatusError { get; private set; }

    [ObservableProperty]
    public partial bool IsStatusOk { get; private set; }

    public bool HasStatus => Status is not null;

    [ObservableProperty]
    public partial bool CanShowTasks { get; private set; }

    // ================================================================ loading

    /// <summary>Loads library and saved commands. Errors are shown in the status line.</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        try
        {
            var library = await _backend.ListLibraryAsync(ct).ConfigureAwait(true);
            var saved = await _backend.ListSavedAsync(ct).ConfigureAwait(true);
            _library = library.Commands;
            _saved = saved.Commands;
            LibraryProblems = library.Problems.Count == 0
                ? null
                : string.Create(CultureInfo.InvariantCulture, $"{library.Problems.Count} library entries could not be loaded: ") + string.Join(" | ", library.Problems.Take(3));
            RebuildTree();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LibrarySummary = "Not loaded";
            SetStatus("Could not load the commands: " + ex.Message, error: true);
        }
    }

    private async Task ReloadSavedAsync()
    {
        _saved = (await _backend.ListSavedAsync(CancellationToken.None).ConfigureAwait(true)).Commands;
        RebuildTree();
    }

    private void RebuildTree()
    {
        LibraryTree.Clear();
        foreach (var node in LibraryNodeViewModel.BuildTree(_library, _saved, LibrarySearch))
        {
            LibraryTree.Add(node);
        }

        LibrarySummary = string.Create(CultureInfo.InvariantCulture, $"{_library.Count} built-in · {_saved.Count} saved");
    }

    // ================================================================ library commands

    [RelayCommand(CanExecute = nameof(CanAddCommand))]
    private void AddCommand()
    {
        if (SelectedNode?.Item is { } item)
        {
            Add(item);
        }
    }

    private bool CanAddCommand() => SelectedNode?.IsCommand == true;

    /// <summary>Adds a library or saved command to the rollout set and selects it.</summary>
    public RolloutCommandViewModel Add(CommandListItem item)
    {
        var vm = new RolloutCommandViewModel(item);
        RolloutItems.Add(vm);
        SelectedRolloutItem = vm;
        IsRawTab = false;
        return vm;
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSaved))]
    private async Task DeleteSavedAsync()
    {
        if (SelectedNode?.Item is not { Source: CommandSources.Saved } item)
        {
            return;
        }

        if (!await _host.ConfirmAsync("Delete saved command", $"Delete \"{item.Command.Name}\" for all OADM clients?", "Delete").ConfigureAwait(true))
        {
            return;
        }

        var reply = await _backend.DeleteAsync(item.Command.Id, CancellationToken.None).ConfigureAwait(true);
        SetStatus(reply.Ok ? $"Deleted \"{item.Command.Name}\"." : reply.Error ?? "Not deleted.", error: !reply.Ok);
        await ReloadSavedAsync().ConfigureAwait(true);
    }

    private bool CanDeleteSaved() => SelectedNode?.IsSaved == true;

    [RelayCommand(CanExecute = nameof(CanDeleteSaved))]
    private Task ExportSelectedAsync() => ExportAsync([SelectedNode!.Item!.Command.Id]);

    [RelayCommand]
    private Task ExportAllAsync() => ExportAsync([]);

    private async Task ExportAsync(IReadOnlyList<string> ids)
    {
        try
        {
            var export = await _backend.ExportAsync(ids, CancellationToken.None).ConfigureAwait(true);
            if (export.Count == 0)
            {
                SetStatus("There are no saved commands to export.", error: true);
                return;
            }

            if (Files is not null && await Files.SaveJsonAsync(export.FileName, export.Json).ConfigureAwait(true))
            {
                SetStatus(string.Create(CultureInfo.InvariantCulture, $"Exported {export.Count} {(export.Count == 1 ? "command" : "commands")} (passwords are never exported)."), error: false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetStatus("Export failed: " + ex.Message, error: true);
        }
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        if (Files is null)
        {
            return;
        }

        try
        {
            var json = await Files.OpenJsonAsync().ConfigureAwait(true);
            if (json is null)
            {
                return;
            }

            await ImportJsonAsync(json).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetStatus("Import failed: " + ex.Message, error: true);
        }
    }

    /// <summary>Imports a command file (library format) into the saved commands.</summary>
    public async Task ImportJsonAsync(string json)
    {
        var reply = await _backend.ImportAsync(json, _host.OwnerName, CancellationToken.None).ConfigureAwait(true);
        var text = string.Create(CultureInfo.InvariantCulture, $"Imported {reply.Imported} {(reply.Imported == 1 ? "command" : "commands")}.");
        if (reply.Problems.Count > 0)
        {
            text += " Skipped: " + string.Join(" | ", reply.Problems);
        }

        SetStatus(text, error: reply.Problems.Count > 0);
        await ReloadSavedAsync().ConfigureAwait(true);
    }

    // ================================================================ rollout set

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp() => Move(-1);

    private bool CanMoveUp() => SelectedRolloutItem is { } item && RolloutItems.IndexOf(item) > 0;

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown() => Move(1);

    private bool CanMoveDown() => SelectedRolloutItem is { } item && RolloutItems.IndexOf(item) < RolloutItems.Count - 1;

    [RelayCommand(CanExecute = nameof(HasSelectedRolloutItem))]
    private void Remove()
    {
        if (SelectedRolloutItem is not { } item)
        {
            return;
        }

        var index = RolloutItems.IndexOf(item);
        RolloutItems.Remove(item);
        SelectedRolloutItem = RolloutItems.Count == 0 ? null : RolloutItems[Math.Min(index, RolloutItems.Count - 1)];
    }

    [RelayCommand]
    private void Clear()
    {
        RolloutItems.Clear();
        SelectedRolloutItem = null;
    }

    [RelayCommand]
    private void ShowRollout() => IsRawTab = false;

    [RelayCommand]
    private void ShowRaw() => IsRawTab = true;

    [RelayCommand(CanExecute = nameof(HasSelectedRolloutItem))]
    private void SavePreset()
    {
        PresetName = SelectedRolloutItem!.Name;
        IsPresetOpen = true;
    }

    [RelayCommand]
    private void CancelPreset() => IsPresetOpen = false;

    /// <summary>Saves the selected command with the entered values as a new saved command (category Custom).</summary>
    [RelayCommand]
    private async Task ConfirmPresetAsync()
    {
        if (SelectedRolloutItem is not { } item)
        {
            return;
        }

        var command = item.WithValuesAsDefaults();
        command.Id = string.Empty;
        command.Name = PresetName.Trim();
        command.Category = CommandCategories.Custom;
        if (await SaveAsync(command).ConfigureAwait(true))
        {
            IsPresetOpen = false;
        }
    }

    /// <summary>Adds the raw request to the rollout set as an inline command.</summary>
    [RelayCommand]
    private void AddRawToRollout()
    {
        var (command, problems) = Raw.Build(forSave: false);
        if (problems.Count > 0)
        {
            Raw.Error = string.Join(" ", problems);
            return;
        }

        Raw.Error = null;
        var name = "Raw: " + command.Request.Method + " " + command.Request.Path;
        command.Name = name.Length > 80 ? name[..80] : name;
        command.Id = "raw.request";
        Add(new CommandListItem { Source = CommandSources.Inline, Command = command });
    }

    private async Task SaveRawAsync(CommandDefinition command)
    {
        if (await SaveAsync(command).ConfigureAwait(true))
        {
            Raw.SaveCompleted();
        }
    }

    private async Task<bool> SaveAsync(CommandDefinition command)
    {
        try
        {
            var reply = await _backend.SaveAsync(new SaveCommandRequest { Command = command, Owner = _host.OwnerName }, CancellationToken.None).ConfigureAwait(true);
            if (reply.Error is not null)
            {
                SetStatus("Not saved: " + reply.Error, error: true);
                return false;
            }

            SetStatus($"Saved \"{reply.Saved!.Command.Name}\" for all OADM clients.", error: false);
            await ReloadSavedAsync().ConfigureAwait(true);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetStatus("Not saved: " + ex.Message, error: true);
            return false;
        }
    }

    private void Move(int delta)
    {
        if (SelectedRolloutItem is not { } item)
        {
            return;
        }

        var index = RolloutItems.IndexOf(item);
        var target = index + delta;
        if (target < 0 || target >= RolloutItems.Count)
        {
            return;
        }

        RolloutItems.Move(index, target);
        SelectedRolloutItem = item;
    }

    private void OnRolloutChanged()
    {
        for (var i = 0; i < RolloutItems.Count; i++)
        {
            RolloutItems[i].Position = i + 1;
        }

        OnPropertyChanged(nameof(HasRolloutItems));
        RunSummaryChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        _ = RefreshCompatibilityAsync();
    }

    // ================================================================ targets

    private void SyncDevices()
    {
        var devices = _host.Devices;
        foreach (var stale in Targets.Where(t => devices.All(d => d.Id != t.Id)).ToList())
        {
            Targets.Remove(stale);
        }

        foreach (var device in devices)
        {
            if (Targets.FirstOrDefault(t => t.Id == device.Id) is { } existing)
            {
                existing.Update(device);
            }
            else
            {
                var target = new TargetDeviceViewModel(device);
                target.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(TargetDeviceViewModel.IsSelected))
                    {
                        OnTargetSelectionChanged();
                    }
                };
                Targets.Add(target);
            }
        }

        RefreshVisibleTargets();
        OnTargetSelectionChanged();
        _ = RefreshCompatibilityAsync();
    }

    private void RefreshVisibleTargets()
    {
        VisibleTargets.Clear();
        foreach (var target in Targets.Where(t => t.Matches(TargetSearch)).OrderBy(t => t.Address, StringComparer.OrdinalIgnoreCase))
        {
            VisibleTargets.Add(target);
        }
    }

    private void OnTargetSelectionChanged()
    {
        if (TryDevice is null || !Targets.Contains(TryDevice))
        {
            var selected = SelectedTargets;
            TryDevice = selected.Count > 0 ? selected[0] : Targets.FirstOrDefault();
        }

        OnPropertyChanged(nameof(TargetSummary));
        OnPropertyChanged(nameof(SelectedTargets));
        RunSummaryChanged();
    }

    [RelayCommand]
    private void SelectAllTargets()
    {
        foreach (var target in VisibleTargets)
        {
            target.IsSelected = true;
        }
    }

    [RelayCommand]
    private void ClearTargets()
    {
        foreach (var target in Targets)
        {
            target.IsSelected = false;
        }
    }

    /// <summary>Selects exactly the devices selected on the Devices page.</summary>
    [RelayCommand]
    private void UseDevicesSelection()
    {
        var selected = _host.SelectedDevices.Select(d => d.Id).ToHashSet();
        foreach (var target in Targets)
        {
            target.IsSelected = selected.Contains(target.Id);
        }
    }

    [RelayCommand]
    private void SelectCompatibleTargets()
    {
        foreach (var target in Targets)
        {
            target.IsSelected = target.AllCompatible;
        }
    }

    /// <summary>Asks the server for the compatibility of every rollout command on every device (cached API lists).</summary>
    public async Task RefreshCompatibilityAsync()
    {
        _compatibilityCts?.Cancel();
        _compatibilityCts?.Dispose();
        var cts = _compatibilityCts = new CancellationTokenSource();
        var items = RolloutItems.ToList();
        if (items.Count == 0 || Targets.Count == 0)
        {
            foreach (var target in Targets)
            {
                target.SetCompatibility([]);
            }

            return;
        }

        try
        {
            var reply = await _backend.CheckCompatibilityAsync(
                new CompatibilityRequest { DeviceIds = [.. Targets.Select(t => t.Id)], Commands = [.. items.Select(i => i.Ref)] },
                cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            foreach (var device in reply.Devices)
            {
                if (Targets.FirstOrDefault(t => t.Id == device.DeviceId) is { } target)
                {
                    target.SetCompatibility(device.Commands.Select((c, i) => new CompatibilityChip(i < items.Count ? items[i].Name : "?", c.State, c.Text)));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // superseded
        }
        catch (Exception ex)
        {
            SetStatus("Could not check compatibility: " + ex.Message, error: true);
        }
    }

    // ================================================================ try

    /// <summary>Sends the selected rollout command (or the raw request) to <see cref="TryDevice"/> now; writes ask first.</summary>
    [RelayCommand(CanExecute = nameof(CanTry))]
    private async Task TryAsync()
    {
        if (TryDevice is not { } device)
        {
            SetStatus("Choose a device to send to.", error: true);
            return;
        }

        CommandRef reference;
        Dictionary<string, System.Text.Json.JsonElement> values;
        string name;
        bool writes;
        if (IsRawTab)
        {
            var (command, problems) = Raw.Build(forSave: false);
            if (problems.Count > 0)
            {
                Raw.Error = string.Join(" ", problems);
                return;
            }

            Raw.Error = null;
            reference = new CommandRef { Source = CommandSources.Inline, Command = command };
            values = Raw.Values();
            name = command.Request.Method + " " + command.Request.Path;
            writes = command.Writes;
        }
        else if (SelectedRolloutItem is { } item)
        {
            reference = item.Ref;
            values = item.Values();
            name = item.Name;
            writes = item.Writes;
        }
        else
        {
            SetStatus("Add a command first.", error: true);
            return;
        }

        if (writes && !await _host.ConfirmAsync(
                "Send command",
                $"\"{name}\" changes {device.Address}. Send it now?",
                "Send").ConfigureAwait(true))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var reply = await _backend.TryAsync(
                new TryCommandRequest { DeviceId = device.Id, Command = reference, Values = values, Confirmed = writes },
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

    private bool CanTry() => !IsBusy;

    [RelayCommand]
    private void CloseTryResult() => TryResult = null;

    // ================================================================ run

    /// <summary>Starts the rollout: one task per device, each command a step. Writes and dangerous commands are confirmed first.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        var targets = SelectedTargets;
        if (RolloutItems.Count == 0 || targets.Count == 0)
        {
            SetStatus(RolloutItems.Count == 0 ? "Add at least one command." : "Select at least one device.", error: true);
            return;
        }

        if (RolloutItems.FirstOrDefault(i => !i.IsValid) is { } invalid)
        {
            SelectedRolloutItem = invalid;
            IsRawTab = false;
            SetStatus($"Check the values of \"{invalid.Name}\".", error: true);
            return;
        }

        var writes = RolloutItems.Where(i => i.Writes).ToList();
        if (writes.Count > 0 && !await _host.ConfirmAsync("Run VAPIX commands", ConfirmationText(targets), "Run").ConfigureAwait(true))
        {
            return;
        }

        var dangerous = RolloutItems.Where(i => i.Dangerous).ToList();
        if (dangerous.Count > 0 && !await _host.ConfirmAsync(
                "Dangerous commands",
                "These commands can restart devices, reset them or cut them off the network:" + Environment.NewLine
                + string.Join(Environment.NewLine, dangerous.Select(d => "• " + d.Name)) + Environment.NewLine + Environment.NewLine
                + string.Create(CultureInfo.InvariantCulture, $"Run them on {targets.Count} {(targets.Count == 1 ? "device" : "devices")}?"),
                "Run dangerous commands").ConfigureAwait(true))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var reply = await _backend.RolloutAsync(
                new RolloutRequest
                {
                    DeviceIds = [.. targets.Select(t => t.Id)],
                    Commands = [.. RolloutItems.Select(i => new RolloutCommand { Command = i.Ref, Values = i.Values() })],
                    StopOnFirstError = StopOnFirstError,
                    Confirmed = true,
                    Owner = _host.OwnerName,
                },
                CancellationToken.None).ConfigureAwait(true);
            if (reply.Error is not null)
            {
                SetStatus(reply.Error, error: true);
                return;
            }

            SetStatus(
                string.Create(CultureInfo.InvariantCulture, $"Started {reply.TaskIds.Count} {(reply.TaskIds.Count == 1 ? "task" : "tasks")}: progress and results in the Tasks pane of the Devices page."),
                error: false);
            CanShowTasks = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetStatus("The rollout could not be started: " + ex.Message, error: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRun() => !IsBusy;

    [RelayCommand]
    private Task ShowTasksAsync() => _host.OpenAsync(HostPages.Devices);

    /// <summary>The confirmation summary: N commands × M devices, then every command with its kind.</summary>
    public string ConfirmationText(IReadOnlyList<TargetDeviceViewModel> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{RolloutItems.Count} {(RolloutItems.Count == 1 ? "command" : "commands")} × {targets.Count} {(targets.Count == 1 ? "device" : "devices")}").AppendLine();
        foreach (var item in RolloutItems)
        {
            text.Append(CultureInfo.InvariantCulture, $"{item.Position}. {item.Name} ({item.KindText})").AppendLine();
        }

        var incompatible = targets.Count(t => t.Compatibility.Any(c => c.IsError));
        if (incompatible > 0)
        {
            text.AppendLine().Append(CultureInfo.InvariantCulture, $"{incompatible} {(incompatible == 1 ? "device does" : "devices do")} not support every command; those commands fail there without changing anything.").AppendLine();
        }

        text.AppendLine().Append(StopOnFirstError
            ? "Stop on first error: the first failure stops the whole rollout."
            : "Every device runs all commands, even when one fails.");
        return text.ToString();
    }

    private void RunSummaryChanged()
    {
        OnPropertyChanged(nameof(RunSummary));
        OnPropertyChanged(nameof(RunText));
    }

    private void SetStatus(string text, bool error)
    {
        Status = text;
        IsStatusError = error;
        IsStatusOk = !error;
        CanShowTasks = false;
    }
}
