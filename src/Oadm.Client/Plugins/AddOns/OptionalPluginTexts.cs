using System.Globalization;

using Oadm.Contracts.V1;

namespace Oadm.Client.Plugins.AddOns;

/// <summary>
/// The texts of the Optional plugins card (docs/spec/add-ons.md "Plugins page"), one phrase per case. Users see add-ons as
/// "optional plugins", never "add-on"; the server sends its own sentences (why a row cannot be installed, Removed, not loaded)
/// in each row's message.
/// </summary>
public static class OptionalPluginTexts
{
    public const string CardTitle = "Optional plugins";

    /// <summary>The progress row of the first read.</summary>
    public const string Reading = "Reading the optional plugins";

    /// <summary>The progress row while the client parts come from the server.</summary>
    public const string GettingParts = "Getting the optional plugins from the server";

    /// <summary>The one line below the optional plugins.</summary>
    public const string Source = "Click Download ZIP, then Install and choose the file. Clients get the plugin from the server.";

    public const string Installed = "Installed";

    public const string NotInstalled = "Not installed";

    /// <summary>The chip of a removed optional plugin (the server's message says when its files go).</summary>
    public const string RemovedState = "Removed";

    public const string Install = "Install";

    /// <summary>The link of a row: the ZIP of the server's version and system.</summary>
    public const string Download = "Download ZIP";

    public const string Cancel = "Cancel";

    /// <summary>The file picker of Install.</summary>
    public const string ZipFileType = "ZIP file";

    public const string Remove = "Remove";

    public const string RemoveTitle = "Remove optional plugin";

    /// <summary>A newer version came from the server while this client has the older one loaded.</summary>
    public const string RestartClient = "Restart the client to use the new version.";

    /// <summary>The question of Remove.</summary>
    public static string RemoveQuestion(string name) => $"Remove {name}? It turns off now. Its settings are kept.";

    /// <summary>The list could not be read.</summary>
    public static string ListFailed(string reason) => $"The optional plugins could not be read: {reason}";

    /// <summary>An older server or a connection without optional plugins.</summary>
    public const string NotOnThisServer = "This connection has no optional plugins.";

    /// <summary>Remove failed.</summary>
    public static string RemoveFailed(string name, string reason) => $"{name} could not be removed: {reason}";

    /// <summary>The install stream ended without done or error.</summary>
    public const string InstallInterrupted = "The install stopped. Try again.";

    /// <summary>The title of Install's file picker.</summary>
    public static string ChooseZip(string name) => $"Choose the ZIP of {name}";

    /// <summary>"Upload 42 %", "Check 100 %", "Install 100 %".</summary>
    public static string Progress(AddOnStep step, int percent) =>
        string.Create(CultureInfo.CurrentCulture, $"{StepName(step)} {Math.Clamp(percent, 0, 100)} %");

    /// <summary>The state of an install another administrator started: "Installing 42 %".</summary>
    public static string Installing(int percent) => string.Create(CultureInfo.CurrentCulture, $"Installing {Math.Clamp(percent, 0, 100)} %");

    public static string StepName(AddOnStep step) => step switch
    {
        AddOnStep.Check => "Check",
        AddOnStep.Install => "Install",
        _ => "Upload",
    };

    /// <summary>"V4.11.0"; empty when the version is.</summary>
    public static string Meta(string version) => string.IsNullOrEmpty(version) ? string.Empty : "V" + version;
}
