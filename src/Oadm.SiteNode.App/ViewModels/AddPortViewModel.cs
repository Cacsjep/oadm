using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client.Validation;
using Oadm.SiteNode.Admin;
using Oadm.SiteNode.App.Services;

namespace Oadm.SiteNode.App.ViewModels;

/// <summary>
/// The Add port dialog (Add of the Ports card): direction, protocol and port. Add sends it to the service (elevated); a
/// refusal stands below the port field, a dismissed prompt keeps the dialog open without an error.
/// </summary>
public sealed partial class AddPortViewModel : ValidatingViewModel
{
    public const string PortProblem = "Enter a port from 1 to 65535.";

    private readonly Func<PortEntry, Task<AdminResponse>> _add;

    public AddPortViewModel(Func<PortEntry, Task<AdminResponse>> add)
    {
        ArgumentNullException.ThrowIfNull(add);
        _add = add;
        Use = Uses[0];
        Protocol = Protocols[0];
        Validation.Rule(nameof(PortText), () => Port() is null ? PortProblem : null);
    }

    /// <summary>Out: the node connects to devices on the port; In: the node listens on it at the site.</summary>
    public IReadOnlyList<Choice<PortUse>> Uses { get; } = [new(PortUse.Connect, PortItem.Out), new(PortUse.Listen, PortItem.In)];

    public IReadOnlyList<Choice<PortProtocol>> Protocols { get; } = [new(PortProtocol.Tcp, "TCP"), new(PortProtocol.Udp, "UDP")];

    [ObservableProperty]
    public partial Choice<PortUse>? Use { get; set; }

    [ObservableProperty]
    public partial Choice<PortProtocol>? Protocol { get; set; }

    [ObservableProperty]
    public partial string PortText { get; set; } = "";

    /// <summary>No request runs (the administrator prompt may be open meanwhile).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand), nameof(CancelCommand))]
    public partial bool IsIdle { get; private set; } = true;

    /// <summary>Why Add is disabled (the port's error), null when it is not.</summary>
    public string? AddBlockedReason => PortText.Trim().Length == 0 ? "Enter a port." : ErrorOf(nameof(PortText));

    /// <summary>The dialog closes: true after the port was added.</summary>
    public event EventHandler<bool>? CloseRequested;

    protected override void OnValidationChanged()
    {
        base.OnValidationChanged();
        OnPropertyChanged(nameof(AddBlockedReason));
    }

    partial void OnPortTextChanged(string value) => OnPropertyChanged(nameof(AddBlockedReason));

    [RelayCommand(CanExecute = nameof(IsIdle))]
    public async Task AddAsync()
    {
        Validation.ShowAll(nameof(PortText));
        if (Port() is not { } port)
            return;
        IsIdle = false;
        AdminResponse response;
        try
        {
            response = await _add(new PortEntry(Use?.Value ?? PortUse.Connect, Protocol?.Value ?? PortProtocol.Tcp, port)).ConfigureAwait(true);
        }
        finally
        {
            IsIdle = true;
        }

        if (response.Ok)
            CloseRequested?.Invoke(this, true);
        else if (response.Error != SystemElevation.Cancelled)
            Validation.SetServerError(nameof(PortText), response.Error);
    }

    /// <summary>Cancel (and the X): only while no request runs, so a refusal or an added port is never lost.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    private int? Port() =>
        int.TryParse(PortText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535 ? port : null;
}
