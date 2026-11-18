using System.Text.Json;

using Oadm.Plugins.Provisioning.Model;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Provisioning.Nodes;

/// <summary>
/// Set password (fixed, first, always stops): the first root password of a factory-new camera from a credential list entry of
/// root, through the server's add page path (<see cref="IDeviceAutoAdd.SetFirstPasswordAsync"/>: the serial and "no password
/// yet" checked right before writing). The password never passes through the plugin.
/// </summary>
public sealed class SetPasswordNode : IProvisioningNode
{
    public const string ChoosePassword = "Choose a password of the credential list, or type one.";

    public NodeKind Kind => NodeKind.SetPassword;

    public string Name => "Set password";

    public bool IsFixed => true;

    public bool AlwaysStops => true;

    public bool NeedsDevice => false;

    public object DefaultSettings => new SetPasswordSettings();

    public string? Check(JsonElement? settings, ProfileCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (NodeSettings.Read<SetPasswordSettings>(settings) is not { } read)
        {
            return TaskNode<SetPasswordSettings>.Unreadable;
        }

        if (read.CredentialId is not { } id || id == Guid.Empty)
        {
            return ChoosePassword;
        }

        if (context.Credentials is { } list)
        {
            if (!list.TryGetValue(id, out var user))
            {
                return DeviceFirstSetupTexts.CredentialGone;
            }

            if (!string.Equals(user, "root", StringComparison.Ordinal))
            {
                return "Choose a password of the user root.";
            }
        }

        return null;
    }

    public async Task<NodeResult> RunAsync(NodeRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.SettingsAs<SetPasswordSettings>()?.CredentialId is not { } id)
        {
            return NodeResult.Error(ChoosePassword);
        }

        try
        {
            var outcome = await run.Host.AutoAdd.SetFirstPasswordAsync(run.Camera.Address, run.Camera.Serial, id, run.Host.Owner, ct).ConfigureAwait(false);
            return outcome.Ok ? NodeResult.Success("Password set.") : NodeResult.Error(outcome.Message);
        }
        catch (NotSupportedException)
        {
            return NodeResult.Error(DeviceFirstSetupTexts.NotSupported);
        }
    }
}

/// <summary>
/// Add to OADM (fixed, second): adds the camera logged in with the password of Set password and gives it the tags (so it
/// lands in the right device group), then waits until OADM has read it (status Ok, its API list) so the next nodes can
/// check their APIs. A camera OADM manages already (the DHCP server's automatic add) keeps its record and gets the tags.
/// </summary>
public sealed class AddToOadmNode : IProvisioningNode
{
    /// <summary>Most tags a device has (the host's limit).</summary>
    public const int MaxTags = 20;

    public const int MaxTagLength = 64;

    public NodeKind Kind => NodeKind.AddToOadm;

    public string Name => "Add to OADM";

    public bool IsFixed => true;

    public bool NeedsDevice => false;

    public object DefaultSettings => new AddToOadmSettings([]);

    public string? Check(JsonElement? settings, ProfileCheckContext context)
    {
        if (NodeSettings.Read<AddToOadmSettings>(settings) is not { } read)
        {
            return TaskNode<AddToOadmSettings>.Unreadable;
        }

        var tags = read.Tags ?? [];
        if (tags.Count > MaxTags)
        {
            return $"A camera has at most {MaxTags} tags.";
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in tags)
        {
            var name = tag?.Trim() ?? string.Empty;
            if (name.Length == 0)
            {
                return "Enter a name for every tag.";
            }

            if (name.Length > MaxTagLength)
            {
                return $"A tag has at most {MaxTagLength} characters.";
            }

            if (!seen.Add(name))
            {
                return $"The tag {name} is there twice.";
            }
        }

        return null;
    }

    public async Task<NodeResult> RunAsync(NodeRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.CredentialId is not { } credential)
        {
            return NodeResult.Error(SetPasswordNode.ChoosePassword);
        }

        var tags = (run.SettingsAs<AddToOadmSettings>()?.Tags ?? []).Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
        DeviceAutoAddOutcome outcome;
        try
        {
            outcome = await run.Host.AutoAdd.AddWithLoginAsync(run.Camera.Address, run.Camera.Serial, credential, tags, run.Host.Owner, ct).ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            return NodeResult.Error(DeviceFirstSetupTexts.NotSupported);
        }

        if (outcome.DeviceId is { } added)
        {
            run.Host.DeviceAdded(run.Camera.Mac, added, null);
        }

        switch (outcome.Result)
        {
            case DeviceAutoAddResult.Added or DeviceAutoAddResult.AlreadyManaged when outcome.DeviceId is { } id:
                break;
            case DeviceAutoAddResult.AddedPasswordNotSet:
                return NodeResult.Error("The camera still has no password.");
            case DeviceAutoAddResult.AddedCredentialsRequired:
                return NodeResult.Error("OADM cannot log in to the camera with the password of Set password.");
            case DeviceAutoAddResult.LimitReached:
                return NodeResult.Error(DeviceLimits.TooManyDevicesText);
            default:
                return NodeResult.Error(outcome.Message);
        }

        var deviceId = outcome.DeviceId!.Value;
        var tagProblem = outcome.Message.IndexOf(" Tags not set:", StringComparison.Ordinal) is var at and >= 0 ? outcome.Message[(at + 1)..] : null;
        var done = outcome.Result == DeviceAutoAddResult.AlreadyManaged ? "OADM manages this camera already." : "Added to OADM.";
        if (!await WaitUntilReadAsync(run.Host, run.Camera.Mac, deviceId, ct).ConfigureAwait(false))
        {
            return NodeResult.Warning(done + " OADM has not read the camera yet; the next nodes may fail." + (tagProblem is null ? string.Empty : " " + tagProblem));
        }

        return tagProblem is null ? NodeResult.Success(done) : NodeResult.Warning(done + " " + tagProblem);
    }

    /// <summary>Waits until OADM read the device (status Ok and its API list), at most <see cref="ProvisioningOptions.DeviceReadTimeout"/>.</summary>
    private static async Task<bool> WaitUntilReadAsync(INodeHost host, string mac, Guid deviceId, CancellationToken ct)
    {
        var end = host.Time.GetUtcNow() + host.Options.DeviceReadTimeout;
        while (true)
        {
            if (await host.Devices.FindAsync(deviceId, ct).ConfigureAwait(false) is { Status: DeviceStatus.Ok, Apis.Count: > 0 } device)
            {
                host.DeviceAdded(mac, deviceId, device.Model);
                return true;
            }

            if (host.Time.GetUtcNow() >= end)
            {
                return false;
            }

            await Task.Delay(host.Options.DevicePoll, host.Time, ct).ConfigureAwait(false);
        }
    }
}

/// <summary>Wait for approval: the camera waits here, outside the 8 places, until a person presses Continue on its row.</summary>
public sealed class WaitForApprovalNode : IProvisioningNode
{
    public NodeKind Kind => NodeKind.WaitForApproval;

    public string Name => "Wait for approval";

    public bool NeedsDevice => false;

    public object DefaultSettings => new WaitForApprovalSettings();

    public string? Check(JsonElement? settings, ProfileCheckContext context) => null;

    public async Task<NodeResult> RunAsync(NodeRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        var user = await run.Host.WaitForApprovalAsync(run.Camera.Mac, ct).ConfigureAwait(false);
        return NodeResult.Success($"Continued by {user}.");
    }
}
