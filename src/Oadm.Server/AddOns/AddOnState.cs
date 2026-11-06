namespace Oadm.Server.AddOns;

/// <summary>The state of an add-on on this server (the Plugins page's Add-ons card).</summary>
public enum AddOnState
{
    /// <summary>Not on this server; Install offers it.</summary>
    NotInstalled = 0,

    /// <summary>Being uploaded, checked or installed (<see cref="AddOnStatus.Percent"/>).</summary>
    Installing = 1,

    /// <summary>Installed and loaded; turned on and off like any plugin.</summary>
    Installed = 2,

    /// <summary>Removed: turned off, still loaded; its folder goes at the next server start.</summary>
    Removed = 3,

    /// <summary>Installed before, not loaded now: a version that may not run here, no files after a restore, or refused by the loader.</summary>
    Failed = 4,
}

/// <summary>An add-on with its state on this server.</summary>
/// <param name="Entry">The catalogue entry.</param>
/// <param name="State">Its state.</param>
/// <param name="Version">The installed version; empty when not installed.</param>
public sealed record AddOnStatus(AddOnEntry Entry, AddOnState State, string Version = "")
{
    /// <summary>0 to 100 while <see cref="AddOnState.Installing"/>.</summary>
    public int Percent { get; init; }

    /// <summary>One plain sentence why it is not loaded or cannot be installed (<see cref="AddOnTexts"/>); null otherwise.</summary>
    public string? Message { get; init; }

    /// <summary>
    /// Whether Install works now: false in a build without the add-on's hash (<see cref="AddOnTexts.NoHash"/>), during a self
    /// update or while installing. A removed add-on that is still loaded can be installed again without a file.
    /// </summary>
    public bool CanInstall { get; init; } = true;

    /// <summary>Where the ZIP of this server's version and system can be downloaded; null in a build that cannot install it.</summary>
    public string? DownloadUrl { get; init; }

    /// <summary>The add-on of an older OADM version runs; Install takes the ZIP of this version, which starts at the next restart.</summary>
    public bool UpdateAvailable { get; init; }

    /// <summary>The ZIP of this version waits for the next restart of the server.</summary>
    public bool UpdateWaits { get; init; }

    /// <summary>The version an install just put in place when <see cref="Version"/> still is the running one (for the audit).</summary>
    public string? InstalledVersion { get; init; }
}

/// <summary>The steps of an install, in this order.</summary>
public enum AddOnStep
{
    /// <summary>Receiving the file from the client.</summary>
    Upload = 0,

    /// <summary>Checking its SHA-256, plugin.json and version.</summary>
    Check = 1,

    /// <summary>Unpacking, swapping in and loading.</summary>
    Install = 2,
}

/// <summary>Progress of an install: the step and its percent (0 to 100).</summary>
/// <param name="Step">The step that runs.</param>
/// <param name="Percent">Its progress, 0 to 100.</param>
public readonly record struct AddOnProgress(AddOnStep Step, int Percent);

/// <summary>A ZIP a client sends to install an add-on.</summary>
/// <param name="Id">The add-on the row stands for.</param>
/// <param name="FileName">The chosen file's name.</param>
/// <param name="Size">Its exact size in bytes.</param>
/// <param name="Data">Its bytes, in chunks; never held in memory as a whole.</param>
public sealed record AddOnUpload(string Id, string FileName, long Size, IAsyncEnumerable<ReadOnlyMemory<byte>> Data);

/// <summary>A file of an add-on's client part.</summary>
/// <param name="RelativePath">Path relative to the add-on folder, "/" separated.</param>
/// <param name="Content">The file's bytes.</param>
/// <param name="Sha256">SHA-256 of <paramref name="Content"/>, lowercase hex.</param>
public sealed record AddOnClientFile(string RelativePath, ReadOnlyMemory<byte> Content, string Sha256);

/// <summary>The client part of an installed add-on for the server's version.</summary>
/// <param name="Id">The add-on's id.</param>
/// <param name="Version">The installed version (the client keeps the files per server version).</param>
/// <param name="Files">The files of the client part.</param>
public sealed record AddOnClientPart(string Id, string Version, IReadOnlyList<AddOnClientFile> Files);

/// <summary>
/// The texts of add-ons on the server (docs/spec/add-ons.md), one phrase per case. Users see add-ons as "optional plugins",
/// never "add-on".
/// </summary>
public static class AddOnTexts
{
    /// <summary>A file that is not the genuine ZIP of this version and system (its SHA-256 differs, or it is no ZIP).</summary>
    public const string WrongFile = "This file is not the ZIP of this OADM version and system. Download it again.";

