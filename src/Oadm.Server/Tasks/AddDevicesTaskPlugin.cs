using Oadm.Core.Devices;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Server.Tasks;

/// <summary>
/// Built-in task behind the "Add devices" entry of the task list: the first full refresh of the
/// devices the add wizard just stored. Hidden from the context menu (<see cref="TaskGrpcService"/>).
/// A device that does not end up Ok fails its task row with the reason, but stays added.
/// </summary>
public sealed class AddDevicesTaskPlugin(DevicePollingService polling) : ITaskPlugin
{
    public const string PluginId = "oadm.add-devices";

    public string Id => PluginId;

    public string DisplayName => "Add devices";

    public string? IconKey => "add";

    public bool ShowInToolbar => false;

    public bool RequiresDialog => false;

    public bool CanRun(IDeviceInfo device) => true;

    public async Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);

        ctx.ReportProgress(10, "Reading device information");
        var refreshed = await polling.RefreshAsync(device.Id, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The device was removed.");

        var message = refreshed.Status switch
        {
            DeviceStatus.Ok => null,
            DeviceStatus.CredentialsRequired => "Added, but the credentials were not accepted.",
            DeviceStatus.PasswordNotSet => "Added without a password (factory default).",
            DeviceStatus.Unreachable => "Added, but the device does not answer.",
            DeviceStatus.CertificateChanged => "Added, but the device certificate changed.",
            _ => "Added, but the device status is unknown.",
        };

        if (message is not null)
        {
            throw new InvalidOperationException(message);
        }

        ctx.ReportProgress(100, "Added");
    }
}
