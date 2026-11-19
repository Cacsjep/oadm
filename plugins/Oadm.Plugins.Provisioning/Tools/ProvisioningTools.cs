using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;

using Oadm.Plugins.Provisioning.Engine;
using Oadm.Plugins.Provisioning.Model;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Provisioning.Tools;

/// <summary>
/// The page's reads and changes as tools (docs/spec/mcp.md "Plugins"): the status, the profiles, the camera table (paged),
/// the credential list's names; saving, deleting and activating a profile, Start and Stop, Run failed again, Continue and
/// removing rows, each a preview with the page's checks and a run through the page's own code. The rights of the page
/// methods: Administrator by default, users with all devices only. A typed password is a secret of the person, never an argument.
/// </summary>
internal static class ProvisioningTools
{
    private static readonly PluginToolSecret Password = new("password", "root password of Set password");

    private static readonly PluginToolSecret NodeSecret = new("secret", "password or passphrase of a node");

    public static PluginPageTools<ProvisioningPlugin> Set { get; } = new PluginPageTools<ProvisioningPlugin>()
        .Read<NoArgs, StatusAnswer>(
            new("status", "Provisioning status", "Whether provisioning is started, the active profile, the DHCP server, the camera counts and the warnings."),
            ProvisioningMethods.GetStatus,
            async (plugin, _, _, ct) => StatusAnswer.Of(await plugin.StatusAsync(ct).ConfigureAwait(false)))
        .Read<NoArgs, ProfilesAnswer>(
            new("profiles", "Provisioning profiles", "The profiles: id, name, number of nodes, whether active and whether without errors."),
            ProvisioningMethods.ListProfiles,
            (plugin, _, _, _) => Task.FromResult(ProfilesAnswer.Of(plugin.ListProfiles())))
        .Read<IdArgs, ProfileAnswer>(
            new("profile", "Provisioning profile", "One profile with its nodes in order, their settings and the errors and warnings of each node."),
            ProvisioningMethods.GetProfile,
            async (plugin, _, input, ct) => ProfileAnswer.Of(await plugin.GetProfileAsync(input.Args.Id, ct).ConfigureAwait(false)))
        .Read<NoArgs, CredentialsAnswer>(
            new("credentials", "Credential list names", "The credential list entries (id and user name, never a password) Set password can use."),
            ProvisioningMethods.Credentials,
            async (plugin, _, _, ct) => new CredentialsAnswer([.. (await plugin.CredentialsAsync(ct).ConfigureAwait(false)).Select(c => new CredentialRow(c.Id, c.UserName))]))
        .Read<NoArgs, CommandsAnswer>(
            new("vapix_commands", "Provisioning VAPIX commands", "The VAPIX Commander commands a VAPIX commands node can run, with their fields (name, type, required, options)."),
            ProvisioningMethods.VapixCommands,
            (plugin, _, _, _) => Task.FromResult(CommandsAnswer.Of(plugin.VapixCommands())))
        .Read<NoArgs, CamerasAnswer>(
            new("cameras", "Provisioning cameras", "The cameras of the session by MAC address: model, address, state, the node it is on and its message.") { Paged = true },
            ProvisioningMethods.ListCameras,
            (plugin, _, input, _) => Task.FromResult(CamerasAnswer.Of(plugin, input.Page)))
        .Change<SaveArgs, SavePlan, SavedAnswer>(
            new("profile_save", "Save provisioning profile", "Adds a profile, or changes the one with id. Nodes in order, Set password and Add to OADM first. With new_password the person enters the root password of Set password.")
            {
                Secrets = [Password],
            },
            ProvisioningMethods.SaveProfile,
            async (plugin, _, input, ct) =>
            {
                var request = input.Args.Request(input.Secret(Password.Name));
                var (profile, check) = await plugin.CheckSaveAsync(request, ct).ConfigureAwait(false);
                return new SavePlan(profile.Name, check.NodeErrors.Count, check.NodeWarnings.Count) { SecretsNeeded = input.Args.NewPassword ? null : [] };
            },
            async (plugin, _, input, _, ct) =>
            {
                var reply = await plugin.SaveProfileAsync(input.Args.Request(input.Secret(Password.Name)), ct).ConfigureAwait(false);
                return new SavedAnswer(reply.Profile.Id, reply.Check.IsValid);
            })
        .Change<NoArgs, SecretPlan, SecretAnswer>(
            new("secret_store", "Keep a node password", "The person enters a password of a Users account or the SD card passphrase; the answer is its secret_id for the node's settings. The password never comes back.")
            {
                Secrets = [NodeSecret],
            },
            ProvisioningMethods.StoreSecret,
            (_, _, input, _) =>
            {
                if (input.IsRun && input.Secret(NodeSecret.Name) is { } value && (value.Length is < 1 or > NodeVault.MaxSecretLength || value.Any(char.IsControl)))
                {
                    throw new PluginToolInputException("secrets.secret", $"Type 1 to {NodeVault.MaxSecretLength} characters, no line breaks.");
                }

                return Task.FromResult(new SecretPlan());
            },
            async (plugin, _, input, _, ct) =>
                new SecretAnswer((await plugin.StoreSecretAsync(new SecretRequest(input.Secret(NodeSecret.Name)), ct).ConfigureAwait(false)).Id))
        .Change<FileArgs, FilePlan, FileAnswer>(
            new("file_store", "Keep a node file", "Keeps a file the caller uploaded (an AXIS OS file, a configuration file of Export configuration) for a node; the answer is its file_id for the node's settings."),
            ProvisioningMethods.StoreFile,
            async (_, caller, input, ct) =>
                await caller.Uploads.FindAsync(input.Args.UploadId?.Trim() ?? string.Empty, ct).ConfigureAwait(false) is { } upload
                    ? new FilePlan(upload.Name)
                    : throw new PluginToolInputException("upload_id", "No uploaded file has this id. Upload it again."),
            async (plugin, caller, input, _, ct) =>
            {
                var file = await plugin.StoreFileAsync(caller, input.Args.UploadId.Trim(), ct).ConfigureAwait(false);
                return new FileAnswer(file.Id, file.Name, file.Size);
            })
        .Change<IdArgs, ProfilePlan, DoneAnswer>(
            new("profile_delete", "Delete provisioning profile", "Deletes a profile. A running session keeps its own copy.") { Destructive = true },
            ProvisioningMethods.DeleteProfile,
            (plugin, _, input, _) => Task.FromResult(new ProfilePlan((plugin.RunningProfiles.Find(input.Args.Id) ?? throw new KeyNotFoundException(ProvisioningPlugin.ProfileGone)).Name)),
            async (plugin, _, input, _, ct) =>
            {
                await plugin.DeleteProfileAsync(input.Args.Id, ct).ConfigureAwait(false);
                return new DoneAnswer("Deleted.");
            })
        .Change<IdArgs, ProfilePlan, DoneAnswer>(
            new("profile_activate", "Activate provisioning profile", "Makes a profile without errors the active one; the next Start runs it."),
            ProvisioningMethods.ActivateProfile,
            async (plugin, _, input, ct) => new ProfilePlan((await plugin.CheckActivateAsync(input.Args.Id, ct).ConfigureAwait(false)).Name),
            async (plugin, _, input, _, ct) =>
            {
                await plugin.ActivateAsync(input.Args.Id, ct).ConfigureAwait(false);
                return new DoneAnswer("Active.");
            })
        .Change<NoArgs, StartPlan, DoneAnswer>(
            new("start", "Start provisioning", "Starts provisioning with the active profile: every factory-new camera that gets an address from OADM's DHCP server runs it."),
            ProvisioningMethods.Start,
            async (plugin, _, _, ct) =>
            {
                var profile = await plugin.CheckStartAsync(ct).ConfigureAwait(false);
                return new StartPlan(profile.Name, (await plugin.StatusAsync(ct).ConfigureAwait(false)).Warnings);
            },
            async (plugin, caller, _, _, ct) =>
            {
                await plugin.StartSessionAsync(caller, ct).ConfigureAwait(false);
                return new DoneAnswer("Provisioning started.");
            })
        .Change<NoArgs, StopPlan, DoneAnswer>(
            new("stop", "Stop provisioning", "Stops provisioning: running flows stop waiting, the reserved addresses are free again."),
            ProvisioningMethods.Stop,
            (plugin, _, _, _) => Task.FromResult(plugin.RunningEngine.Session is null ? throw new InvalidOperationException(ProvisioningEngine.NotStarted) : new StopPlan(plugin.RunningEngine.Counts().Running)),
            async (plugin, _, _, _, ct) =>
            {
                await plugin.StopSessionAsync(ct).ConfigureAwait(false);
                return new DoneAnswer("Provisioning stopped.");
            })
        .Change<MacsArgs, CamerasPlan, CountAnswer>(
            new("cameras_run_failed_again", "Run failed nodes again", "Runs the red nodes of failed cameras again, in flow order."),
            ProvisioningMethods.RunFailedAgain,
            (plugin, _, input, _) => Task.FromResult(Plan(plugin, input.Args, r => r.State == CameraState.Failed)),
            (plugin, _, input, _, _) => Task.FromResult(new CountAnswer(plugin.RunningEngine.RunFailedAgain(input.Args.Macs))))
        .Change<MacsArgs, CamerasPlan, CountAnswer>(
            new("cameras_continue", "Continue cameras", "Presses Continue for cameras that wait for approval."),
            ProvisioningMethods.Continue,
            (plugin, _, input, _) => Task.FromResult(Plan(plugin, input.Args, r => r.WaitingForApproval)),
            (plugin, caller, input, _, _) => Task.FromResult(new CountAnswer(plugin.RunningEngine.Continue(input.Args.Macs, caller.UserName))))
        .Change<MacsArgs, CamerasPlan, CountAnswer>(
            new("cameras_remove", "Remove camera rows", "Removes rows from the camera table; the cameras stay in OADM.") { Destructive = true },
            ProvisioningMethods.RemoveCameras,
            (plugin, _, input, _) => Task.FromResult(Plan(plugin, input.Args, r => r.State is not (CameraState.Queued or CameraState.Running))),
            (plugin, _, input, _, _) => Task.FromResult(new CountAnswer(plugin.RunningEngine.Remove(input.Args.Macs))));

