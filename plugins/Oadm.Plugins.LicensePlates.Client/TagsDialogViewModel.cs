using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Client.Validation;

namespace Oadm.Plugins.LicensePlates.Client;

/// <summary>The tags to add to and remove from the chosen plates or cameras (OK of a Change dialog).</summary>
public sealed record TagChange(IReadOnlyList<Guid> Add, IReadOnlyList<Guid> Remove);

/// <summary>One plate tag in the tags dialog: its colour and name, how many plates and cameras use it, and its check box.</summary>
public sealed partial class TagChoiceRow : ObservableObject
{
    private readonly int _initialHave;
    private readonly int _of;

    internal TagChoiceRow(TagInfo tag, int have, int of)
    {
        Tag = tag;
        _initialHave = have;
        _of = of;
        IsChecked = have == 0 ? false : have == of ? true : null;
        ToggleCommand = new RelayCommand(Toggle);
    }

    public TagInfo Tag { get; }

    public Guid Id => Tag.Id;

    public string Name => Tag.Name;

    public PlateTagColor Color => Tag.Color;

    /// <summary>"12 plates · 2 cameras".</summary>
    public string CountText => PageTexts.PlateCount(Tag.Plates) + " · " + PageTexts.CameraCount(Tag.Cameras);

    /// <summary>All (true), some (null) or none (false) of the chosen plates or cameras have it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    public partial bool? IsChecked { get; private set; }

    /// <summary>"All 3 selected have it", "2 of 3 selected have it", "None of 3 selected has it".</summary>
    public string StateText => IsChecked switch
    {
        true => string.Create(CultureInfo.CurrentCulture, $"All {_of:N0} selected have it"),
        null => string.Create(CultureInfo.CurrentCulture, $"{_initialHave:N0} of {_of:N0} selected have it"),
        _ => string.Create(CultureInfo.CurrentCulture, $"None of {_of:N0} selected has it"),
    };

    /// <summary>The box was clicked: some becomes all, all becomes none, none becomes all.</summary>
    public IRelayCommand ToggleCommand { get; }

    /// <summary>The click changed it from what the chosen ones had.</summary>
    public bool IsChanged => IsChecked != (_initialHave == 0 ? false : _initialHave == _of ? true : null);

    private void Toggle() => IsChecked = IsChecked != true;

    internal void SetChecked(bool value) => IsChecked = value;
}

/// <summary>
/// The tags dialog of the page (docs/spec/license-plate-center.md "Plates", "Cameras"): <b>Change tags</b> of the selected plates
/// and <b>Allowed tags</b> of the selected cameras (a check box per tag: all, some or none of them have it; OK adds and removes
/// what was clicked), and <b>Manage tags</b> (names and colours: New tag, rename or recolor, Delete, which asks when the tag is
/// in use). New tag and the changes of a tag need "Manage plates"; at most 200 tags.
/// </summary>
public sealed partial class TagsDialogViewModel : ValidatingViewModel
{
    private readonly LicensePlatesViewModel _page;
    private readonly Func<Guid, int> _haveCount;
    private readonly int _of;
    private readonly Dictionary<Guid, bool?> _clicked = [];

    private TagsDialogViewModel(LicensePlatesViewModel page, string title, bool isChange, Func<Guid, int> have, int of)
    {
        _page = page;
        Title = title;
        IsChange = isChange;
        _haveCount = have;
        _of = of;
        Validation.Rule(nameof(TagName), () => TagNameProblem(TagName, EditingRow?.Id ?? Guid.Empty));
        Validation.Validate();
        Rebuild();
        page.Tags.Changed += (_, _) => Rebuild();
    }

