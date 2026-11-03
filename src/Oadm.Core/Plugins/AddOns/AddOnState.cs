namespace Oadm.Core.Plugins.AddOns;

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

    /// <summary>False while the server cannot reach the release (<see cref="AddOnTexts.CannotReach"/>).</summary>
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

/// <summary>The texts of add-ons (docs/spec/add-ons.md), one phrase per case.</summary>
public static class AddOnTexts
{
    /// <summary>The row's text while the server cannot reach the release.</summary>
    public const string CannotReach = "The server cannot reach github.com.";

    /// <summary>A download that failed.</summary>
    public const string DownloadFailed = "Could not download the add-on. Check that the server can reach github.com.";

    /// <summary>A download whose SHA-256 does not match the release's list.</summary>
    public const string Damaged = "The download is damaged. Try again.";

    /// <summary>Install or remove while a self update runs.</summary>
    public const string Updating = "The server is updating.";

    /// <summary>The row after Remove.</summary>
    public const string Removed = "Removed. The files go at the next restart of the server.";

    /// <summary>An installed add-on whose new version could not be downloaded after an update.</summary>
    public static string NotLoadedAfterUpdate(string name, string version) =>
        $"Could not download {name} V{version}. Try again on the Plugins page.";
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
