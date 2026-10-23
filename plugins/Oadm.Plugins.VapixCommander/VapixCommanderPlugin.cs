using System.Globalization;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.VapixCommander;

/// <summary>
/// VAPIX Commander core plugin: command library (<c>Library/*.json</c>), saved commands shared by all clients,
/// "Try on one device" and rollouts (one <see cref="RolloutTaskPlugin"/> task per device). Backend of the
/// Commander page; the methods are listed in <see cref="CommanderMethods"/>.
/// </summary>
public sealed partial class VapixCommanderPlugin : ICorePlugin, Oadm.Sdk.Actions.IActionSource
{
    public const string PluginId = "oadm.vapix-commander";

    private readonly string? _libraryDirectory;
    private readonly RolloutTaskPlugin _rolloutTask;
    private ICorePluginContext? _ctx;
    private ILogger _logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    private SavedCommandStore? _saved;
    private IReadOnlyList<CommandListItem> _savedSnapshot = [];

    public VapixCommanderPlugin()
        : this(null)
    {
    }

    /// <param name="libraryDirectory">Folder with the library files; null = "Library" in the plugin folder.</param>
    internal VapixCommanderPlugin(string? libraryDirectory)
    {
        _libraryDirectory = libraryDirectory;
        _rolloutTask = new RolloutTaskPlugin(this);
        TaskPlugins = [_rolloutTask];
        ActionPlugins = [new RunCommandAction(this)];
    }

    public string Id => PluginId;

    public string DisplayName => "VAPIX Commander";

    public string? IconKey => "command";
    public CorePluginGroup Group => CorePluginGroup.Automation;

    public IReadOnlyList<ITaskPlugin> TaskPlugins { get; }

    /// <summary>The event rule action "Run VAPIX command".</summary>
    public IReadOnlyList<Oadm.Sdk.Actions.IActionPlugin> ActionPlugins { get; }

    public CommandLibrary Library { get; private set; } = CommandLibrary.Empty;

    /// <summary>Up to this many devices are looked up one by one; more use one device list.</summary>
    private const int FindOneByOneLimit = 16;

    internal RolloutRegistry Rollouts { get; } = new();

    private ICorePluginContext Context => _ctx ?? throw new InvalidOperationException("The VAPIX Commander is not started.");

    private SavedCommandStore Saved => _saved ?? throw new InvalidOperationException("The VAPIX Commander is not started.");

