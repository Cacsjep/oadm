using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Grpc.Core;

using Oadm.Client.Api;
using Oadm.Client.Devices;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client.Validation;

namespace Oadm.Client.Tags;

/// <summary>One tag in the Tags dialog: color dot, three-state check box (all / some / none of the selected devices), count.</summary>
public sealed partial class TagChoiceViewModel : ObservableObject
{
    internal TagChoiceViewModel(TagInfo tag, bool? initial, int deviceCount)
    {
        Tag = tag;
        Name = tag.Name;
        Color = tag.Color;
        Initial = initial;
        IsChecked = initial;
        DeviceCount = deviceCount;
    }

    /// <summary>The shared tag of the client (rows and chips).</summary>
    public TagInfo Tag { get; private set; }

    [ObservableProperty] public partial string Name { get; private set; }

    [ObservableProperty] public partial TagColor Color { get; private set; }

    /// <summary>true: every selected device has the tag, null: some, false: none. A click cycles all / none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChanged))]
    public partial bool? IsChecked { get; set; }

    /// <summary>The state when the dialog opened.</summary>
    public bool? Initial { get; }

    public bool IsChanged => IsChecked != Initial;

    /// <summary>All devices (not only the selected ones) with this tag; the delete confirmation names it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    public partial int DeviceCount { get; private set; }

    /// <summary>"42 devices".</summary>
    public string CountText => TagStore.DevicesText(DeviceCount);

    /// <summary>Tooltip of the check box.</summary>
    public string StateText => Initial switch
    {
        true => "All selected devices have this tag.",
        null => "Some of the selected devices have this tag.",
        _ => "None of the selected devices has this tag.",
    };

    /// <summary>The click of the check box: some or none become all, all becomes none.</summary>
    [RelayCommand]
    private void Toggle() => IsChecked = IsChecked != true;

    internal void Renamed(TagInfo tag)
    {
        Tag = tag;
        Name = tag.Name;
        Color = tag.Color;
    }
}

/// <summary>
/// The Tags dialog (device context menu "Tags") for one or many selected devices: every tag with its color and a
/// three-state check box, a search, "+ New tag" (name below-field validation, palette), and for administrators per-row
/// rename / recolor and delete (with a confirmation naming the devices). Creating, renaming and deleting a tag happen at
/// once on the server; the check boxes are applied with OK in one SetDeviceTags call for the whole selection.
/// O(devices x tags) once when the dialog opens; the list is virtualized.
/// </summary>
public sealed partial class DeviceTagsViewModel : ValidatingViewModel
{
    private readonly IOadmApi _api;
    private readonly TagStore _tags;
    private readonly IReadOnlyList<DeviceRowViewModel> _devices;
    private readonly Func<string, string, string, Task<bool>> _confirm;
    private readonly List<TagChoiceViewModel> _all;

    /// <param name="devices">The selected devices.</param>
    /// <param name="allDevices">Every device of the client (device counts of the delete confirmation).</param>
    /// <param name="isAdmin">Administrators rename, recolor and delete tags.</param>
    /// <param name="confirm">The shared confirmation window (title, message, confirm text).</param>
    public DeviceTagsViewModel(
        IOadmApi api, TagStore tags, IReadOnlyList<DeviceRowViewModel> devices, IEnumerable<DeviceRowViewModel> allDevices, bool isAdmin,
        Func<string, string, string, Task<bool>> confirm)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(allDevices);
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _tags = tags ?? throw new ArgumentNullException(nameof(tags));
        _confirm = confirm ?? throw new ArgumentNullException(nameof(confirm));
        _devices = devices;
        IsAdmin = isAdmin;

        // Counts: the selection (check box state) and all devices (delete confirmation), one pass each.
        var selected = new Dictionary<TagInfo, int>();
        foreach (DeviceRowViewModel device in devices)
        {
            foreach (TagInfo tag in device.TagChips)
            {
                selected[tag] = selected.GetValueOrDefault(tag) + 1;
            }
        }

        var total = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (DeviceRowViewModel device in allDevices)
        {
            foreach (string tag in device.Tags)
            {
                total[tag] = total.GetValueOrDefault(tag) + 1;
            }
        }

        var choices = new List<TagInfo>(tags.Tags);
        choices.AddRange(selected.Keys.Where(t => !choices.Contains(t)));
        choices.Sort(TagStore.Compare);
        _all = choices.Select(t =>
        {
            int count = selected.GetValueOrDefault(t);
            bool? state = count == 0 ? false : count == devices.Count ? true : null;
            return new TagChoiceViewModel(t, state, total.GetValueOrDefault(t.Name));
        }).ToList();
        Choices.ReplaceAll(_all);
        NewTagColor = TagPalette.Next(_all.Count);

