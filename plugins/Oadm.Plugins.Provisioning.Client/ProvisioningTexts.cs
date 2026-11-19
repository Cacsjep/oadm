using System.Globalization;

using Oadm.Plugins.Provisioning.Engine;
using Oadm.Plugins.Provisioning.Model;

namespace Oadm.Plugins.Provisioning.Client;

/// <summary>Every text of the Provisioning page and its profile editor (CLAUDE.md "Wording"), one phrase per concept.</summary>
public static class ProvisioningTexts
{
    public const string Subtitle = "Sets up factory-new cameras when they get an address from the DHCP server.";

    public const string Reading = "Reading provisioning";

    // ---- DHCP card ----
    public const string DhcpServer = "DHCP server";
    public const string DhcpNotInstalled = "Not available";
    public const string TurnOn = "Turn on";
    public const string Edit = "Edit";
    public const string DhcpEditTip = "Opens the DHCP server page";

    // ---- Toolbar ----
    public const string StartProvisioning = "Start provisioning";
    public const string StopProvisioning = "Stop provisioning";
    public const string StopTitle = "Stop provisioning";
    public const string StopMessage = "New cameras are no longer set up. Cameras that run a node finish that node first.";
    public const string Stop = "Stop";
    public const string NotStarted = "Not started";
    public const string Profile = "Profile";
    public const string NoProfile = "No profile";
    public const string ManageProfiles = "Manage profiles";
    public const string ReadingProfiles = "Reading the profiles";
    public const string NoProfiles = "Press New to create the first profile.";
    public const string New = "New";
    public const string Activate = "Activate";
    public const string ActivateTip = "Provisioning uses it from the next Start";
    public const string ActiveAlready = "This profile is active.";
    public const string Active = "Active";
    public const string Delete = "Delete";
    public const string Close = "Close";
    public const string NodesHeader = "Nodes";
    public const string Time = "Time";

    public static string DeleteProfileMessage(string name) => $"The profile {name} is deleted.";
    public const string DeleteProfile = "Delete profile";
    public const string DhcpFirst = "Turn on the DHCP server first.";
    public const string ProfileFirst = "Choose the active profile first.";
    public const string FixProfileFirst = "Fix the profile first.";
    public const string StartedAlready = "Provisioning is started.";
    public const string StopFirst = "Stop provisioning first.";

    // ---- Cameras ----
    public const string Cameras = "Cameras";
    public const string RunFailedAgain = "Run failed again";
    public const string RunFailedAgainTip = "Runs only the red nodes again";
    public const string Continue = "Continue";
    public const string ContinueTip = "Lets the cameras go on past Wait for approval";
    public const string Remove = "Remove";
    public const string RemoveTip = "Removes the rows; the cameras stay in OADM";
    public const string SearchCameras = "Search MAC, model, address";
    public const string NoMatch = "No camera matches the search.";
    public const string EmptyDhcpFirst = "Turn on the DHCP server first.";
    public const string EmptyProfileFirst = "Create a profile in Manage profiles first.";
    public const string EmptyNotStarted = "Press Start provisioning, then plug in factory-new cameras.";
    public const string EmptyStarted = "Plug in factory-new cameras. They show here when they get an address.";
    public const string RemoveTitle = "Remove cameras";

    public static string RemoveMessage(int count) => count == 1
        ? "The row is removed. The camera stays in OADM."
        : $"{count.ToString("N0", CultureInfo.CurrentCulture)} rows are removed. The cameras stay in OADM.";

    // Column headers
    public const string MacAddress = "MAC address";
    public const string Model = "Model";
    public const string Serial = "Serial number";
    public const string Address = "IP address";
    public const string State = "State";
    public const string Node = "Node";
    public const string Problem = "Problem";

    // States (the row's state chip; one phrase each)
    public const string Queued = "Queued";
    public const string Running = "Running";
    public const string WaitingForApproval = "Waiting for approval";
    public const string Done = "Done";
    public const string DoneWithWarnings = "Done with warnings";
    public const string Failed = "Failed";
    public const string NotNew = "Not new, left as it is";

    public static string StateText(CameraState state, bool waiting) => state switch
    {
        CameraState.Queued => Queued,
        CameraState.Running when waiting => WaitingForApproval,
        CameraState.Running => Running,
        CameraState.Done => Done,
        CameraState.DoneWithWarnings => DoneWithWarnings,
        CameraState.Failed => Failed,
        CameraState.NotNew => NotNew,
        _ => state.ToString(),
    };

    public static string Started(DateTimeOffset at, string? by) =>
        "Started " + at.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + (string.IsNullOrEmpty(by) ? string.Empty : " by " + by);

