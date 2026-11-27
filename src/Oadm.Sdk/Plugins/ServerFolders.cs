namespace Oadm.Sdk.Plugins;

/// <summary>
/// Whether a folder on the server's computer may hold data a plugin writes there (SDK 0.17.0,
/// <see cref="ICorePluginContext.FolderCheck"/>): the folder an administrator chose with the client's server folder picker,
/// e.g. the plate log of License Plate Center. The same checks as the picker's (docs/spec/security.md "Folders a plugin
/// writes to"): a full path that exists, no system or program folder, not OADM's own folders, writable by the server and,
/// as the installed service, changeable only by administrators / root. A plugin checks again when it saves the setting and
/// refuses a folder with the returned text as the field's error.
/// </summary>
public interface IServerFolderCheck
{
    /// <summary>Null when the folder may be used, else why not (one of <see cref="ServerFolderTexts"/>).</summary>
    /// <param name="path">A full path on the server.</param>
    /// <param name="ct">Cancels the check.</param>
    Task<string?> CheckAsync(string path, CancellationToken ct);
}

/// <summary>
/// The texts and limits of the server folders (the checks of <see cref="IServerFolderCheck"/>, the client's folder picker
/// and its gRPC calls), one phrase per concept for server and client.
/// </summary>
public static class ServerFolderTexts
{
    /// <summary>Most folders in one answer of a listing; more are cut off (sorted by name).</summary>
    public const int MaxEntries = 2000;

    /// <summary>Longest name of a new folder.</summary>
    public const int MaxNameLength = 255;

    /// <summary>Longest path the server accepts.</summary>
    public const int MaxPathLength = 1024;

    /// <summary>No folder chosen (an empty field, the list of drives).</summary>
    public const string ChooseFolder = "Choose a folder.";

    /// <summary>A relative path, "..", a device path or a path the server's system cannot have.</summary>
    public const string NotFullPath = "Enter a full path on the server.";

    /// <summary>The folder is not there.</summary>
    public const string NotFound = "This folder does not exist on the server.";

    /// <summary>The service may not list the folder.</summary>
    public const string CannotOpen = "The server cannot open this folder.";

    /// <summary>The service cannot create a file in the folder.</summary>
    public const string CannotWrite = "The server cannot write to this folder.";

    /// <summary>The installed service: users other than administrators / root could change what is in the folder.</summary>
    public const string OthersCanChange = "Users other than administrators can change this folder. Make one with New folder.";

    /// <summary>The operating system's or a program's folder.</summary>
    public const string SystemFolder = "This is a system folder. Choose another folder.";

    /// <summary>OADM's data folder itself, its plugins or its program folder.</summary>
    public const string OadmFolder = "OADM keeps its own files here. Choose another folder.";

    /// <summary>A drive or the top folder (C:\, /).</summary>
    public const string WholeDrive = "Choose a folder, not a whole drive.";

    /// <summary>The name of a new folder is empty.</summary>
    public const string NameEmpty = "Enter a name.";

    /// <summary>The name holds a character no system allows.</summary>
    public const string NameInvalid = "Use a name without \\ / : * ? \" < > |.";

    /// <summary>The name starts with a dot (hidden on Linux and macOS) or ends with a dot or a space (not on Windows).</summary>
    public const string NameDots = "Do not start the name with a dot or end it with a dot or a space.";

    /// <summary>A name Windows keeps for devices (CON, NUL, COM1, ...).</summary>
    public const string NameReserved = "This name is reserved. Choose another name.";

    /// <summary>The name is longer than <see cref="MaxNameLength"/>.</summary>
    public const string NameTooLong = "Use at most 255 characters.";

    /// <summary>The folder exists already.</summary>
    public const string NameExists = "A folder with this name exists already.";

    /// <summary>The service may not create a folder in the parent.</summary>
    public const string CannotCreate = "The server cannot create a folder here.";

    private static readonly System.Buffers.SearchValues<char> InvalidNameChars = System.Buffers.SearchValues.Create("\\/:*?\"<>|");

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Null when <paramref name="name"/> (trimmed at the start) is a folder name every system the server runs on accepts, else
    /// why not. The same rule on Windows, Linux and macOS, so a name never depends on where the server runs.
    /// </summary>
    public static string? CheckName(string? name)
    {
        var text = name?.TrimStart() ?? string.Empty;
        if (text.Length == 0)
        {
            return NameEmpty;
        }

        if (text.Length > MaxNameLength)
        {
            return NameTooLong;
        }

        if (text.AsSpan().IndexOfAny(InvalidNameChars) >= 0 || text.Any(char.IsControl))
        {
            return NameInvalid;
        }

        if (text[0] == '.' || text[^1] is '.' or ' ')
        {
            return NameDots;
        }

        var stem = text.Split('.')[0].TrimEnd();
        return ReservedNames.Contains(stem) ? NameReserved : null;
    }
}
