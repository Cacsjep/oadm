using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;

using Oadm.Plugins.Provisioning.Engine;
using Oadm.Plugins.Provisioning.Model;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Provisioning.Tools;

/// <summary>
/// The reads and changes of Provisioning management as tools (docs/spec/mcp.md "Plugins"): the status, the profiles, the
/// credential list's names, the last run of a camera; saving, deleting and activating a profile, On and Off, Run again, each a
/// preview with the dialog's checks and a run through the plugin's own code. The rights of the plugin's methods: Administrator
/// by default, users with all devices only. A typed password is a secret of the person, never an argument.
/// </summary>
internal static class ProvisioningTools
{
    private static readonly PluginToolSecret Password = new("password", "root password of Set password");

    private static readonly PluginToolSecret NodeSecret = new("secret", "password or passphrase of a node");

    public static PluginPageTools<ProvisioningPlugin> Set { get; } = new PluginPageTools<ProvisioningPlugin>()
        .Read<NoArgs, StatusAnswer>(
            new("status", "Provisioning status", "Whether provisioning is on (who turned it on, when), the active profile, why it cannot be turned on, the provisioning tasks running and the warnings."),
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
        .Read<NoArgs, PoliciesAnswer>(
            new("policies", "PKI policies for provisioning", "PKI's HTTPS and IEEE 802.1X certificate policies (id, name, use) a Certificate or 802.1X node can choose."),
            ProvisioningMethods.Policies,
            async (plugin, _, _, ct) => new PoliciesAnswer([.. (await plugin.PoliciesAsync(ct).ConfigureAwait(false) ?? []).Select(p => new PolicyRow(p.Id, p.Name, p.Use == Oadm.Sdk.Certificates.IssuingPolicyUse.Dot1x ? "802.1X" : "HTTPS"))]))
        .Read<NoArgs, CommandsAnswer>(
            new("vapix_commands", "Provisioning VAPIX commands", "The VAPIX Commander commands a VAPIX commands node can run, with their fields (name, type, required, options)."),
            ProvisioningMethods.VapixCommands,
            (plugin, _, _, _) => Task.FromResult(CommandsAnswer.Of(plugin.VapixCommands())))
        .Read<DeviceArgs, RunAnswer>(
            new("camera", "Last provisioning of a camera", "How the last provisioning task of a camera ended: its profile, whether the camera was new, each node's result and whether run_again would start."),
            ProvisioningMethods.GetRun,
            async (plugin, _, input, ct) => RunAnswer.Of(await plugin.RunAsync(input.Args.DeviceId, ct).ConfigureAwait(false)))
        .Change<SaveArgs, SavePlan, SavedAnswer>(
            new("profile_save", "Save provisioning profile", "Adds a profile, or changes the one with id. Nodes in order, set_password first. With new_password the person enters the root password of Set password.")
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
            new("profile_delete", "Delete provisioning profile", "Deletes a profile; the active one only while provisioning is off. Running tasks keep their own copy.") { Destructive = true },
            ProvisioningMethods.DeleteProfile,
            (plugin, _, input, _) => Task.FromResult(new ProfilePlan(plugin.CheckDelete(input.Args.Id).Name)),
            async (plugin, _, input, _, ct) =>
            {
                await plugin.DeleteProfileAsync(input.Args.Id, ct).ConfigureAwait(false);
                return new DoneAnswer("Deleted.");
            })
        .Change<IdArgs, ProfilePlan, DoneAnswer>(
            new("profile_activate", "Activate provisioning profile", "Makes a profile without errors the active one: the next cameras found run it."),
            ProvisioningMethods.ActivateProfile,
            async (plugin, _, input, ct) => new ProfilePlan((await plugin.CheckActivateAsync(input.Args.Id, ct).ConfigureAwait(false)).Name),
            async (plugin, _, input, _, ct) =>
            {
                await plugin.ActivateAsync(input.Args.Id, ct).ConfigureAwait(false);
                return new DoneAnswer("Active.");
            })
        .Change<NoArgs, TurnOnPlan, DoneAnswer>(
            new("turn_on", "Turn provisioning on", "Turns provisioning on with the active profile: every factory-new camera OADM finds (discovery, network range, add manually, import, a lease of OADM's DHCP server) is added and gets a task that runs the profile."),
            ProvisioningMethods.TurnOn,
            async (plugin, _, _, ct) =>
            {
                var profile = await plugin.CheckTurnOnAsync(ct).ConfigureAwait(false);
                return new TurnOnPlan(profile.Name, (await plugin.StatusAsync(ct).ConfigureAwait(false)).Warnings);
            },
            async (plugin, caller, _, _, ct) =>
            {
                await plugin.TurnOnAsync(caller, ct).ConfigureAwait(false);
                return new DoneAnswer("Provisioning is on.");
            })
        .Change<NoArgs, TurnOffPlan, DoneAnswer>(
            new("turn_off", "Turn provisioning off", "Turns provisioning off: no new camera gets a task; running provisioning tasks run to their end."),
            ProvisioningMethods.TurnOff,
            (plugin, _, _, _) => Task.FromResult(plugin.RunningService.IsOn ? new TurnOffPlan(plugin.RunningService.ActiveTasks) : throw new InvalidOperationException(ProvisioningService.AlreadyOff)),
            async (plugin, _, _, _, ct) =>
            {
                await plugin.TurnOffAsync(ct).ConfigureAwait(false);
                return new DoneAnswer("Provisioning is off.");
            })
        .Change<DevicesArgs, RunAgainPlan, RunAgainAnswer>(
            new("run_again", "Run provisioning again", "For cameras whose provisioning task failed: a new task with the same profile that runs only the failed steps, in order."),
            ProvisioningMethods.RunAgain,
            async (plugin, _, input, ct) =>
            {
                var ids = new List<Guid>();
                foreach (var id in (input.Args.DeviceIds ?? []).Distinct())
                {
                    if (await plugin.RunningService.PlanAgainAsync(id, ct).ConfigureAwait(false) is not null)
                    {
                        ids.Add(id);
                    }
                }

                return ids.Count == 0
                    ? throw new PluginToolInputException("device_ids", "None of these cameras has a failed provisioning task to run again.")
                    : new RunAgainPlan(ids);
            },
            async (plugin, _, input, _, ct) =>
            {
                var reply = await plugin.RunAgainAsync(input.Args.DeviceIds ?? [], ct).ConfigureAwait(false);
                return new RunAgainAnswer(reply.Started, reply.Skipped);
            });
}

[Description("No arguments.")]
internal sealed record NoArgs;

internal sealed record IdArgs([property: Description("The profile id.")] Guid Id);

internal sealed record DeviceArgs([property: Description("The OADM device id of the camera.")] Guid DeviceId);

internal sealed record DevicesArgs(
    [property: Description("OADM device ids of cameras whose provisioning task failed."), MaxLength(1_000)] IReadOnlyList<Guid> DeviceIds);

internal sealed record NodeArgs(
    [property: Description("The node's id in the profile: letters, digits and hyphens."), MaxLength(32)] string Id,
    [property: Description("set_password, network, upgrade_online, upgrade_files, date_time, configuration, users, certificate, dot1x, sd_card, autofocus or vapix_commands.")] NodeKind Kind,
    [property: Description(NodeArgs.SettingsHelp), MaxLength(NodeArgs.MaxSettingsLength)] string? Settings = null,
    [property: Description("Go on with the next node after an error (ignored for set_password, which always stops).")] bool ContinueOnError = true)
{
    /// <summary>Longest settings text of one node.</summary>
    public const int MaxSettingsLength = 20_000;

    /// <summary>The settings of every kind, for agents (the secrets and files by the ids of secret_store and file_store).</summary>
    public const string SettingsHelp =
        "The node's settings as the text of a JSON object (leave out for the defaults). set_password: credential_id. "
        + "network: ip (keep, pool), first, last, subnet_mask, gateway (the pool), dns_servers, search_domain; at least one part set. "
        + "upgrade_online: target (ownTrack, latestLts, latestActive), only_if_older_than (e.g. 12.6.95). "
        + "upgrade_files: files [{file_id, name}] (one AXIS OS file per model; never a downgrade). "
        + "date_time: time_zone, ntp_servers (none: from DHCP), nts, oadm_ntp (OADM's NTP server). "
        + "configuration: file_id, parameters, action_rules, schedules. "
        + "users: accounts [{name, role (viewer, operator, administrator), ptz, secret_id}]. "
        + "certificate: mode (policy, est), policy_id (none: the first HTTPS certificate policy). dot1x: none. "
        + "sd_card: format, encrypt, secret_id (the passphrase), max_age_days. autofocus: none. "
        + "vapix_commands: commands [{command_id, values}] of the VAPIX Commander library.";
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

internal sealed record TurnOnPlan(
    [property: Description("The profile the cameras run.")] string Profile,
    [property: Description("Warnings; On still works.")] IReadOnlyList<string> Warnings) : PluginToolPlan(1, "setting");

internal sealed record TurnOffPlan([property: Description("Provisioning tasks that run or wait now; they run to their end.")] int RunningTasks) : PluginToolPlan(1, "setting");

internal sealed record RunAgainPlan([property: Description("The cameras that get a task.")] IReadOnlyList<Guid> DeviceIds) : PluginToolPlan(DeviceIds.Count, "camera");

internal sealed record RunAgainAnswer([property: Description("Tasks started.")] int Started, [property: Description("Cameras left out (nothing failed, a task runs).")] int Skipped);

internal sealed record SavedAnswer([property: Description("The profile id.")] Guid Id, [property: Description("The profile has no errors.")] bool IsValid);

internal sealed record DoneAnswer([property: Description("What happened.")] string Message);

internal sealed record CredentialRow(Guid Id, string UserName);

internal sealed record CredentialsAnswer(IReadOnlyList<CredentialRow> Credentials);

internal sealed record PoliciesAnswer(IReadOnlyList<PolicyRow> Policies);

internal sealed record PolicyRow(Guid Id, string Name, string Use);

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
    bool On,
    string? OnBy,
    DateTimeOffset? OnAt,
    string? ActiveProfile,
    bool ActiveProfileValid,
    [property: Description("Why provisioning cannot be turned on now.")] string? TurnOnRefusal,
    int RunningTasks,
    IReadOnlyList<string> Warnings)
{
    public static StatusAnswer Of(StatusReply s) =>
        new(s.On, s.OnBy, s.OnAt, s.ActiveProfileName, s.ActiveProfileValid, s.TurnOnRefusal, s.RunningTasks, s.Warnings);
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

internal sealed record RunNodeRow(string Node, [property: Description("success, warning, error, notNeeded; null: not run.")] NodeState? State, string? Message);

internal sealed record RunAnswer(
    Guid DeviceId,
    string Profile,
    DateTimeOffset Started,
    [property: Description("The camera passed the check that it is new.")] bool Checked,
    IReadOnlyList<RunNodeRow> Nodes,
    [property: Description("run_again would start a task.")] bool CanRunAgain)
{
    public static RunAnswer Of(RunReply reply) => new(
        reply.DeviceId,
        reply.ProfileName,
        reply.Started,
        reply.Checked,
        [.. reply.Nodes.Select(n => new RunNodeRow(n.Name, n.Result?.State, n.Result?.Message))],
        reply.CanRunAgain);
}