    // ---- A camera's flow ----
    public const string NotRun = "Not run";
    public const string WaitsForContinue = "Waits for Continue";

    // ---- Profile editor ----
    public const string NewProfileTitle = "New profile";
    public const string EditProfileTitle = "Edit profile";
    public const string ReadingProfile = "Reading the profile";
    public const string Flow = "Flow";
    public const string MoveUp = "Move up";
    public const string MoveDown = "Move down";
    public const string RemoveNode = "Remove";
    public const string RemoveNodeTip = "Removes the node from the profile";
    public const string FixedTip = "Set password and Add to OADM always come first";
    public const string Name = "Name";
    public const string EnterName = "Enter a name.";
    public const string ContinueOnError = "Continue on error";
    public const string ContinueOnErrorTip = "The next nodes run after an error";
    public const string AlwaysStopsHint = "An error here always stops the camera's flow.";
    public const string NoSettings = "This node has no settings.";

    public const string EnterMask = "Enter a subnet mask.";
    public const string UnknownNode = "This version of OADM cannot change this node.";
    public const string StoredSecret = "Kept on the server";
    public const string StoredFile = "A file chosen before";
    public const string ChooseFiles = "Choose files";

    public static string TypeIt(string label) => $"Type the {label.ToLowerInvariant()}.";

    public static string ChooseIt(string label) => $"Choose the {label.ToLowerInvariant()}.";

    public static string FileCount(int count) => count == 1 ? "1 file" : count.ToString("N0", CultureInfo.CurrentCulture) + " files";

    // Upgrade AXIS OS online and from files
    public const string OsTarget = "Install";
    public const string OwnTrack = "Newest of the camera's own track";
    public const string LatestLts = "Newest LTS";
    public const string LatestActive = "Newest of the active track";
    public const string OnlyIfOlder = "Only if older than";
    public const string MyAxisHint = "Downloads from axis.com with the MyAxis sign-in of who starts provisioning.";
    public const string OsFiles = "AXIS OS files";
    public const string ChooseOsFiles = "Choose the AXIS OS files.";
    public const string OsFilesHint = "Each camera gets the file for its model. A file older than the camera's AXIS OS is never installed.";

    // Network settings
    public const string SearchDomain = "Search domain";

    // Date and time
    public const string TimeSource = "Time from";
    public const string NtpFromDhcp = "The NTP servers DHCP gives";
    public const string NtpOwn = "These NTP servers";
    public const string NtpOadm = "OADM's NTP server";

    // Configuration
    public const string ConfigSource = "Settings from";
    public const string ConfigFromFile = "A configuration file";
    public const string ConfigFromCamera = "A template camera";
    public const string ConfigFile = "Configuration file";
    public const string ConfigFileRule = "Choose a file of Export configuration (.json).";
    public const string TemplateCamera = "Template camera";
    public const string ChooseCamera = "Choose camera";
    public const string ChooseTemplate = "Choose the template camera.";
    public const string ChooseTemplateHeading = "Choose the template camera";
    public const string Apply = "Apply";
    public const string Parameters = "Parameters";
    public const string ActionRules = "Action rules";
    public const string Schedules = "Schedules";
    public const string ChooseAreas = "Choose what to apply.";

    // Users
    public const string Accounts = "Accounts";
    public const string AddAccount = "Add account";
    public const string AddAccountFirst = "Add an account.";
    public const string AccountName = "User name";
    public const string AccountPassword = "Password";
    public const string Role = "Role";
    public const string Ptz = "PTZ control";
    public const string EnterAccountName = "Enter a user name.";
    public const string AccountNameRule = "Use 1 to 14 letters and digits.";
    public const string RootIsSetPassword = "root is the account of Set password.";
    public const string RemoveAccount = "Remove the account";
    public const string RemoveFile = "Remove the file";

    // Certificate, 802.1X, Autofocus
    public const string CertificateMode = "Certificate";
    public const string CertificatePolicy = "From PKI's HTTPS certificate policy";
    public const string CertificateEst = "EST enrollment (OADM PRO)";
    public const string Dot1xHint = "Uses the certificate and CA of PKI's 802.1X policy.";
    public const string Dot1xLine = "PKI's 802.1X policy";
    public const string AutofocusHint = "Starts the autofocus and waits until it is done. Cameras without remote focus skip it.";
    public const string AutofocusLine = "Finds the focus";

    // SD card
    public const string SdCardDo = "Prepare";
    public const string Format = "Format";
    public const string Encrypt = "Encrypt";
    public const string Passphrase = "Passphrase";
    public const string KeepDays = "Delete recordings older than (days)";
    public const string KeepDaysHint = "Optional";
    public const string ChooseSdCard = "Choose what to do with the SD card.";

