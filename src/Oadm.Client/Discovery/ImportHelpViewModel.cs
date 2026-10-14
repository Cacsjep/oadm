using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Oadm.Client.Discovery;

/// <summary>
/// "Import from file" before the file picker: the columns and an example file, so the file is right the first time.
/// Choose file closes it with true (the picker opens next), Cancel and the X with false.
/// </summary>
public sealed partial class ImportHelpViewModel : ObservableObject
{
    public const string Intro = "A CSV file with a header row, or a text file with one address per line.";

    public const string AddressText = "IP address or host name, with port or https:// when needed. The header may also say IP address, IP or Host name.";

    public const string LoginText = "Optional. Tried first and never saved. A password without a user name is for root. Lines without a login use the credential list.";

    public const string TagsText = "Optional. Several tags are separated with ;.";

    public static string LimitsText { get; } = $"Comma or semicolon. Up to {DeviceImportFile.MaxLines:N0} addresses per file.";

    public const string Example = DeviceImportFile.Example;

    /// <summary>True: choose the file now.</summary>
    public event EventHandler<bool>? CloseRequested;

    [RelayCommand]
    private void Choose() => CloseRequested?.Invoke(this, true);

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);
}
