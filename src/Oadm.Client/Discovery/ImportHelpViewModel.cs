using CommunityToolkit.Mvvm.Input;

namespace Oadm.Client.Discovery;

/// <summary>One explained part of an import file: the column or field and what OADM does with it.</summary>
public sealed record ImportHelpRow(string Label, string Text);

/// <summary>
/// The dialog before the file picker of "Import from file" and "Import from ADM": what the file holds and what OADM does
/// with it (and for a CSV an example), so the file is right the first time. Choose file closes it with true (the picker
/// opens next), Cancel and the X with false.
/// </summary>
public sealed partial class ImportHelpViewModel
{
    private ImportHelpViewModel(string title, string intro, IReadOnlyList<ImportHelpRow> rows, string? example, string limits, string? warning)
    {
        Title = title;
        Intro = intro;
        Rows = rows;
        Example = example;
        Limits = limits;
        Warning = warning;
    }

    public string Title { get; }

    public string Intro { get; }

    public IReadOnlyList<ImportHelpRow> Rows { get; }

    /// <summary>An example file; null for an ADM export (the user exports it, nobody writes it).</summary>
    public string? Example { get; }

    public bool HasExample => Example is not null;

    public string Limits { get; }

    /// <summary>What the user must know about the file itself; null when nothing.</summary>
    public string? Warning { get; }

    public bool HasWarning => Warning is not null;

    /// <summary>"Import from file": a CSV or a list of addresses.</summary>
    public static ImportHelpViewModel ForFile() => new(
        "Import from file",
        "A CSV file with a header row, or a text file with one address per line.",
        [
            new("Address", "IP address or host name, with port or https:// when needed. The header may also say IP address, IP or Host name."),
            new("User name, Password", "Optional. Tried first and never saved. A password without a user name is for root. Lines without a login use the credential list."),
            new("Tags", "Optional. Several tags are separated with ;."),
        ],
        DeviceImportFile.Example,
        $"Comma or semicolon. Up to {DeviceImportFile.MaxLines:N0} addresses per file.",
        null);

    /// <summary>"Import from ADM": the device export of AXIS Device Manager.</summary>
    public static ImportHelpViewModel ForAdm() => new(
        "Import from ADM",
        "Choose the device export of AXIS Device Manager (a .json file).",
        [
            new("Addresses", "With their ports. A device is only added when it answers with the MAC address of the export."),
            new("Logins", "Tried first and never saved. Devices without one use the credential list."),
            new("Tags", "Given to the devices you add. Missing tags are created."),
            new("Everything else", "OADM reads the model, firmware and settings from the devices."),
            new("Servers", "OADM is one server. Devices it cannot reach show Not found, for example cameras of another ADM server."),
        ],
        null,
        $"Up to {DeviceImportFile.MaxLines:N0} devices per file.",
        "The export holds the device passwords in plain text. Delete it after the import.");

    /// <summary>True: choose the file now.</summary>
    public event EventHandler<bool>? CloseRequested;

    [RelayCommand]
    private void Choose() => CloseRequested?.Invoke(this, true);

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);
}