    /// <summary>Change tags of the selected plates.</summary>
    public static TagsDialogViewModel ForPlates(LicensePlatesViewModel page, IReadOnlyList<Plate> plates)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(plates);
        var counts = Count(plates.SelectMany(p => p.Tags));
        return new TagsDialogViewModel(page, PageTexts.ChangeTags, true, id => counts.GetValueOrDefault(id), plates.Count);
    }

    /// <summary>Allowed tags of the selected cameras.</summary>
    public static TagsDialogViewModel ForCameras(LicensePlatesViewModel page, IReadOnlyList<CameraRow> cameras)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(cameras);
        var counts = Count(cameras.SelectMany(c => c.AllowedTags));
        return new TagsDialogViewModel(page, PageTexts.AllowedTags, true, id => counts.GetValueOrDefault(id), cameras.Count);
    }

    /// <summary>Manage tags: names and colours.</summary>
    public static TagsDialogViewModel ForManage(LicensePlatesViewModel page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return new TagsDialogViewModel(page, PageTexts.ManageTags, false, _ => 0, 0);
    }

    public const int MaxTagName = LicensePlateLimits.MaxTagNameLength;

    public string Title { get; }

    /// <summary>Cancel beside OK; Close alone when managing.</summary>
    public string CancelText => IsChange ? PageTexts.Cancel : PageTexts.Close;

    /// <summary>Change tags or Allowed tags: check boxes and OK; Manage tags: Close only.</summary>
    public bool IsChange { get; }

    public bool IsManage => !IsChange;

    /// <summary>New tag, rename, recolor and delete ("Manage plates").</summary>
    public bool MayManage => _page.MayManagePlates;

    /// <summary>What OK applies; null until OK.</summary>
    public TagChange? Change { get; private set; }

    public RangeObservableCollection<TagChoiceRow> Rows { get; } = [];

    [ObservableProperty] public partial string SearchText { get; set; } = string.Empty;

    /// <summary>The inline editor is open for a new tag.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditorOpen), nameof(EditorTitle), nameof(EditorButton))]
    public partial bool IsCreating { get; private set; }

    /// <summary>The inline editor is open for this tag (rename or recolor).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditorOpen), nameof(EditorTitle), nameof(EditorButton))]
    public partial TagChoiceRow? EditingRow { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveTagCommand))]
    public partial string TagName { get; set; } = string.Empty;

    [ObservableProperty] public partial int TagColor { get; set; } = 1;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveTagCommand), nameof(OkCommand), nameof(StartNewTagCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; private set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    /// <summary>While the inline editor is open its button is the one accent button (CLAUDE.md "One accent button per view").</summary>
    public bool IsEditorOpen => IsCreating || EditingRow is not null;

    public string EditorTitle => EditingRow is { } row ? "Change tag " + row.Name : PageTexts.NewTag;

    public string EditorButton => EditingRow is not null ? PageTexts.Save : PageTexts.CreateTag;

    public bool IsEmpty => Rows.Count == 0;

    public string EmptyText => _page.Tags.All.Count == 0 ? PageTexts.NoPlateTags : "No tags match the search.";

    /// <summary>OK (Change) or Close (Manage): the view closes the window.</summary>
    public event EventHandler<bool>? CloseRequested;

    /// <summary>The save or delete that runs now (tests wait for it).</summary>
    public Task WorkTask { get; private set; } = Task.CompletedTask;

    public bool CanSave => !IsBusy && IsFormValid;

    partial void OnSearchTextChanged(string value) => Rebuild();

    partial void OnIsCreatingChanged(bool value) => OkCommand.NotifyCanExecuteChanged();

    partial void OnEditingRowChanged(TagChoiceRow? value) => OkCommand.NotifyCanExecuteChanged();

    private bool CanStartNew() => MayManage && !IsBusy && _page.Tags.All.Count < LicensePlateLimits.MaxTags;

    [RelayCommand(CanExecute = nameof(CanStartNew))]
    private void StartNewTag()
    {
        EditingRow = null;
        TagName = string.Empty;
        TagColor = Sdk.Client.Controls.TagColors.Next(_page.Tags.All.Count);
        Validation.Reset(nameof(TagName));
        IsCreating = true;
    }

    [RelayCommand]
    private void StartEdit(TagChoiceRow? row)
    {
        if (row is null || !MayManage)
        {
            return;
        }

        IsCreating = false;
        EditingRow = row;
        TagName = row.Name;
        TagColor = (int)row.Color;
        Validation.Reset(nameof(TagName));
    }

    [RelayCommand]
    private void CancelEditor()
    {
        IsCreating = false;
        EditingRow = null;
        Validation.Reset(nameof(TagName));
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task SaveTagAsync() => WorkTask = RunAsync(async () =>
    {
        Validation.ShowAll(nameof(TagName));
        if (!IsFormValid)
        {
            return;
        }

        var tag = new PlateTag { Id = EditingRow?.Id ?? Guid.Empty, Name = TagName.Trim(), Color = (PlateTagColor)TagColor };
        var reply = await _page.Client.CallAsync<SaveTagReply>(LicensePlatesMethods.SaveTag, new SaveTagRequest(tag)).ConfigureAwait(true);
        if (reply.Errors is { Count: > 0 } errors)
        {
            Validation.SetServerError(nameof(TagName), errors.GetValueOrDefault("name") ?? errors.Values.First());
            return;
        }

        IsCreating = false;
        EditingRow = null;
        await _page.ReloadTagsAsync().ConfigureAwait(true);
    });

    [RelayCommand]
    private Task DeleteAsync(TagChoiceRow? row) => row is null || !MayManage ? Task.CompletedTask : WorkTask = RunAsync(async () =>
    {
        var reply = await _page.Client.CallAsync<DeleteTagReply>(LicensePlatesMethods.DeleteTag, new DeleteTagRequest(row.Id)).ConfigureAwait(true);
        if (!reply.Deleted)
        {
            if (!await _page.Client.Context.ConfirmAsync(PageTexts.DeleteTagTitle, PageTexts.DeleteTagMessage(row.Name, reply.Plates, reply.Cameras), PageTexts.Delete).ConfigureAwait(true))
            {
                return;
            }

            await _page.Client.CallAsync<DeleteTagReply>(LicensePlatesMethods.DeleteTag, new DeleteTagRequest(row.Id, Confirmed: true)).ConfigureAwait(true);
        }

        if (EditingRow?.Id == row.Id)
        {
            EditingRow = null;
        }

        await _page.ReloadTagsAsync().ConfigureAwait(true);
    });

    private bool CanOk() => !IsBusy;

    /// <summary>OK of a Change dialog: the clicked tags; Close of Manage tags.</summary>
    [RelayCommand(CanExecute = nameof(CanOk))]
    private void Ok()
    {
        if (IsChange)
        {
            Change = new TagChange(
                [.. _clicked.Where(c => c.Value == true).Select(c => c.Key)],
                [.. _clicked.Where(c => c.Value == false).Select(c => c.Key)]);
        }

        CloseRequested?.Invoke(this, IsChange && (Change!.Add.Count > 0 || Change.Remove.Count > 0));
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    protected override void OnValidationChanged()
    {
        SaveTagCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanSave));
    }

    private string? TagNameProblem(string name, Guid id)
    {
        if (!IsEditorOpen)
        {
            return null;
        }

        var (_, errors) = Plates.PlateRules.Check(new PlateTag { Id = id, Name = name, Color = (PlateTagColor)Math.Clamp(TagColor, 1, 8) },
            _page.Tags.All.Select(t => new PlateTag { Id = t.Id, Name = t.Name, Color = t.Color }));
        return errors.GetValueOrDefault("name");
    }

    private void Rebuild()
    {
        // Keep the clicks across a reload of the tags (a tag made here shows at once).
        foreach (var row in Rows.Where(r => r.IsChanged))
        {
            _clicked[row.Id] = row.IsChecked;
        }

        var text = SearchText.Trim();
        var rows = _page.Tags.All
            .Where(t => text.Length == 0 || t.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase))
            .Select(t =>
            {
                var row = new TagChoiceRow(t, _haveCount(t.Id), _of);
                if (_clicked.TryGetValue(t.Id, out var state) && state is { } chosen)
                {
                    row.SetChecked(chosen);
                }

                row.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(TagChoiceRow.IsChecked))
                    {
                        _clicked[row.Id] = row.IsChecked;
                    }
                };
                return row;
            });
        Rows.ReplaceAll([.. rows]);
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyText));
        StartNewTagCommand.NotifyCanExecuteChanged();
    }

    private async Task RunAsync(Func<Task> work)
    {
        IsBusy = true;
        ErrorText = null;
        try
        {
            ErrorText = await GuardedRun.RunAsync(work).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static Dictionary<Guid, int> Count(IEnumerable<Guid> ids)
    {
        var counts = new Dictionary<Guid, int>();
        foreach (var id in ids)
        {
            counts[id] = counts.GetValueOrDefault(id) + 1;
        }

        return counts;
    }
}
