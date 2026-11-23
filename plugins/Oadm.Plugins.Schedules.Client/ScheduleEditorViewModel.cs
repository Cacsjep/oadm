using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;

using Avalonia.Controls;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.Provisioning;
using Oadm.Plugins.Provisioning.Client;
using Oadm.Plugins.Provisioning.Engine;
using Oadm.Plugins.Provisioning.Model;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Client.Validation;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Schedules.Client;

/// <summary>What the wizard window does for the view model (the device picker, the file picker); tests fake it.</summary>
public interface IScheduleEditorUi
{
    /// <summary>Shows the shared device picker; true when the user took the choice.</summary>
    Task<bool> ChooseDevicesAsync(DevicePickerViewModel picker);

    /// <summary>The local files the user chose; empty when closed.</summary>
    Task<IReadOnlyList<string>> PickFilesAsync(string title, bool multiple, IReadOnlyList<string> patterns);
}

/// <summary>A choice of the When select.</summary>
public sealed record RepeatChoice(Recurrence Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A device the choice of step Devices gives.</summary>
public sealed record ScheduleDeviceRow(string Address, string HostName, string Model);

/// <summary>An action kind in the node list of step Then: its icon, colour, group and line (<see cref="NodeLook"/>).</summary>
public sealed record ActionItem(ActionKindInfo Kind)
    : NodePickerItem(Kind.Name, NodeLook.Description(Kind.Kind), NodeLook.IconKey(Kind.Kind), NodeLook.BrushKey(Kind.Kind), NodeLook.Group(Kind.Kind));

/// <summary>
/// The schedule wizard (docs/spec/schedules.md "The editor: a wizard"), like the Event rule editor: 1 Schedule (name, turned
/// on, roll out in stages), 2 Devices (the shared device choice and the devices it gives), 3 When, 4 Then (Provisioning's
/// node list, node picker and side panel), 5 Overview (only the facts the steps before do not show), Save. A step opens when
/// every step before is complete; Next is disabled with the step's first problem as tooltip. After every change the server's
/// check (<see cref="SchedulesMethods.Check"/>) runs; its errors show below their field once that field was edited or left,
/// or a step was left or Save tried.
/// </summary>
public sealed partial class ScheduleEditorViewModel : ValidatingViewModel, INodeFormServices
{
    public const int ScheduleStep = 0;
    public const int DevicesStep = 1;
    public const int WhenStep = 2;
    public const int ThenStep = 3;
    public const int OverviewStep = 4;

    /// <summary>The node list's width (ScheduleEditorWindow.axaml).</summary>
    public const double PickerWidth = 340;

    private static readonly string[] StepNames =
        [SchedulesTexts.StepSchedule, SchedulesTexts.StepDevices, SchedulesTexts.StepWhen, SchedulesTexts.StepThen, SchedulesTexts.StepOverview];

    private static readonly string[] WhenFields = [nameof(Repeat), nameof(Date), nameof(Days), nameof(DayOfMonth), nameof(Time), nameof(WindowEnd)];

    private readonly ICorePluginClientContext _ctx;
    private readonly Guid? _scheduleId;
    private readonly TimeProvider _time;
    private Dictionary<NodeKind, ActionKindInfo> _kinds = [];
    private IReadOnlyList<IssuingPolicyInfo> _policies = [];
    private IReadOnlyList<ProfileCommand> _commands = [];
    private ScheduleCheck? _check;
    private Guid _id;
    private bool _filling;
    private bool _checking;
    private bool _checkAgain;
    private bool _syncing;
    private bool _thenShown;
    private int _addAfter = -1;

    public ScheduleEditorViewModel(ICorePluginClientContext ctx, Guid? scheduleId, TimeProvider? time = null)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _scheduleId = scheduleId;
        _time = time ?? TimeProvider.System;
        _id = scheduleId ?? Guid.NewGuid();
        Title = scheduleId is null ? SchedulesTexts.NewTitle : SchedulesTexts.EditTitle;
        Steps = [.. StepNames.Select((name, i) => new WizardStepItem(i, name, () => Step, CanOpen, GoTo))];
        _filling = true; // the defaults below are no edits
        Days = DayChoice.Week();
        foreach (var day in Days)
        {
            day.PropertyChanged += OnDayChanged;
        }

        Days[0].IsChecked = true; // Mondays
        DayOfMonth = 1;
        Date = new DateTimeOffset(_time.GetLocalNow().Date.AddDays(1));
        Chain.CanAdd = true;
        Chain.AddTip = SchedulesTexts.AddAfterTip;
        Chain.PropertyChanged += OnChainChanged;
        Chain.AddRequested += (_, e) => OpenPicker(e.Index, e.At.X, e.At.Y);
        Picker.Chosen += (_, item) =>
        {
            if (item is ActionItem action)
            {
                AddAfter(_addAfter, action.Kind);
            }
        };

        Validation.Rule(nameof(Name), () =>
            Name.Trim().Length == 0 ? SchedulesTexts.EnterName
            : Name.Trim().Length > SchedulesInfo.MaxNameLength ? SchedulesTexts.NameTooLong
            : _check?.NameError);
        Validation.Rule(nameof(Choice), () => Choice.IsEmpty ? SchedulesTexts.ChooseDevicesFirst : _check?.DevicesError);
        Validation.Rule(nameof(Repeat), () => _check?.WhenError);
        Validation.Rule(nameof(Date), () => IsOnce && Date is null ? SchedulesTexts.ChooseDate : null);
        Validation.Rule(nameof(Days), () => IsWeekly && !Days.Any(d => d.IsChecked) ? SchedulesTexts.ChooseDays : null);
        Validation.Rule(nameof(DayOfMonth), () => IsMonthly && DayOfMonth is null ? SchedulesTexts.ChooseDay : null);
        Validation.Rule(nameof(Time), () => ParseTime(Time) is null ? SchedulesTexts.EnterTime : null);
        Validation.Rule(nameof(WindowEnd), () =>
            WindowEnd.Trim().Length == 0 ? null
            : ParseTime(WindowEnd) is not { } end ? SchedulesTexts.EnterTime
            : end == ParseTime(Time) ? SchedulesTexts.SameAsStart
            : null);
        _filling = false;
        Validation.Reset();
    }

    /// <summary>Set by the window.</summary>
    public IScheduleEditorUi? Ui { get; set; }

    public string Title { get; }

    public IReadOnlyList<WizardStepItem> Steps { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsScheduleStep), nameof(IsDevicesStep), nameof(IsWhenStep), nameof(IsThenStep), nameof(IsOverviewStep), nameof(CanGoBack), nameof(StepProblem))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    public partial int Step { get; private set; }

    public bool IsScheduleStep => Step == ScheduleStep;

    public bool IsDevicesStep => Step == DevicesStep;

    public bool IsWhenStep => Step == WhenStep;

    public bool IsThenStep => Step == ThenStep;

    public bool IsOverviewStep => Step == OverviewStep;

    public bool CanGoBack => Step > ScheduleStep;

    // ---- 1 Schedule ----
    [ObservableProperty] public partial string Name { get; set; } = string.Empty;

    [ObservableProperty] public partial bool On { get; set; } = true;

    [ObservableProperty] public partial bool Stages { get; set; }

    // ---- 2 Devices ----
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DevicesButtonText))]
    public partial DeviceChoice Choice { get; private set; } = DeviceChoice.Devices([]);

    /// <summary>"Choose devices" while nothing is chosen, else "Selected devices: All", "Selected devices: Tag Site A and 3 devices".</summary>
    public string DevicesButtonText => Choice.IsEmpty ? SchedulesTexts.ChooseDevices
        : SchedulesTexts.SelectedDevices + (Choice.Kind == DeviceChoiceKind.All ? "All" : DeviceSelectorTexts.Brief(Choice.DeviceIds.Count, Choice.Tags));

    /// <summary>The devices the choice gives now (virtualized list).</summary>
    public RangeObservableCollection<ScheduleDeviceRow> ChosenDevices { get; } = [];

    /// <summary>The choice gives devices: their list shows (no empty grid).</summary>
    public bool HasChosenDevices => ChosenDevices.Count > 0;

    /// <summary>The device choice's error below its button (shown after an edit or when the step was left).</summary>
    public string? ChoiceError => ErrorOf(nameof(Choice));

    // ---- 3 When ----
    public static IReadOnlyList<RepeatChoice> RepeatChoices { get; } =
    [
        new(Recurrence.Once, SchedulesTexts.Once),
        new(Recurrence.Daily, SchedulesTexts.Daily),
        new(Recurrence.Weekly, SchedulesTexts.Weekly),
        new(Recurrence.Monthly, SchedulesTexts.Monthly),
    ];

    public static IReadOnlyList<int> DaysOfMonth { get; } = [.. Enumerable.Range(1, 28)];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOnce), nameof(IsWeekly), nameof(IsMonthly))]
    public partial RepeatChoice Repeat { get; set; } = RepeatChoices[2];

    public bool IsOnce => Repeat.Value == Recurrence.Once;

    public bool IsWeekly => Repeat.Value == Recurrence.Weekly;

    public bool IsMonthly => Repeat.Value == Recurrence.Monthly;

    /// <summary>The day of a run Once (the date picker).</summary>
    [ObservableProperty] public partial DateTimeOffset? Date { get; set; }

    /// <summary>The days of a weekly run (the shared day chips).</summary>
    public IReadOnlyList<DayChoice> Days { get; }

    [ObservableProperty] public partial int? DayOfMonth { get; set; }

    [ObservableProperty] public partial string Time { get; set; } = "23:00";

    [ObservableProperty] public partial string WindowEnd { get; set; } = string.Empty;

    public string? DateError => ErrorOf(nameof(Date));

    public string? DaysError => ErrorOf(nameof(Days));

    /// <summary>Below When, only when it applies and When has no error: a run takes longer than the time between two runs.</summary>
    public string? WhenWarning => ErrorOf(nameof(Repeat)) is null ? _check?.WhenWarning : null;

    public bool HasWhenWarning => WhenWarning is not null;

    // ---- 4 Then ----
    /// <summary>The actions from top to bottom.</summary>
    public ObservableCollection<NodeEditorViewModel> Nodes { get; } = [];

    /// <summary>What the canvas draws.</summary>
    public NodeChain Chain { get; } = new();

    /// <summary>The node list of Add action and the + below a node (the shared <see cref="NodePicker"/>).</summary>
    public NodePickerModel Picker { get; } = new();

    /// <summary>The kinds the node list offers, in the order of Provisioning's node list (<see cref="NodeLook.Order"/>).</summary>
    public IReadOnlyList<ActionKindInfo> AddableKinds { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(ShowsChooseAction))]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand), nameof(MoveDownCommand), nameof(RemoveNodeCommand))]
    public partial NodeEditorViewModel? SelectedNode { get; set; }

    public bool HasSelection => SelectedNode is not null;

    /// <summary>Actions but none selected: the side panel says what to do.</summary>
    public bool ShowsChooseAction => SelectedNode is null && Nodes.Count > 0;

    /// <summary>No action yet: the canvas says what to do first.</summary>
    public bool ShowsAddFirst => Nodes.Count == 0;

    /// <summary>The error of the whole list below the canvas (shown once the step was changed or left).</summary>
    public string? ActionsError => _thenShown ? ListProblem : null;

    public bool HasActionsError => ActionsError is not null;

    // ---- 5 Overview ----
    /// <summary>One short line per fact the steps before do not show.</summary>
    public ObservableCollection<OverviewLine> Overview { get; } = [];

    // ---- State ----
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(NextCommand))]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsSaving { get; private set; }

    /// <summary>Why reading or saving failed (left of Save), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorText))]
    public partial string? ErrorText { get; private set; }

    public bool HasErrorText => ErrorText is not null;

    /// <summary>The first problem of the shown step (Next tooltip).</summary>
    public string? StepProblem => Step == OverviewStep ? null : ProblemOf(Step);

    /// <summary>The first problem of the whole schedule (Save tooltip), null when it can be saved.</summary>
    public string? Problem => ProblemOf(ScheduleStep) ?? ProblemOf(DevicesStep) ?? ProblemOf(WhenStep) ?? ProblemOf(ThenStep);

    /// <summary>The saved schedule (after Save).</summary>
    public Schedule? Saved { get; private set; }

    /// <summary>Close the window: true after Save, false on Cancel.</summary>
    public event EventHandler<bool>? CloseRequested;

    /// <summary>The check that runs now (tests wait for it).</summary>
    public Task CheckTask { get; private set; } = Task.CompletedTask;

    /// <summary>Owner of the wizard window.</summary>
    internal Window? Owner => _ctx.Owner;

    IReadOnlyList<CredentialChoice> INodeFormServices.Credentials => [];

    IReadOnlyList<IssuingPolicyInfo> INodeFormServices.Policies => _policies;

    IReadOnlyList<ProfileCommand> INodeFormServices.Commands => _commands;

    bool INodeFormServices.CanRevealCredentials => false;

    Task<string> INodeFormServices.RevealCredentialAsync(Guid credentialId, CancellationToken ct) =>
        Task.FromException<string>(new NotSupportedException("A schedule has no passwords."));

    string? INodeFormServices.Note(NodeKind kind) => kind switch
    {
        NodeKind.UpgradeOnline => SchedulesTexts.MyAxisNote,
        NodeKind.Restart => SchedulesTexts.RestartNote,
        _ => NodeForms.Note(kind),
    };

    /// <summary>Reads the action kinds, PKI's policies, the VAPIX commands and the schedule (or starts a new one).</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        IsLoading = true;
        ErrorText = null;
        var isNew = false;
        try
        {
            var kinds = Parse<List<ActionKindInfo>>(await _ctx.InvokeAsync(SchedulesMethods.ActionKinds, null, ct).ConfigureAwait(true));
            _kinds = kinds.ToDictionary(k => k.Kind);
            AddableKinds = [.. kinds.Where(k => ActionKinds.IsAction(k.Kind)).OrderBy(k => NodeLook.Rank(k.Kind))];
            _policies = Parse<List<IssuingPolicyInfo>>(await _ctx.InvokeAsync(SchedulesMethods.Policies, null, ct).ConfigureAwait(true));
            _commands = Parse<List<ProfileCommand>>(await _ctx.InvokeAsync(SchedulesMethods.VapixCommands, null, ct).ConfigureAwait(true));
            if (_scheduleId is { } id)
            {
                var reply = Parse<ScheduleReply>(await _ctx.InvokeAsync(SchedulesMethods.Get, Serialize(new IdRequest(id)), ct).ConfigureAwait(true));
                Fill(reply.Schedule);
                ApplyCheck(reply.Check);
            }
            else
            {
                RebuildChain();
                isNew = true;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ErrorText = ServerErrors.Message(ex);
        }
        finally
        {
            IsLoading = false;
            RefreshState();
        }

        if (isNew)
        {
            RequestCheck(); // the Overview's facts and the server's errors of a new schedule
        }
    }

    /// <summary>Fills the wizard from a schedule (editing; also the tests' start).</summary>
    public void Fill(Schedule schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        _filling = true;
        try
        {
            _id = schedule.Id;
            Name = schedule.Name;
            On = schedule.On;
            Stages = schedule.Stages;
            Choice = schedule.Devices.Kind == ScheduleDevicesKind.All ? DeviceChoice.AllDevices : DeviceChoice.Devices(schedule.Devices.DeviceIds, schedule.Devices.Tags);
            var when = schedule.When;
            Repeat = RepeatChoices.First(c => c.Value == when.Recurrence);
            Date = when.Date is { } date ? new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue)) : Date;
            foreach (var day in Days)
            {
                day.IsChecked = when.Days.Contains(day.Day);
            }

            DayOfMonth = when.DayOfMonth ?? DayOfMonth;
            Time = ScheduleFormat.Time(when.Time);
            WindowEnd = when.WindowEnd is { } end ? ScheduleFormat.Time(end) : string.Empty;
            foreach (var node in Nodes)
            {
                node.Changed -= OnNodeChanged;
            }

            Nodes.Clear();
            foreach (var action in schedule.Actions)
            {
                Add(Nodes.Count, action);
            }

            SelectedNode = null;
        }
        finally
        {
            _filling = false;
        }

        UpdateDevices();
        Validation.Reset();
        RebuildChain();
    }

    /// <summary>The schedule as the wizard holds it now.</summary>
    public Schedule ToSchedule() => new()
    {
        Id = _id,
        Name = Name.Trim(),
        On = On,
        Stages = Stages,
        Devices = Choice.Kind == DeviceChoiceKind.All
            ? new ScheduleDevices { Kind = ScheduleDevicesKind.All }
            : new ScheduleDevices { Kind = ScheduleDevicesKind.Chosen, DeviceIds = [.. Choice.DeviceIds], Tags = [.. Choice.Tags] },
        When = new ScheduleWhen
        {
            Recurrence = Repeat.Value,
            Date = IsOnce && Date is { } date ? DateOnly.FromDateTime(date.Date) : null,
            Time = ParseTime(Time) ?? new TimeOnly(23, 0),
            Days = IsWeekly ? [.. Days.Where(d => d.IsChecked).Select(d => d.Day)] : [],
            DayOfMonth = IsMonthly ? DayOfMonth : null,
            WindowEnd = ParseTime(WindowEnd),
        },
        Actions = [.. Nodes.Select(n => n.ToFlowNode())],
    };

    /// <summary>Replaces the device choice (the picker's OK, tests).</summary>
    public void SetChoice(DeviceChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        Choice = choice.Kind == DeviceChoiceKind.EventDevice ? DeviceChoice.Devices([]) : choice;
        UpdateDevices();
        RequestCheck();
    }

    // ---- Wizard navigation ----
    internal string? ProblemOf(int step) => step switch
    {
        ScheduleStep => Validation.FirstErrorOf([nameof(Name)]),
        DevicesStep => Validation.FirstErrorOf([nameof(Choice)]),
        WhenStep => Validation.FirstErrorOf(WhenFields),
        ThenStep => ListProblem ?? Nodes.Select(n => n.CanvasError is { } e ? n.Name + ": " + e : null).FirstOrDefault(p => p is not null),
        _ => null,
    };

    /// <summary>A step can be opened when every step before it is complete (going back always works).</summary>
    internal bool CanOpen(int step) => step <= Step || Enumerable.Range(0, step).All(s => ProblemOf(s) is null);

    private bool CanGoNext() => !IsLoading && Step < OverviewStep && ProblemOf(Step) is null;

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void Next() => GoTo(Step + 1);

    [RelayCommand]
    private void Back()
    {
        if (Step > ScheduleStep)
        {
            GoTo(Step - 1);
        }
    }

    internal void GoTo(int step)
    {
        if (step < ScheduleStep || step > OverviewStep || !CanOpen(step))
        {
            return;
        }

        ShowErrorsOf(Step);
        Picker.Close();
        if (step == OverviewStep)
        {
            BuildOverview();
        }

        Step = step;
        RefreshState();
    }

    private void ShowErrorsOf(int step)
    {
        switch (step)
        {
            case ScheduleStep:
                Validation.ShowAll(nameof(Name));
                break;
            case DevicesStep:
                Validation.ShowAll(nameof(Choice));
                break;
            case WhenStep:
                Validation.ShowAll(WhenFields);
                break;
            case ThenStep:
                _thenShown = true;
                foreach (var node in Nodes)
                {
                    node.ShowErrors();
                }

                break;
        }
    }

    private void BuildOverview()
    {
        var check = _check;
        var lines = new List<OverviewLine>
        {
            new(SchedulesTexts.NextRun, !On ? SchedulesTexts.TurnedOff : check?.NextRun is { } next ? ScheduleFormat.Moment(next, _time.GetUtcNow()) : "None"),
            new(SchedulesTexts.Then, string.Join(", ", Nodes.Select(n => n.Name))),
        };
        if (Stages && check is { StageSizes.Count: > 0 })
        {
            lines.Add(new(SchedulesTexts.Stages, ScheduleFormat.StagesText(check.StageSizes)));
        }

        if (check?.Estimate is { } estimate)
        {
            lines.Add(new(SchedulesTexts.Estimated, ScheduleFormat.Duration(estimate)));
        }

        Overview.Clear();
        foreach (var line in lines)
        {
            Overview.Add(line);
        }
    }

    // ---- 2 Devices ----
    [RelayCommand]
    private async Task ChooseDevicesAsync()
    {
        if (Ui is not { } ui)
        {
            return;
        }

        var picker = new DevicePickerViewModel(_ctx.Devices, Choice, new DevicePickerOptions { AllowAll = true, AllowTags = true });
        if (await ui.ChooseDevicesAsync(picker).ConfigureAwait(true))
        {
            SetChoice(picker.Choice);
        }
    }

    /// <summary>The devices the choice gives, in one pass over the device list (O(n)).</summary>
    private void UpdateDevices()
    {
        var choice = Choice;
        IEnumerable<IDeviceInfo> devices = _ctx.Devices;
        if (choice.Kind != DeviceChoiceKind.All)
        {
            var ids = choice.DeviceIds.ToHashSet();
            var tags = choice.Tags.ToHashSet(StringComparer.OrdinalIgnoreCase);
            devices = choice.IsEmpty ? [] : devices.Where(d => ids.Contains(d.Id) || (tags.Count > 0 && d.Tags.Any(tags.Contains)));
        }

        ChosenDevices.ReplaceAll([.. devices.Select(d => (Key: AddressKey(d.Address), Row: new ScheduleDeviceRow(d.Address, d.HostName ?? string.Empty, d.Model ?? string.Empty)))
            .OrderBy(r => r.Key, StringComparer.Ordinal)
            .Select(r => r.Row)]);
        OnPropertyChanged(nameof(HasChosenDevices));
    }

    // ---- 4 Then ----
    /// <summary>Add action: the node list at the top left of the canvas; the new action goes last.</summary>
    [RelayCommand(CanExecute = nameof(CanAddAction))]
    private void AddAction() => OpenPicker(Nodes.Count - 1, 16, 16);

    private bool CanAddAction() => !IsLoading && Nodes.Count < SchedulesInfo.MaxActions;

    /// <summary>The node list at (<paramref name="left"/>, <paramref name="top"/>); the new action goes right after the one at <paramref name="index"/> (-1: first).</summary>
    public void OpenPicker(int index, double left, double top)
    {
        if (index < -1 || index >= Nodes.Count || IsLoading || Nodes.Count >= SchedulesInfo.MaxActions)
        {
            return;
        }

        _addAfter = index;
        Picker.Show(AddableKinds.Select(k => new ActionItem(k)), left, top);
    }

    /// <summary>A kind chosen in the node list: the new action right after the one at <paramref name="index"/> (-1: first), selected.</summary>
    public NodeEditorViewModel? AddAfter(int index, ActionKindInfo kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        if (index < -1 || index >= Nodes.Count || Nodes.Count >= SchedulesInfo.MaxActions || !ActionKinds.IsAction(kind.Kind))
        {
            return null;
        }

        var node = Add(index + 1, new FlowNode { Id = NextId(), Kind = kind.Kind, ContinueOnError = true, Settings = kind.DefaultSettings });
        SelectedNode = node;
        _thenShown = true;
        AfterChange();
        return node;
    }

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    public void MoveUp() => Move(-1);

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    public void MoveDown() => Move(1);

    [RelayCommand(CanExecute = nameof(CanRemoveNode))]
    public void RemoveNode()
    {
        if (SelectedNode is not { } node)
        {
            return;
        }

        var index = Nodes.IndexOf(node);
        node.Changed -= OnNodeChanged;
        Nodes.RemoveAt(index);
        SelectedNode = Nodes.Count > 0 ? Nodes[Math.Min(index, Nodes.Count - 1)] : null;
        _thenShown = true;
        AfterChange();
    }

    public bool CanMoveUp() => SelectedNode is { } node && Nodes.IndexOf(node) > 0;

    public bool CanMoveDown() => SelectedNode is { } node && Nodes.IndexOf(node) < Nodes.Count - 1;

    public bool CanRemoveNode() => SelectedNode is not null;

    /// <summary>Every action in the view again.</summary>
    [RelayCommand]
    public void FitView() => Chain.FitView();

    // ---- Save ----
    public bool CanSave() => !IsLoading && !IsSaving && Problem is null;

    [RelayCommand(CanExecute = nameof(CanSave))]
    public async Task SaveAsync()
    {
        Validation.ShowAll();
        ShowErrorsOf(ThenStep);
        RefreshState();
        if (!CanSave())
        {
            return;
        }

        IsSaving = true;
        ErrorText = null;
        try
        {
            var reply = Parse<ScheduleReply>(await _ctx.InvokeAsync(SchedulesMethods.Save, Serialize(new ScheduleRequest(ToSchedule())), CancellationToken.None).ConfigureAwait(true));
            Saved = reply.Schedule;
            CloseRequested?.Invoke(this, true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ErrorText = ServerErrors.Message(ex);
        }
        finally
        {
            IsSaving = false;
        }
    }

    [RelayCommand]
    public void Cancel() => CloseRequested?.Invoke(this, false);

    async Task<IReadOnlyList<StoredFile>> INodeFormServices.ChooseFilesAsync(string title, bool multiple, IReadOnlyList<string> patterns)
    {
        if (Ui is not { } ui)
        {
            return [];
        }

        var stored = new List<StoredFile>();
        foreach (var path in await ui.PickFilesAsync(title, multiple, patterns).ConfigureAwait(true))
        {
            var upload = await _ctx.UploadAsync(path, null, CancellationToken.None).ConfigureAwait(true);
            stored.Add(Parse<StoredFile>(await _ctx.InvokeAsync(SchedulesMethods.StoreFile, Serialize(new FileRequest(upload.Id)), CancellationToken.None).ConfigureAwait(true)));
        }

        return stored;
    }

    async Task<string> INodeFormServices.ReadConfigFileAsync(Guid fileId, CancellationToken ct) =>
        Parse<ConfigFileReply>(await _ctx.InvokeAsync(SchedulesMethods.ReadConfigFile, Serialize(new IdRequest(fileId)), ct).ConfigureAwait(true)).Content;

    protected override void OnValidationChanged()
    {
        base.OnValidationChanged();
        RefreshState();
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!_filling && e.PropertyName is nameof(Name) or nameof(On) or nameof(Stages) or nameof(Repeat) or nameof(Date) or nameof(DayOfMonth)
            or nameof(Time) or nameof(WindowEnd) or nameof(Days))
        {
            RequestCheck();
        }
    }

    partial void OnSelectedNodeChanged(NodeEditorViewModel? value)
    {
        value?.Preview?.Refresh(); // the file preview reads its file once the action shows
        if (!_syncing)
        {
            _syncing = true;
            Chain.SelectedIndex = value is null ? -1 : Nodes.IndexOf(value);
            _syncing = false;
        }
    }

    private void OnDayChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DayChoice.IsChecked))
        {
            OnPropertyChanged(nameof(Days)); // marks the field edited and checks again
        }
    }

    private void OnChainChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NodeChain.SelectedIndex) && !_syncing)
        {
            Picker.Close(); // a click on the canvas closes the node list
            _syncing = true;
            SelectedNode = Chain.SelectedIndex >= 0 && Chain.SelectedIndex < Nodes.Count ? Nodes[Chain.SelectedIndex] : null;
            _syncing = false;
        }
    }

    private NodeEditorViewModel Add(int index, FlowNode node)
    {
        var info = _kinds.GetValueOrDefault(node.Kind);
        var kind = new NodeKindInfo(node.Kind, info?.Name ?? SchedulesTexts.ActionName(node.Kind), false, false, info?.DefaultSettings ?? default);
        var editor = new NodeEditorViewModel(node, kind, this);
        editor.Changed += OnNodeChanged;
        Nodes.Insert(index, editor);
        return editor;
    }

    private void Move(int by)
    {
        if (SelectedNode is not { } node)
        {
            return;
        }

        var index = Nodes.IndexOf(node);
        var to = index + by;
        if (to < 0 || to >= Nodes.Count)
        {
            return;
        }

        Nodes.Move(index, to);
        _syncing = true;
        Chain.SelectedIndex = to;
        _syncing = false;
        AfterChange();
    }

    private string NextId()
    {
        var ids = Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        for (var i = Nodes.Count + 1; ; i++)
        {
            if (!ids.Contains("n" + i))
            {
                return "n" + i;
            }
        }
    }

    private void OnNodeChanged(object? sender, EventArgs e) => AfterChange();

    private void AfterChange()
    {
        RebuildChain();
        RequestCheck();
    }

    /// <summary>The list's own problem: no action, too many, or the server's (an action the caller may not run).</summary>
    private string? ListProblem =>
        Nodes.Count == 0 ? SchedulesTexts.AddActionFirst
        : Nodes.Count > SchedulesInfo.MaxActions ? SchedulesTexts.TooManyActions
        : _check?.ActionsError;

    /// <summary>The server's check after a change: one at a time, and once more when something changed meanwhile.</summary>
    private void RequestCheck()
    {
        if (_filling || IsLoading)
        {
            return;
        }

        if (_checking)
        {
            _checkAgain = true;
            return;
        }

        CheckTask = CheckAsync();
    }

    private async Task CheckAsync()
    {
        _checking = true;
        try
        {
            do
            {
                _checkAgain = false;
                try
                {
                    var answer = await _ctx.InvokeAsync(SchedulesMethods.Check, Serialize(new ScheduleRequest(ToSchedule())), CancellationToken.None).ConfigureAwait(true);
                    if (!_checkAgain)
                    {
                        ApplyCheck(Parse<ScheduleCheck>(answer));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ErrorText = ServerErrors.Message(ex);
                }
            }
            while (_checkAgain);
        }
        finally
        {
            _checking = false;
        }
    }

    private void ApplyCheck(ScheduleCheck check)
    {
        _check = check;
        foreach (var node in Nodes)
        {
            node.CheckError = check.ActionErrors.GetValueOrDefault(node.Id);
            node.CheckWarning = check.ActionWarnings.GetValueOrDefault(node.Id);
        }

        Validation.Validate();
        RebuildChain();
        if (Step == OverviewStep)
        {
            BuildOverview();
        }
    }

    private void RebuildChain()
    {
        var canAdd = Nodes.Count < SchedulesInfo.MaxActions;
        Chain.Nodes = [.. Nodes.Select(n => ToChainNode(n) with { CanAddAfter = canAdd })];
        _syncing = true;
        Chain.SelectedIndex = SelectedNode is null ? -1 : Nodes.IndexOf(SelectedNode);
        _syncing = false;
        RefreshState();
    }

    private void RefreshState()
    {
        OnPropertyChanged(nameof(StepProblem));
        OnPropertyChanged(nameof(Problem));
        OnPropertyChanged(nameof(ChoiceError));
        OnPropertyChanged(nameof(DateError));
        OnPropertyChanged(nameof(DaysError));
        OnPropertyChanged(nameof(WhenWarning));
        OnPropertyChanged(nameof(HasWhenWarning));
        OnPropertyChanged(nameof(ActionsError));
        OnPropertyChanged(nameof(HasActionsError));
        OnPropertyChanged(nameof(ShowsAddFirst));
        OnPropertyChanged(nameof(ShowsChooseAction));
        NextCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        AddActionCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        RemoveNodeCommand.NotifyCanExecuteChanged();
        foreach (var item in Steps)
        {
            item.Refresh();
        }
    }

    private static NodeChainNode ToChainNode(NodeEditorViewModel node)
    {
        var error = node.CanvasError;
        var below = error ?? node.CheckWarning;
        var summary = node.Summary;
        return new NodeChainNode(node.Name, NodeLook.IconKey(node.Kind), NodeLook.BrushKey(node.Kind))
        {
            Line = summary,
            Below = below,
            BelowBrushKey = error is null && below is not null ? "Oadm.StatusWarningBrush" : null,
            Tip = summary.Length == 0 ? null : summary,
        };
    }

    /// <summary>"23:00", "7:30" -> the time; null when not a time.</summary>
    internal static TimeOnly? ParseTime(string? text) =>
        TimeOnly.TryParseExact((text ?? string.Empty).Trim(), ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time : null;

    private static string AddressKey(string address)
    {
        var parts = address.Split(':')[0].Split('.');
        return parts.Length == 4 && parts.All(p => byte.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            ? string.Join('.', parts.Select(p => p.PadLeft(3, '0')))
            : "~" + address;
    }

    private static string Serialize<T>(T value) => PluginJson.Serialize(value, SchedulesJson.Options);

    private static T Parse<T>(string? json) => PluginJson.Parse<T>(json, SchedulesJson.Options);
}
