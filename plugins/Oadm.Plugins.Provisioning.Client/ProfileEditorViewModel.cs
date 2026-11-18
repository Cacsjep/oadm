using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.Provisioning.Model;
using Oadm.Plugins.Provisioning.Nodes;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Client.Validation;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Provisioning.Client;

/// <summary>
/// The profile editor (spec "Profiles: the flow"): the name, the straight chain of nodes on the canvas (add from the list,
/// move up and down, remove; never the two fixed nodes), the selected node's settings in the side panel with their errors
/// below the fields, the profile check of the server (one error per node, on the node) after every change, Save. A password
/// typed for Set password goes to the server once, with Save.
/// </summary>
public sealed partial class ProfileEditorViewModel : ValidatingViewModel
{
    private readonly ICorePluginClientContext _ctx;
    private readonly Guid? _profileId;
    private Dictionary<NodeKind, NodeKindInfo> _kinds = [];
    private IReadOnlyList<CredentialChoice> _credentials = [];
    private Guid _id;
    private bool _checking;
    private bool _checkAgain;
    private bool _syncing;
    private Task _check = Task.CompletedTask;

    /// <param name="ctx">The page's context.</param>
    /// <param name="profileId">The profile to edit; null: a new profile.</param>
    public ProfileEditorViewModel(ICorePluginClientContext ctx, Guid? profileId)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _profileId = profileId;
        Title = profileId is null ? ProvisioningTexts.NewProfileTitle : ProvisioningTexts.EditProfileTitle;
        Chain.PropertyChanged += OnChainChanged;
        Validation.Rule(nameof(Name), () =>
            Name.Trim().Length == 0 ? ProvisioningTexts.EnterName
            : Name.Trim().Length > ProfileCheck.MaxNameLength ? $"The name has at most {ProfileCheck.MaxNameLength} characters."
            : null);
        Validation.Validate();
    }

    public string Title { get; }

    /// <summary>The nodes from top to bottom.</summary>
    public ObservableCollection<NodeEditorViewModel> Nodes { get; } = [];

    /// <summary>What the canvas draws.</summary>
    public NodeChain Chain { get; } = new();

    /// <summary>The kinds the Add node list offers (every kind but the fixed ones).</summary>
    public ObservableCollection<NodeKindInfo> AddableKinds { get; } = [];

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(ShowsProfileForm))]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand), nameof(MoveDownCommand), nameof(RemoveNodeCommand))]
    public partial NodeEditorViewModel? SelectedNode { get; set; }

    public bool HasSelection => SelectedNode is not null;

    /// <summary>Nothing selected: the side panel shows the profile's name.</summary>
    public bool ShowsProfileForm => SelectedNode is null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsSaving { get; set; }

    /// <summary>An error of the whole profile from the check (below the canvas), or null.</summary>
    [ObservableProperty]
    public partial string? ProfileError { get; set; }

    /// <summary>Why reading or saving failed (left of Save), or null.</summary>
    [ObservableProperty]
    public partial string? ErrorText { get; set; }

    public bool HasProfileError => ProfileError is not null;

    public bool HasErrorText => ErrorText is not null;

    /// <summary>The saved profile (after Save).</summary>
    public Profile? Saved { get; private set; }

    /// <summary>Close the window: true after Save, false on Cancel.</summary>
    public event EventHandler<bool>? CloseRequested;

    /// <summary>The check that runs now (tests wait for it).</summary>
    public Task CheckTask => _check;

    /// <summary>Why Save is disabled, or what it does.</summary>
    public string SaveTip => SaveBlockedReason ?? ProvisioningTexts.SaveTip;

    private string? SaveBlockedReason =>
        FormError
        ?? Nodes.Select(n => n.FirstError is { } e ? n.Name + ": " + e : null).FirstOrDefault(e => e is not null)
        ?? ProfileError
        ?? Nodes.Select(n => n.CanvasError is { } e ? n.Name + ": " + e : null).FirstOrDefault(e => e is not null);

    /// <summary>Reads the node kinds, the credential list and the profile (or starts a new one with the two fixed nodes).</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        IsLoading = true;
        ErrorText = null;
        try
        {
            var json = ProvisioningJson.Options;
            var kinds = PluginJson.Parse<List<NodeKindInfo>>(await _ctx.InvokeAsync(ProvisioningMethods.NodeKinds, null, ct).ConfigureAwait(true), json);
            var credentials = PluginJson.Parse<List<DeviceCredentialName>>(await _ctx.InvokeAsync(ProvisioningMethods.Credentials, null, ct).ConfigureAwait(true), json);
            _kinds = kinds.ToDictionary(k => k.Kind);
            _credentials = FieldRules.RootEntries(credentials);
            AddableKinds.Clear();
            foreach (var kind in kinds.Where(k => !k.IsFixed))
            {
                AddableKinds.Add(kind);
            }

            Profile profile;
            ProfileCheckResult? check = null;
            if (_profileId is { } id)
            {
                var reply = PluginJson.Parse<ProfileReply>(
                    await _ctx.InvokeAsync(ProvisioningMethods.GetProfile, PluginJson.Serialize(new IdRequest(id), json), ct).ConfigureAwait(true), json);
                profile = reply.Profile;
                check = reply.Check;
            }
            else
            {
                profile = new Profile
                {
                    Id = Guid.NewGuid(),
                    Name = ProvisioningTexts.DefaultName,
                    Nodes = [.. kinds.Where(k => k.IsFixed).Select((k, i) => new FlowNode { Id = "n" + (i + 1), Kind = k.Kind, ContinueOnError = !k.AlwaysStops, Settings = k.DefaultSettings })],
                };
            }

            Fill(profile);
            if (check is not null)
            {
                ApplyCheck(check);
            }
            else
            {
                RequestCheck();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ErrorText = ServerErrors.Message(ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Fills the editor from a profile (also the tests' start).</summary>
    public void Fill(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _id = profile.Id;
        Name = profile.Name;
        foreach (var node in Nodes)
        {
            node.Changed -= OnNodeChanged;
        }

        Nodes.Clear();
        foreach (var node in profile.Nodes)
        {
            Add(Nodes.Count, node);
        }

        SelectedNode = null;
        Validation.Reset();
        RebuildChain();
    }

    /// <summary>The profile as the editor holds it now.</summary>
    public Profile ToProfile() => new() { Id = _id, Name = Name.Trim(), Nodes = [.. Nodes.Select(n => n.ToFlowNode())] };

    /// <summary>Add node: the kind after the selected node (never before the two fixed ones), or at the end.</summary>
    [RelayCommand]
    public void AddNode(NodeKindInfo? kind)
    {
        if (kind is null || kind.IsFixed)
        {
            return;
        }

        var index = SelectedNode is { } selected ? Nodes.IndexOf(selected) + 1 : Nodes.Count;
        index = Math.Clamp(index, Math.Min(2, Nodes.Count), Nodes.Count);
        var node = Add(index, new FlowNode { Id = NextId(), Kind = kind.Kind, ContinueOnError = !kind.AlwaysStops, Settings = kind.DefaultSettings });
        SelectedNode = node;
        AfterChange();
    }

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    public void MoveUp() => Move(-1);

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    public void MoveDown() => Move(1);

    [RelayCommand(CanExecute = nameof(CanRemoveNode))]
    public void RemoveNode()
    {
        if (SelectedNode is not { IsFixed: false } node)
        {
            return;
        }

        var index = Nodes.IndexOf(node);
        node.Changed -= OnNodeChanged;
        Nodes.RemoveAt(index);
        SelectedNode = Nodes.Count > 0 ? Nodes[Math.Min(index, Nodes.Count - 1)] : null;
        AfterChange();
    }

    public bool CanMoveUp => SelectedNode is { IsFixed: false } node && Nodes.IndexOf(node) > 2;

    public bool CanMoveDown => SelectedNode is { IsFixed: false } node && Nodes.IndexOf(node) < Nodes.Count - 1;

    public bool CanRemoveNode => SelectedNode is { IsFixed: false };

    /// <summary>Why a fixed node cannot be moved or removed (the toolbar's tooltip), or null.</summary>
    public string? FixedTip => SelectedNode is { IsFixed: true } ? ProvisioningTexts.FixedTip : null;

    [RelayCommand(CanExecute = nameof(CanSave))]
    public async Task SaveAsync()
    {
        Validation.ShowAll();
        foreach (var node in Nodes)
        {
            node.ShowErrors();
        }

        if (!CanSave())
        {
            return;
        }

        IsSaving = true;
        ErrorText = null;
        try
        {
            var json = ProvisioningJson.Options;
            var password = Nodes.Select(n => n.TypedPassword).FirstOrDefault(p => p is not null);
            var reply = PluginJson.Parse<ProfileReply>(
                await _ctx.InvokeAsync(ProvisioningMethods.SaveProfile, PluginJson.Serialize(new SaveProfileRequest(ToProfile(), password), json), CancellationToken.None).ConfigureAwait(true),
                json);
            Saved = reply.Profile;
            ForgetTypedPassword(reply.Profile);
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

    /// <summary>Every node in the view again.</summary>
    [RelayCommand]
    public void FitView() => Chain.FitView();

    public bool CanSave() => !IsLoading && !IsSaving && Nodes.Count > 0 && SaveBlockedReason is null;

    protected override void OnValidationChanged()
    {
        base.OnValidationChanged();
        Refresh();
    }

    partial void OnNameChanged(string value) => RequestCheck();

    partial void OnSelectedNodeChanged(NodeEditorViewModel? value)
    {
        OnPropertyChanged(nameof(FixedTip));
        if (!_syncing)
        {
            _syncing = true;
            Chain.SelectedIndex = value is null ? -1 : Nodes.IndexOf(value);
            _syncing = false;
        }
    }

    partial void OnProfileErrorChanged(string? value) => OnPropertyChanged(nameof(HasProfileError));

    partial void OnErrorTextChanged(string? value) => OnPropertyChanged(nameof(HasErrorText));

    private void OnChainChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NodeChain.SelectedIndex) && !_syncing)
        {
            _syncing = true;
            SelectedNode = Chain.SelectedIndex >= 0 && Chain.SelectedIndex < Nodes.Count ? Nodes[Chain.SelectedIndex] : null;
            _syncing = false;
        }
    }

    private NodeEditorViewModel Add(int index, FlowNode node)
    {
        var editor = new NodeEditorViewModel(node, _kinds.GetValueOrDefault(node.Kind), _credentials);
        editor.Changed += OnNodeChanged;
        Nodes.Insert(index, editor);
        return editor;
    }

    private void Move(int by)
    {
        if (SelectedNode is not { IsFixed: false } node)
        {
            return;
        }

        var index = Nodes.IndexOf(node);
        var to = index + by;
        if (to < 2 || to >= Nodes.Count)
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

    /// <summary>The profile check after a change: one at a time, and once more when something changed meanwhile.</summary>
    private void RequestCheck()
    {
        if (IsLoading && Nodes.Count == 0)
        {
            return;
        }

        if (_checking)
        {
            _checkAgain = true;
            return;
        }

        _check = CheckAsync();
    }

    private async Task CheckAsync()
    {
        _checking = true;
        try
        {
            do
            {
                _checkAgain = false;
                var json = ProvisioningJson.Options;
                try
                {
                    var answer = await _ctx.InvokeAsync(ProvisioningMethods.CheckProfile, PluginJson.Serialize(new ProfileRequest(ToProfile()), json), CancellationToken.None).ConfigureAwait(true);
                    if (!_checkAgain)
                    {
                        ApplyCheck(PluginJson.Parse<ProfileCheckResult>(answer, json));
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

    private void ApplyCheck(ProfileCheckResult check)
    {
        foreach (var node in Nodes)
        {
            node.CheckError = check.NodeErrors.GetValueOrDefault(node.Id);
            node.CheckWarning = check.NodeWarnings.GetValueOrDefault(node.Id);
        }

        // The name's errors belong below the name; the others (the fixed nodes) below the canvas.
        var nameError = check.ProfileErrors.FirstOrDefault(e => e.Contains("name", StringComparison.OrdinalIgnoreCase));
        Validation.SetServerError(nameof(Name), nameError);
        ProfileError = check.ProfileErrors.FirstOrDefault(e => e != nameError);
        RebuildChain();
    }

    private void RebuildChain()
    {
        Chain.Nodes = [.. Nodes.Select(ToChainNode)];
        _syncing = true;
        Chain.SelectedIndex = SelectedNode is null ? -1 : Nodes.IndexOf(SelectedNode);
        _syncing = false;
        Refresh();
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(SaveTip));
        OnPropertyChanged(nameof(CanMoveUp));
        OnPropertyChanged(nameof(CanMoveDown));
        SaveCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        RemoveNodeCommand.NotifyCanExecuteChanged();
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

    /// <summary>After Save the typed password is an entry of the credential list: the field points at it and forgets the text.</summary>
    private void ForgetTypedPassword(Profile saved)
    {
        var id = saved.Nodes.FirstOrDefault(n => n.Kind == NodeKind.SetPassword) is { Settings: { ValueKind: JsonValueKind.Object } s }
            && s.TryGetProperty(PasswordFieldViewModel.CredentialKey, out var c) && c.ValueKind == JsonValueKind.String && Guid.TryParse(c.GetString(), out var g)
            ? g
            : (Guid?)null;
        foreach (var field in Nodes.SelectMany(n => n.Fields).OfType<PasswordFieldViewModel>())
        {
            field.Saved(id is { } entry ? new CredentialChoice(entry, "root") : null);
        }
    }
}
