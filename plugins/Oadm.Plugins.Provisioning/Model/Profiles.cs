using System.Text.Json;

namespace Oadm.Plugins.Provisioning.Model;

/// <summary>The kinds of nodes a profile is made of (docs/spec/provisioning.md "Nodes"). Stored by name, never renamed.</summary>
public enum NodeKind
{
    /// <summary>Set password (fixed, first): the first root password from the credential list.</summary>
    SetPassword = 0,

    /// <summary>Add to OADM (fixed, second): adds the camera with that login and the tags.</summary>
    AddToOadm = 1,

    /// <summary>Wait for approval: the camera waits until a person presses Continue on its row.</summary>
    WaitForApproval = 2,

    /// <summary>Set IP: the next free address of the node's own pool, written as a fixed address.</summary>
    SetIp = 3,

    /// <summary>Date and time: NTP servers and time zone, through the Date and time task.</summary>
    DateTime = 4,

    /// <summary>Upgrade AXIS OS online: the newest version of a track from axis.com, with the MyAxis sign-in of who started.</summary>
    UpgradeOnline = 5,

    /// <summary>Upgrade AXIS OS from files: each camera the file for its model.</summary>
    UpgradeFiles = 6,

    /// <summary>Network settings: host name from a pattern, DNS servers and search domain.</summary>
    NetworkSettings = 7,

    /// <summary>Configuration: a configuration file or a template camera, the chosen areas.</summary>
    Configuration = 8,

    /// <summary>Users: more accounts on the camera.</summary>
    Users = 9,

    /// <summary>Certificate: a PKI issuing policy's certificate, or EST enrollment.</summary>
    Certificate = 10,

    /// <summary>802.1X with PKI's 802.1X policy.</summary>
    Dot1x = 11,

    /// <summary>SD card: format, encrypt, how long recordings stay.</summary>
    SdCard = 12,

    /// <summary>Autofocus: runs the autofocus and waits until it is done.</summary>
    Autofocus = 13,

    /// <summary>VAPIX commands of the VAPIX Commander library with their values.</summary>
    VapixCommands = 14,
}

/// <summary>One node of a profile: its kind, its settings (the kind's own record as JSON) and whether the flow goes on after an error.</summary>
public sealed record FlowNode
{
    /// <summary>The node's id within its profile ("n1"): letters, digits and hyphens, at most 32 characters.</summary>
    public string Id { get; init; } = string.Empty;

    public NodeKind Kind { get; init; }

    /// <summary>On error the flow goes on with the next node (default). Ignored for Set password, which always stops.</summary>
    public bool ContinueOnError { get; init; } = true;

    /// <summary>The settings record of the kind (<see cref="Nodes.IProvisioningNode"/>); null: the kind's defaults.</summary>
    public JsonElement? Settings { get; init; }
}

/// <summary>A profile: a name and a straight chain of nodes, Set password and Add to OADM first.</summary>
public sealed record Profile
{
    public Guid Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public IReadOnlyList<FlowNode> Nodes { get; init; } = [];
}

/// <summary>The stored profiles and which one is active (plugin setting <c>profiles</c>).</summary>
public sealed record ProfileBook
{
    public IReadOnlyList<Profile> Profiles { get; init; } = [];

    /// <summary>The active profile; null: none.</summary>
    public Guid? ActiveId { get; init; }
}

/// <summary>Settings of Set password: the credential list entry of root whose password is set (never the password itself).</summary>
public sealed record SetPasswordSettings(Guid? CredentialId = null);

/// <summary>Settings of Add to OADM: the tags the camera gets.</summary>
public sealed record AddToOadmSettings(IReadOnlyList<string>? Tags = null);

/// <summary>Settings of Wait for approval: none.</summary>
public sealed record WaitForApprovalSettings;

/// <summary>Settings of Set IP: the pool (first and last address), subnet mask, router and DNS servers.</summary>
public sealed record SetIpSettings(
    string? First = null,
    string? Last = null,
    string? SubnetMask = null,
    string? Gateway = null,
    IReadOnlyList<string>? DnsServers = null);

/// <summary>
/// Settings of Date and time: the time zone and the NTP servers (none: the servers DHCP gives), or OADM's NTP server
/// (<see cref="OadmNtp"/>: the NTP server plugin's address for the camera filled in when it runs).
/// </summary>
public sealed record DateTimeSettings(
    string? TimeZone = null,
    IReadOnlyList<string>? NtpServers = null,
    bool Nts = false,
    bool OadmNtp = false);

