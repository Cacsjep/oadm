using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Grpc.Core;

using Oadm.Client.Api;
using Oadm.Sdk.Client;

namespace Oadm.Client.Devices;

/// <summary>One entry of the first password's credential choice: a typed password (no id) or an entry of root.</summary>
/// <param name="CredentialId">The credential list entry; null for "Type a new password".</param>
/// <param name="Label">"Type a new password" or the entry's user name.</param>
/// <param name="AddedText">"Added 2026-11-18 14:02"; empty for the typed password.</param>
public sealed record FirstPasswordOption(string? CredentialId, string Label, string AddedText);

/// <summary>
/// The credential choice right of a first password's fields (the add page's first password editor, the context menu's
/// Set password; shared control <c>FirstPasswordChoiceBox</c>): "Type a new password" first, then the credential list's
/// entries of root with the time they were added. A chosen entry's password is set by the server and never reaches the
/// client; the eye shows it with the Credentials page's rules (SettingsService.RevealCredential: Administrators only,
/// audited). The credential list is Administrator only, so other users never see the choice.
/// </summary>
public sealed partial class FirstPasswordChoice : ObservableObject
{
    /// <summary>The first entry: the password is typed in the fields.</summary>
    public const string TypeNew = "Type a new password";

    /// <summary>The label of the choice.</summary>
    public const string Label = "Credential";

    /// <summary>The user of every first password.</summary>
    public const string Root = "root";

    private static readonly FirstPasswordOption Typed = new(null, TypeNew, string.Empty);

    private readonly IOadmApi _api;
    private int _reveal;

    /// <param name="api">The server.</param>
    /// <param name="isAdministrator">Only an Administrator reads the credential list (and sees its passwords).</param>
    public FirstPasswordChoice(IOadmApi api, bool isAdministrator)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        IsAdministrator = isAdministrator;
        Options = [Typed];
        SelectedOption = Typed;
    }

    /// <summary>The signed-in user is an Administrator: the choice and its eye can show.</summary>
    public bool IsAdministrator { get; }

    /// <summary>"Type a new password", then the entries of root, oldest first.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAvailable))]
    public partial IReadOnlyList<FirstPasswordOption> Options { get; private set; }

    /// <summary>The choice shows: an Administrator and the credential list has a password of root.</summary>
    public bool IsAvailable => IsAdministrator && Options.Count > 1;

    /// <summary>The chosen entry; "Type a new password" when nothing else is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsesCredential), nameof(CredentialId), nameof(ShowsStoredPassword))]
    public partial FirstPasswordOption? SelectedOption { get; set; }

    /// <summary>An entry of the credential list is chosen: the typed fields are off.</summary>
    public bool UsesCredential => SelectedOption?.CredentialId is not null;

    /// <summary>The chosen entry's id, or null for a typed password.</summary>
    public string? CredentialId => SelectedOption?.CredentialId;

    /// <summary>The eye shows (Administrators only; the server refuses everyone else anyway).</summary>
    public bool CanReveal => IsAdministrator;

    /// <summary>The chosen entry's password after the eye asked for it; null while masked.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRevealed), nameof(PasswordText))]
    public partial string? RevealedPassword { get; private set; }

    public bool IsRevealed => RevealedPassword is not null;

    /// <summary>The masked or shown password line below the choice: an entry is chosen.</summary>
    public bool ShowsStoredPassword => CanReveal && UsesCredential;

    /// <summary>The stored password, or the bullets of the Credentials page while masked.</summary>
    public string PasswordText => RevealedPassword ?? CredentialTexts.Masked;

    /// <summary>Why showing the password failed (below the choice), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRevealError), nameof(FieldError))]
    public partial string? RevealError { get; private set; }

    public bool HasRevealError => RevealError is not null;

    /// <summary>The server refused the chosen entry (e.g. it is gone); shown below the choice until another choice.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FieldError))]
    public partial string? ServerError { get; set; }

    /// <summary>The error directly below the choice: the server's refusal, else why the eye failed.</summary>
    public string? FieldError => ServerError ?? RevealError;

    /// <summary>
    /// Reads the credential list's entries of root (Administrators only; others keep the typed password). Keeps the chosen
    /// entry when it is still there. Never fails: without the list the typed password stays.
    /// </summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        if (!IsAdministrator)
        {
            return;
        }

        try
        {
            var entries = await _api.ListCredentialsAsync(ct).ConfigureAwait(true);
            var chosen = CredentialId;
            Options =
            [
                Typed,
                .. entries.Where(e => string.Equals(e.UserName, Root, StringComparison.Ordinal))
                    .OrderBy(e => e.Created?.ToDateTimeOffset() ?? DateTimeOffset.MinValue)
                    .Select(e => new FirstPasswordOption(e.Id, e.UserName, e.Created is null ? string.Empty : CredentialTexts.Added(e.Created.ToDateTimeOffset()))),
            ];
            Select(chosen);
        }
#pragma warning disable CA1031 // Only the choice: the typed password always works.
        catch (Exception ex) when (ex is RpcException or InvalidOperationException or OperationCanceledException)
#pragma warning restore CA1031
        {
        }
    }

    /// <summary>Chooses the entry <paramref name="credentialId"/>, or "Type a new password" for null or an entry that is gone.</summary>
    public void Select(string? credentialId) =>
        SelectedOption = Options.FirstOrDefault(o => credentialId is not null && o.CredentialId == credentialId) ?? Typed;

    /// <summary>The eye: loads the chosen entry's password and shows it; a second click masks and forgets it.</summary>
    [RelayCommand]
    private async Task ToggleRevealAsync()
    {
        if (IsRevealed)
        {
            Forget();
            return;
        }

        if (!CanReveal || CredentialId is not { } id)
        {
            return;
        }

        var attempt = ++_reveal;
        RevealError = null;
        try
        {
            var password = await _api.RevealCredentialAsync(id, CancellationToken.None).ConfigureAwait(true);
            if (attempt == _reveal)
            {
                RevealedPassword = password; // a later choice or hide wins over this answer
            }
        }
        catch (RpcException ex)
        {
            if (attempt == _reveal)
            {
                RevealError = CredentialTexts.ShowFailed + ex.Status.Detail;
            }
        }
    }

    /// <summary>Masks and forgets a shown password (another choice, the editor or dialog closes).</summary>
    public void Forget()
    {
        _reveal++;
        RevealedPassword = null;
        RevealError = null;
    }

    partial void OnSelectedOptionChanged(FirstPasswordOption? value)
    {
        if (value is null)
        {
            SelectedOption = Typed; // a list reset clears the selection: back to the typed password
            return;
        }

        ServerError = null;
        Forget();
    }
}