    public async Task StartAsync(ICorePluginContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        _ctx = ctx;
        _logger = ctx.Logger;
        _saved = new SavedCommandStore(ctx.Settings, ctx.Secrets);
        var directory = _libraryDirectory
            ?? (ctx.PluginDirectory is { } dir ? Path.Combine(dir, "Library") : Path.Combine(AppContext.BaseDirectory, "Library"));
        Library = CommandLibrary.Load(directory);
        LogLibraryLoaded(Library.Commands.Count, directory);
        foreach (var problem in Library.Problems)
        {
            LogLibraryProblem(problem);
        }

        await RefreshSavedSnapshotAsync(ct).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>Sending a request and rolling out commands reach devices: both go to the audit log.</summary>
    public bool IsAudited(string method) => method is CommanderMethods.TryRequest or CommanderMethods.Rollout;

    /// <summary>Sending to one device, rolling out and the saved commands are rights of their own; reading is "show".</summary>
    public IReadOnlyList<PermissionDefinition> Permissions { get; } =
    [
        new("send", "Send to one device", "Send a command or a request of your own to one device and see its answer.") { FullDeviceControl = true },
        new("rollout", "Run on many devices", "Run the commands of the rollout set on the chosen devices.") { FullDeviceControl = true },
        new("saved", "Change saved commands", "Save, change, delete and import the saved commands everyone shares."),
    ];

    public string? RequiredPermission(string method) => method switch
    {
        CommanderMethods.TryRequest => "send",
        CommanderMethods.Rollout => "rollout",
        CommanderMethods.Save or CommanderMethods.Delete or CommanderMethods.Import => "saved",
        _ => PermissionKeys.Show,
    };

    /// <summary>Changing the saved commands everyone shares is for users with all devices (docs/spec/device-scope.md).</summary>
    public bool RequiresAllDevices(string method) => method is CommanderMethods.Save or CommanderMethods.Delete or CommanderMethods.Import;

    /// <summary>In-process callers and tests without a user: the context's own devices.</summary>
    public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => InvokeCoreAsync(null, method, payloadJson, ct);

    /// <summary>The page's call (SDK 0.4.0): devices, clients and tasks only through <paramref name="caller"/>.</summary>
    public Task<string?> InvokeAsync(ICallContext caller, string method, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return InvokeCoreAsync(caller, method, payloadJson, ct);
    }

    private async Task<string?> InvokeCoreAsync(ICallContext? caller, string method, string? payloadJson, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        return method switch
        {
            CommanderMethods.ListLibrary => CommandJson.Write(ListLibrary()),
            CommanderMethods.ListSaved => CommandJson.Write(new CommandListReply { Commands = [.. await Saved.ListAsync(ct).ConfigureAwait(false)] }),
            CommanderMethods.Save => CommandJson.Write(await SaveAsync(WithOwner(CommandJson.Read<SaveCommandRequest>(payloadJson), caller), ct).ConfigureAwait(false)),
            CommanderMethods.Delete => CommandJson.Write(await DeleteAsync(CommandJson.Read<DeleteCommandRequest>(payloadJson), ct).ConfigureAwait(false)),
            CommanderMethods.Export => CommandJson.Write(await Saved.ExportAsync(CommandJson.Read<ExportRequest>(payloadJson).Ids, ct).ConfigureAwait(false)),
            CommanderMethods.Import => CommandJson.Write(await ImportAsync(WithOwner(CommandJson.Read<ImportRequest>(payloadJson), caller), ct).ConfigureAwait(false)),
            CommanderMethods.TryRequest => CommandJson.Write(await TryAsync(CommandJson.Read<TryCommandRequest>(payloadJson), caller, ct).ConfigureAwait(false)),
            CommanderMethods.CheckCompatibility => CommandJson.Write(await CheckCompatibilityAsync(CommandJson.Read<CompatibilityRequest>(payloadJson), caller, ct).ConfigureAwait(false)),
            CommanderMethods.Rollout => CommandJson.Write(await RolloutAsync(CommandJson.Read<RolloutRequest>(payloadJson), caller, ct).ConfigureAwait(false)),
            _ => throw new NotSupportedException($"Unknown method '{method}'."),
        };
    }

    /// <summary>The user who saves is the caller, never a name the page sent.</summary>
    private static SaveCommandRequest WithOwner(SaveCommandRequest request, ICallContext? caller)
    {
        if (caller is not null)
        {
            request.Owner = caller.UserName;
        }

        return request;
    }

    private static ImportRequest WithOwner(ImportRequest request, ICallContext? caller)
    {
        if (caller is not null)
        {
            request.Owner = caller.UserName;
        }

        return request;
    }

    /// <summary>The devices of the call (the user's view), or the context's own without a user.</summary>
    private IDeviceRepository DevicesOf(ICallContext? caller) => caller?.Devices ?? Context.Devices;

    internal CommandListReply ListLibrary() => new()
    {
        Commands = [.. Library.Commands.Select(c => new CommandListItem { Source = CommandSources.Library, Command = c.Clone() })],
        Problems = [.. Library.Problems],
    };

    internal void CancelTask(Guid taskId) => _ctx?.Tasks.Cancel(taskId);

    internal async Task<SaveCommandReply> SaveAsync(SaveCommandRequest request, CancellationToken ct)
    {
        try
        {
            var saved = await Saved.SaveAsync(request.Command, request.PreviousId, request.Owner, ct).ConfigureAwait(false);
            await RefreshSavedSnapshotAsync(ct).ConfigureAwait(false);
            LogSaved(saved.Command.Id, request.Owner ?? "?");
            return new SaveCommandReply { Saved = saved };
        }
        catch (CommandValidationException ex)
        {
            return new SaveCommandReply { Error = ex.Message };
        }
    }

    private async Task<OkReply> DeleteAsync(DeleteCommandRequest request, CancellationToken ct)
    {
        var deleted = await Saved.DeleteAsync(request.Id, ct).ConfigureAwait(false);
        await RefreshSavedSnapshotAsync(ct).ConfigureAwait(false);
        return deleted ? new OkReply { Ok = true } : new OkReply { Error = "The saved command no longer exists." };
    }

    private async Task<ImportReply> ImportAsync(ImportRequest request, CancellationToken ct)
    {
        var reply = await Saved.ImportAsync(request.Json, request.Owner, ct).ConfigureAwait(false);
        await RefreshSavedSnapshotAsync(ct).ConfigureAwait(false);
        LogImported(reply.Imported, reply.Problems.Count);
        return reply;
    }

    private async Task RefreshSavedSnapshotAsync(CancellationToken ct) =>
        _savedSnapshot = await Saved.ListAsync(ct).ConfigureAwait(false);

    /// <summary>Library commands an event rule can send as they are: every required field has a default.</summary>
    private static bool NeedsNoInput(CommandDefinition command) => command.Fields.All(f => !f.IsRequired || f.Default is not null);

    /// <summary>The commands the event rule action offers: saved ones first, then library ones that need no input.</summary>
    internal IReadOnlyList<Oadm.Sdk.Actions.ActionChoice> ActionChoices() =>
    [
        .. _savedSnapshot.OrderBy(c => c.Command.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => new Oadm.Sdk.Actions.ActionChoice(RunCommandAction.SavedPrefix + c.Command.Id, "Saved: " + c.Command.Name)),
        .. Library.Commands.Where(NeedsNoInput).OrderBy(c => c.Category).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => new Oadm.Sdk.Actions.ActionChoice(RunCommandAction.LibraryPrefix + c.Id, c.Category + ": " + c.Name)),
    ];