/// <summary>Which AXIS OS of axis.com Upgrade AXIS OS online installs.</summary>
public enum OsTarget
{
    /// <summary>The newest version of the camera's own track (11.11 stays 11.11).</summary>
    OwnTrack = 0,

    /// <summary>The newest version of the newest LTS track.</summary>
    LatestLts = 1,

    /// <summary>The newest version of the active track.</summary>
    LatestActive = 2,
}

/// <summary>Settings of Upgrade AXIS OS online: the target and, optionally, only for cameras older than a version.</summary>
public sealed record UpgradeOnlineSettings(OsTarget Target = OsTarget.OwnTrack, string? OnlyIfOlderThan = null);

/// <summary>An AXIS OS file of the vault (<c>fileId</c>) with its name ("P3265-LVE_12_6_95.bin").</summary>
public sealed record OsFile(Guid? FileId = null, string? Name = null);

/// <summary>Settings of Upgrade AXIS OS from files: the files (one per model) and whether an older version may go on.</summary>
public sealed record UpgradeFilesSettings(IReadOnlyList<OsFile>? Files = null, bool AllowDowngrade = false);

/// <summary>Settings of Network settings: the host name pattern with its first number, the DNS servers and the search domain.</summary>
public sealed record NetworkSettingsSettings(
    string? HostName = null,
    int StartNumber = 1,
    IReadOnlyList<string>? DnsServers = null,
    string? SearchDomain = null);

/// <summary>Where Configuration takes the settings from.</summary>
public enum ConfigSource
{
    /// <summary>A configuration file of Device configuration (Export configuration).</summary>
    File = 0,

    /// <summary>A camera OADM manages, read when the node runs.</summary>
    TemplateCamera = 1,
}

/// <summary>Settings of Configuration: the file (<c>fileId</c>) or the template camera's serial number, and the areas to apply.</summary>
public sealed record ConfigurationSettings(
    ConfigSource Source = ConfigSource.File,
    Guid? FileId = null,
    string? TemplateSerial = null,
    bool Parameters = true,
    bool ActionRules = false,
    bool Schedules = false);

/// <summary>The role of an account on the camera.</summary>
public enum CameraRole
{
    Viewer = 0,

    Operator = 1,

    Administrator = 2,
}

/// <summary>One account of Users: name, role, PTZ control and its typed password (<c>secretId</c> in the vault).</summary>
public sealed record UserAccount(string? Name = null, CameraRole Role = CameraRole.Viewer, bool Ptz = false, Guid? SecretId = null);

/// <summary>Settings of Users: the accounts to add.</summary>
public sealed record UsersSettings(IReadOnlyList<UserAccount>? Accounts = null);

/// <summary>How Certificate gives the camera its certificate.</summary>
public enum CertificateMode
{
    /// <summary>The HTTPS certificate of a PKI issuing policy, HTTPS on.</summary>
    Policy = 0,

    /// <summary>EST enrollment: the camera enrolls and renews by itself (OADM PRO).</summary>
    Est = 1,
}

/// <summary>Settings of Certificate: the mode and the PKI policy (none: the first HTTPS certificate policy).</summary>
public sealed record CertificateSettings(CertificateMode Mode = CertificateMode.Policy, Guid? PolicyId = null);

/// <summary>Settings of 802.1X: none (PKI's 802.1X policies decide).</summary>
public sealed record Dot1xSettings;

/// <summary>Settings of SD card: format, encrypt with the typed passphrase (<c>secretId</c>), how many days recordings stay.</summary>
public sealed record SdCardSettings(bool Format = true, bool Encrypt = false, Guid? SecretId = null, int? MaxAgeDays = null);

/// <summary>Settings of Autofocus: none.</summary>
public sealed record AutofocusSettings;

/// <summary>One command of the VAPIX Commander library with the values of its fields.</summary>
public sealed record VapixCommandStep(string? CommandId = null, IReadOnlyDictionary<string, JsonElement>? Values = null);

/// <summary>Settings of VAPIX commands: the commands in order.</summary>
public sealed record VapixCommandsSettings(IReadOnlyList<VapixCommandStep>? Commands = null);