    // VAPIX commands
    public const string Commands = "Commands";
    public const string AddCommand = "Add command";
    public const string AddCommandFirst = "Add a command.";
    public const string Command = "Command";
    public const string ChooseCommand = "Choose a command.";
    public const string RemoveCommand = "Remove the command";
    public const string EnterValue = "Enter a value.";
    public const string EnterNumber = "Enter a number.";

    // Drafts
    public const string DraftTip = "Saves a draft: the profile cannot be active until its errors are fixed";
    public const string HasErrors = "Has errors";
    public const string WaitHint = "The camera waits here until you press Continue on its row.";
    public const string ChooseNode = "Choose a node to change its settings.";
    public const string Save = "Save";
    public const string Saving = "Saving the profile";
    public const string SaveTip = "Stores the profile";
    public const string DefaultName = "New profile";
    public const string Optional = "Optional";

    // Set password
    public const string Password = "Root password";
    public const string FromCredentials = "From the credential list";
    public const string TypePassword = "Type a password";
    public const string Credential = "Credential";
    public const string NewPassword = "New password";
    public const string ConfirmPassword = "Confirm password";
    public const string ChooseCredential = "Choose a password of the user root.";
    public const string NoRootCredential = "The credential list has no password of root.";
    public const string EnterPassword = "Enter a password.";
    public const string PasswordRule = "1 to 64 printable ASCII characters.";
    public const string PassphraseTooLong = "The passphrase is too long.";
    public const string PassphraseCharacters = "The passphrase contains characters the camera does not accept.";
    public const string PasswordsDiffer = "The passwords do not match.";
    public const string PasswordStoredHint = "Added to the credential list when you save; never shown again.";

    // Set IP and other address fields
    public const string FirstAddress = "First address";
    public const string LastAddress = "Last address";
    public const string SubnetMask = "Subnet mask";
    public const string DefaultRouter = "Default router";
    public const string DnsServers = "DNS servers";
    public const string EnterAddress = "Enter an IP address.";
    public const string NotAnAddress = "Not an IP address.";
    public const string NotAMask = "Not a subnet mask.";
    public const string LastBeforeFirst = "Must not be before the first address.";
    public const string ListHint = "Separate them with commas.";

    // Date and time
    public const string TimeZone = "Time zone";
    public const string ChooseTimeZone = "Choose a time zone.";
    public const string NtpServers = "NTP servers";
    public const string NtpHint = "Empty: the servers DHCP gives.";
    public const string Nts = "Use NTS where the camera has it";

    // Add to OADM
    public const string Tags = "Tags";

    public static string NotAnAddressList(string value) => $"{value} is not an IP address.";

    // ---- The "?" of every field of the profile editor (at most about 8 words, never the label again) ----
    public const string NameHelp = "Shown in Manage profiles and on the page";
    public const string PasswordHelp = "Each new camera gets this root password";
    public const string CredentialHelp = "A saved password of the user root";
    public const string NewPasswordHelp = "Up to 64 letters, digits and symbols";
    public const string ConfirmHelp = "Type the same password again";
    public const string TagsHelp = "Device tags in OADM, separated by commas";
    public const string FirstAddressHelp = "The pool starts at this address";
    public const string LastAddressHelp = "The pool ends at this address";
    public const string MaskHelp = "The mask of the fixed address";
    public const string RouterHelp = "The gateway of the camera's network";
    public const string DnsHelp = "Up to 2, separated by commas";
    public const string TimeZoneHelp = "Type a city to jump to it";
    public const string TimeSourceHelp = "Where the camera gets its time";
    public const string NtpServersHelp = "Up to 5 host names or IP addresses";
    public const string NtsHelp = "The camera checks that the time is genuine";
    public const string OsTargetHelp = "The AXIS OS version each camera gets";
    public const string OnlyIfOlderHelp = "Cameras with this version or newer skip it";
    public const string OsFilesHelp = "One file per camera model";
    public const string SearchDomainHelp = "Added to short host names";
    public const string ConfigSourceHelp = "Where the settings are copied from";
    public const string ConfigFileHelp = "A file of Export configuration";
    public const string TemplateCameraHelp = "A camera set up the way you want";
    public const string ApplyHelp = "What is copied to each camera";
    public const string AccountNameHelp = "1 to 14 letters and digits";
    public const string RoleHelp = "What the account may do";
    public const string PtzHelp = "The account may move the camera";
    public const string SecretHelp = "Kept encrypted on the server";
    public const string CertificateHelp = "Where the HTTPS certificate comes from";
    public const string PrepareHelp = "Format erases everything on the card";
    public const string KeepDaysHelp = "Empty: the camera keeps its setting";
    public const string CommandHelp = "A command of the VAPIX Commander library";
}
