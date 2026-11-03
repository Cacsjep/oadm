using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Contracts.Updates;

namespace Oadm.Client.Updates;

/// <summary>
/// The helper's window: "Installing OADM V4.9.0" while the installer runs (with the system's prompt), "Updating the server
/// to V4.9.0" while it waits for the server on this computer. The new client opens when it is done.
/// </summary>
public sealed partial class UpdateHelperViewModel(ClientHelperArguments arguments) : ObservableObject
{
    public string Title { get; } = arguments?.Mode == ClientHelperArguments.Install
        ? "Installing OADM " + UpdateVersions.Display(arguments.To)
        : UpdateTexts.Updating(arguments?.To ?? string.Empty);

    public string Status { get; } = arguments?.Mode == ClientHelperArguments.Install
        ? "OADM opens again when the installer is done"
        : "OADM opens again when the server is back";
}
