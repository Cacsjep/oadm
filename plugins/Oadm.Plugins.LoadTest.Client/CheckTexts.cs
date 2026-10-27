using System.Globalization;

using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.LoadTest.Client;

/// <summary>The page's texts and number formats (CLAUDE.md "Wording"): one phrase per concept.</summary>
internal static class CheckTexts
{

    public static string RunPermission => PermissionKeys.Of(LoadTestInfo.PluginId, LoadTestInfo.RunPermission);

    public const string ChooseFirst = "Choose the cameras to check.";
    public const string ReadingCameras = "Reading the cameras";
    public const string NotAllowed = "Your role does not allow running the check.";
    public const string DeleteNotAllowed = "Your role does not allow deleting runs.";
    public const string SitesNoPacketLoss = "Packet loss is not measured at sites";
    public const string NotMeasured = "Not measured";
    public const string NoStreams = "None of the chosen devices has video.";
    public const string NoneLeftOut = "All chosen cameras can be checked.";
    public const string NoRuns = "No runs yet. Choose cameras and click Start.";
    public const string NoValues = "No values yet";

    public static string PhaseName(CheckPhase phase) => phase switch
    {
        CheckPhase.Ramp25 => "Ramp 25 %",
        CheckPhase.Ramp50 => "Ramp 50 %",
        CheckPhase.Ramp75 => "Ramp 75 %",
        CheckPhase.Normal => "Normal",
        _ => "Low light",
    };

    public static string BarLabel(CheckPhase phase) => phase switch
    {
        CheckPhase.Ramp25 => "25 %",
        CheckPhase.Ramp50 => "50 %",
        CheckPhase.Ramp75 => "75 %",
        CheckPhase.Normal => "Normal",
        _ => "Low light",
    };

    public static string Result(PhaseResult result) => result switch
    {
        PhaseResult.Pending => "Waiting",
        PhaseResult.Running => "Running",
        PhaseResult.Passed => "Passed",
        PhaseResult.Failed => "Failed",
        PhaseResult.Interrupted => "Interrupted",
        _ => "N/A",
    };

    public static string State(RunState state) => state switch
    {
        RunState.Running => "Running",
        RunState.Completed => "Completed",
        RunState.Cancelled => "Cancelled",
        _ => "Failed",
    };

    public static string Mbps(double value) => string.Create(CultureInfo.CurrentCulture, $"{value:N1} Mbit/s");

    public static string MbpsWhole(double value) => string.Create(CultureInfo.CurrentCulture, $"{value:N0} Mbit/s");

    public static string Percent(double value) =>
        value.ToString(value < 10 ? "0.00" : "0.0", CultureInfo.CurrentCulture) + " %";

    public static string Ms(double value) => string.Create(CultureInfo.CurrentCulture, $"{value:0.0} ms");

    public static string Count(int n, string one, string many) =>
        string.Create(CultureInfo.CurrentCulture, $"{n:N0} {(n == 1 ? one : many)}");

    /// <summary>"about 32 min", "about 1 h 5 min".</summary>
    public static string Duration(TimeSpan time)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling(time.TotalMinutes));
        return minutes < 60
            ? string.Create(CultureInfo.CurrentCulture, $"{minutes} min")
            : string.Create(CultureInfo.CurrentCulture, $"{minutes / 60} h {minutes % 60} min");
    }

    public static string Date(DateTimeOffset utc) => utc.ToLocalTime().ToString("d MMM yyyy HH:mm", CultureInfo.CurrentCulture);

    /// <summary>The exposure warning of the confirmation (docs/spec/load-test.md "Confirmation").</summary>
    public static string ExposureWarning(int minutes) => string.Create(CultureInfo.CurrentCulture,
        $"The exposure of these cameras changes for {minutes} minutes during the check. Recordings of your VMS see the brighter, noisier image.");
}
