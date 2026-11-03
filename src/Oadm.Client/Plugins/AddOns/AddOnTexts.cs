using System.Globalization;

using Oadm.Contracts.V1;

namespace Oadm.Client.Plugins.AddOns;

/// <summary>The texts of the Add-ons card (docs/spec/add-ons.md "Plugins page"), one phrase per case.</summary>
public static class AddOnTexts
{
    public const string CardTitle = "Add-ons";

    /// <summary>The one line below the add-ons.</summary>
    public const string Source = "Add-ons come from the OADM release on GitHub and are checked before they are installed. Clients get them from the server.";

    public const string Installed = "Installed";

    public const string NotInstalled = "Not installed";

    /// <summary>The row after Remove (the server's <c>AddOnTexts.Removed</c>).</summary>
    public const string Removed = "Removed. The files go at the next restart of the server.";

    public const string Install = "Install";

    public const string Remove = "Remove";

    public const string RemoveTitle = "Remove add-on";

    /// <summary>The question of Remove.</summary>
    public static string RemoveQuestion(string name) => $"Remove {name}? It turns off now. Its readers and settings are kept.";

    /// <summary>The list could not be read.</summary>
    public static string ListFailed(string reason) => $"The add-ons could not be read: {reason}";

    /// <summary>Remove failed.</summary>
    public static string RemoveFailed(string name, string reason) => $"{name} could not be removed: {reason}";

    /// <summary>The install stream ended without done or error.</summary>
    public const string InstallInterrupted = "The install stopped. Try again.";

    /// <summary>"Download 42 %", "Check 100 %", "Install 100 %".</summary>
    public static string Progress(AddOnStep step, int percent) =>
        string.Create(CultureInfo.CurrentCulture, $"{StepName(step)} {Math.Clamp(percent, 0, 100)} %");

    /// <summary>The state of an install another administrator started: "Installing 42 %".</summary>
    public static string Installing(int percent) => string.Create(CultureInfo.CurrentCulture, $"Installing {Math.Clamp(percent, 0, 100)} %");

    public static string StepName(AddOnStep step) => step switch
    {
        AddOnStep.Check => "Check",
        AddOnStep.Install => "Install",
        _ => "Download",
    };

    /// <summary>"V4.11.0 · 38 MB"; the size left out when unknown, the version when empty.</summary>
    public static string Meta(string version, long sizeBytes)
    {
        var parts = new List<string>(2);
        if (!string.IsNullOrEmpty(version))
        {
            parts.Add("V" + version);
        }

        if (sizeBytes > 0)
        {
            parts.Add(Size(sizeBytes));
        }

        return string.Join(" · ", parts);
    }

    /// <summary>A download size in KB, MB or GB (1,000 based, like the release page).</summary>
    public static string Size(long bytes) => bytes switch
    {
        < 1_000_000 => string.Create(CultureInfo.CurrentCulture, $"{Math.Max(1, (long)Math.Round(bytes / 1_000d))} KB"),
        < 1_000_000_000 => string.Create(CultureInfo.CurrentCulture, $"{Math.Round(bytes / 1_000_000d)} MB"),
        _ => string.Create(CultureInfo.CurrentCulture, $"{Math.Round(bytes / 1_000_000_000d, 1)} GB"),
    };
}
