using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Grpc.Core;

using Oadm.Client.Api;
using Oadm.Client.Settings;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Validation;

namespace Oadm.Client.Discovery;

/// <summary>One entry of the Choose credentials dialog: the Credentials page's entry (eye, masked password) and its tick.</summary>
public sealed partial class CredentialChoiceItem : ObservableObject
{
    private readonly CredentialChoiceViewModel _owner;

    internal CredentialChoiceItem(CredentialItemViewModel entry, CredentialChoiceViewModel owner)
    {
        Entry = entry;
        _owner = owner;
    }

    /// <summary>User name, added time and the password once the eye asked for it.</summary>
    public CredentialItemViewModel Entry { get; }

    /// <summary>Ticked: tried on every device of the add page.</summary>
    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    /// <summary>The box can change: ticked, or fewer than the maximum are ticked.</summary>
    public bool CanCheck => IsChecked || _owner.CheckedCount < CredentialChoiceViewModel.MaxCredentials;

    partial void OnIsCheckedChanged(bool value) => _owner.CheckedChanged();

    internal void RaiseCanCheck() => OnPropertyChanged(nameof(CanCheck));
}

/// <summary>
/// "Choose credentials" (docs/spec/add-devices.md, decision 2026-11-25): before the add devices page, when the credential
/// list holds more entries than one add tries (<see cref="MaxCredentials"/>) and the user is an Administrator (the only
/// role that reads the list). The ticked entries are tried in the list's order; the eye shows a password with the
/// Credentials page's rules; Add credential saves a new entry to the list and ticks it. Continue opens the add page,
/// Cancel opens nothing.
/// </summary>
public sealed partial class CredentialChoiceViewModel : ValidatingViewModel
{
    /// <summary>
    /// The entries one add tries per device: cameras block an address after 5 failed logins in 30 s. The same number as
    /// <c>Oadm.Server.AddDevices.DiscoveryAuthenticator.MaxAttemptsPerDevice</c> (server project; keep them equal).
    /// </summary>
    public const int MaxCredentials = 4;

    public const string Title = "Choose credentials";

    public const string Intro = "Cameras block logins after 5 failures, so OADM tries at most 4.";

    /// <summary>Below the list while nothing is ticked.</summary>
    public const string NoneChosenText = "Choose a credential.";

    private static readonly string[] NewFields = [nameof(NewUserName), nameof(NewPassword)];

    private readonly IOadmApi _api;

    /// <param name="api">The server (reveal, add).</param>
    /// <param name="entries">The credential list in its order.</param>
    /// <param name="remembered">The last choice of this client session; none of them left (or none): the first ones are ticked.</param>
    public CredentialChoiceViewModel(IOadmApi api, IReadOnlyList<CredentialEntry> entries, IReadOnlyCollection<string> remembered)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(remembered);
        _api = api ?? throw new ArgumentNullException(nameof(api));
        foreach (CredentialEntry entry in entries)
        {
            Items.Add(new CredentialChoiceItem(ToItem(entry), this));
        }

        var keep = remembered.ToHashSet(StringComparer.Ordinal);
        List<CredentialChoiceItem> ticked = [.. Items.Where(i => keep.Contains(i.Entry.Id)).Take(MaxCredentials)];
        foreach (CredentialChoiceItem item in ticked.Count > 0 ? ticked : Items.Take(MaxCredentials))
        {
            item.IsChecked = true;
        }

