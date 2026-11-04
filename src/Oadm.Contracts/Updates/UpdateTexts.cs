namespace Oadm.Contracts.Updates;

/// <summary>The texts of self update (docs/spec/self-update.md), one phrase per concept, shared by server, client and helpers.</summary>
public static class UpdateTexts
{
    public const string DownloadPage = "https://oadm.app/start/install/";

    // Steps (the dialog's list).
    public const string StepDownload = "Download the update";
    public const string StepCheck = "Check the download";
    public const string StepInstall = "Install the update";

    // Refusals (FAILED_PRECONDITION) and problems before anything changed.
    public const string NotInstalled = "This server was not installed with the OADM installer. Install the update from the download page.";
    public const string ClientNotInstalled = "This client was not installed with the OADM installer. Install the update from the download page.";
    public const string NoInstaller = "The release has no installer for this computer. Install the update from the download page.";
    public const string PreRelease = "Only stable releases can be installed.";
    public const string SameVersion = "This version is already installed.";
    public const string Downgrade = "An older version cannot be installed.";
    public const string DevelopmentBuild = "A development build cannot be updated.";
    public const string NotLatest = "Only the latest release can be installed.";
    public const string Running = "An update is already running.";
    public const string AddOnRunning = "An optional plugin is being installed or removed. Try again when it is done.";
    public const string NotEnoughSpace = "Not enough free disk space for the update.";
    public const string DownloadFailed = "Could not download the update. Install it from the download page.";
    public const string WrongChecksum = "The download does not match the release's checksum. It was deleted.";
    public const string WrongName = "The download is not the installer this computer needs. It was deleted.";
    public const string TooBig = "The installer is larger than 400 MB. It was deleted.";
    public const string NoChecksum = "The release lists no checksum for the installer. Nothing was downloaded.";
    public const string HelperFailed = "The update could not start the installer.";
    public const string HelperStopped = "The update stopped before it finished.";

    /// <summary>"3 tasks are running" (Install now's tooltip and the refusal).</summary>
    public static string TasksRunning(int count) => count == 1 ? "1 task is running" : $"{count:N0} tasks are running";

    // Installer results (rule 12).
    public const string RestartComputer = "Restart the computer to finish the update";
    public const string InstallerBusy = "Another installation is running. Try again later.";
    public const string InstallerFailed = "The installer failed.";
    public const string Cancelled = "The update was cancelled.";
    public const string NewVersionSilent = "The new version did not answer within 5 minutes.";
    public const string InstallerDidNotFinish = "The installer did not finish.";

    /// <summary>"The installer failed (code 1603).".</summary>
    public static string InstallerFailedCode(int code) => $"The installer failed (code {code}).";

    // What the clients show.
    public static string Updating(string version) => $"Updating the server to {UpdateVersions.Display(version)}";

    public static string ServerUpdated(string version) => $"Server updated to {UpdateVersions.Display(version)}";

    public static string ServerFailed(string runs) => $"The update failed. The server runs {UpdateVersions.Display(runs)}.";

    public static string ClientUpdated(string version) => $"OADM updated to {UpdateVersions.Display(version)}";

    public const string ClientFailed = "The update failed";
    public const string NotBack = "The server did not come back";
    public const string NotBackHelp = "Check that the service OADM Server runs on the server's computer, and read the installer log there.";
    public const string UpdateSiteNodes = "Update the site nodes on their computers";
    public const string UpdateNodeHere = "Update on its computer";

    /// <summary>"Installer log on the server: C:\ProgramData\OADM\update\install.log".</summary>
    public static string ServerLog(string path) => "Installer log on the server: " + path;

    /// <summary>"A copy of the database: ..." (rule 11: putting it back is a manual step of the user docs).</summary>
    public static string DatabaseCopy(string path) => "Copy of the database before the update: " + path;
}