    /// <summary>A command of an event rule action ("saved:&lt;id&gt;" or "library:&lt;id&gt;"), null when unknown.</summary>
    internal CommandDefinition? FindForAction(string reference)
    {
        if (reference.StartsWith(RunCommandAction.SavedPrefix, StringComparison.Ordinal))
        {
            var id = reference[RunCommandAction.SavedPrefix.Length..];
            return _savedSnapshot.FirstOrDefault(c => c.Command.Id == id)?.Command;
        }

        return reference.StartsWith(RunCommandAction.LibraryPrefix, StringComparison.Ordinal)
            ? Library.Find(reference[RunCommandAction.LibraryPrefix.Length..])
            : null;
    }

    /// <summary>The command and values an event rule action sends (stored values and passwords of a saved command).</summary>
    internal Task<(CommandDefinition Command, Dictionary<string, JsonElement> Values)> ResolveForActionAsync(string reference, CancellationToken ct)
    {
        var commandRef = reference.StartsWith(RunCommandAction.SavedPrefix, StringComparison.Ordinal)
            ? new CommandRef { Source = CommandSources.Saved, Id = reference[RunCommandAction.SavedPrefix.Length..] }
            : reference.StartsWith(RunCommandAction.LibraryPrefix, StringComparison.Ordinal)
                ? new CommandRef { Source = CommandSources.Library, Id = reference[RunCommandAction.LibraryPrefix.Length..] }
                : throw new InvalidOperationException("Choose a command.");
        return ResolveAsync(commandRef, null, ct); // event rules are for users with all devices only
    }

