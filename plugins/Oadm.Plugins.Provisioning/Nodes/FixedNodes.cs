using System.Text.Json;

using Oadm.Plugins.Provisioning.Model;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Provisioning.Nodes;

/// <summary>
/// Set password (fixed, first, always stops): the first root password of a factory-new camera from a credential list entry of
/// root, through the server's add page path (<see cref="IDeviceAutoAdd.SetFirstPasswordAsync"/>: the serial and "no password
/// yet" checked right before writing; the device keeps the password as its login). Then it waits until OADM has read the
/// camera with it, so the next nodes can check their APIs. The password never passes through the plugin.
/// </summary>
public sealed class SetPasswordNode : IProvisioningNode
{
    public const string ChoosePassword = "Choose a password of the credential list, or type one.";

    public NodeKind Kind => NodeKind.SetPassword;

    public string Name => "Set password";

    public string StepName => "Set the password";

    public bool IsFixed => true;

    public bool AlwaysStops => true;

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

        DeviceFirstPasswordOutcome outcome;
        try
        {
            outcome = await run.Host.AutoAdd.SetFirstPasswordAsync(run.Camera.Address, run.Camera.Serial, id, run.Host.Owner, ct).ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            return NodeResult.Error(DeviceFirstSetupTexts.NotSupported);
        }
        catch (ArgumentException)
        {
            return NodeResult.Error(NodeTexts.NeedsIpAddress);
        }

        if (!outcome.Ok)
        {
            return NodeResult.Error(outcome.Message);
        }

        return run.Camera.DeviceId is { } deviceId && !await WaitUntilReadAsync(run.Host, deviceId, ct).ConfigureAwait(false)
            ? NodeResult.Warning(PasswordSet + " " + NotReadYet)
            : NodeResult.Success(PasswordSet);
    }

    public const string PasswordSet = "Password set.";

    public const string NotReadYet = "OADM has not read the camera yet; the next steps may fail.";

    /// <summary>
    /// Waits until OADM has read the device with the new password (status Ok and its API list), at most
    /// <see cref="ProvisioningOptions.DeviceReadTimeout"/>, so the next nodes can check their APIs.
    /// </summary>
    private static async Task<bool> WaitUntilReadAsync(INodeHost host, Guid deviceId, CancellationToken ct)
    {
        var end = host.Time.GetUtcNow() + host.Options.DeviceReadTimeout;
        while (true)
        {
            if (await host.Devices.FindAsync(deviceId, ct).ConfigureAwait(false) is { Status: DeviceStatus.Ok, Apis.Count: > 0 })
            {
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