        Validation
            .Rule(nameof(NewUserName), () => IsAdding ? CredentialsViewModel.UserNameError(NewUserName) : null)
            .Rule(nameof(NewPassword), () => IsAdding ? CredentialsViewModel.PasswordError(NewPassword) : null);
        Validation.Validate();
    }

    /// <summary>
    /// The dialog is asked before the add page: an Administrator and more entries than one add tries. Otherwise the server
    /// takes the first ones of the list.
    /// </summary>
    public static bool Applies(bool isAdministrator, int entryCount) => isAdministrator && entryCount > MaxCredentials;

    /// <summary>The credential list in its order (at most 20 entries: not virtualized).</summary>
    public ObservableCollection<CredentialChoiceItem> Items { get; } = [];

    public int CheckedCount { get; private set; }

    /// <summary>The ticked entries in the list's order: the order they are tried.</summary>
    public IReadOnlyList<string> ChosenIds => [.. Items.Where(i => i.IsChecked).Select(i => i.Entry.Id)];

    /// <summary>Below the list: nothing ticked (more cannot be ticked: the other boxes are off).</summary>
    public string? ChoiceError => CheckedCount == 0 ? NoneChosenText : null;

    /// <summary>Continue's tooltip while it is off.</summary>
    public string? ContinueBlockedReason => ChoiceError;

    /// <summary>Every password of the list is shown (Show passwords); a new entry then shows its own too.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PasswordsButtonText), nameof(PasswordsButtonIcon))]
    public partial bool ArePasswordsShown { get; private set; }

    public string PasswordsButtonText => ArePasswordsShown ? "Hide passwords" : "Show passwords";

    public string PasswordsButtonIcon => ArePasswordsShown ? "eyeOff" : "eye";

    /// <summary>A failed reveal, directly below the list.</summary>
    [ObservableProperty]
    public partial string? ErrorText { get; private set; }

    /// <summary>The Add credential editor is open (its Add is then the accent button).</summary>
    [ObservableProperty]
    public partial bool IsAdding { get; private set; }

    [ObservableProperty] public partial string NewUserName { get; set; } = "root";
    [ObservableProperty] public partial string NewPassword { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial bool IsSaving { get; private set; }

    /// <summary>Add's tooltip while it is off.</summary>
    public string? AddBlockedReason => Validation.FirstErrorOf(NewFields);

    /// <summary>Continue (true) or Cancel (false).</summary>
    public event EventHandler<bool>? CloseRequested;

    protected override void OnValidationChanged()
    {
        OnPropertyChanged(nameof(AddBlockedReason));
        AddCommand.NotifyCanExecuteChanged();
    }

    internal void CheckedChanged()
    {
        CheckedCount = Items.Count(i => i.IsChecked);
        foreach (CredentialChoiceItem item in Items)
        {
            item.RaiseCanCheck();
        }

        OnPropertyChanged(nameof(CheckedCount));
        OnPropertyChanged(nameof(ChosenIds));
        OnPropertyChanged(nameof(ChoiceError));
        OnPropertyChanged(nameof(ContinueBlockedReason));
        ContinueCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanContinue))]
    private void Continue()
    {
        Forget();
        CloseRequested?.Invoke(this, true);
    }

    private bool CanContinue() => CheckedCount is > 0 and <= MaxCredentials;

    [RelayCommand]
    private void Cancel()
    {
        Forget();
        CloseRequested?.Invoke(this, false);
    }

    /// <summary>A shown password does not outlive the dialog.</summary>
    public void Forget()
    {
        foreach (CredentialChoiceItem item in Items)
        {
            item.Entry.Hide();
        }

        ArePasswordsShown = false;
    }

    /// <summary>
    /// Show passwords: loads every stored password from the server and shows them all; Hide passwords masks them again. A
    /// failed read masks them all and says why below the list.
    /// </summary>
    [RelayCommand]
    private async Task TogglePasswordsAsync()
    {
        if (ArePasswordsShown)
        {
            Forget();
            return;
        }

        try
        {
            foreach (CredentialChoiceItem item in Items.ToList())
            {
                item.Entry.Reveal(await _api.RevealCredentialAsync(item.Entry.Id, CancellationToken.None).ConfigureAwait(true));
            }

            ArePasswordsShown = true;
            ErrorText = null;
        }
        catch (Exception ex)
        {
            Forget();
            ErrorText = CredentialTexts.ShowFailed + Detail(ex);
        }
    }

    /// <summary>The header's Add credential: opens the editor below the list.</summary>
    [RelayCommand]
    private void StartAdd()
    {
        IsAdding = true;
        Validation.Reset(NewFields);
    }

    [RelayCommand]
    private void CancelAdd()
    {
        IsAdding = false;
        NewPassword = "";
        Validation.Reset(NewFields);
    }

    /// <summary>
    /// Saves the new entry to the credential list and ticks it. With the maximum ticked already, the last ticked entry
    /// makes room (the new one is what the user wants now).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAdd))]
    private async Task AddAsync()
    {
        if (!Validation.IsValidFor(NewFields))
        {
            Validation.ShowAll(NewFields);
            return;
        }

        IsSaving = true;
        try
        {
            CredentialEntry entry = await _api.AddCredentialAsync(NewUserName.Trim(), NewPassword, CancellationToken.None).ConfigureAwait(true);
            if (CheckedCount >= MaxCredentials && Items.LastOrDefault(i => i.IsChecked) is { } last)
            {
                last.IsChecked = false;
            }

            var item = new CredentialChoiceItem(ToItem(entry), this);
            if (ArePasswordsShown)
            {
                item.Entry.Reveal(NewPassword);
            }

            Items.Add(item);
            item.IsChecked = true;
            IsAdding = false;
            NewPassword = "";
            Validation.Reset(NewFields);
        }
        catch (Exception ex)
        {
            // e.g. the entry exists already or the list is full: below the user name, like the Credentials page
            Validation.SetServerError(nameof(NewUserName), "Adding failed: " + Detail(ex));
        }
        finally
        {
            IsSaving = false;
        }
    }

    private bool CanAdd() => !IsSaving && Validation.IsValidFor(NewFields);

    private static CredentialItemViewModel ToItem(CredentialEntry entry) =>
        new(entry.Id, entry.UserName, entry.Created is null ? "" : CredentialTexts.Added(entry.Created.ToDateTimeOffset()));

    private static string Detail(Exception ex) => ex is RpcException rpc ? rpc.Status.Detail : ex.Message;
}