    /// <summary>The server's definition of a command plus the stored password values of a saved one.</summary>
    internal Task<(CommandDefinition Command, Dictionary<string, JsonElement> Values)> ResolveAsync(
        CommandRef reference, IReadOnlyDictionary<string, JsonElement>? values, CancellationToken ct) =>
        ResolveAsync(reference, values, allowSavedSecrets: true, ct);

    /// <summary>
    /// The server's definition of a command plus the stored password values of a saved one. Without
    /// <paramref name="allowSavedSecrets"/> (a user with a device group) a stored password the command
    /// would use refuses it (<see cref="CommanderTexts.SavedPasswordsRefused"/>): the user enters the passwords instead.
    /// </summary>
    internal async Task<(CommandDefinition Command, Dictionary<string, JsonElement> Values)> ResolveAsync(
        CommandRef reference, IReadOnlyDictionary<string, JsonElement>? values, bool allowSavedSecrets, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var merged = new Dictionary<string, JsonElement>(values ?? new Dictionary<string, JsonElement>(), StringComparer.Ordinal);
        switch (reference.Source)
        {
            case CommandSources.Library:
                return (Library.Find(reference.Id ?? string.Empty)?.Clone()
                    ?? throw new CommandValidationException($"The library has no command \"{reference.Id}\"."), merged);
            case CommandSources.Saved:
            {
                var id = reference.Id ?? string.Empty;
                var command = await Saved.FindAsync(id, ct).ConfigureAwait(false)
                    ?? throw new CommandValidationException("The saved command no longer exists.");
                foreach (var (field, secret) in await Saved.GetSecretsAsync(id, ct).ConfigureAwait(false))
                {
                    if (!merged.TryGetValue(field, out var entered) || entered.ValueKind != JsonValueKind.String || entered.GetString()!.Length == 0)
                    {
                        if (!allowSavedSecrets)
                        {
                            throw new CommandValidationException(CommanderTexts.SavedPasswordsRefused);
                        }

                        merged[field] = JsonSerializer.SerializeToElement(secret);
                    }
                }

                return (command, merged);
            }

            case CommandSources.Inline:
                return (reference.Command?.Clone() ?? throw new CommandValidationException("The command is missing."), merged);
            default:
                throw new CommandValidationException($"Unknown command source \"{reference.Source}\".");
        }
    }

    internal Task<TryCommandReply> TryAsync(TryCommandRequest request, CancellationToken ct) => TryAsync(request, null, ct);