    /// <summary>The rows of the given MAC addresses that the change applies to; none is a refusal.</summary>
    private static CamerasPlan Plan(ProvisioningPlugin plugin, MacsArgs args, Func<CameraRow, bool> applies)
    {
        var rows = (args.Macs ?? []).Select(plugin.RunningEngine.Find).OfType<CameraRow>().Where(applies).ToList();
        return rows.Count == 0
            ? throw new PluginToolInputException("macs", "None of these cameras can do this now.")
            : new CamerasPlan([.. rows.Select(r => r.Mac)]);
    }
}

[Description("No arguments.")]
internal sealed record NoArgs;

internal sealed record IdArgs([property: Description("The profile id.")] Guid Id);

internal sealed record MacsArgs(
    [property: Description("MAC addresses of cameras in the table, e.g. AC:CC:8E:01:02:03."), MaxLength(1_000)] IReadOnlyList<string> Macs);

internal sealed record NodeArgs(
    [property: Description("The node's id in the profile: letters, digits and hyphens."), MaxLength(32)] string Id,
    [property: Description("set_password, add_to_oadm, wait_for_approval, upgrade_online, upgrade_files, network_settings, date_time, configuration, users, certificate, dot1x, sd_card, autofocus, vapix_commands or set_ip.")] NodeKind Kind,
    [property: Description(NodeArgs.SettingsHelp), MaxLength(NodeArgs.MaxSettingsLength)] string? Settings = null,
    [property: Description("Go on with the next node after an error (ignored for set_password, which always stops).")] bool ContinueOnError = true)
{
    /// <summary>Longest settings text of one node.</summary>
    public const int MaxSettingsLength = 20_000;

    /// <summary>The settings of every kind, for agents (the secrets and files by the ids of secret_store and file_store).</summary>
    public const string SettingsHelp =
        "The node's settings as the text of a JSON object (leave out for the defaults). set_password: credential_id. add_to_oadm: tags. "
        + "upgrade_online: target (ownTrack, latestLts, latestActive), only_if_older_than (e.g. 12.6.95). "
        + "upgrade_files: files [{file_id, name}] (one AXIS OS file per model; never a downgrade). "
        + "network_settings: dns_servers, search_domain. "
        + "date_time: time_zone, ntp_servers (none: from DHCP), nts, oadm_ntp (OADM's NTP server). "
        + "configuration: source (file, templateCamera), file_id or template_serial, parameters, action_rules, schedules. "
        + "users: accounts [{name, role (viewer, operator, administrator), ptz, secret_id}]. "
        + "certificate: mode (policy, est), policy_id (none: the first HTTPS certificate policy). dot1x: none. "
        + "sd_card: format, encrypt, secret_id (the passphrase), max_age_days. autofocus: none. "
        + "vapix_commands: commands [{command_id, values}] of the VAPIX Commander library. "
        + "set_ip: first, last, subnet_mask, gateway, dns_servers.";
}