        Validation
            .Rule(nameof(NewTagName), () => IsAddingTag ? NameProblem(NewTagName, null) : null)
            .Rule(nameof(EditName), () => EditingChoice is { } editing ? NameProblem(EditName, editing) : null);
        Validation.Validate();
    }

    /// <summary>The tags shown (search applied), sorted by name.</summary>
    public RangeObservableCollection<TagChoiceViewModel> Choices { get; } = [];

    public bool IsAdmin { get; }

    /// <summary>"Tags of 10.0.0.21 (AXIS M3106-L Mk II)" / "Tags of 12 selected devices".</summary>
    public string Intro => _devices.Count == 1
        ? $"Tags of {Label(_devices[0])}."
        : string.Create(CultureInfo.CurrentCulture, $"Tags of {_devices.Count:N0} selected devices.");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial string SearchText { get; set; } = "";

    /// <summary>No tag (matches the search).</summary>
    public bool IsEmpty => Choices.Count == 0;

    public string EmptyText => _all.Count == 0 ? "No tags yet." : "No tag matches the search.";

    // ---------------------------------------------------------------- new tag

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInlineEditorOpen))]
    public partial bool IsAddingTag { get; private set; }

    [ObservableProperty] public partial string NewTagName { get; set; } = "";

    [ObservableProperty] public partial TagColor NewTagColor { get; set; }

    // ---------------------------------------------------------------- edit (administrators)

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing), nameof(IsInlineEditorOpen))]
    public partial TagChoiceViewModel? EditingChoice { get; private set; }

    public bool IsEditing => EditingChoice is not null;

    /// <summary>New tag or rename editor open: OK is demoted, the editor's button is the accent one.</summary>
    public bool IsInlineEditorOpen => IsAddingTag || IsEditing;

    [ObservableProperty] public partial string EditName { get; set; } = "";

    [ObservableProperty] public partial TagColor EditColor { get; set; }

    // ---------------------------------------------------------------- state

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand), nameof(CreateTagCommand), nameof(SaveEditCommand), nameof(DeleteCommand))]
    public partial bool IsBusy { get; private set; }

    public bool IsIdle => !IsBusy;

    /// <summary>Why the last server call failed; shown directly below the list.</summary>
    [ObservableProperty] public partial string? ErrorText { get; private set; }

    /// <summary>Devices whose tags OK changed (result of the last apply).</summary>
    public int DevicesChanged { get; private set; }

    /// <summary>True: tags applied; false: cancelled.</summary>
    public event EventHandler<bool>? CloseRequested;

    partial void OnSearchTextChanged(string value) => ApplySearch();

    private void ApplySearch()
    {
        string term = SearchText.Trim();
        Choices.ReplaceAll(term.Length == 0 ? _all : _all.Where(c => c.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase)).ToList());
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyText));
    }

    [RelayCommand]
    private void StartNewTag()
    {
        CancelEdit();
        IsAddingTag = true;
        NewTagName = SearchText.Trim();
        NewTagColor = TagPalette.Next(_all.Count);
        Validation.Reset(nameof(NewTagName));
    }

    [RelayCommand]
    private void CancelNewTag()
    {
        IsAddingTag = false;
        NewTagName = "";
        Validation.Reset(nameof(NewTagName));
    }

    /// <summary>Creates the tag on the server and checks it for the selection (applied with OK).</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task CreateTagAsync()
    {
        Validation.ShowAll();
        if (ErrorOf(nameof(NewTagName)) is not null)
        {
            return;
        }

        await RunAsync(async () =>
        {
            DeviceTag created = await _api.CreateTagAsync(NewTagName.Trim(), NewTagColor, CancellationToken.None).ConfigureAwait(true);
            TagInfo tag = _tags.Apply(created);
            TagChoiceViewModel? choice = _all.Find(c => c.Tag == tag);
            if (choice is null)
            {
                choice = new TagChoiceViewModel(tag, initial: false, deviceCount: 0);
                _all.Add(choice);
                _all.Sort((a, b) => TagStore.Compare(a.Tag, b.Tag));
            }

            choice.IsChecked = true;
            IsAddingTag = false;
            NewTagName = "";
            SearchText = "";
            ApplySearch();
            Validation.Reset(nameof(NewTagName));
        }, nameof(NewTagName)).ConfigureAwait(true);
    }

    [RelayCommand]
    private void StartEdit(TagChoiceViewModel? choice)
    {
        if (!IsAdmin || choice is null)
        {
            return;
        }

        CancelNewTag();
        EditingChoice = choice;
        EditName = choice.Name;
        EditColor = choice.Color == TagColor.Unspecified ? TagPalette.Next(_all.Count) : choice.Color;
        Validation.Reset(nameof(EditName));
    }

    [RelayCommand]
    private void CancelEdit()
    {
        EditingChoice = null;
        EditName = "";
        Validation.Reset(nameof(EditName));
    }

    /// <summary>Administrators: rename and / or recolor at once (the server rewrites every device's tag in one transaction).</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task SaveEditAsync()
    {
        if (EditingChoice is not { } choice)
        {
            return;
        }

        Validation.ShowAll();
        if (ErrorOf(nameof(EditName)) is not null)
        {
            return;
        }

        string newName = EditName.Trim();
        bool renamed = !string.Equals(newName, choice.Name, StringComparison.Ordinal);
        bool recolored = EditColor != choice.Color;
        if (!renamed && !recolored)
        {
            CancelEdit();
            return;
        }

        await RunAsync(async () =>
        {
            string previous = choice.Name;
            DeviceTag updated = await _api.UpdateTagAsync(previous, renamed ? newName : null, recolored ? EditColor : TagColor.Unspecified, CancellationToken.None).ConfigureAwait(true);
            choice.Renamed(_tags.Apply(updated, previous));
            _all.Sort((a, b) => TagStore.Compare(a.Tag, b.Tag));
            ApplySearch();
            CancelEdit();
        }, nameof(EditName)).ConfigureAwait(true);
    }

    /// <summary>Administrators: deletes the tag after a confirmation ("Remove tag PTZ from 42 devices?").</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task DeleteAsync(TagChoiceViewModel? choice)
    {
        if (!IsAdmin || choice is null)
        {
            return;
        }

        string question = choice.DeviceCount == 0
            ? $"Delete the tag {choice.Name}?"
            : $"Remove tag {choice.Name} from {TagStore.DevicesText(choice.DeviceCount)}?";
        if (!await _confirm("Delete tag", question, "Delete").ConfigureAwait(true))
        {
            return;
        }

        await RunAsync(async () =>
        {
            await _api.DeleteTagAsync(choice.Name, CancellationToken.None).ConfigureAwait(true);
            _tags.Forget(choice.Name);
            _all.Remove(choice);
            if (EditingChoice == choice)
            {
                CancelEdit();
            }

            ApplySearch();
        }, null).ConfigureAwait(true);
    }

    /// <summary>OK: the changed check boxes as one SetDeviceTags call for the whole selection; nothing changed closes at once.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task ApplyAsync()
    {
        var (add, remove) = Changes();
        if (add.Count == 0 && remove.Count == 0)
        {
            CloseRequested?.Invoke(this, false);
            return;
        }

        bool ok = await RunAsync(async () =>
        {
            SetDeviceTagsReply reply = await _api.SetDeviceTagsAsync(_devices.Select(d => d.Id).ToList(), add, remove, CancellationToken.None).ConfigureAwait(true);
            DevicesChanged = reply.DevicesChanged;
            foreach (DeviceTag created in reply.Created)
            {
                _tags.Apply(created);
            }
        }, null).ConfigureAwait(true);
        if (ok)
        {
            CloseRequested?.Invoke(this, true);
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    /// <summary>The names to add (checked, were not all) and to remove (cleared, were some or all).</summary>
    public (List<string> Add, List<string> Remove) Changes() =>
        (_all.Where(c => c.IsChecked == true && c.Initial != true).Select(c => c.Name).ToList(),
         _all.Where(c => c.IsChecked == false && c.Initial != false).Select(c => c.Name).ToList());

    /// <summary>The tag a name stands for (tests).</summary>
    public TagChoiceViewModel? Find(string name) => _all.Find(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Same rules as the server (TagNames), plus unique among the other tags.</summary>
    internal string? NameProblem(string? name, TagChoiceViewModel? except)
    {
        string text = name?.Trim() ?? "";
        if (text.Length == 0)
        {
            return "Enter a tag name.";
        }

        if (text.Length > MaxNameLength)
        {
            return string.Create(CultureInfo.InvariantCulture, $"A tag name has at most {MaxNameLength} characters.");
        }

        if (text.Contains(';', StringComparison.Ordinal))
        {
            return "A tag name cannot contain \";\".";
        }

        if (text.Any(char.IsControl))
        {
            return "A tag name cannot contain control characters.";
        }

        return _all.Any(c => c != except && string.Equals(c.Name, text, StringComparison.OrdinalIgnoreCase))
            ? $"A tag named \"{text}\" already exists."
            : null;
    }

    /// <summary>Longest tag name (the server's TagNames.MaxLength).</summary>
    public const int MaxNameLength = 32;

    /// <summary>Runs a server call: busy while it runs; a failure shows below its field (or below the list).</summary>
    private async Task<bool> RunAsync(Func<Task> call, string? field)
    {
        ErrorText = null;
        IsBusy = true;
        try
        {
            await call().ConfigureAwait(true);
            return true;
        }
        catch (RpcException ex)
        {
            string message = string.IsNullOrEmpty(ex.Status.Detail) ? ex.Message : ex.Status.Detail;
            if (field is not null && ex.StatusCode is StatusCode.InvalidArgument or StatusCode.AlreadyExists)
            {
                Validation.SetServerError(field, message);
            }
            else
            {
                ErrorText = ex.StatusCode == StatusCode.PermissionDenied ? "Only administrators can rename or delete tags." : message;
            }

            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string Label(DeviceRowViewModel device) =>
        string.IsNullOrEmpty(device.Model) ? device.DisplayAddress : $"{device.DisplayAddress} ({device.Model})";
}
