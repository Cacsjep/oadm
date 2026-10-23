using Oadm.Core.Devices;
using Oadm.Sdk.Devices;

namespace Oadm.Core.Tests.Devices;

/// <summary><see cref="DeviceChangeFeed.MembershipVersion"/>: moves only for changes that can move a device into or out of a group.</summary>
public sealed class DeviceChangeFeedMembershipTests
{
    [Fact]
    public void OnlyAddsRemovalsAndChangesOfSerialSiteTagsOrAdderMoveTheMembershipVersion()
    {
        var feed = new DeviceChangeFeed();
        var device = new Device { Id = Guid.NewGuid(), Serial = "ACCC8E000001", Address = "10.0.0.1", Tags = ["PTZ"], AddedByAllDevices = true };
        long Moves(Action<Device> change, DeviceChangeKind kind = DeviceChangeKind.Updated)
        {
            var before = feed.MembershipVersion;
            change(device);
            feed.Publish(new DeviceChange(kind, device.Id, kind == DeviceChangeKind.Removed ? null : device.Clone()));
            return feed.MembershipVersion - before;
        }

        Assert.Equal(1, Moves(_ => { }, DeviceChangeKind.Added));
        Assert.Equal(0, Moves(d => d.Status = DeviceStatus.Unreachable));
        Assert.Equal(0, Moves(d => d.FirmwareVersion = "12.0.0"));
        Assert.Equal(0, Moves(d => d.Address = "10.0.0.2"));
        Assert.Equal(1, Moves(d => d.Tags = ["PTZ", "Lobby"]));
        Assert.Equal(1, Moves(d => d.SiteId = Guid.NewGuid()));
        Assert.Equal(1, Moves(d => d.AddedByGroupId = Guid.NewGuid()));
        Assert.Equal(1, Moves(d => d.AddedByAllDevices = false));
        Assert.Equal(1, Moves(d => d.Serial = "ACCC8E000002"));
        Assert.Equal(1, Moves(_ => { }, DeviceChangeKind.Removed));

        // A device the feed has not seen yet (loaded before the server started): its first update counts, once.
        var known = new Device { Id = Guid.NewGuid(), Serial = "ACCC8E000003", Address = "10.0.0.3" };
        var start = feed.MembershipVersion;
        feed.PublishMany([new DeviceChange(DeviceChangeKind.Updated, known.Id, known.Clone())]);
        feed.PublishMany([new DeviceChange(DeviceChangeKind.Updated, known.Id, known.Clone())]);
        Assert.Equal(start + 1, feed.MembershipVersion);
    }
}