internal sealed record SaveArgs(
    [property: Description("The profile name."), MaxLength(64)] string Name,
    [property: Description("The nodes in order."), MaxLength(ProfileCheck.MaxNodes)] IReadOnlyList<NodeArgs> Nodes,
    [property: Description("The profile id to change; leave out for a new profile.")] Guid? Id = null,
    [property: Description("The person enters a new root password for Set password (it goes into the credential list).")] bool NewPassword = false)
{
    public SaveProfileRequest Request(string? password) => new(
        new Profile
        {
            Id = Id ?? Guid.Empty,
            Name = Name,
            Nodes = [.. (Nodes ?? []).Select(n => new FlowNode { Id = n.Id, Kind = n.Kind, ContinueOnError = n.ContinueOnError, Settings = Snake(n.Settings) })],
        },
        NewPassword ? password : null);

    /// <summary>
    /// The settings of a tool (JSON text, snake_case) as the page writes them (camelCase), at any depth (the accounts of
    /// Users); the values of a VAPIX command keep the names of its fields. Not a JSON object: an input error.
    /// </summary>
    private static JsonElement? Snake(string? settings)
    {
        if (string.IsNullOrWhiteSpace(settings))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(settings);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? Camel(document.RootElement)
                : throw new PluginToolInputException("nodes.settings", "Give the settings as a JSON object.");
        }
        catch (JsonException)
        {
            throw new PluginToolInputException("nodes.settings", "The settings are no valid JSON.");
        }
    }

    /// <summary>Stored settings (camelCase) as the tools show them: snake_case JSON text, the values of a VAPIX command as they are.</summary>
    public static string? SnakeText(JsonElement? settings) => settings is { } element ? JsonSerializer.Serialize(SnakeOf(element)) : null;

    private static JsonElement SnakeOf(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    var name = string.Concat(property.Name.Select((c, i) => char.IsUpper(c) && i > 0 ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
                    map[name] = property.Name == "values" ? property.Value : SnakeOf(property.Value);
                }

                return JsonSerializer.SerializeToElement(map);
            case JsonValueKind.Array:
                return JsonSerializer.SerializeToElement(element.EnumerateArray().Select(SnakeOf).ToList());
            default:
                return element;
        }
    }

    private static JsonElement Camel(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    var parts = property.Name.Split('_');
                    var name = parts[0] + string.Concat(parts.Skip(1).Select(p => p.Length == 0 ? p : char.ToUpperInvariant(p[0]) + p[1..]));
                    map[name] = name == "values" ? property.Value : Camel(property.Value);
                }

                return JsonSerializer.SerializeToElement(map);
            case JsonValueKind.Array:
                return JsonSerializer.SerializeToElement(element.EnumerateArray().Select(Camel).ToList());
            default:
                return element;
        }
    }
}

