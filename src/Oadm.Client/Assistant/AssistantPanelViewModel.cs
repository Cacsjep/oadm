using System.Collections.ObjectModel;
using System.Collections.Specialized;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Grpc.Core;

using Oadm.Client.Api;
using Oadm.Client.Devices;
using Oadm.Client.Dialogs;
using Oadm.Client.Shell;
using Oadm.Client.Tasks;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Plugins;

namespace Oadm.Client.Assistant;

/// <summary>A file attached to the next message (uploaded through FileService.Upload); Remove takes it off again.</summary>
public sealed class AssistantAttachmentItem(string fileId, string name, long size)
{
    public string FileId { get; } = fileId;

    public string Name { get; } = name;

    public long Size { get; } = size;

    public string SizeText => Oadm.Sdk.Formatting.ByteSize.Text(Size);
}

/// <summary>
/// The Assistant panel (docs/spec/assistant.md "The panel"): a chat with the model on the right of every page, opened by
/// the title bar button. A conversation id per open panel and New chat (ended on close, New chat and sign out: nothing is
/// kept). Each message carries the model of the Model picker (the provider's list from ListModels, editable without one;
/// the choice kept per user on this client), the device grid's selection (at most 10,000) and the attached files; the answer streams as
/// items (Markdown text, tool lines, preview cards, pictures, device tables, notices). Run of a preview is the user's click
/// only. Lives on the UI thread.
/// </summary>
public sealed partial class AssistantPanelViewModel : ObservableObject
{
    private static readonly FileType AnyFile = new("Files", "*", "application/octet-stream");

    private readonly IOadmApi _api;
    private readonly UserSession? _session;
    private readonly AssistantAvailability _availability;
    private readonly DevicesViewModel? _devices;
    private readonly TaskStore? _tasks;
    private readonly IDialogService? _dialogs;
    private readonly IClipboardService? _clipboard;
    private readonly Infrastructure.IClientSettingsStore? _settings;
    private int _modelRead;
    private readonly HashSet<string> _notices = new(StringComparer.Ordinal);
    private CancellationTokenSource? _chat;
    private AssistantAnswerItem? _answer;
    private readonly Infrastructure.IUiDispatcher? _ui;
    private readonly TimeProvider _time;
    private long _lastFlush = long.MinValue;
    private ITimer? _flushTimer;

    /// <summary>An answer that streams shows its new text at most this often (one Markdown render), and once at the end.</summary>
    public static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(100);

