using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.LicensePlates.Client;

/// <summary>A plate of the list in the Plates grid.</summary>
public sealed partial class PlateRow : ObservableObject
{
    internal PlateRow(Plate plate, PlateTagStore tags, DateTime now)
    {
        Plate = plate;
        Text = plate.Text;
        Name = plate.Name;
        AllowedFromText = PlateTimes.Text(plate.AllowedFrom);
        AllowedToText = PlateTimes.Text(plate.AllowedTo);
        IsEnded = plate.AllowedTo is { } to && to <= now;
        Tags = tags.Chips(plate.Tags);
    }

    public Plate Plate { get; }

    public string Text { get; }

    public string Name { get; }

    public string AllowedFromText { get; }

    public string AllowedToText { get; }

    /// <summary>Allowed to has passed and the plate is still there: a warning triangle in Allowed to.</summary>
    public bool IsEnded { get; }

    [ObservableProperty] public partial IReadOnlyList<TagChip> Tags { get; private set; }

    public DateTime FromSortKey => Plate.AllowedFrom ?? DateTime.MinValue;

    public DateTime ToSortKey => Plate.AllowedTo ?? DateTime.MaxValue;

    public string TagsSortKey => string.Join(", ", Tags.Select(t => t.Name));

    internal void ApplyTags(PlateTagStore tags)
    {
        Tags = tags.Chips(Plate.Tags);
        OnPropertyChanged(nameof(TagsSortKey));
    }
}

/// <summary>The times of plates: wall-clock times of the server's time zone, shown as entered ("12 Nov 2026 08:00").</summary>
public static class PlateTimes
{
    public static string Text(DateTime? time) => time is { } t ? t.ToString("d MMM yyyy HH:mm", CultureInfo.CurrentCulture) : string.Empty;
}