internal sealed record FileArgs([property: Description("The id of a file the caller uploaded."), MaxLength(64)] string UploadId);

internal sealed record SecretPlan() : PluginToolPlan(1, "secret");

internal sealed record FilePlan([property: Description("The file name.")] string Name) : PluginToolPlan(1, "file");

internal sealed record SecretAnswer([property: Description("The secret_id to put into the node's settings.")] Guid SecretId);

internal sealed record FileAnswer([property: Description("The file_id to put into the node's settings.")] Guid FileId, string Name, long Size);

internal sealed record SavePlan(
    [property: Description("The profile name.")] string Name,
    [property: Description("Nodes with an error (the profile cannot be active until they are fixed).")] int NodesWithErrors,
    [property: Description("Nodes with a warning.")] int NodesWithWarnings) : PluginToolPlan(1, "profile");

internal sealed record ProfilePlan([property: Description("The profile name.")] string Name) : PluginToolPlan(1, "profile");

internal sealed record StartPlan(
    [property: Description("The profile that runs.")] string Profile,
    [property: Description("Warnings; Start still works.")] IReadOnlyList<string> Warnings) : PluginToolPlan(1, "session");

internal sealed record StopPlan([property: Description("Cameras whose flow runs now.")] int Running) : PluginToolPlan(1, "session");

internal sealed record CamerasPlan([property: Description("The cameras the run changes.")] IReadOnlyList<string> Macs) : PluginToolPlan(Macs.Count, "camera");

internal sealed record SavedAnswer([property: Description("The profile id.")] Guid Id, [property: Description("The profile has no errors.")] bool IsValid);

internal sealed record DoneAnswer([property: Description("What happened.")] string Message);

internal sealed record CountAnswer([property: Description("How many cameras.")] int Count);