    internal async Task<TryCommandReply> TryAsync(TryCommandRequest request, ICallContext? caller, CancellationToken ct)
    {
        CommandDefinition command;
        RenderedRequest rendered;
        try
        {
            (command, var values) = await ResolveAsync(request.Command, request.Values, caller?.HasAllDevices ?? true, ct).ConfigureAwait(false);
            rendered = CommandRenderer.Render(command, values);
        }
        catch (CommandValidationException ex)
        {
            return new TryCommandReply { Error = ex.Message };
        }

        var device = await DevicesOf(caller).FindAsync(request.DeviceId, ct).ConfigureAwait(false);
        if (device is null)
        {
            // A user with a device group: an unknown device and one outside the group answer the same, nothing is sent.
            return caller is { HasAllDevices: false } ? throw new DevicesNotAvailableException() : new TryCommandReply { Error = DeviceMessages.Removed };
        }

        if (device.Status == DeviceStatus.CertificateChanged)
        {
            return new TryCommandReply { Error = DeviceMessages.CertificateChanged };
        }

        if (device.Status == DeviceStatus.SiteOffline)
        {
            return new TryCommandReply { Error = DeviceMessages.Refusal(device) }; // nothing is sent
        }

        var cached = Compatibility.Check(command, device.Apis, device.HasVideo);
        if (cached.State is CompatibilityState.MissingApi or CompatibilityState.VersionTooOld or CompatibilityState.NoVideo)
        {
            return new TryCommandReply { Error = $"{cached.Text}. Nothing was sent." };
        }

        if (command.Writes && !request.Confirmed)
        {
            return new TryCommandReply { NeedsConfirmation = true };
        }

        IVapixClient vapix;
        try
        {
            vapix = await (caller?.Vapix ?? Context.Vapix).CreateAsync(device.Id, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new TryCommandReply { Error = "Cannot reach the device: " + TransportErrors.Describe(ex, rendered.Timeout) };
        }

        // The factory owns (and caches) the client: never dispose it here.
        if (command.Writes)
        {
            // Device safety: a fresh API list before the first write.
            try
            {
                Compatibility.Require(command, await vapix.GetApiListAsync(ct).ConfigureAwait(false));
            }
            catch (DeviceNotCompatibleException ex)
            {
                return new TryCommandReply { Error = ex.Message };
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return new TryCommandReply { Error = "Could not read the API list: " + TransportErrors.Describe(ex, rendered.Timeout) + ". Nothing was changed." };
            }
        }

        var outcome = await CommandExecutor.ExecuteAsync(vapix, command, rendered, ct).ConfigureAwait(false);
        LogTried(command.Id, device.Address, outcome.RequestLine ?? string.Empty, outcome.StatusCode ?? 0, outcome.Success);
        return new TryCommandReply { Outcome = TryOutcome.From(outcome) };
    }

    internal Task<CompatibilityReply> CheckCompatibilityAsync(CompatibilityRequest request, CancellationToken ct) => CheckCompatibilityAsync(request, null, ct);

    /// <summary>
    /// Cached compatibility per device and command. A user with a device group: every device must be in the user's view
    /// (an unknown or outside id refuses the whole request).
    /// </summary>
    internal async Task<CompatibilityReply> CheckCompatibilityAsync(CompatibilityRequest request, ICallContext? caller, CancellationToken ct)
    {
        var commands = new List<CommandDefinition?>();
        foreach (var reference in request.Commands)
        {
            try
            {
                commands.Add((await ResolveAsync(reference, null, ct).ConfigureAwait(false)).Command);
            }
            catch (CommandValidationException)
            {
                commands.Add(null);
            }
        }

        var reply = new CompatibilityReply();
        var ids = request.DeviceIds.Distinct().ToList();

        // Thousands of devices: one device list and a dictionary, not one repository lookup per device.
        Dictionary<Guid, IDeviceInfo>? known = null;
        if (ids.Count > FindOneByOneLimit)
        {
            known = [];
            foreach (var d in await DevicesOf(caller).ListAsync(ct).ConfigureAwait(false))
            {
                known.TryAdd(d.Id, d);
            }
        }

        var missing = 0;
        foreach (var deviceId in ids)
        {
            var device = known is not null ? known.GetValueOrDefault(deviceId) : await DevicesOf(caller).FindAsync(deviceId, ct).ConfigureAwait(false);
            if (device is null && caller is { HasAllDevices: false })
            {
                missing++;
                continue;
            }

            reply.Devices.Add(new DeviceCompatibility
            {
                DeviceId = deviceId,
                Commands = [.. commands.Select(c => Entry(c, device))],
            });
        }

        return missing > 0 ? throw new DevicesNotAvailableException(missing) : reply;

        static CompatibilityEntry Entry(CommandDefinition? command, IDeviceInfo? device)
        {
            if (device is null)
            {
                return new CompatibilityEntry { State = CompatibilityState.Unknown, Text = "Device not found" };
            }

            if (command is null)
            {
                return new CompatibilityEntry { State = CompatibilityState.Unknown, Text = "Command not found" };
            }

            var result = Compatibility.Check(command, device.Apis, device.HasVideo);
            return new CompatibilityEntry { State = result.State, Text = result.Text };
        }
    }

    internal Task<RolloutReply> RolloutAsync(RolloutRequest request, CancellationToken ct) => RolloutAsync(request, null, ct);

    /// <summary>The rollout as tasks of the caller: only on devices of the user's view, the whole run is refused otherwise.</summary>
    internal async Task<RolloutReply> RolloutAsync(RolloutRequest request, ICallContext? caller, CancellationToken ct)
    {
        var deviceIds = request.DeviceIds.Distinct().ToList();
        if (deviceIds.Count == 0)
        {
            return new RolloutReply { Error = "Select at least one device." };
        }

        if (request.Commands.Count == 0)
        {
            return new RolloutReply { Error = "Add at least one command." };
        }

        if (request.Commands.Count > RolloutTaskPlugin.MaxCommands)
        {
            return new RolloutReply { Error = string.Create(CultureInfo.InvariantCulture, $"At most {RolloutTaskPlugin.MaxCommands} commands per rollout.") };
        }

        var payload = new RolloutPayload { RolloutId = Guid.NewGuid(), StopOnFirstError = request.StopOnFirstError };
        var problems = new List<string>();
        for (var i = 0; i < request.Commands.Count; i++)
        {
            CommandDefinition? resolved = null;
            try
            {
                var (command, values) = await ResolveAsync(request.Commands[i].Command, request.Commands[i].Values, caller?.HasAllDevices ?? true, ct).ConfigureAwait(false);
                resolved = command;
                _ = CommandRenderer.Render(command, values); // validation only: nothing is sent when a value is wrong
                payload.Commands.Add(new RolloutPayloadCommand { Command = command, Values = values });
            }
            catch (CommandValidationException ex)
            {
                var name = resolved?.Name ?? request.Commands[i].Command.Command?.Name ?? request.Commands[i].Command.Id;
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"Command {i + 1} ({name}): {ex.Message}"));
            }
        }

