using System.Globalization;

using Oadm.Plugins.Provisioning.Model;

namespace Oadm.Plugins.Schedules.Client;

/// <summary>Every text of the Schedules page and its wizard (CLAUDE.md "Wording"), one phrase per concept.</summary>
public static class SchedulesTexts
{
    // ---- Page ----
    public const string Subtitle = "Runs tasks on devices at a set time.";
    public const string Reading = "Reading the schedules";
    public const string NewSchedule = "New schedule";
    public const string Edit = "Edit";
    public const string RunNow = "Run now";
    public const string RunNowTip = "Starts a run at once";
    public const string TurnOn = "Turn on";
    public const string TurnOff = "Turn off";
    public const string TurnedOff = "Turned off";
    public const string Delete = "Delete";
    public const string Search = "Search schedules";
    public const string Empty = "Create a schedule to run tasks at a set time.";
    public const string NoRuns = "No runs yet.";
    public const string ChooseSchedule = "Choose a schedule to see its runs.";
    public const string DeleteTitle = "Delete schedule";
    public const string ManageOnly = "Your role cannot change schedules";

    public static string DeleteMessage(string name) => $"Delete the schedule {name}? A run that goes on runs to its end.";

    // Grid columns
    public const string Name = "Name";
    public const string Devices = "Devices";
    public const string When = "When";
    public const string Then = "Then";
    public const string NextRun = "Next run";
    public const string LastRun = "Last run";
    public const string Owner = "Owner";
    public const string Started = "Started";
    public const string Ended = "Ended";
    public const string Result = "Result";
    public const string Device = "Device";
    public const string Step = "Step";
    public const string Message = "Message";

    /// <summary>The devices of a schedule: "All devices", "Tag Site A and 12 devices".</summary>
    public static string DevicesText(ScheduleDevicesKind kind, int count, IReadOnlyList<string> tags) =>
        kind == ScheduleDevicesKind.All ? AllDevices : Oadm.Sdk.Client.Controls.DeviceSelectorTexts.Brief(count, tags);

    public const string AllDevices = "All devices";

    // ---- Wizard ----
    public const string NewTitle = "New schedule";
    public const string EditTitle = "Edit schedule";
    public const string ReadingSchedule = "Reading the schedule";
    public const string StepSchedule = "Schedule";
    public const string StepDevices = "Devices";
    public const string StepWhen = "When";
    public const string StepThen = "Then";
    public const string StepOverview = "Overview";
    public const string Back = "Back";
    public const string Next = "Next";
    public const string Save = "Save";

    // Step Schedule
    public const string Enabled = "Enabled";
    public const string TurnedOn = "Turned on";
    public const string Stages = "Stages";
    public const string RollOutInStages = "Roll out in stages";
    public const string StagesTip = "One device first; a failure stops the run";
    public const string EnterName = "Enter a name.";

    public static string NameTooLong => string.Create(CultureInfo.InvariantCulture, $"At most {SchedulesInfo.MaxNameLength} characters.");

    // Step Devices
    public const string ChooseDevices = "Choose devices";
    public const string ChooseDevicesFirst = "Choose at least one device or tag.";
    public const string SelectedDevices = "Selected devices: ";
    public const string PickerHeading = "Devices of the schedule";
    public const string Address = "Address";
    public const string HostName = "Host name";
    public const string Model = "Model";

    // Step When
    public const string Once = "Once";
    public const string Daily = "Daily";
    public const string Weekly = "Weekly";
    public const string Monthly = "Monthly";
    public const string Date = "Date";
    public const string Days = "Days";
    public const string DayOfMonth = "Day of month";
    public const string Time = "Time";
    public const string WindowEnd = "Window end";
    public const string WindowEndInfo = "No device starts after this time.";
    public const string Optional = "Optional";
    public const string ChooseDate = "Choose a date.";
    public const string ChooseDays = "Choose at least one day.";
    public const string ChooseDay = "Choose a day.";
    public const string EnterTime = "Enter a time like 23:00.";
    public const string TimePlaceholder = "23:00";
    public const string WindowPlaceholder = "05:00";
    public const string SameAsStart = "Choose another time than the start.";

    // Step Then
    public const string AddAction = "Add action";
    public const string AddActionTip = "Adds an action at the end";
    public const string MoveUp = "Move up";
    public const string MoveDown = "Move down";
    public const string Remove = "Remove";
    public const string RemoveTip = "Removes the action from the schedule";
    public const string FitView = "Fit view";
    public const string AddFirstAction = "Add the first action.";
    public const string SearchActions = "Search actions";
    public const string AddActionFirst = "Add at least one action.";
    public const string ChooseAction = "Choose an action to change its settings.";
    public const string AddAfterTip = "Add an action after this one";
    public const string MyAxisNote = "Downloads from axis.com with the owner's MyAxis sign-in.";
    public const string RestartNote = "Restarts the device and waits until it answers again.";

    public static string TooManyActions => string.Create(CultureInfo.InvariantCulture, $"At most {SchedulesInfo.MaxActions} actions.");

    // Step Overview
    public const string Estimated = "Estimated";

    /// <summary>The action kinds' names (the server's <see cref="ActionKindInfo.Name"/> wins where known).</summary>
    public static string ActionName(NodeKind kind) => kind switch
    {
        NodeKind.UpgradeOnline => "Upgrade AXIS OS online",
        NodeKind.UpgradeFiles => "Upgrade AXIS OS from files",
        NodeKind.Configuration => "Configuration",
        NodeKind.Restart => "Restart",
        NodeKind.Certificate => "Certificate",
        NodeKind.Dot1x => "802.1X",
        NodeKind.VapixCommands => "VAPIX commands",
        _ => kind.ToString(),
    };

    // ---- Runs ----
    public const string Running = "Running";
    public const string Done = "Done";
    public const string DoneWithWarnings = "Done with warnings";
    public const string Failed = "Failed";
    public const string Stopped = "Stopped";
    public const string Missed = "Missed";
    public const string Skipped = "Skipped";

    public static string ResultText(RunResult result) => result switch
    {
        RunResult.Running => Running,
        RunResult.Done => Done,
        RunResult.DoneWithWarnings => DoneWithWarnings,
        RunResult.Failed => Failed,
        RunResult.Stopped => Stopped,
        RunResult.Missed => Missed,
        RunResult.Skipped => Skipped,
        _ => result.ToString(),
    };
}
