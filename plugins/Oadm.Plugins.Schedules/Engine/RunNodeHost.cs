using Oadm.Plugins.Provisioning.Engine;
using Oadm.Plugins.Provisioning.Model;
using Oadm.Plugins.Provisioning.Nodes;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Schedules.Engine;

/// <summary>
/// What an action (a Provisioning node) may use during a schedule's run: the plugin's task runner for the actions' tasks, the
/// owner's task runner for Upgrade AXIS OS online (<see cref="StarterTasks"/>: the owner's MyAxis sign-in and view apply), the
/// kept files. Never the add page's pipeline or an address pool: a schedule never sets a first password or an address.
/// </summary>
internal sealed class RunNodeHost(
    ITaskRunner tasks,
    ITaskRunner? ownerTasks,
    IDeviceRepository devices,
    IVapixClientFactory vapix,
    NodeVault vault,
    TimeProvider time,
    ProvisioningOptions options,
    string owner) : INodeHost
{
    public const string NotInSchedules = "A schedule never sets a first password or an address.";

    public IDeviceAutoAdd AutoAdd => throw new NotSupportedException(NotInSchedules);

    public ITaskRunner Tasks => tasks;

    public IDeviceRepository Devices => devices;

    public IVapixClientFactory Vapix => vapix;

    public ITaskRunner? StarterTasks => ownerTasks;

    public NodeVault Vault => vault;

    public TimeProvider Time => time;

    public ProvisioningOptions Options => options;

    public string Owner => owner;

    public Task<AddressTake> TakeAddressAsync(CameraInfo camera, NetworkNode.Pool pool, CancellationToken ct) =>
        throw new NotSupportedException(NotInSchedules);

    public Task ReleaseAddressAsync(CameraInfo camera, string address, CancellationToken ct) =>
        throw new NotSupportedException(NotInSchedules);
}
