namespace Oadm.Server.AddOns;

/// <summary>
/// The texts of the add-on installer that <see cref="Core.Plugins.AddOns.AddOnTexts"/> does not have yet (docs/spec/add-ons.md),
/// one phrase per case.
/// </summary>
public static class AddOnInstallTexts
{
    /// <summary>A second install or remove while one runs.</summary>
    public const string Busy = "An add-on is being installed or removed. Try again when it is done.";

    /// <summary>A development build or a pre-release: there is no release of this version with add-ons.</summary>
    public const string NoRelease = "This OADM version has no add-ons to download.";

    /// <summary>The data folder has too little room for the download.</summary>
    public const string NotEnoughSpace = "Not enough free disk space for the add-on.";

    /// <summary>The download holds another plugin or no readable plugin.json.</summary>
    public const string WrongPackage = "The download is not the add-on of this server. It was deleted.";

    /// <summary>The add-on's minSdkVersion is newer than the server's SDK.</summary>
    public const string NeedsNewerServer = "The add-on needs a newer OADM server. It was deleted.";

    /// <summary>Unpacking or swapping in failed on the server's disk.</summary>
    public const string CouldNotInstall = "Could not install the add-on. The server log has the details.";

    /// <summary>The client part is larger than a client takes in one answer.</summary>
    public const string ClientPartTooBig = "The add-on's client part is too large to send.";

    /// <summary>The add-on's folder is there but the loader refused it.</summary>
    public static string CouldNotLoad(string name) => $"{name} could not be loaded. Remove it and install it again.";

    /// <summary>Install of an add-on that is installed.</summary>
    public static string AlreadyInstalled(string name) => $"{name} is installed.";

    /// <summary>Remove of an add-on that is not installed.</summary>
    public static string NotInstalled(string name) => $"{name} is not installed.";

    /// <summary>The add-on's id is a plugin that came with the server (bundled or development folder).</summary>
    public static string PartOfServer(string name) => $"{name} is part of this server.";
}