/// <summary>A choice of the tag filter: All tags, or one tag.</summary>
public sealed record TagFilterChoice(Guid? Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// The Plates tab (docs/spec/license-plate-center.md "Plates"): every plate of the server, read in pages of 5,000 (the first
/// page shows at once), searched and filtered by tag in memory (O(n), no per-row UI work); Add plate, Import, Export, Change
/// tags, Manage tags and Delete. A <c>plates</c> event reads the list again (one read at a time).
/// </summary>
public sealed partial class PlatesViewModel : ObservableObject
{
    private readonly LicensePlatesViewModel _page;
    private List<PlateRow> _all = [];
    private Dictionary<string, PlateRow> _byText = new(StringComparer.Ordinal);
    private bool _reloading;
    private bool _reloadAgain;

    internal PlatesViewModel(LicensePlatesViewModel page)
    {
        _page = page;
        page.Tags.Changed += (_, _) => OnTagsChanged();
        TagFilters = [new TagFilterChoice(null, PageTexts.AllTags)];
        SelectedTagFilter = TagFilters[0];
    }

    public RangeObservableCollection<PlateRow> Rows { get; } = [];

    public IReadOnlyList<PlateRow> Selected { get; private set; } = [];

    [ObservableProperty] public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty] public partial IReadOnlyList<TagFilterChoice> TagFilters { get; private set; }

    [ObservableProperty] public partial TagFilterChoice? SelectedTagFilter { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty), nameof(ShowNoMatch))]
    public partial bool IsLoading { get; private set; } = true;

    /// <summary>Later pages are read (a progress row above the grid).</summary>
    [ObservableProperty] public partial bool IsLoadingMore { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddPlateCommand), nameof(EditCommand), nameof(ImportCommand), nameof(ExportCommand), nameof(ChangeTagsCommand),
        nameof(ManageTagsCommand), nameof(DeleteCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; private set; }

    [ObservableProperty] public partial string Footer { get; private set; } = string.Empty;

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    public bool ShowEmpty => !IsLoading && _all.Count == 0;

    public bool ShowNoMatch => !IsLoading && _all.Count > 0 && Rows.Count == 0;

    public bool MayManage => _page.MayManagePlates;

    /// <summary>Every plate of the list.</summary>
    public int Count => _all.Count;

    public Task ReloadTask { get; private set; } = Task.CompletedTask;

    /// <summary>The change that runs now (tests wait for it).</summary>
    public Task WorkTask { get; private set; } = Task.CompletedTask;

    public void SetSelection(IEnumerable<PlateRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        Selected = [.. rows];
        UpdateFooter();
        RaiseCommands();
    }

    /// <summary>A plate with this text is in the list (the editor's duplicate check, the log's Add to plates).</summary>
    public bool Exists(string text) => _byText.ContainsKey(text);

    public void RequestReload()
    {
        if (_reloading)
        {
            _reloadAgain = true;
            return;
        }

        ReloadTask = ReloadLoopAsync();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedTagFilterChanged(TagFilterChoice? value) => ApplyFilter();

    private bool CanManage() => MayManage && !IsBusy;

    private bool CanManageSelected() => CanManage() && Selected.Count > 0;

    private bool CanEdit() => CanManage() && Selected.Count == 1;

    [RelayCommand(CanExecute = nameof(CanManage))]
    private Task AddPlateAsync() => OpenEditorAsync(null, null);

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task EditAsync() => Selected is [var row] ? OpenEditorAsync(row.Plate, null) : Task.CompletedTask;

    /// <summary>Add to plates of the log: the Add plate dialog with the plate filled in.</summary>
    internal Task AddFromReadAsync(string text) => OpenEditorAsync(null, text);

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task ImportAsync()
    {
        if (_page.Dialogs is not { } dialogs)
        {
            return;
        }

        var import = new ImportViewModel(_page);
        if (await dialogs.ImportAsync(import).ConfigureAwait(true))
        {
            RequestReload();
        }
    }

    private bool CanExport() => !IsBusy && _all.Count > 0;

    [RelayCommand(CanExecute = nameof(CanExport))]
    private Task ExportAsync() => WorkTask = RunAsync(async () =>
    {
        if (_page.Dialogs is not { } dialogs || await dialogs.SaveFileAsync(PageTexts.Export, PageTexts.PlatesFile).ConfigureAwait(true) is not { } target)
        {
            return;
        }

        await using (target.ConfigureAwait(true))
        {
            var download = await _page.Client.DownloadAsync(LicensePlatesMethods.ExportPlates, null).ConfigureAwait(true);
            await using (download.ConfigureAwait(true))
            {
                await download.Content.CopyToAsync(target).ConfigureAwait(true);
            }
        }
    });

    [RelayCommand(CanExecute = nameof(CanManageSelected))]
    private async Task ChangeTagsAsync()
    {
        if (_page.Dialogs is not { } dialogs)
        {
            return;
        }

        var plates = Selected.Select(r => r.Plate).ToList();
        var dialog = TagsDialogViewModel.ForPlates(_page, plates);
        if (!await dialogs.TagsAsync(dialog).ConfigureAwait(true) || dialog.Change is not { } change)
        {
            return;
        }

        await (WorkTask = RunAsync(async () =>
        {
            await _page.Client.CallAsync<CountReply>(LicensePlatesMethods.ChangePlateTags,
                new ChangePlateTagsRequest([.. plates.Select(p => p.Text)], change.Add, change.Remove)).ConfigureAwait(true);
            RequestReload();
            await ReloadTask.ConfigureAwait(true);
        })).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task ManageTagsAsync()
    {
        if (_page.Dialogs is { } dialogs)
        {
            await dialogs.TagsAsync(TagsDialogViewModel.ForManage(_page)).ConfigureAwait(true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanManageSelected))]
    private async Task DeleteAsync()
    {
        var texts = Selected.Select(r => r.Text).ToList();
        if (!await _page.Client.Context.ConfirmAsync(PageTexts.DeletePlatesTitle, PageTexts.DeletePlatesMessage(texts.Count), PageTexts.Delete).ConfigureAwait(true))
        {
            return;
        }

        await (WorkTask = RunAsync(async () =>
        {
            await _page.Client.CallAsync<CountReply>(LicensePlatesMethods.DeletePlates, new PlateTextsRequest(texts)).ConfigureAwait(true);
            RequestReload();
            await ReloadTask.ConfigureAwait(true);
        })).ConfigureAwait(true);
    }

    internal void OnPermissionsChanged()
    {
        OnPropertyChanged(nameof(MayManage));
        RaiseCommands();
    }

    private async Task OpenEditorAsync(Plate? original, string? text)
    {
        if (_page.Dialogs is not { } dialogs)
        {
            return;
        }

        var editor = new PlateEditorViewModel(_page, original, text, t => Exists(t) && t != original?.Text);
        if (await dialogs.EditPlateAsync(editor).ConfigureAwait(true))
        {
            RequestReload();
            await ReloadTask.ConfigureAwait(true);
            if (editor.Saved is { } saved && _byText.TryGetValue(saved.Text, out var row))
            {
                SetSelection([row]);
                OnPropertyChanged(nameof(SelectedRow));
            }
        }
    }

    /// <summary>The one row to select in the grid after a save (the view follows it).</summary>
    public PlateRow? SelectedRow => Selected is [var row] ? row : null;

    private async Task ReloadLoopAsync()
    {
        _reloading = true;
        try
        {
            do
            {
                _reloadAgain = false;
                ErrorText = await GuardedRun.RunAsync(LoadAllAsync).ConfigureAwait(true);
                IsLoading = false;
                IsLoadingMore = false;
            }
            while (_reloadAgain);
        }
        finally
        {
            _reloading = false;
        }
    }

    /// <summary>Every page of the list; the first one is shown at once, the rest when all arrived (one Reset).</summary>
    private async Task LoadAllAsync()
    {
        var now = _page.Time.GetLocalNow().DateTime;
        var plates = new List<PlateRow>();
        string? after = null;
        var first = true;
        do
        {
            var page = await _page.Client.CallAsync<PlatesPage>(LicensePlatesMethods.Plates, new PlatesRequest { After = after, Limit = LicensePlateLimits.MaxPlatePage }).ConfigureAwait(true);
            plates.AddRange(page.Plates.Select(p => new PlateRow(p, _page.Tags, now)));
            after = page.Next;
            if (first && after is not null)
            {
                Set(plates);
                IsLoading = false;
                IsLoadingMore = true;
            }

            first = false;
        }
        while (after is not null);

        Set(plates);
    }

    private void Set(List<PlateRow> plates)
    {
        _all = [.. plates];
        _byText = new Dictionary<string, PlateRow>(_all.Count, StringComparer.Ordinal);
        foreach (var row in _all)
        {
            _byText[row.Text] = row;
        }

        ApplyFilter();
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(Count));
    }

    private void OnTagsChanged()
    {
        var shown = SelectedTagFilter?.Id;
        TagFilters = [new TagFilterChoice(null, PageTexts.AllTags), .. _page.Tags.All.Select(t => new TagFilterChoice(t.Id, t.Name))];
        SelectedTagFilter = TagFilters.FirstOrDefault(f => f.Id == shown) ?? TagFilters[0];
        foreach (var row in _all)
        {
            row.ApplyTags(_page.Tags);
        }
    }

    /// <summary>Search (the plate from its start, without spaces and dashes, or the name) and the tag filter, one pass.</summary>
    private void ApplyFilter()
    {
        var text = SearchText.Trim();
        var plate = Plates.PlateText.Normalize(text);
        var tag = SelectedTagFilter?.Id;
        var selected = Selected;
        Rows.ReplaceAll(text.Length == 0 && tag is null
            ? _all
            : [.. _all.Where(r => (tag is null || r.Plate.Tags.Contains(tag.Value))
                && (text.Length == 0 || (plate.Length > 0 && r.Text.StartsWith(plate, StringComparison.Ordinal)) || r.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase)))]);
        if (selected.Count > 0)
        {
            var kept = new HashSet<string>(selected.Select(s => s.Text), StringComparer.Ordinal);
            Selected = [.. Rows.Where(r => kept.Contains(r.Text))];
        }

        OnPropertyChanged(nameof(ShowNoMatch));
        UpdateFooter();
        RaiseCommands();
    }

    private void UpdateFooter() => Footer = PageTexts.PlatesFooter(Rows.Count, Selected.Count);

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

    private void RaiseCommands()
    {
        AddPlateCommand.NotifyCanExecuteChanged();
        EditCommand.NotifyCanExecuteChanged();
        ImportCommand.NotifyCanExecuteChanged();
        ExportCommand.NotifyCanExecuteChanged();
        ChangeTagsCommand.NotifyCanExecuteChanged();
        ManageTagsCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
    }
}
