using System.Collections.ObjectModel;
using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Grpc.Core;

using Microsoft.Extensions.Logging;

using Oadm.Client.Api;
using Oadm.Client.Dialogs;
using Oadm.Client.Shell;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client.Validation;

namespace Oadm.Client.Settings;

/// <summary>
/// One credential list entry: user name, when it was added and, only after the eye button asked the server
/// (<see cref="IOadmApi.RevealCredentialAsync"/>), its password. Masked again (and forgotten) on the second click.
/// </summary>
public sealed partial class CredentialItemViewModel(string id, string userName, string addedText) : ObservableObject
{
    /// <summary>Shown while the password is hidden; always the same length so it says nothing about the password.</summary>
    public const string MaskedText = "••••••••";

    public string Id { get; } = id;
    public string UserName { get; } = userName;
    public string AddedText { get; } = addedText;

    /// <summary>The revealed password; null while hidden.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRevealed), nameof(PasswordText), nameof(RevealTooltip))]
    public partial string? Password { get; private set; }

    public bool IsRevealed => Password is not null;

    public string PasswordText => Password ?? MaskedText;

    public string RevealTooltip => IsRevealed ? "Hide password" : "Show password";

    internal void Reveal(string password) => Password = password;

    internal void Hide() => Password = null;
}

/// <summary>
/// Navigation page Credentials (Admin only, the server allows the credential list calls only for administrators): the
/// technician's credential list, tried on every discovered device when adding devices. Entries with eye and copy
/// buttons, add form (errors below the fields, Add credential disabled while a field has an error).
/// </summary>
public sealed partial class CredentialsViewModel : ValidatingViewModel
{
    private static readonly string[] CredentialFields = [nameof(NewCredentialUserName), nameof(NewCredentialPassword)];

    private readonly IOadmApi _api;
    private readonly IClipboardService _clipboard;
    private readonly UserSession? _session;
    private readonly ILogger<CredentialsViewModel>? _logger;

    public CredentialsViewModel(IOadmApi api, ServerConnection connection, IClipboardService clipboard,
        UserSession? session = null, ILogger<CredentialsViewModel>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(clipboard);
        _api = api;
        _clipboard = clipboard;
        _session = session;
        _logger = logger;
        connection.Connected += (_, _) => _ = LoadAsync();
        if (session is not null)
        {
            session.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(UserSession.IsAdmin) && !session.IsAdmin)
                {
                    // Another user logged in as operator: forget the entries (and any revealed password) at once.
                    Credentials.Clear();
                    OnPropertyChanged(nameof(HasNoCredentials));
                }
            };
        }

        Validation
            .Rule(nameof(NewCredentialUserName), () => NewCredentialUserName.Trim().Length == 0 ? "Enter a user name." : null)
            .Rule(nameof(NewCredentialPassword), () => NewCredentialPassword.Length == 0 ? "Enter the password." : null);
        Validation.Validate();
        Validation.Reset();
    }

    /// <summary>The credential list is read and changed only by administrators (the server checks it too).</summary>
    public bool IsAdmin => _session?.IsAdmin ?? true;

    /// <summary>The server's credential list, tried on every discovered device when adding devices.</summary>
    public ObservableCollection<CredentialItemViewModel> Credentials { get; } = [];

    [ObservableProperty] public partial string NewCredentialUserName { get; set; } = "root";
    [ObservableProperty] public partial string NewCredentialPassword { get; set; } = "";
    [ObservableProperty] public partial string? CredentialMessage { get; private set; }
    [ObservableProperty] public partial bool CredentialMessageIsError { get; private set; }

    public bool HasNoCredentials => Credentials.Count == 0;

    /// <summary>Why Add credential is disabled (tooltip).</summary>
    public string? AddCredentialBlockedReason => Validation.FirstErrorOf(CredentialFields);

    protected override void OnValidationChanged()
    {
        OnPropertyChanged(nameof(AddCredentialBlockedReason));
        AddCredentialCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Reads the list from the server (nothing for operators).</summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        if (!IsAdmin)
        {
            Credentials.Clear();
            OnPropertyChanged(nameof(HasNoCredentials));
            return;
        }

        try
        {
            IReadOnlyList<CredentialEntry> entries = await _api.ListCredentialsAsync(CancellationToken.None).ConfigureAwait(true);
            Credentials.Clear();
            foreach (CredentialEntry entry in entries)
            {
                string added = entry.Created is null ? "" : "Added " + entry.Created.ToDateTime().ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
                Credentials.Add(new CredentialItemViewModel(entry.Id, entry.UserName, added));
            }

            OnPropertyChanged(nameof(HasNoCredentials));
        }
        catch (Exception ex)
        {
            CredentialMessageIsError = true;
            CredentialMessage = "The credential list is not available: " + Detail(ex);
            if (_logger is not null)
            {
                LogLoadFailed(_logger, Detail(ex));
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanAddCredential))]
    private async Task AddCredentialAsync()
    {
        string user = NewCredentialUserName.Trim();
        if (!Validation.IsValidFor(CredentialFields))
        {
            Validation.ShowAll(CredentialFields);
            return;
        }

        CredentialMessage = null;

        try
        {
            await _api.AddCredentialAsync(user, NewCredentialPassword, CancellationToken.None).ConfigureAwait(true);
            NewCredentialPassword = "";
            Validation.Reset(CredentialFields); // ready for the next one
            CredentialMessageIsError = false;
            CredentialMessage = $"Added. OADM tries {user} on every device it finds.";
            await LoadAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // e.g. the entry exists already or the list is full: below the user name
            Validation.SetServerError(nameof(NewCredentialUserName), "Adding failed: " + Detail(ex));
        }
    }

    private bool CanAddCredential() => Validation.IsValidFor(CredentialFields);

    [RelayCommand]
    private async Task RemoveCredentialAsync(CredentialItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            await _api.RemoveCredentialAsync(item.Id, CancellationToken.None).ConfigureAwait(true);
            CredentialMessage = null;
            await LoadAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            CredentialMessageIsError = true;
            CredentialMessage = "Removing failed: " + Detail(ex);
        }
    }

    /// <summary>Eye button: loads the stored password from the server and shows it; a second click masks it again.</summary>
    [RelayCommand]
    private async Task ToggleRevealCredentialAsync(CredentialItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (item.IsRevealed)
        {
            item.Hide();
            return;
        }

        try
        {
            item.Reveal(await _api.RevealCredentialAsync(item.Id, CancellationToken.None).ConfigureAwait(true));
            CredentialMessage = null;
        }
        catch (Exception ex)
        {
            CredentialMessageIsError = true;
            CredentialMessage = "Showing the password failed: " + Detail(ex);
        }
    }

    /// <summary>Copy button: copies the stored password to the clipboard (loaded from the server unless shown already).</summary>
    [RelayCommand]
    private async Task CopyCredentialPasswordAsync(CredentialItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            string password = item.Password ?? await _api.RevealCredentialAsync(item.Id, CancellationToken.None).ConfigureAwait(true);
            bool copied = await _clipboard.SetTextAsync(password).ConfigureAwait(true);
            CredentialMessageIsError = !copied;
            CredentialMessage = copied ? $"Password of {item.UserName} copied to the clipboard." : "The clipboard is not available.";
        }
        catch (Exception ex)
        {
            CredentialMessageIsError = true;
            CredentialMessage = "Copying the password failed: " + Detail(ex);
        }
    }

    private static string Detail(Exception ex) => ex is RpcException rpc ? rpc.Status.Detail : ex.Message;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the credential list: {Reason}")]
    private static partial void LogLoadFailed(ILogger logger, string reason);
}
