namespace Oadm.Server.AddOns;

/// <summary>The state of an add-on on this server (the Plugins page's Add-ons card).</summary>
public enum AddOnState
{
    /// <summary>Not on this server; Install offers it.</summary>
    NotInstalled = 0,

    /// <summary>Being downloaded, checked or installed (<see cref="AddOnStatus.Percent"/>).</summary>
    Installing = 1,

    /// <summary>Installed and loaded; turned on and off like any plugin.</summary>
    Installed = 2,

    /// <summary>Removed: turned off, still loaded; its folder goes at the next server start.</summary>
    Removed = 3,

    /// <summary>Installed before, but the add-on of this version could not be downloaded after an update: not loaded.</summary>
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
    /// Whether Install works now: false while the server cannot reach the release (<see cref="AddOnTexts.CannotReach"/>), in a
    /// development build, during a self update or while installing. A removed add-on that is still loaded can be installed
    /// again without a download.
    /// </summary>
    public bool CanInstall { get; init; } = true;
}

/// <summary>The steps of an install, in this order.</summary>
public enum AddOnStep
{
    /// <summary>Downloading the release file.</summary>
    Download = 0,

    /// <summary>Checking its SHA-256 and its plugin.json.</summary>
    Check = 1,

    /// <summary>Unpacking, swapping in and loading.</summary>
    Install = 2,
}

/// <summary>Progress of an install: the step and its percent (0 to 100).</summary>
/// <param name="Step">The step that runs.</param>
/// <param name="Percent">Its progress, 0 to 100.</param>
public readonly record struct AddOnProgress(AddOnStep Step, int Percent);

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
    /// <summary>The row's text while the server cannot reach the release.</summary>
    public const string CannotReach = "The server cannot reach github.com.";

    /// <summary>A download that failed.</summary>
    public const string DownloadFailed = "Could not download the optional plugin. Check that the server can reach github.com.";

    /// <summary>A download whose SHA-256 does not match the release's list.</summary>
    public const string Damaged = "The download is damaged. Try again.";

    /// <summary>Install or remove while a self update runs.</summary>
    public const string Updating = "The server is updating.";

    /// <summary>The row after Remove.</summary>
    public const string Removed = "Removed. The files go at the next restart of the server.";

    /// <summary>An installed add-on whose new version could not be downloaded after an update.</summary>
    public static string NotLoadedAfterUpdate(string name, string version) =>
        $"Could not download {name} V{version}. Install it again.";

    /// <summary>A second install or remove while one runs.</summary>
    public const string Busy = "An optional plugin is being installed or removed. Try again when it is done.";

    /// <summary>A development build or a pre-release: there is no release of this version with add-ons.</summary>
    public const string NoRelease = "This OADM version has no optional plugins to download.";

    /// <summary>The data folder has too little room for the download.</summary>
    public const string NotEnoughSpace = "Not enough free disk space for the optional plugin.";

    /// <summary>The download holds another plugin or no readable plugin.json.</summary>
    public const string WrongPackage = "The download is not the optional plugin of this server. It was deleted.";

    /// <summary>The add-on's minSdkVersion is newer than the server's SDK.</summary>
    public const string NeedsNewerServer = "The optional plugin needs a newer OADM server. It was deleted.";

    /// <summary>Unpacking or swapping in failed on the server's disk.</summary>
    public const string CouldNotInstall = "Could not install the optional plugin. The server log has the details.";

    /// <summary>A second request for a client part while the same session waits for one.</summary>
    public const string ClientPartBusy = "The optional plugin is being sent to this client. Try again when it is done.";

    /// <summary>The client part is larger than a client takes in one answer.</summary>
    public const string ClientPartTooBig = "The optional plugin is too large to send to the clients.";

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

/// <summary>A failure of an install or a download with a message for the user (<see cref="AddOnTexts"/>).</summary>
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