    /// <summary>A ZIP with the right hash that cannot be unpacked (a build error; never a file from the release).</summary>
    public const string Damaged = "The file is damaged. Download it again.";

    /// <summary>The upload ended early or stood still.</summary>
    public const string UploadStopped = "The upload stopped. Try again.";

    /// <summary>A build without the hash of this add-on (development): Install stays off.</summary>
    public const string NoHash = "This OADM build cannot check optional plugins. Put the plugin into its folder by hand.";

    /// <summary>Installed before, not loaded now: a version that may not run here, after a restore, or it does not load.</summary>
    public const string InstallAgain = "Not loaded. Install it again for this OADM version.";

    /// <summary>
    /// Installed before, deleted at this start: until now users other than administrators could change its folder
    /// (docs/spec/security.md, Folders). The row says so until the next start, then <see cref="InstallAgain"/>.
    /// </summary>
    public const string FolderWasLoose = "Removed: other users could change its folder. Install it again.";

    /// <summary>The ZIP of this version is in place while the older add-on still runs (a running plugin is never swapped).</summary>
    public const string UpdateWaits = "The new version starts at the next restart of the server.";

    /// <summary>Restart server with no update waiting (another Administrator restarted it meanwhile).</summary>
    public const string NothingWaits = "No optional plugin waits for a restart.";

    /// <summary>Install or remove while a self update runs.</summary>
    public const string Updating = "The server is updating.";

    /// <summary>Why Restart server cannot run now; null: it can.</summary>
    public static string? RestartRefused(bool updating, bool addOnWork) => updating ? Updating : addOnWork ? Busy : null;

    /// <summary>The row after Remove.</summary>
    public const string Removed = "Removed. The files go at the next restart of the server.";

    /// <summary>A second install or remove while one runs.</summary>
    public const string Busy = "An optional plugin is being installed or removed. Try again when it is done.";

    /// <summary>A client that still asks the server to fetch the add-on (InstallAddOn without a file).</summary>
    public const string OlderClient = "Update this client to install optional plugins.";

    /// <summary>The data folder has too little room for the file.</summary>
    public const string NotEnoughSpace = "Not enough free disk space for the optional plugin.";

    /// <summary>The add-on's minSdkVersion is newer than the server's SDK.</summary>
    public const string NeedsNewerServer = "The optional plugin needs a newer OADM server.";

    /// <summary>Unpacking or swapping in failed on the server's disk.</summary>
    public const string CouldNotInstall = "Could not install the optional plugin. The server log has the details.";

    /// <summary>A second request for a client part while the same session waits for one.</summary>
    public const string ClientPartBusy = "The optional plugin is being sent to this client. Try again when it is done.";

    /// <summary>The client part is larger than a client takes in one answer.</summary>
    public const string ClientPartTooBig = "The optional plugin is too large to send to the clients.";

    /// <summary>The file holds another plugin.</summary>
    public static string WrongPlugin(string name) => $"This ZIP holds another plugin. Choose the ZIP of {name}.";

    /// <summary>The file is of another OADM version.</summary>
    public static string WrongVersion(string version) => $"This ZIP is for another OADM version. This server needs V{version}.";

    /// <summary>The file is for another server platform.</summary>
    public static string WrongSystem(string runtimeId) => $"This ZIP is for another system. This server needs {runtimeId}.";

    /// <summary>The add-on's folder is there but the loader refused it.</summary>
    public static string CouldNotLoad(string name) => $"{name} could not be loaded. Remove it and install it again.";

    /// <summary>Install of an add-on that is installed.</summary>
    public static string AlreadyInstalled(string name) => $"{name} is installed.";

    /// <summary>Remove of an add-on that is not installed.</summary>
    public static string NotInstalled(string name) => $"{name} is not installed.";

    /// <summary>The add-on's id is a plugin that came with the server (bundled or development folder).</summary>
    public static string PartOfServer(string name) => $"{name} is part of this server.";

    /// <summary>An id that is no add-on of this version.</summary>
    public static string Unknown(string id) => $"Unknown optional plugin '{id}'.";
}

/// <summary>A failure of an install with a message for the user (<see cref="AddOnTexts"/>).</summary>
public sealed class AddOnException : Exception
{
    public AddOnException()
    {
    }

    public AddOnException(string message)
        : base(message)
    {
    }

    public AddOnException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
