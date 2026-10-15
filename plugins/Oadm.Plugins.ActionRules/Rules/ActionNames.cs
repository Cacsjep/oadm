using System.Globalization;

namespace Oadm.Plugins.ActionRules.Rules;

/// <summary>
/// The names of the action templates as the device's web interface shows them (AXIS OS 12.11), so installers recognize
/// them: "Send notification through TCP" and "... while the rule is active". A template OADM does not know gets a name
/// from its token ("Send images ftp").
/// </summary>
public static class ActionNames
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
    {
        ["com.axis.action.fixed.answer.call"] = "Answer call",
        ["com.axis.action.fixed.autopilot"] = "Turn on autopilot",
        ["com.axis.action.fixed.autopilotvideo"] = "Start autopilot tracking",
        ["com.axis.action.fixed.btbeacon"] = "Broadcast signal",
        ["com.axis.action.fixed.dad"] = "Move toward sound source",
        ["com.axis.action.fixed.dad_ptz_control"] = "Turn on PTZ move",
        ["com.axis.action.fixed.defog"] = "Set Defog mode",
        ["com.axis.action.fixed.dynamicledstrip"] = "Dynamic LED strip",
        ["com.axis.action.fixed.erase.configuration"] = "Erase configuration",
        ["com.axis.action.fixed.goto.preset"] = "Go to preset position",
        ["com.axis.action.fixed.guardtour"] = "Start guard tour",
        ["com.axis.action.fixed.hdmi_video_source"] = "Use HDMI",
        ["com.axis.action.fixed.io.toggle"] = "Toggle I/O once",
        ["com.axis.action.fixed.iobox_led"] = "Flash I/O interface status LED",
        ["com.axis.action.fixed.irlight"] = "Use lights",
        ["com.axis.action.fixed.ledcontrol"] = "Flash status LED",
        ["com.axis.action.fixed.motiontracking"] = "Track moving object",
        ["com.axis.action.fixed.mqttpublish"] = "Send MQTT publish message",
        ["com.axis.action.fixed.networkpairing_siren_and_light"] = "Run light and siren profile",
        ["com.axis.action.fixed.networkpairing_siren_and_light_stop_activities"] = "Stop activities",
        ["com.axis.action.fixed.notification.http"] = "Send notification through HTTP",
        ["com.axis.action.fixed.notification.https"] = "Send notification through HTTPS",
        ["com.axis.action.fixed.notification.smtp"] = "Send notification to email",
        ["com.axis.action.fixed.notification.tcp"] = "Send notification through TCP",
        ["com.axis.action.fixed.play.audioclip"] = "Play audio clip",
        ["com.axis.action.fixed.power_saving_mode"] = "Power saving mode",
        ["com.axis.action.fixed.ptz-autotracking.autotriggers"] = "Toggle Autotracking",
        ["com.axis.action.fixed.ptz-autotracking.profile"] = "Toggle Autotracking profile",
        ["com.axis.action.fixed.ptz-autotracking.temp-zone"] = "Start temporary detection",
        ["com.axis.action.fixed.radar"] = "Radar detection",
        ["com.axis.action.fixed.radar_autotracking"] = "Radar autotracking",
        ["com.axis.action.fixed.radar_recording"] = "Record radar data",
        ["com.axis.action.fixed.recordedtour"] = "Start guard tour",
        ["com.axis.action.fixed.recording.storage"] = "Record video",
        ["com.axis.action.fixed.send_images.ftp"] = "Send images through FTP",
        ["com.axis.action.fixed.send_images.http"] = "Send images through HTTP",
        ["com.axis.action.fixed.send_images.https"] = "Send images through HTTPS",
        ["com.axis.action.fixed.send_images.networkshare"] = "Send images to network share",
        ["com.axis.action.fixed.send_images.sftp"] = "Send images through SFTP",
        ["com.axis.action.fixed.send_images.smtp"] = "Send images to email",
        ["com.axis.action.fixed.send_videoclip.ftp"] = "Send video clip through FTP",
        ["com.axis.action.fixed.send_videoclip.http"] = "Send video clip through HTTP",
        ["com.axis.action.fixed.send_videoclip.https"] = "Send video clip through HTTPS",
        ["com.axis.action.fixed.send_videoclip.networkshare"] = "Send video clip to network share",
        ["com.axis.action.fixed.send_videoclip.sftp"] = "Send video clip through SFTP",
        ["com.axis.action.fixed.send_videoclip.smtp"] = "Send video clip to email",
        ["com.axis.action.fixed.set_overlay"] = "Use overlay text",
        ["com.axis.action.fixed.sip"] = "Make call",
        ["com.axis.action.fixed.sip.terminate_calls"] = "End calls",
        ["com.axis.action.fixed.siren_and_light"] = "Profile",
        ["com.axis.action.fixed.siren_and_light_health_check"] = "Run health check",
        ["com.axis.action.fixed.siren_and_light_stop_activities"] = "Stop activities",
        ["com.axis.action.fixed.snmp.trap"] = "Send SNMP trap message",
        ["com.axis.action.fixed.stop.audioclip"] = "Stop playing audio clip",
        ["com.axis.action.fixed.washer"] = "Use washer",
        ["com.axis.action.fixed.wdr"] = "Set WDR mode",
        ["com.axis.action.fixed.wiper"] = "Use wiper",
        ["com.axis.action.fixed.zwave.binaryswitch"] = "Toggle Z-Wave once",
        ["com.axis.action.unlimited.autopilot"] = "Turn on autopilot while the rule is active",
        ["com.axis.action.unlimited.autopilotvideo"] = "Run autopilot tracking while the rule is active",
        ["com.axis.action.unlimited.dad"] = "Move toward sound source while the rule is active",
        ["com.axis.action.unlimited.dad_ptz_control"] = "Turn on PTZ move while the rule is active",
        ["com.axis.action.unlimited.defog"] = "Set Defog mode while the rule is active",
        ["com.axis.action.unlimited.dynamicledstrip"] = "Use dynamic LED strip while the rule is active",
        ["com.axis.action.unlimited.goto.preset"] = "Go to preset position while the rule is active",
        ["com.axis.action.unlimited.guardtour"] = "Run guard tour while the rule is active",
        ["com.axis.action.unlimited.hdmi_video_source"] = "Use HDMI while the rule is active",
        ["com.axis.action.unlimited.io.toggle"] = "Toggle I/O while the rule is active",
        ["com.axis.action.unlimited.iobox_led"] = "Flash I/O interface status LED while the rule is active",
        ["com.axis.action.unlimited.ircutfilter"] = "Use day-night mode while the rule is active",
        ["com.axis.action.unlimited.irlight"] = "Use lights while the rule is active",
        ["com.axis.action.unlimited.ledcontrol"] = "Flash status LED while the rule is active",
        ["com.axis.action.unlimited.motiontracking"] = "Track object while the rule is active",
        ["com.axis.action.unlimited.networkpairing_siren_and_light"] = "Run light and siren profile while the rule is active",
        ["com.axis.action.unlimited.notification.tcp"] = "Send notification through TCP while the rule is active",
        ["com.axis.action.unlimited.play.audioclip"] = "Play audio clip while the rule is active",
        ["com.axis.action.unlimited.power_saving_mode"] = "Power saving mode while the rule is active",
        ["com.axis.action.unlimited.ptz-autotracking.autotriggers"] = "Toggle Autotracking while the rule is active",
        ["com.axis.action.unlimited.ptz-autotracking.profile"] = "Toggle Autotracking profile while the rule is active",
        ["com.axis.action.unlimited.radar"] = "Use radar detection while the rule is active",
        ["com.axis.action.unlimited.radar_autotracking"] = "Use radar autotracking while the rule is active",
        ["com.axis.action.unlimited.radar_recording"] = "Record radar data while the rule is active",
        ["com.axis.action.unlimited.recordedtour"] = "Run recorded guard tour while the rule is active",
        ["com.axis.action.unlimited.recording.storage"] = "Record video while the rule is active",
        ["com.axis.action.unlimited.recording_group.storage"] = "Record with recording group",
        ["com.axis.action.unlimited.sceneprofile2d"] = "Set temporary scene profile",
        ["com.axis.action.unlimited.send_images.ftp"] = "Send images through FTP while the rule is active",
        ["com.axis.action.unlimited.send_images.http"] = "Send images through HTTP while the rule is active",
        ["com.axis.action.unlimited.send_images.https"] = "Send images through HTTPS while the rule is active",
        ["com.axis.action.unlimited.send_images.networkshare"] = "Send images to network share while the rule is active",
        ["com.axis.action.unlimited.send_images.sftp"] = "Send images through SFTP while the rule is active",
        ["com.axis.action.unlimited.send_images.smtp"] = "Send images to email while the rule is active",
        ["com.axis.action.unlimited.send_videoclip.ftp"] = "Send video clip through FTP while the rule is active",
        ["com.axis.action.unlimited.send_videoclip.http"] = "Send video clip through HTTP while the rule is active",
        ["com.axis.action.unlimited.send_videoclip.https"] = "Send video clip through HTTPS while the rule is active",
        ["com.axis.action.unlimited.send_videoclip.networkshare"] = "Send video clip to network share while the rule is active",
        ["com.axis.action.unlimited.send_videoclip.sftp"] = "Send video clip through SFTP while the rule is active",
        ["com.axis.action.unlimited.send_videoclip.smtp"] = "Send video clip to email while the rule is active",
        ["com.axis.action.unlimited.set_overlay"] = "Use overlay text while the rule is active",
        ["com.axis.action.unlimited.siren_and_light"] = "Profile",
        ["com.axis.action.unlimited.snmp.trap"] = "Send SNMP trap message while the rule is active",
        ["com.axis.action.unlimited.wdr"] = "Set WDR mode while the rule is active",
        ["com.axis.action.unlimited.zwave.binaryswitch"] = "Toggle Z-Wave while the rule is active",
    };

    /// <summary>Why OADM does not put this action on devices: it erases their configuration ("Erase configuration").</summary>
    public const string RefusedText = "OADM does not put rules on devices that erase their configuration.";

    /// <summary>Actions OADM never offers, changes a rule to or copies: they reset the device when the event happens.</summary>
    public static bool IsRefused(string? template) => template is not null && template.Contains(".erase.", StringComparison.Ordinal);

    /// <summary>The name of an action template.</summary>
    public static string Of(string template)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (Names.TryGetValue(template, out var name))
        {
            return name;
        }

        var parts = template.Split('.');
        var whileActive = parts.Contains("unlimited", StringComparer.Ordinal);
        var words = string.Join(' ', parts.SkipWhile(p => p is "com" or "axis" or "action" or "fixed" or "unlimited").Select(p => p.Replace('_', ' ')));
        words = words.Length == 0 ? template : char.ToUpper(words[0], CultureInfo.InvariantCulture) + words[1..];
        return whileActive ? words + " while the rule is active" : words;
    }

    /// <summary>Whether OADM knows the template's name.</summary>
    public static bool IsKnown(string template) => Names.ContainsKey(template);
}