        if (problems.Count > 0)
        {
            return new RolloutReply { Error = string.Join(Environment.NewLine, problems) };
        }

        if (payload.Commands.Any(c => c.Command.Writes) && !request.Confirmed)
        {
            return new RolloutReply { NeedsConfirmation = true };
        }

        var rollout = Rollouts.Get(payload.RolloutId, payload.StopOnFirstError);
        var tasks = caller?.Tasks ?? Context.Tasks;
        var taskIds = await tasks.RunAsync(
            RolloutTaskPlugin.PluginId,
            deviceIds,
            JsonSerializer.Serialize(payload, CommandJson.Api),
            caller?.UserName ?? (string.IsNullOrWhiteSpace(request.Owner) ? "VAPIX Commander" : request.Owner),
            ct).ConfigureAwait(false);
        foreach (var taskId in rollout.SetTaskIds(taskIds))
        {
            tasks.Cancel(taskId);
        }

        LogRolloutStarted(payload.RolloutId, payload.Commands.Count, deviceIds.Count, payload.StopOnFirstError);
        return new RolloutReply { RolloutId = payload.RolloutId, TaskIds = [.. taskIds] };
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "VAPIX Commander: {Count} library commands loaded from {Directory}")]
    private partial void LogLibraryLoaded(int count, string directory);

    [LoggerMessage(Level = LogLevel.Warning, Message = "VAPIX Commander library: {Problem}")]
    private partial void LogLibraryProblem(string problem);

    [LoggerMessage(Level = LogLevel.Information, Message = "VAPIX Commander: saved command {Id} by {Owner}")]
    private partial void LogSaved(string id, string owner);

    [LoggerMessage(Level = LogLevel.Information, Message = "VAPIX Commander: imported {Count} commands, {Problems} problems")]
    private partial void LogImported(int count, int problems);

    [LoggerMessage(Level = LogLevel.Information, Message = "VAPIX Commander: tried {CommandId} on {Address}: {Request} -> {Status} (success {Success})")]
    private partial void LogTried(string commandId, string address, string request, int status, bool success);

    [LoggerMessage(Level = LogLevel.Information, Message = "VAPIX Commander: rollout {RolloutId} started, {Commands} commands on {Devices} devices (stop on first error {Stop})")]
    private partial void LogRolloutStarted(Guid rolloutId, int commands, int devices, bool stop);
}
