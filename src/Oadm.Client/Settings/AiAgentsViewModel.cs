using System.Globalization;
using System.Text;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Grpc.Core;

using Oadm.Client.Api;
using Oadm.Client.Dialogs;
using Oadm.Client.Shell;
using Oadm.Contracts.Mcp;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client.Network;
using Oadm.Sdk.Client.Validation;
using Oadm.Sdk.Network;

namespace Oadm.Client.Settings;

/// <summary>
/// The rail page MCP &amp; Assistant, card MCP (docs/spec/mcp.md "Transport and certificate"), and its card Assistant: MCP is off by default; Turn on,
/// Port and Listen on for administrators (Save), the certificate the PKI policy MCP server picks, and Download CA certificate
/// for the agent's computer. Without PKI: "PKI is not running. The server log says why." and no Turn on. Every user sees the state.
/// </summary>
public sealed partial class AiAgentsViewModel : ValidatingViewModel
{
    /// <summary>The key of the rail page MCP &amp; Assistant.</summary>
    public const string PageKey = "ai";

    /// <summary>The rail entry and title of the page.</summary>
    public const string PageTitle = "MCP & Assistant";
    public const string PageSubtitle = "Agents and the Assistant work with your devices.";
    public const string ChooseCertificate = McpTexts.CertificateOnTurnOn;
    public const string NoCertificate = "None";
    public const string PortRange = McpTexts.PortRange;
    public const string NoChanges = "No changes to save.";

    private static readonly FileType PemFile = new("CA certificate", "pem", "application/x-pem-file");

    private readonly IOadmApi _api;
    private readonly IDialogService _dialogs;
    private static readonly FileType ZipFile = new("Skill", "zip", "application/zip");

    private readonly UserSession? _session;
    private readonly IClipboardService? _clipboard;
    private McpState? _state;

