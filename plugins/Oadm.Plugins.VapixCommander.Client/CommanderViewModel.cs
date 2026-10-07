using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
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
public sealed partial class CommanderViewModel : ObservableObject
{
    private readonly ICommanderBackend _backend;
    private readonly ICorePluginClientContext _host;
    private List<CommandListItem> _library = [];
    private List<CommandListItem> _saved = [];
    private Dictionary<Guid, TargetDeviceViewModel> _targetsById = [];
    private int _selectedCount;
    private bool _bulkSelection;

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
        UseDevicesSelection();
    }

    /// <summary>Set by the view: export/import file pickers.</summary>
    public ICommanderFiles? Files { get; set; }

    // ---------------------------------------------------------------- library

    public ObservableCollection<LibraryNodeViewModel> LibraryTree { get; } = [];

    [ObservableProperty]
    public partial string? LibrarySearch { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteSavedCommand), nameof(ExportSavedCommand))]
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

    // A site can have thousands of devices: the lists are replaced as a whole (one reset, no per-item events), the
    // selection is counted incrementally and bulk changes notify once; the view virtualizes the rows.

    /// <summary>Every managed device, sorted by address.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<TargetDeviceViewModel> Targets { get; private set; } = [];

    /// <summary>The devices matching <see cref="TargetSearch"/>, in address order.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<TargetDeviceViewModel> VisibleTargets { get; private set; } = [];

    [ObservableProperty]
    public partial string? TargetSearch { get; set; }

    public string TargetSummary => string.Create(CultureInfo.InvariantCulture, $"{_selectedCount:N0} of {Targets.Count:N0} selected");

    public IReadOnlyList<TargetDeviceViewModel> SelectedTargets => [.. Targets.Where(t => t.IsSelected)];

    public int SelectedTargetCount => _selectedCount;

    /// <summary>"4,812 compatible · 188 not compatible · 0 API list not read" over all devices; null without commands.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCompatibilitySummary))]
    public partial string? CompatibilitySummary { get; private set; }

    public bool HasCompatibilitySummary => CompatibilitySummary is not null;

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
            var devices = _selectedCount;
            var text = string.Create(CultureInfo.InvariantCulture, $"{commands} {(commands == 1 ? "command" : "commands")} × {devices:N0} {(devices == 1 ? "device" : "devices")}");
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

    public string RunText => string.Create(CultureInfo.InvariantCulture, $"Run on {_selectedCount:N0} {(_selectedCount == 1 ? "device" : "devices")}");

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

    /// <summary>
    /// A click on a library node: a command is added to the rollout set at once, a group only expands or collapses.
    /// Null means the selected node (Enter in the tree).
    /// </summary>
    [RelayCommand]
    private void Activate(LibraryNodeViewModel? node)
    {
        node ??= SelectedNode;
        if (node is null)
        {
            return;
        }

        if (node.Item is { } item)
        {
            Add(item);
        }
        else
        {
            node.IsExpanded = !node.IsExpanded;
        }
    }

    /// <summary>Adds a library or saved command to the rollout set and selects it.</summary>
    public RolloutCommandViewModel Add(CommandListItem item)
    {
        var vm = new RolloutCommandViewModel(item);
        RolloutItems.Add(vm);
        SelectedRolloutItem = vm;
        IsRawTab = false;
        return vm;
    }

    /// <summary>Deletes a saved command (the given node, else the selected one) for all clients, after a confirmation.</summary>
    [RelayCommand(CanExecute = nameof(CanDeleteSaved))]
    private async Task DeleteSavedAsync(LibraryNodeViewModel? node)
    {
        if ((node ?? SelectedNode)?.Item is not { Source: CommandSources.Saved } item)
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

    private bool CanDeleteSaved(LibraryNodeViewModel? node) => (node ?? SelectedNode)?.IsSaved == true;

    /// <summary>Exports one saved command (the given node, else the selected one) as a library file.</summary>
    [RelayCommand(CanExecute = nameof(CanDeleteSaved))]
    private Task ExportSavedAsync(LibraryNodeViewModel? node) =>
        (node ?? SelectedNode)?.Item is { Source: CommandSources.Saved } item ? ExportAsync([item.Command.Id]) : Task.CompletedTask;

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

    // Per-row icon buttons pass their row; the keyboard (Delete, Ctrl+Up, Ctrl+Down) passes null = the selected row.

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp(RolloutCommandViewModel? item) => Move(item ?? SelectedRolloutItem, -1);

    private bool CanMoveUp(RolloutCommandViewModel? item) => (item ?? SelectedRolloutItem) is { } row && RolloutItems.IndexOf(row) > 0;

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown(RolloutCommandViewModel? item) => Move(item ?? SelectedRolloutItem, 1);

    private bool CanMoveDown(RolloutCommandViewModel? item)
    {
        var index = (item ?? SelectedRolloutItem) is { } row ? RolloutItems.IndexOf(row) : -1;
        return index >= 0 && index < RolloutItems.Count - 1;
    }

    [RelayCommand(CanExecute = nameof(CanUseRow))]
    private void Remove(RolloutCommandViewModel? item)
    {
        if ((item ?? SelectedRolloutItem) is not { } row)
        {
            return;
        }

        var index = RolloutItems.IndexOf(row);
        if (index < 0)
        {
            return;
        }

        var wasSelected = row == SelectedRolloutItem;
        var selected = SelectedRolloutItem;
        RolloutItems.Remove(row);
        if (wasSelected)
        {
            IsPresetOpen = false;
            SelectedRolloutItem = RolloutItems.Count == 0 ? null : RolloutItems[Math.Min(index, RolloutItems.Count - 1)];
        }
        else
        {
            SelectedRolloutItem = selected;
        }
    }

    private bool CanUseRow(RolloutCommandViewModel? item) => (item ?? SelectedRolloutItem) is not null;

    [RelayCommand(CanExecute = nameof(HasRolloutItems))]
    private void Clear()
    {
        RolloutItems.Clear();
        SelectedRolloutItem = null;
        IsPresetOpen = false;
    }

    [RelayCommand]
    private void ShowRollout() => IsRawTab = false;

    [RelayCommand]
    private void ShowRaw() => IsRawTab = true;

    /// <summary>Opens "Save with values" for the row (selects it, so its field form shows the values being saved).</summary>
    [RelayCommand(CanExecute = nameof(CanUseRow))]
    private void SavePreset(RolloutCommandViewModel? item)
    {
        if ((item ?? SelectedRolloutItem) is not { } row)
        {
            return;
        }

        SelectedRolloutItem = row;
        PresetName = row.Name;
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

    private void Move(RolloutCommandViewModel? item, int delta)
    {
        if (item is null)
        {
            return;
        }

        var index = RolloutItems.IndexOf(item);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= RolloutItems.Count)
        {
            return;
        }

        // The grid may drop its selection when rows move; keep the selected row selected.
        var selected = SelectedRolloutItem;
        RolloutItems.Move(index, target);
        SelectedRolloutItem = selected;
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
        ClearCommand.NotifyCanExecuteChanged();
        RefreshCompatibility();
    }

    // ================================================================ targets

    /// <summary>Mirrors the host's device list: existing rows are kept (selection, compatibility), one list reset.</summary>
    private void SyncDevices()
    {
        var devices = _host.Devices;
        var byId = new Dictionary<Guid, TargetDeviceViewModel>(devices.Count);
        foreach (var device in devices)
        {
            if (byId.ContainsKey(device.Id))
            {
                continue;
            }

            if (_targetsById.TryGetValue(device.Id, out var existing))
            {
                existing.Update(device);
                byId[device.Id] = existing;
            }
            else
            {
                var target = new TargetDeviceViewModel(device);
                target.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(TargetDeviceViewModel.IsSelected))
                    {
                        OnTargetSelected(target);
                    }
                };
                byId[device.Id] = target;
            }
        }

        _targetsById = byId;
        // IPv4 addresses in numeric order (10.0.0.9 before 10.0.0.10), host names after them.
        List<TargetDeviceViewModel> targets =
        [
            .. byId.Values
                .Select(t => (Target: t, Ip: IPAddress.TryParse(t.Address, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork
                    ? BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes())
                    : (uint?)null))
                .OrderBy(x => x.Ip is null)
                .ThenBy(x => x.Ip)
                .ThenBy(x => x.Target.Address, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Target),
        ];
        Targets = targets;
        _selectedCount = targets.Count(t => t.IsSelected);
        RefreshVisibleTargets();
        OnTargetSelectionChanged();
        RefreshCompatibility();
    }

    private void RefreshVisibleTargets()
    {
        var search = TargetSearch?.Trim() ?? string.Empty;
        VisibleTargets = search.Length == 0 ? Targets : [.. Targets.Where(t => t.Matches(search))];
    }

    private void OnTargetSelected(TargetDeviceViewModel target)
    {
        if (_bulkSelection)
        {
            return;
        }

        _selectedCount += target.IsSelected ? 1 : -1;
        OnTargetSelectionChanged();
    }

    /// <summary>Sets the selection of many devices with one notification at the end (O(n), no per-device page work).</summary>
    private void SelectTargets(IEnumerable<TargetDeviceViewModel> targets, Func<TargetDeviceViewModel, bool> selected)
    {
        _bulkSelection = true;
        try
        {
            foreach (var target in targets)
            {
                target.IsSelected = selected(target);
            }
        }
        finally
        {
            _bulkSelection = false;
        }

        _selectedCount = Targets.Count(t => t.IsSelected);
        OnTargetSelectionChanged();
    }

    private void OnTargetSelectionChanged()
    {
        if (TryDevice is null || !_targetsById.ContainsKey(TryDevice.Id))
        {
            TryDevice = Targets.FirstOrDefault(t => t.IsSelected) ?? (Targets.Count > 0 ? Targets[0] : null);
        }

        OnPropertyChanged(nameof(TargetSummary));
        OnPropertyChanged(nameof(SelectedTargetCount));
        RunSummaryChanged();
    }

    /// <summary>Selects every device matching the search.</summary>
    [RelayCommand]
    private void SelectAllTargets() => SelectTargets(VisibleTargets, _ => true);

    [RelayCommand]
    private void ClearTargets() => SelectTargets(Targets, _ => false);

    /// <summary>Selects exactly the devices selected on the Devices page.</summary>
    [RelayCommand]
    private void UseDevicesSelection()
    {
        var selected = _host.SelectedDevices.Select(d => d.Id).ToHashSet();
        SelectTargets(Targets, t => selected.Contains(t.Id));
    }

    [RelayCommand]
    private void SelectCompatibleTargets() => SelectTargets(Targets, t => t.AllCompatible);

    /// <summary>
    /// Compatibility of every rollout command on every device, computed here from the cached API list of each device
    /// (no request per device; the server checks a fresh list again before a write). Also builds the summary.
    /// </summary>
    public void RefreshCompatibility()
    {
        var commands = RolloutItems.Select(i => (i.Name, i.Command)).ToList();
        if (commands.Count == 0)
        {
            foreach (var target in Targets)
            {
                target.SetCompatibility([]);
            }

            CompatibilitySummary = null;
            return;
        }

        int compatible = 0, notCompatible = 0, unknown = 0;
        foreach (var target in Targets)
        {
            var chips = new CompatibilityChip[commands.Count];
            for (var i = 0; i < commands.Count; i++)
            {
                var result = Compatibility.Check(commands[i].Command, target.Device.Apis, target.Device.HasVideo);
                chips[i] = new CompatibilityChip(commands[i].Name, result.State, result.Text);
            }

            target.SetCompatibility(chips);
            if (target.AllCompatible)
            {
                compatible++;
            }
            else if (target.IsCompatibilityError)
            {
                notCompatible++;
            }
            else
            {
                unknown++;
            }
        }

        var parts = new List<string> { string.Create(CultureInfo.InvariantCulture, $"{compatible:N0} compatible") };
        if (notCompatible > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{notCompatible:N0} not compatible"));
        }

        if (unknown > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{unknown:N0} not checked"));
        }

        CompatibilitySummary = string.Join(" · ", parts);
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