internal sealed record CredentialRow(Guid Id, string UserName);

internal sealed record CredentialsAnswer(IReadOnlyList<CredentialRow> Credentials);

internal sealed record CommandsAnswer(IReadOnlyList<CommandToolRow> Commands)
{
    public static CommandsAnswer Of(IReadOnlyList<ProfileCommand> commands) => new([.. commands.Select(c => new CommandToolRow(
        c.Id,
        c.Name,
        c.Category,
        c.Description,
        [.. c.Fields.Select(f => new CommandFieldRow(
            f.Name,
            f.Label,
            f.Type,
            f.IsRequired && f.Default is null,
            f.Default is { } value ? VapixCommander.FieldValues.TextOf(value) : null,
            f.Min,
            f.Max,
            f.Unit,
            f.Options is { Count: > 0 } options ? [.. options.Select(o => VapixCommander.FieldValues.TextOf(o.Value))] : null))]))]);
}

internal sealed record CommandToolRow(
    [property: Description("The command_id of a vapix_commands node.")] string Id,
    string Name,
    string Category,
    string? Description,
    IReadOnlyList<CommandFieldRow> Fields);

internal sealed record CommandFieldRow(
    [property: Description("The field's name in the command's values.")] string Name,
    string Label,
    [property: Description("string, integer, number, boolean or enum.")] string Type,
    [property: Description("A value must be given.")] bool Required,
    [property: Description("The value used when none is given.")] string? Default,
    double? Min,
    double? Max,
    string? Unit,
    [property: Description("The values an enum field takes.")] IReadOnlyList<string>? Options);

internal sealed record StatusAnswer(
    bool Started,
    DateTimeOffset? StartedAt,
    string? StartedBy,
    string? ActiveProfile,
    string? SessionProfile,
    bool DhcpRunning,
    string? DhcpStatus,
    CameraCounts Counts,
    IReadOnlyList<string> Warnings)
{
    public static StatusAnswer Of(StatusReply s) =>
        new(s.Started, s.StartedAt, s.StartedBy, s.ActiveProfileName, s.SessionProfileName, s.Dhcp?.IsRunning == true, s.Dhcp?.StatusText, s.Counts, s.Warnings);
}

internal sealed record ProfileRow(Guid Id, string Name, int Nodes, bool IsActive, bool IsValid);

internal sealed record ProfilesAnswer(IReadOnlyList<ProfileRow> Profiles)
{
    public static ProfilesAnswer Of(ProfilesReply reply) => new([.. reply.Profiles.Select(p => new ProfileRow(p.Id, p.Name, p.Nodes, p.IsActive, p.IsValid))]);
}

internal sealed record NodeRow(string Id, NodeKind Kind, bool ContinueOnError, [property: Description("The node's settings as the text of a JSON object, as profile_save takes them.")] string? Settings, string? Error, string? Warning);

internal sealed record ProfileAnswer(Guid Id, string Name, IReadOnlyList<NodeRow> Nodes, IReadOnlyList<string> ProfileErrors)
{
    public static ProfileAnswer Of(ProfileReply reply) => new(
        reply.Profile.Id,
        reply.Profile.Name,
        [.. reply.Profile.Nodes.Select(n => new NodeRow(n.Id, n.Kind, n.ContinueOnError, SaveArgs.SnakeText(n.Settings), reply.Check.NodeErrors.GetValueOrDefault(n.Id), reply.Check.NodeWarnings.GetValueOrDefault(n.Id)))],
        reply.Check.ProfileErrors);
}

internal sealed record CameraToolRow(string Mac, string? Model, string Address, CameraState State, string? Node, bool WaitingForApproval, string? Message);

internal sealed record CamerasAnswer(int Total, IReadOnlyList<CameraToolRow> Rows, string? Next)
{
    public static CamerasAnswer Of(ProvisioningPlugin plugin, PluginToolPage page)
    {
        var engine = plugin.RunningEngine;
        var all = engine.Page(page.After, 1_000);
        var rows = all.Rows.Select(r => new CameraToolRow(r.Mac, r.Model, r.Address, r.State, r.NodeName, r.WaitingForApproval,
            r.Message ?? (r.NodeIndex >= 0 && r.NodeIndex < r.Results.Count ? r.Results[r.NodeIndex]?.Message : null)));
        var (taken, more) = page.Take(rows, ProvisioningTools.Set.Json);
        return new CamerasAnswer(all.Total, taken, more || all.Next is not null ? taken[^1].Mac : null);
    }
}