    public AiAgentsViewModel(IOadmApi api, IDialogService dialogs, UserSession? session = null, IClipboardService? clipboard = null,
        Assistant.AssistantSettingsViewModel? assistant = null, ServerConnection? connection = null)
    {
        _api = api;
        Assistant = assistant ?? new Assistant.AssistantSettingsViewModel(api, session);
        _dialogs = dialogs;
        _clipboard = clipboard;
        Keys = new AccessKeysViewModel(api, dialogs, clipboard);
        _session = session;
        if (connection is not null)
        {
            connection.Connected += (_, _) => _ = LoadAsync(); // the page loads on connect, like the other pages
        }

        if (session is not null)
        {
            session.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(UserSession.IsAdmin))
                {
                    OnPropertyChanged(nameof(IsAdmin));
                    _ = IsAdmin && IsLoaded ? Keys.LoadAsync() : Task.CompletedTask;
                    NotifyCommands();
                }
            };
        }

        Listen.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(InterfaceSelection.Selected))
            {
                OnPropertyChanged(nameof(SelectedInterface)); // the field the server's error shows below
            }

            NotifyCommands();
        };
        Validation.Rule(nameof(Port), () => int.TryParse(Port, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1025 and <= 65535 ? null : PortRange);
        Validation.Validate();
        Validation.Reset();
    }

    /// <summary>Administrators change it; the server checks it too.</summary>
    public bool IsAdmin => _session?.IsAdmin ?? true;

    /// <summary>The card Assistant right of MCP (docs/spec/assistant.md "Settings").</summary>
    public Assistant.AssistantSettingsViewModel Assistant { get; }

    /// <summary>The section Access keys (administrators).</summary>
    public AccessKeysViewModel Keys { get; }

    /// <summary>The choices of Listen on (the server's interfaces).</summary>
    public InterfaceSelection Listen { get; } = new();

    /// <summary>The chosen interface of Listen on (bind <c>SelectedItem</c>): the field of its validation error.</summary>
    public InterfaceOption? SelectedInterface
    {
        get => Listen.Selected;
        set => Listen.Selected = value;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyPropertyChangedFor(nameof(SaveTip))]
    public partial string Port { get; set; } = "5082";

    [ObservableProperty] public partial bool IsLoaded { get; private set; }

    [ObservableProperty] public partial bool IsBusy { get; private set; }

    [ObservableProperty] public partial string? Message { get; private set; }

    [ObservableProperty] public partial bool MessageIsError { get; private set; }

    public bool IsOn => _state?.Settings.Enabled ?? false;

    public bool Running => _state?.Running ?? false;

    /// <summary>"Running on port 5082", "Off", or why it does not listen.</summary>
    public string StatusText => _state?.Status ?? McpTexts.Off;

    /// <summary>On but not listening: the status is a problem.</summary>
    public bool HasProblem => IsOn && !Running;

    /// <summary>The certificate the PKI policy picks (known while it is on), or where it comes from; on without one the status says why.</summary>
    public string CertificateText => _state is null ? string.Empty
        : !_state.PkiAvailable ? McpTexts.NoPki
        : _state.Certificate.Length > 0 ? _state.Certificate
        : _state.Settings.Enabled ? NoCertificate
        : ChooseCertificate;

    /// <summary>Listen on as plain text (users who read the card).</summary>
    public string ListenText => Listen.Selected?.Label ?? InterfaceOptions.AllLabel;

    public bool ShowTurnOn => IsAdmin && !IsOn;

    public bool ShowTurnOff => IsAdmin && IsOn;

    /// <summary>The configurations of Connect an agent for this server's address (while it runs and its names are known).</summary>
    public IReadOnlyList<AgentConnection> Connections { get; private set; } = [];

    private IReadOnlyList<AgentConnection> BuildConnections() =>
        _state is { Running: true } state && AgentConnections.HostOf([.. state.CertificateNames]) is { } host
            ? AgentConnections.For(host, state.Settings.Port)
            : [];

    public bool HasConnections => Connections.Count > 0;

    /// <summary>The configuration chosen in Connect an agent (its index in <see cref="Connections"/>; Claude Code first).</summary>
    [ObservableProperty] public partial int SelectedConnection { get; set; }

    /// <summary>The button Access keys (administrators): the dialog with the list, Create access key and Delete.</summary>
    [RelayCommand]
    private async Task ShowAccessKeysAsync()
    {
        _ = Keys.LoadAsync(); // the dialog shows its ring while the list reads
        await _dialogs.ShowAccessKeysAsync(Keys).ConfigureAwait(true);
    }

    /// <summary>Copy of Connect an agent: the command or the file text of the chosen configuration.</summary>
    public bool CanSetUpDesktopApp => Running && File.Exists(Path.Combine(AppContext.BaseDirectory, "OADM.mcpb"));

    [RelayCommand]
    private Task SetUpDesktopAppAsync() => IsAdmin && CanSetUpDesktopApp
        ? Keys.OpenFormAsync(desktopSetup: true) : Task.CompletedTask;

    [RelayCommand]
    private Task CopySelectedAsync() => CopyAsync(Connections.ElementAtOrDefault(Math.Max(0, SelectedConnection)));

    public bool CanDownloadCa => _state?.HasCaCertificate ?? false;

    /// <summary>Why Save is disabled.</summary>
    public string? SaveTip => Validation.FirstError ?? (Changed ? null : NoChanges);

    /// <summary>Why Turn on is disabled.</summary>
    public string? TurnOnTip => _state is { PkiAvailable: false } ? McpTexts.NoPki : Validation.FirstError;

    private bool Changed => _state is { } state
        && (Port != state.Settings.Port.ToString(CultureInfo.InvariantCulture) || !SameInterface(Listen.SelectedId, state.Settings.ListenOn));

    protected override void OnValidationChanged() => NotifyCommands();

    public async Task LoadAsync()
    {
        await Assistant.LoadAsync().ConfigureAwait(true);
        try
        {
            Apply(await _api.GetMcpAsync(CancellationToken.None).ConfigureAwait(true));
            IsLoaded = true;
            if (IsAdmin)
            {
                await Keys.LoadAsync().ConfigureAwait(true); // the access keys: administrators only
            }
        }
        catch (Exception ex) when (ex is RpcException or NotSupportedException)
        {
            IsLoaded = false;
            MessageIsError = true;
            Message = ex is RpcException rpc ? rpc.Status.Detail : ex.Message;
        }
    }

    private bool CanSave => IsLoaded && IsAdmin && !IsBusy && Validation.IsValid && Changed;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task SaveAsync() => SetAsync(IsOn);

    private bool CanTurnOn => IsLoaded && IsAdmin && !IsBusy && Validation.IsValid && (_state?.PkiAvailable ?? false);

    [RelayCommand(CanExecute = nameof(CanTurnOn))]
    private Task TurnOnAsync() => SetAsync(true);

    private bool CanTurnOff => IsLoaded && IsAdmin && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanTurnOff))]
    private async Task TurnOffAsync()
    {
        if (await _dialogs.ConfirmAsync("Turn off the MCP server", "Agents can no longer connect until you turn it on again.", "Turn off").ConfigureAwait(true))
        {
            await SetAsync(false).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task DownloadCaAsync()
    {
        try
        {
            var ca = await _api.GetMcpCaCertificateAsync(CancellationToken.None).ConfigureAwait(true);
            if (await _dialogs.SaveFileAsync("Download CA certificate", ca.FileName, PemFile, Encoding.ASCII.GetBytes(ca.Pem)).ConfigureAwait(true) is { } name)
            {
                MessageIsError = false;
                Message = $"Saved {name}.";
            }
        }
        catch (RpcException ex)
        {
            MessageIsError = true;
            Message = ex.Status.Detail;
        }
    }

    [RelayCommand]
    private async Task DownloadSkillAsync()
    {
        try
        {
            var skill = await _api.GetMcpSkillAsync(CancellationToken.None).ConfigureAwait(true);
            if (await _dialogs.SaveFileAsync("Download skill", skill.FileName, ZipFile, skill.Content.ToByteArray()).ConfigureAwait(true) is { } name)
            {
                MessageIsError = false;
                Message = $"Saved {name}.";
            }
        }
        catch (RpcException ex)
        {
            MessageIsError = true;
            Message = ex.Status.Detail;
        }
    }

    [RelayCommand]
    private async Task CopyAsync(AgentConnection? connection)
    {
        if (connection is not null && _clipboard is not null && await _clipboard.SetTextAsync(connection.Text).ConfigureAwait(true))
        {
            MessageIsError = false;
            Message = $"Copied {connection.Title}.";
        }
    }

    private async Task SetAsync(bool enabled)
    {
        if (!Validation.IsValid)
        {
            Validation.ShowAll();
            return;
        }

        IsBusy = true;
        NotifyCommands();
        try
        {
            var settings = new McpSettings
            {
                Enabled = enabled,
                Port = int.Parse(Port, CultureInfo.InvariantCulture),
                ListenOn = InterfaceOptions.IsAll(Listen.SelectedId) ? string.Empty : Listen.SelectedId!,
            };
            Apply(await _api.SetMcpAsync(settings, CancellationToken.None).ConfigureAwait(true));
            Message = null;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.InvalidArgument && ex.Status.Detail == PortRange)
        {
            Validation.SetServerError(nameof(Port), ex.Status.Detail);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.InvalidArgument && ex.Status.Detail == McpTexts.UnknownInterface)
        {
            Validation.SetServerError(nameof(SelectedInterface), ex.Status.Detail);
        }
        catch (RpcException ex)
        {
            MessageIsError = true;
            Message = ex.Status.Detail;
        }
        finally
        {
            IsBusy = false;
            NotifyCommands();
        }
    }

    private void Apply(McpState state)
    {
        _state = state;
        Port = state.Settings.Port.ToString(CultureInfo.InvariantCulture);
        var options = state.Interfaces.Select(i => new InterfaceOption(i.Id, i.Label, i.Name, [.. i.Addresses])).ToList();
        if (options.Count == 0)
        {
            options.Add(InterfaceOptions.All);
        }

        var configured = InterfaceOptions.IsAll(state.Settings.ListenOn) ? InterfaceOptions.AllId : state.Settings.ListenOn;
        Listen.Selected = null;
        Listen.Apply(options, configured, null);
        Validation.Reset();
        OnPropertyChanged(nameof(IsOn));
        OnPropertyChanged(nameof(Running));
        OnPropertyChanged(nameof(CanSetUpDesktopApp));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasProblem));
        OnPropertyChanged(nameof(CertificateText));
        OnPropertyChanged(nameof(ListenText));
        OnPropertyChanged(nameof(CanDownloadCa));
        var connections = BuildConnections();
        if (!connections.Select(c => c.Text).SequenceEqual(Connections.Select(c => c.Text)))
        {
            Connections = connections; // only a new address or port gives a new list, so the choice stays
            OnPropertyChanged(nameof(Connections));
            SelectedConnection = 0;
        }

        OnPropertyChanged(nameof(HasConnections));
        NotifyCommands();
    }

    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(ShowTurnOn));
        OnPropertyChanged(nameof(ShowTurnOff));
        OnPropertyChanged(nameof(SaveTip));
        OnPropertyChanged(nameof(TurnOnTip));
        OnPropertyChanged(nameof(ListenText));
        SaveCommand.NotifyCanExecuteChanged();
        TurnOnCommand.NotifyCanExecuteChanged();
        TurnOffCommand.NotifyCanExecuteChanged();
    }

    private static bool SameInterface(string? a, string? b) =>
        InterfaceOptions.IsAll(a) ? InterfaceOptions.IsAll(b) : string.Equals(a, b, StringComparison.Ordinal);
}