    public AssistantPanelViewModel(IOadmApi api, AssistantAvailability availability, UserSession? session = null, DevicesViewModel? devices = null,
        TaskStore? tasks = null, IDialogService? dialogs = null, IClipboardService? clipboard = null, ServerConnection? connection = null,
        Infrastructure.IClientSettingsStore? settings = null, Infrastructure.IUiDispatcher? ui = null, TimeProvider? time = null)
    {
        _settings = settings;
        _ui = ui;
        _time = time ?? TimeProvider.System;
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(availability);
        _api = api;
        _availability = availability;
        _session = session;
        _devices = devices;
        _tasks = tasks;
        _dialogs = dialogs;
        _clipboard = clipboard;
        availability.Changed += (_, _) => OnAvailabilityChanged();
        if (session is not null)
        {
            session.PermissionsChanged += (_, _) => OnAvailabilityChanged();
            session.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(UserSession.IsLoggedIn) && !session.IsLoggedIn)
                {
                    Close(); // sign out: the chat is forgotten
                }

                if (e.PropertyName is nameof(UserSession.IsLoggedIn) or nameof(UserSession.UserName))
                {
                    OnAvailabilityChanged();
                }
            };
        }

        if (connection is not null)
        {
            connection.Connected += (_, _) => _ = RefreshAsync(CancellationToken.None);
        }

        if (devices is not null)
        {
            devices.SelectedDevices.CollectionChanged += OnSelectionChanged;
        }

        Attachments.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasAttachments));
            OnPropertyChanged(nameof(AttachTip));
            AttachFileCommand.NotifyCanExecuteChanged();
        };
        Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsEmpty));
            CopyChatCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(ShowJumpToLatest));
            OnPreviewsChanged();
            UpdateLastActions();
        };
        InitializeExtras();
    }

    /// <summary>The conversation, the user's messages and the answers (virtualized list).</summary>
    public RangeObservableCollection<AssistantItem> Items { get; } = [];

    /// <summary>Files attached to the next message.</summary>
    public ObservableCollection<AssistantAttachmentItem> Attachments { get; } = [];

    public bool HasAttachments => Attachments.Count > 0;

    /// <summary>A preview card waits for Run: its Run is the panel's main button, Send turns secondary.</summary>
    public bool HasReadyPreview => Items.Any(i => i is AssistantPreviewItem { IsReady: true } or AssistantDocsOfferItem { IsReady: true });

    /// <summary>
    /// Only the newest card that waits (Run of a preview, Look up online of an offer) has the accent button; Send turns
    /// secondary while one waits.
    /// </summary>
    private void OnPreviewsChanged()
    {
        AssistantItem? newest = null;
        for (int i = Items.Count - 1; i >= 0 && newest is null; i--)
        {
            if (Items[i] is AssistantPreviewItem { IsReady: true } or AssistantDocsOfferItem { IsReady: true })
            {
                newest = Items[i];
            }
        }

        foreach (var item in Items)
        {
            switch (item)
            {
                case AssistantPreviewItem preview:
                    preview.IsNewestReady = ReferenceEquals(preview, newest);
                    break;
                case AssistantDocsOfferItem offer:
                    offer.IsNewestReady = ReferenceEquals(offer, newest);
                    break;
            }
        }

        OnPropertyChanged(nameof(HasReadyPreview));
        OnPropertyChanged(nameof(SendIsQuiet));
    }

    /// <summary>The empty panel shows the start suggestions.</summary>
    public bool IsEmpty => Items.Count == 0;

    /// <summary>The live status line shows (Thinking, or the tool that runs): from Send until text streams, again with each tool.</summary>
    [ObservableProperty] public partial bool IsThinking { get; private set; }

    /// <summary>
    /// The list follows new content (the view sets it: the user is at the end, or within 40 px of it). Scrolled up, it stays
    /// where the user is and Go to the latest shows.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowJumpToLatest))]
    public partial bool IsFollowing { get; set; } = true;

    /// <summary>The round Go to the latest button above the message box.</summary>
    public bool ShowJumpToLatest => !IsFollowing && !IsEmpty;

    /// <summary>The view scrolls to the end (Go to the latest, Send).</summary>
    public event EventHandler? ScrollToEndRequested;

    /// <summary>The view puts the focus into the message box with the caret at the end (Edit).</summary>
    public event EventHandler? FocusMessageRequested;

    /// <summary>Files are dragged over the panel: the drop hint shows.</summary>
    [ObservableProperty] public partial bool IsDragOver { get; set; }

    /// <summary>Go to the latest: the list goes to the end and follows again.</summary>
    [RelayCommand]
    private void JumpToLatest()
    {
        IsFollowing = true;
        ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A card waits for Run: the send arrow is not the accent (one accent per view).</summary>
    public bool SendIsQuiet => HasReadyPreview;

    /// <summary>The problem of the message box, directly below it: the selection limit, else the model's problem.</summary>
    public string? ComposerError => SelectionError ?? ModelError;

    public bool HasComposerError => ComposerError is not null;

    /// <summary>The user's message that Edit put back into the box: sending replaces that exchange. An empty box drops it.</summary>
    private AssistantUserItem? _editing;

    private int _pastedImages;

    /// <summary>The folder that holds the own folder of each pasted picture: the system's temp folder; tests use their own.</summary>
    internal string PasteRoot { get; set; } = Path.GetTempPath();

    /// <summary>Try again under the end of the last answer, Edit on the last message of the user; nothing while an answer streams.</summary>
    private void UpdateLastActions()
    {
        int userAt = -1;
        for (int i = Items.Count - 1; i >= 0 && userAt < 0; i--)
        {
            if (Items[i] is AssistantUserItem)
            {
                userAt = i;
            }
        }

        AssistantItem? retry = null;
        if (!IsStreaming && userAt >= 0)
        {
            if (Items[^1] is AssistantNoticeItem notice && (notice.IsError || notice.IsStopped))
            {
                retry = notice;
            }
            else
            {
                for (int i = Items.Count - 1; i > userAt && retry is null; i--)
                {
                    if (Items[i] is AssistantAnswerItem answer)
                    {
                        retry = answer;
                    }
                }
            }
        }

        for (int i = 0; i < Items.Count; i++)
        {
            var item = Items[i];
            item.ShowsRetry = ReferenceEquals(item, retry);
            if (item is AssistantUserItem user)
            {
                user.CanEdit = !IsStreaming && i == userAt;
            }
        }
    }

    private AssistantUserItem? LastUser => Items.LastOrDefault(i => i is AssistantUserItem) as AssistantUserItem;

    /// <summary>The title bar button shows: the assistant is on and the role may use it.</summary>
    public bool IsAvailable => _availability.IsOn && (_session is null || (_session.IsLoggedIn && _session.Has(HostPermissionKeys.AssistantUse)));

    /// <summary>The conversation of the open panel; null while it is closed.</summary>
    [ObservableProperty] public partial string? ConversationId { get; private set; }

    [ObservableProperty] public partial bool IsOpen { get; private set; }

    /// <summary>An answer streams: Stop instead of Send.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(StopCommand), nameof(SendSuggestionCommand), nameof(LookUpCommand))]
    [NotifyPropertyChangedFor(nameof(SendTip))]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand), nameof(EditCommand))]
    public partial bool IsStreaming { get; private set; }

    partial void OnIsStreamingChanged(bool value) => UpdateLastActions();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(AttachFileCommand), nameof(SendSuggestionCommand), nameof(LookUpCommand), nameof(RetryCommand))]
    [NotifyPropertyChangedFor(nameof(SendTip))]
    public partial bool IsUploading { get; private set; }

    /// <summary>The message box (Enter sends, Shift+Enter a new line, at most 8,000 characters).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyPropertyChangedFor(nameof(SendTip))]
    public partial string Draft { get; set; } = string.Empty;

    partial void OnDraftChanged(string value)
    {
        if (value.Length == 0)
        {
            _editing = null; // the edited text is gone: the next message is a new one
        }
    }

    /// <summary>The provider's models for the picker (empty when it gives none: the picker takes a typed name).</summary>
    [ObservableProperty] public partial IReadOnlyList<string> Models { get; private set; } = [];

    /// <summary>The provider gives no list: the user types the model.</summary>
    [ObservableProperty] public partial bool IsModelEditable { get; private set; }

    /// <summary>ListModels runs: a ring in place of the picker.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SendTip))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(SendSuggestionCommand), nameof(LookUpCommand), nameof(RetryCommand))]
    public partial bool IsLoadingModels { get; private set; }

    /// <summary>The model every message carries; kept per user on this client. Changing it keeps the conversation.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SendTip))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(SendSuggestionCommand), nameof(LookUpCommand), nameof(RetryCommand))]
    public partial string Model { get; set; } = string.Empty;

    /// <summary>A problem of the model, directly below the picker ("Choose a model.", the server's refusal, the list's error).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelErrors), nameof(ComposerError), nameof(HasComposerError))]
    public partial string? ModelError { get; private set; }

    /// <summary><see cref="ModelError"/> as the picker's data validation errors (red border, the text below it).</summary>
    public IReadOnlyList<object>? ModelErrors => ModelError is { } error ? [error] : null;

    partial void OnModelChanged(string value)
    {
        ModelError = null;
        if (value.Trim().Length > 0 && _settings is { } store && ModelKey is { } key && !IsLoadingModels)
        {
            store.Current.AssistantModels[key] = value.Trim();
            store.Save();
        }
    }

    /// <summary>"user@server": the remembered model is the user's, on this client.</summary>
    private string? ModelKey => _session is { IsLoggedIn: true } session ? $"{session.UserName}@{session.ServerAddress}".ToLowerInvariant() : null;

    /// <summary>Reads the provider's models (on open): the remembered choice when it is still offered, else the suggested one, else the first.</summary>
    public async Task LoadModelsAsync(CancellationToken ct)
    {
        var read = ++_modelRead;
        IsLoadingModels = true;
        try
        {
            var list = await _api.ListAssistantModelsAsync(ct).ConfigureAwait(true);
            if (read != _modelRead)
            {
                return;
            }

            Models = [.. list.Models];
            IsModelEditable = list.Editable || list.Models.Count == 0;
            var remembered = ModelKey is { } key && _settings?.Current.AssistantModels.GetValueOrDefault(key) is { Length: > 0 } kept ? kept : null;
            Model = remembered is not null && (IsModelEditable || Models.Contains(remembered, StringComparer.Ordinal)) ? remembered
                : list.Suggested.Length > 0 ? list.Suggested
                : Models.Count > 0 ? Models[0] : remembered ?? string.Empty;
            ModelError = null;
        }
        catch (RpcException ex)
        {
            if (read == _modelRead)
            {
                ModelError = ex.Status.Detail;
            }
        }
        finally
        {
            if (read == _modelRead)
            {
                IsLoadingModels = false;
            }
        }
    }

    /// <summary>The picture shown larger over the window; null: none.</summary>
    [ObservableProperty] public partial AssistantPictureItem? Enlarged { get; private set; }

    /// <summary>The full-size bitmap of <see cref="Enlarged"/>, decoded on Enlarge and released on close.</summary>
    [ObservableProperty] public partial Avalonia.Media.Imaging.Bitmap? EnlargedPicture { get; private set; }

    /// <summary>"Copied." after Copy, or a problem with an attachment.</summary>
    [ObservableProperty] public partial string? Message { get; private set; }

    [ObservableProperty] public partial bool MessageIsError { get; private set; }

    /// <summary>"12 devices selected" below the box; empty without a selection.</summary>
    public string SelectionText => AssistantTexts.SelectionCount(SelectionCount);

    public bool HasSelection => SelectionCount > 0;

    public int SelectionCount => _devices?.SelectedDevices.Count ?? 0;

    /// <summary>More than 10,000 selected: the message below the box, Send off.</summary>
    public string? SelectionError => SelectionCount > AssistantTexts.MaxSelection ? AssistantTexts.TooManySelected : null;

    public bool HasSelectionError => SelectionError is not null;

    public static int MaxMessageLength => AssistantTexts.MaxMessageLength;

    /// <summary>The send arrow's tooltip: why it is off, else "Send".</summary>
    public string SendButtonTip => SendTip ?? AssistantTexts.Send;

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(SendTip))
        {
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(SendButtonTip)));
        }
        else if (e.PropertyName == nameof(AttachTip))
        {
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(AttachButtonTip)));
        }
    }

    /// <summary>Why Send is off.</summary>
    public string? SendTip => IsStreaming ? AssistantTexts.WaitForAnswer
        : IsUploading ? AssistantTexts.WaitForUpload
        : IsLoadingModels ? AssistantTexts.LoadingModels
        : SelectionError ?? (Draft.Trim().Length == 0 ? AssistantTexts.EnterMessage : Model.Trim().Length == 0 ? AssistantTexts.ChooseModel : null);

    /// <summary>"95,333 tokens today" next to Send: the server's tokens of today, read when the panel opens and after each answer.</summary>
    [ObservableProperty] public partial string? UsageText { get; private set; }

    private async Task RefreshUsageAsync()
    {
        try
        {
            UsageText = AssistantTexts.TokensToday((await _api.GetAssistantSettingsAsync(CancellationToken.None).ConfigureAwait(true)).UsedToday);
        }
        catch (Exception ex) when (ex is RpcException or NotSupportedException)
        {
            UsageText = null; // no number rather than a wrong one
        }
    }

    /// <summary>The server has it on and the role may use it (read on every connect).</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            _availability.Apply(await _api.GetAssistantSettingsAsync(ct).ConfigureAwait(true));
        }
        catch (RpcException)
        {
            _availability.Apply(null); // an older server, or none: no button
        }
    }

    /// <summary>The title bar button: opens the panel with a new conversation, or closes it.</summary>
    [RelayCommand]
    private void Toggle()
    {
        if (IsOpen)
        {
            Close();
        }
        else
        {
            Open();
        }
    }

    public void Open()
    {
        if (IsOpen || !IsAvailable)
        {
            return;
        }

        ConversationId = Guid.NewGuid().ToString();
        IsOpen = true;
        _ = LoadModelsAsync(CancellationToken.None);
        _ = RefreshUsageAsync();
    }

    /// <summary>Closes the panel and forgets the conversation (End on the server).</summary>
    [RelayCommand]
    public void Close()
    {
        EndConversation();
        IsOpen = false;
        ConversationId = null;
    }

    /// <summary>A fresh conversation: the old one ends.</summary>
    [RelayCommand]
    private void NewChat()
    {
        EndConversation();
        ConversationId = Guid.NewGuid().ToString();
        IsFollowing = true;
    }

    private bool CanSend => !IsStreaming && !IsUploading && !IsLoadingModels && SelectionError is null && Draft.Trim().Length > 0 && Model.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private Task SendAsync() => SendTextAsync(Draft, [.. Attachments], _editing, Mentioned);

    private bool CanRetry(AssistantItem? item) => !IsStreaming && !IsUploading && !IsLoadingModels && SelectionError is null && Model.Trim().Length > 0;

    /// <summary>Try again: the last message again (its files too), in place of its answer.</summary>
    [RelayCommand(CanExecute = nameof(CanRetry))]
    private Task RetryAsync(AssistantItem? item) => LastUser is { } user ? SendTextAsync(user.Text, user.Files, user, user.Mentioned) : Task.CompletedTask;

    private bool CanEdit(AssistantUserItem? user) => !IsStreaming;

    /// <summary>Edit: the last message back in the box with its files; sending it replaces that exchange.</summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void Edit(AssistantUserItem? user)
    {
        if (IsStreaming || user is null || !ReferenceEquals(user, LastUser))
        {
            return;
        }

        Attachments.Clear();
        foreach (var file in user.Files)
        {
            Attachments.Add(file);
        }

        Mentioned = user.Mentioned;
        Draft = user.Text;
        _editing = user;
        Message = null;
        FocusMessageRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Up arrow in an empty box: the last message back for editing. False: nothing to edit (the key does its usual work).</summary>
    public bool EditLast()
    {
        if (IsStreaming || Draft.Length > 0 || LastUser is not { } user)
        {
            return false;
        }

        Edit(user);
        return true;
    }

    private bool CanSendSuggestion(string? text) => !IsStreaming && !IsUploading && !IsLoadingModels && SelectionError is null && Model.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanSendSuggestion))]
    private Task SendSuggestionAsync(string? text) => string.IsNullOrWhiteSpace(text) ? Task.CompletedTask : SendTextAsync(text, [], null, Mentioned);

    private bool CanLookUp(AssistantDocsOfferItem? offer) => offer is { IsReady: true } && CanSendSuggestion(AssistantTexts.LookUpMessage);

    /// <summary>
    /// Look up online: the user's yes to the offer (docs/spec/assistant.md "Looking on axis.com"). Only this click sends
    /// the offer's id, so only the next answer may read axis.com.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanLookUp))]
    private Task LookUpAsync(AssistantDocsOfferItem? offer) =>
        offer is { IsReady: true } ? SendTextAsync(AssistantTexts.LookUpMessage, [], null, [], offer) : Task.CompletedTask;

    /// <summary>Not now: nothing is sent; the offer ends with the next message on the server.</summary>
    [RelayCommand]
    private void DeclineLookUp(AssistantDocsOfferItem? offer)
    {
        if (offer is { IsReady: true })
        {
            offer.MarkDeclined();
            OnPreviewsChanged();
        }
    }

    /// <summary>
    /// Sends a message with its files. <paramref name="replace"/> (Try again, Edit): that message and everything after it
    /// leave the list in one step, and the server drops them too when it had them (<c>replace_last</c>).
    /// </summary>
    private async Task SendTextAsync(string text, IReadOnlyList<AssistantAttachmentItem> files, AssistantUserItem? replace, IReadOnlyList<string> mentioned,
        AssistantDocsOfferItem? lookUp = null)
    {
        var conversation = ConversationId;
        text = text.Trim();
        if (conversation is null || text.Length == 0 || IsStreaming)
        {
            return;
        }

        if (text.Length > AssistantTexts.MaxMessageLength)
        {
            text = text[..AssistantTexts.MaxMessageLength];
        }

        if (Model.Trim().Length == 0)
        {
            ModelError = AssistantTexts.ChooseModel;
            return;
        }

        var message = new AssistantMessage { ConversationId = conversation, Text = text, Model = Model.Trim(), DocsOfferId = lookUp?.OfferId ?? string.Empty };

        // Any message ends the offers that still wait (the server forgets them); Look up online takes its own.
        lookUp?.MarkAccepted();
        foreach (var waiting in Items.OfType<AssistantDocsOfferItem>().Where(o => o.IsReady).ToList())
        {
            waiting.MarkDeclined();
        }

        OnPreviewsChanged();
        if (_devices is not null && SelectionError is null)
        {
            message.SelectedDeviceIds.AddRange(_devices.SelectedDevices.Select(d => d.Id));
        }

        message.Attachments.AddRange(files.Select(a => new AssistantAttachment { FileId = a.FileId, Name = a.Name, Size = a.Size }));
        message.MentionedDeviceIds.AddRange(mentioned);
        var mentionText = mentioned.Count == 0 ? null : ReferenceEquals(mentioned, Mentioned) ? MentionText : replace?.MentionText;
        if (replace is not null && Items.IndexOf(replace) is var at and >= 0)
        {
            message.ReplaceLast = replace.Reached;
            RemoveFrom(at);
        }

        _editing = null;
        var user = new AssistantUserItem(text, [.. files], [.. mentioned], mentionText);
        Mentioned = [];
        var attached = files.ToList();
        if (Items.Any(i => i is AssistantFollowUpsItem))
        {
            Items.RemoveAll(i => i is AssistantFollowUpsItem); // the next questions of an older answer go (one step)
        }

        Items.Add(user);
        _user = user;
        _steps = new AssistantStepsItem();
        _answerFailed = false;
        _cards = null;
        Attachments.Clear();
        Draft = string.Empty;
        Message = null;
        _answer = null;
        var chat = new CancellationTokenSource();
        _chat = chat;
        IsStreaming = true;
        JumpToLatest(); // a new message: the list goes to its end and follows
        ShowThinking();
        try
        {
            try
            {
                await StreamAsync(message, user, chat).ConfigureAwait(true);
            }
            catch (RpcException ex) when (message.ReplaceLast && ex.StatusCode == StatusCode.FailedPrecondition && ex.Status.Detail == AssistantTexts.NothingToRetry
                && ReferenceEquals(_chat, chat))
            {
                message.ReplaceLast = false; // the server forgot the conversation meanwhile (30 minutes): a new message
                await StreamAsync(message, user, chat).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (chat.IsCancellationRequested)
        {
            // closed or New chat: nothing to show
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && chat.IsCancellationRequested)
        {
            // closed or New chat
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.InvalidArgument && ex.Trailers.GetValue("oadm-field") == "model")
        {
            // The model was refused: its error below the picker, the message back in the box to send again.
            if (ReferenceEquals(_chat, chat))
            {
                Items.Remove(user);
                Mentioned = user.Mentioned;
                Draft = text;
                foreach (var file in attached)
                {
                    Attachments.Add(file);
                }

                ModelError = ex.Status.Detail;
            }
        }
        catch (RpcException ex)
        {
            if (ReferenceEquals(_chat, chat))
            {
                _answerFailed = true;
                Add(new AssistantNoticeItem(ex.Status.Detail, isError: true));
                if (ex.StatusCode is StatusCode.FailedPrecondition or StatusCode.Unimplemented or StatusCode.PermissionDenied)
                {
                    _ = RefreshAsync(CancellationToken.None); // turned off meanwhile: the button follows
                }
            }
        }
        finally
        {
            if (ReferenceEquals(_chat, chat))
            {
                EndThinking();
                FlushAnswer(); // the end: whatever is left shows once
                FoldSteps();
                _chat = null;
                _answer = null;
                IsStreaming = false;
            }

            chat.Dispose();
        }
    }

    private async Task StreamAsync(AssistantMessage message, AssistantUserItem user, CancellationTokenSource chat)
    {
        await foreach (var e in _api.AssistantChatAsync(message, chat.Token).ConfigureAwait(true))
        {
            if (!ReferenceEquals(_chat, chat))
            {
                break; // New chat or closed meanwhile
            }

            user.Reached = true;
            Apply(e);
        }
    }

    /// <summary>The entries from <paramref name="index"/> to the end leave the list in one step (Try again, Edit).</summary>
    private void RemoveFrom(int index)
    {
        var gone = new HashSet<AssistantItem>(ReferenceEqualityComparer.Instance);
        for (int i = index; i < Items.Count; i++)
        {
            gone.Add(Items[i]);
            (Items[i] as IDisposable)?.Dispose();
        }

        Items.RemoveAll(gone.Contains);
    }

    /// <summary>The live status line while the answer waits (Thinking, then the running tool's line); null while text streams.</summary>
    private AssistantStatusItem? _thinking;

    /// <summary>The message the answer belongs to and the tool lines of that answer (folded into Steps at its end).</summary>
    private AssistantUserItem? _user;
    private AssistantStepsItem? _steps;

    /// <summary>The first card since the answer's last text: the next text shows above it (answer first, cards as proof).</summary>
    private AssistantItem? _cards;

    /// <summary>The answer ended with an error: its steps stay open.</summary>
    private bool _answerFailed;

    private void ShowThinking(string? text = null)
    {
        if (_thinking is null)
        {
            _thinking = new AssistantStatusItem();
            Items.Add(_thinking);
        }

        _thinking.Text = text ?? AssistantTexts.Thinking;
        IsThinking = true;
    }

    /// <summary>Streamed text (or the end): the status line goes; a tool brings it back.</summary>
    private void EndThinking()
    {
        if (_thinking is { } thinking)
        {
            _thinking = null;
            Items.Remove(thinking);
            IsThinking = false;
        }
    }

    /// <summary>
    /// The end of the answer: its tool lines become one row "Steps (N)" at its top (below the user's message and the notice
    /// where the data goes), open only when the answer failed.
    /// </summary>
    private void FoldSteps()
    {
        var steps = _steps;
        _steps = null;
        if (steps is not { Count: > 0 } || _user is not { } user || Items.IndexOf(user) is not (>= 0 and var at))
        {
            return;
        }

        int index = at + 1;
        while (index < Items.Count && Items[index] is AssistantNoticeItem { IsError: false, IsStopped: false })
        {
            index++;
        }

        steps.IsExpanded = _answerFailed;
        Items.Insert(index, steps);
    }

    /// <summary>One event of the answer into the list: text grows the current answer part, everything else ends it.</summary>
    private void Apply(AssistantEvent e)
    {
        switch (e.KindCase)
        {
            case AssistantEvent.KindOneofCase.Text:
                EndThinking(); // the text itself shows that the answer runs
                if (_answer is null)
                {
                    _answer = new AssistantAnswerItem();
                    // The finding first, its cards below as proof: above the cards that came since the last text.
                    int at = _cards is { } first && Items.IndexOf(first) is >= 0 and var index ? index : Items.Count;
                    Items.Insert(at, _answer);
                    _cards = null;
                }

                _answer.Append(e.Text);
                FlushSoon();
                return;
            case AssistantEvent.KindOneofCase.Tool:
                FlushAnswer();
                _answer = null;
                _steps?.Add(new AssistantToolItem(e.Tool.Text, e.Tool.Failed));
                ShowThinking(e.Tool.Text); // one live line, at the end
                return;
            case AssistantEvent.KindOneofCase.Sources:
                if (e.Sources.Chips.Count > 0)
                {
                    Add(new AssistantSourcesItem([.. e.Sources.Chips]));
                }

                return;
            case AssistantEvent.KindOneofCase.FollowUps:
                if (e.FollowUps.Questions.Count > 0)
                {
                    Add(new AssistantFollowUpsItem([.. e.FollowUps.Questions.Take(3)]));
                }

                return;
            case AssistantEvent.KindOneofCase.Preview:
                Add(new AssistantPreviewItem(e.Preview));
                return;
            case AssistantEvent.KindOneofCase.File:
                Add(new AssistantFileItem(e.File));
                return;
            case AssistantEvent.KindOneofCase.DocsOffer:
                Add(new AssistantDocsOfferItem(e.DocsOffer));
                return;
            case AssistantEvent.KindOneofCase.Picture:
                Add(new AssistantPictureItem(e.Picture.DeviceId, e.Picture.DeviceName, e.Picture.Jpeg.ToByteArray()));
                return;
            case AssistantEvent.KindOneofCase.Table:
                Add(new AssistantTableItem(e.Table) { RowChosen = row => SelectInGrid([row.DeviceId]), ShowsLabel = !AnswerHasTable() });
                return;
            case AssistantEvent.KindOneofCase.Notice:
                if (_notices.Add(e.Notice))
                {
                    Add(new AssistantNoticeItem(e.Notice)); // where the data goes: above the status line
                }

                return;
            case AssistantEvent.KindOneofCase.Done:
                _ = RefreshUsageAsync();
                if (e.Done.Error.Length > 0)
                {
                    _answerFailed = true;
                    Add(new AssistantNoticeItem(e.Done.Error, isError: true));
                }
                else if (e.Done.Stopped)
                {
                    Add(new AssistantNoticeItem(AssistantTexts.Stopped, isStopped: true));
                }

                return;
            default:
                return;
        }
    }

    /// <summary>
    /// A new entry ends the current answer part; while the status line shows it stays last (the entry goes above it). A card
    /// (table, plan, file, picture) starts or joins the run of cards the next text goes above; any other entry ends that run.
    /// </summary>
    private void Add(AssistantItem item)
    {
        FlushAnswer();
        _answer = null;
        if (item is AssistantTableItem or AssistantPreviewItem or AssistantFileItem or AssistantPictureItem)
        {
            _cards ??= item;
        }
        else
        {
            _cards = null;
        }

        if (_thinking is not null && Items.Count > 0 && ReferenceEquals(Items[^1], _thinking))
        {
            Items.Insert(Items.Count - 1, item);
        }
        else
        {
            Items.Add(item);
        }
    }

    /// <summary>The answer that streams already shows a device table (only the first one has the label RESULTS).</summary>
    private bool AnswerHasTable()
    {
        for (int i = Items.Count - 1; i >= 0 && !ReferenceEquals(Items[i], _user); i--)
        {
            if (Items[i] is AssistantTableItem)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Streamed text shows at most every <see cref="FlushInterval"/>: at once when the last flush is older, else by a timer
    /// (on the UI thread); every other entry and the end of the answer flush too.
    /// </summary>
    private void FlushSoon()
    {
        long now = _time.GetTimestamp();
        if (_lastFlush == long.MinValue || _time.GetElapsedTime(_lastFlush, now) >= FlushInterval)
        {
            FlushAnswer();
            return;
        }

        if (_flushTimer is null && _ui is { } ui)
        {
            _flushTimer = _time.CreateTimer(_ => ui.Post(FlushAnswer), null, FlushInterval - _time.GetElapsedTime(_lastFlush, now), Timeout.InfiniteTimeSpan);
        }
    }

    private void FlushAnswer()
    {
        _flushTimer?.Dispose();
        _flushTimer = null;
        if (_answer is { HasPending: true } answer)
        {
            answer.Flush();
            _lastFlush = _time.GetTimestamp();
        }
    }

    private bool CanStop => IsStreaming;

    /// <summary>Ends the answer that streams; the conversation stays (its Chat ends with done.stopped).</summary>
    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        if (ConversationId is not { } conversation)
        {
            return;
        }

        try
        {
            await _api.AssistantStopAsync(conversation, CancellationToken.None).ConfigureAwait(true);
        }
        catch (RpcException ex)
        {
            _chat?.Cancel(); // the server cannot be told: at least the panel stops listening
            ShowMessage(ex.Status.Detail, isError: true);
        }
    }

    /// <summary>Runs a preview: only this click runs it (rule 2); the card then follows the tasks.</summary>
    [RelayCommand]
    private async Task RunPreviewAsync(AssistantPreviewItem? preview)
    {
        if (preview is not { IsReady: true } || ConversationId is not { } conversation)
        {
            return;
        }

        if (!preview.CanRun)
        {
            foreach (var secret in preview.Secrets)
            {
                secret.ShowError(); // the empty field says so below itself
            }

            return;
        }

        preview.MarkStarting();
        OnPreviewsChanged();
        try
        {
            // The card's secrets go with Run straight to the server, never into the chat (docs/spec/mcp.md "Secrets").
            var request = new AssistantRunRequest { ConversationId = conversation, Handle = preview.Handle };
            request.Secrets.Add(preview.SecretValues());
            var reply = await _api.AssistantRunAsync(request, CancellationToken.None).ConfigureAwait(true);
            preview.ClearSecrets();
            preview.Follow([.. reply.TaskIds], _tasks);
            var at = Items.IndexOf(preview);
            foreach (var file in reply.Files)
            {
                // A file the run made (Back up now): a file card with Save right below its preview card.
                var card = new AssistantFileItem(file);
                if (at >= 0)
                {
                    Items.Insert(++at, card);
                }
                else
                {
                    Add(card);
                }
            }

            if (reply.Secret is { FileRef.Length: > 0 } secret)
            {
                Add(new AssistantSecretItem(secret)); // a new access key: Copy fetches it once, it never enters the chat
            }
        }
        catch (RpcException ex) when (preview.HasSecrets && ex.StatusCode is StatusCode.FailedPrecondition or StatusCode.InvalidArgument
            && ex.Trailers.GetValue(Contracts.Assistant.AssistantTexts.FieldTrailer) is { Length: > 0 } field)
        {
            preview.MarkSecretRefused(field, ex.Status.Detail); // the card stays: the error below its field
            OnPreviewsChanged();
        }
        catch (RpcException ex)
        {
            preview.ClearSecrets();
            preview.MarkRefused(ex.Status.Detail);
        }
    }

    /// <summary>A file card's Save: where to keep it, then the file fetched now with the user's rights (never through the model).</summary>
    [RelayCommand]
    private async Task SaveFileAsync(AssistantFileItem? file)
    {
        if (file is null or { IsSaving: true } || ConversationId is not { } conversation || _dialogs is null)
        {
            return;
        }

        var extension = Path.GetExtension(file.Name).TrimStart('.');
        var type = new Dialogs.FileType(extension.Length > 0 ? extension.ToUpperInvariant() + " file" : "File", extension.Length > 0 ? extension : "*",
            file.ContentType.Length > 0 ? file.ContentType : "application/octet-stream");
        var target = await _dialogs.CreateFileAsync(AssistantTexts.SaveFileTitle, file.Name, type).ConfigureAwait(true);
        if (target is null)
        {
            return;
        }

        file.IsSaving = true;
        file.HasFailed = false;
        file.StatusText = AssistantTexts.Saving;
        var saved = false;
        try
        {
            await using (target.Content.ConfigureAwait(true))
            {
                var download = await _api.AssistantDownloadAsync(conversation, file.FileRef, CancellationToken.None).ConfigureAwait(true);
                await using (download.ConfigureAwait(true))
                {
                    await download.Content.CopyToAsync(target.Content).ConfigureAwait(true);
                }
            }

            saved = true;
            file.IsSaved = true;
            file.StatusText = AssistantTexts.FileSaved;
        }
        catch (RpcException ex)
        {
            file.HasFailed = true;
            file.StatusText = ex.Status.Detail;
        }
        catch (IOException ex)
        {
            file.HasFailed = true;
            file.StatusText = ex.Message;
        }
        finally
        {
            file.IsSaving = false;
            if (!saved && target.Delete is { } delete)
            {
                await delete().ConfigureAwait(true); // never a cut file
            }
        }
    }

    /// <summary>A secret card's Copy: the secret fetched now, once, straight to the clipboard (never shown, kept or in the chat).</summary>
    [RelayCommand]
    private async Task CopySecretAsync(AssistantSecretItem? secret)
    {
        if (secret is not { CanCopy: true } || ConversationId is not { } conversation || _clipboard is null)
        {
            return;
        }

        secret.IsCopying = true;
        secret.HasFailed = false;
        try
        {
            var download = await _api.AssistantDownloadAsync(conversation, secret.FileRef, CancellationToken.None).ConfigureAwait(true);
            string text;
            await using (download.ConfigureAwait(true))
            {
                using var reader = new StreamReader(download.Content, System.Text.Encoding.UTF8);
                text = await reader.ReadToEndAsync().ConfigureAwait(true);
            }

            if (await _clipboard.SetTextAsync(text).ConfigureAwait(true))
            {
                secret.IsCopied = true;
                secret.StatusText = AssistantTexts.Copied;
            }
        }
        catch (RpcException ex)
        {
            secret.HasFailed = true;
            secret.StatusText = ex.Status.Detail;
        }
        catch (IOException ex)
        {
            secret.HasFailed = true;
            secret.StatusText = ex.Message;
        }
        finally
        {
            secret.IsCopying = false;
        }
    }

    [RelayCommand]
    private void CancelPreview(AssistantPreviewItem? preview)
    {
        if (preview is { IsReady: true })
        {
            preview.MarkCancelled(); // the handle simply ends on the server
            OnPreviewsChanged();
        }
    }

    /// <summary>The tasks pane of the Devices page (a started preview's link).</summary>
    [RelayCommand]
    private void ShowTasks()
    {
        ShowDevicesRequested?.Invoke(this, EventArgs.Empty);
        _devices?.Tasks.ShowTasksCommand.Execute(null);
    }

    /// <summary>A row of a table: that device selected in the grid.</summary>
    [RelayCommand]
    private void SelectDevice(AssistantTableRow? row)
    {
        if (row is not null)
        {
            SelectInGrid([row.DeviceId]);
        }
    }

    /// <summary>Select all in the grid: every device of the table (at most 10,000).</summary>
    [RelayCommand]
    private void SelectAll(AssistantTableItem? table)
    {
        if (table is not null)
        {
            SelectInGrid(table.AllDeviceIds);
        }
    }

    private void SelectInGrid(IReadOnlyList<string> ids)
    {
        ShowDevicesRequested?.Invoke(this, EventArgs.Empty);
        _devices?.SelectInGrid(ids);
    }

    /// <summary>The shell shows the Devices page (a table row, Select all in the grid, Show tasks).</summary>
    public event EventHandler? ShowDevicesRequested;

    [RelayCommand]
    private async Task CopyAsync(AssistantAnswerItem? answer)
    {
        if (answer is not null && _clipboard is not null && await _clipboard.SetTextAsync(answer.Text).ConfigureAwait(true))
        {
            ShowMessage(AssistantTexts.Copied, isError: false);
        }
    }

    [RelayCommand]
    private void Enlarge(AssistantPictureItem? picture)
    {
        CloseEnlarged();
        Enlarged = picture;
        EnlargedPicture = picture?.Decode();
    }

    [RelayCommand]
    private void CloseEnlarged()
    {
        var picture = EnlargedPicture;
        Enlarged = null;
        EnlargedPicture = null;
        picture?.Dispose();
    }

    private bool CanAttach => !IsUploading && Attachments.Count < AssistantTexts.MaxAttachments;

    /// <summary>Why Attach file is off (10 files attached).</summary>
    public string? AttachTip => Attachments.Count >= AssistantTexts.MaxAttachments ? AssistantTexts.TooManyFiles : null;

    /// <summary>The paperclip's tooltip: why it is off, else "Attach file".</summary>
    public string AttachButtonTip => AttachTip ?? AssistantTexts.AttachFile;

    /// <summary>Attach file: the file picker, then the upload of the task dialogs (FileService.Upload, its limits and audit).</summary>
    [RelayCommand(CanExecute = nameof(CanAttach))]
    private async Task AttachFileAsync()
    {
        if (_dialogs is null || await _dialogs.PickFilePathAsync(AssistantTexts.AttachFile, AnyFile).ConfigureAwait(true) is not { } path)
        {
            return;
        }

        await AttachAsync(path).ConfigureAwait(true);
    }

    /// <summary>Uploads a local file and attaches it to the next message. False: it failed, the reason shows below the box.</summary>
    public async Task<bool> AttachAsync(string path)
    {
        IsUploading = true;
        Message = null;
        try
        {
            var file = await _api.UploadFileAsync(path, null, CancellationToken.None).ConfigureAwait(true);
            Attachments.Add(new AssistantAttachmentItem(file.Id, file.Name, file.Size));
            return true;
        }
        catch (RpcException ex)
        {
            ShowMessage(ex.Status.Detail, isError: true);
        }
        catch (IOException ex)
        {
            ShowMessage(ex.Message, isError: true);
        }
        catch (UnauthorizedAccessException ex)
        {
            ShowMessage(ex.Message, isError: true);
        }
        finally
        {
            IsUploading = false;
        }

        return false;
    }

    /// <summary>
    /// Files dropped on the panel or pasted into the box: each attached like Attach file (the same upload and limits), one
    /// after the other; folders are skipped. Beyond 10 files the rest stays out with "Attach at most 10 files.".
    /// </summary>
    public async Task AttachFilesAsync(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (IsUploading)
        {
            ShowMessage(AssistantTexts.WaitForUpload, isError: true);
            return;
        }

        foreach (var path in paths.Where(File.Exists).ToList())
        {
            if (Attachments.Count >= AssistantTexts.MaxAttachments)
            {
                ShowMessage(AssistantTexts.TooManyFiles, isError: true);
                return;
            }

            if (!await AttachAsync(path).ConfigureAwait(true))
            {
                return;
            }
        }
    }

    /// <summary>
    /// A picture pasted from the clipboard (no file): saved as "Pasted image N.png" in a folder of its own, attached like
    /// Attach file, the local copy deleted again. The model gets its id, name and size like every file, never the picture.
    /// </summary>
    public async Task AttachPastedImageAsync(byte[] png, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(png);
        if (IsUploading)
        {
            ShowMessage(AssistantTexts.WaitForUpload, isError: true);
            return;
        }

        if (Attachments.Count >= AssistantTexts.MaxAttachments)
        {
            ShowMessage(AssistantTexts.TooManyFiles, isError: true);
            return;
        }

        var folder = Path.Combine(PasteRoot, "oadm-paste-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, AssistantTexts.PastedImage(++_pastedImages));
            await File.WriteAllBytesAsync(path, png, ct).ConfigureAwait(true);
            await AttachAsync(path).ConfigureAwait(true);
        }
        catch (IOException ex)
        {
            ShowMessage(ex.Message, isError: true);
        }
        catch (UnauthorizedAccessException ex)
        {
            ShowMessage(ex.Message, isError: true);
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // the system cleans its temp folder
            }
        }
    }

    [RelayCommand]
    private void RemoveAttachment(AssistantAttachmentItem? attachment)
    {
        if (attachment is not null)
        {
            Attachments.Remove(attachment);
        }
    }

    private void ShowMessage(string text, bool isError)
    {
        MessageIsError = isError;
        Message = text;
    }

    /// <summary>The current conversation ends: the answer stops streaming, the list empties, End on the server.</summary>
    private void EndConversation()
    {
        if (_chat is { } chat)
        {
            _chat = null;
            chat.Cancel();
            IsStreaming = false;
        }

        _flushTimer?.Dispose();
        _flushTimer = null;
        _lastFlush = long.MinValue;
        _answer = null;
        _thinking = null;
        _cards = null;
        IsThinking = false;
        _user = null;
        _steps = null;
        _editing = null;
        _pastedImages = 0;
        Mentioned = [];
        foreach (var item in Items.OfType<IDisposable>())
        {
            item.Dispose();
        }

        Items.Clear();
        Attachments.Clear();
        _notices.Clear();
        CloseEnlarged();
        Message = null;
        Draft = string.Empty;
        if (ConversationId is { } conversation)
        {
            _ = EndQuietlyAsync(conversation);
        }
    }

    private async Task EndQuietlyAsync(string conversation)
    {
        try
        {
            await _api.AssistantEndAsync(conversation, CancellationToken.None).ConfigureAwait(true);
        }
        catch (RpcException)
        {
            // the server forgets it after 30 minutes or with the session anyway
        }
    }

    /// <summary>
    /// The button follows at once. An open panel stays (its last answer and a refusal stay readable, its X closes it); a role
    /// that lost the permission closes it.
    /// </summary>
    private void OnAvailabilityChanged()
    {
        OnPropertyChanged(nameof(IsAvailable));
        if (IsOpen && _session is { IsLoggedIn: true } session && !session.Has(HostPermissionKeys.AssistantUse))
        {
            Close();
        }
    }

    private void OnSelectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(SelectionCount));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionError));
        OnPropertyChanged(nameof(HasSelectionError));
        OnPropertyChanged(nameof(ComposerError));
        OnPropertyChanged(nameof(HasComposerError));
        OnPropertyChanged(nameof(SendTip));
        SendCommand.NotifyCanExecuteChanged();
        SendSuggestionCommand.NotifyCanExecuteChanged();
        LookUpCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
        UpdateStart();
    }
}
