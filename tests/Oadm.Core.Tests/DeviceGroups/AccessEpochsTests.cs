using Oadm.Core.Auth;

namespace Oadm.Core.Tests.DeviceGroups;

/// <summary><see cref="AccessEpochs"/> stays bounded however many sessions end, and a dropped change still refuses (fail closed).</summary>
public sealed class AccessEpochsTests
{
    [Fact]
    public void RolePermissionChangesAreRecordedWithoutAnyRegisteredJobs()
    {
        var epochs = new AccessEpochs();
        var role = Guid.NewGuid();
        var admitted = epochs.Capture();
        epochs.RolePermissionsChanged(role);

        Assert.True(epochs.RolePermissionsChangedSince(admitted, role));
        Assert.False(epochs.RolePermissionsChangedSince(admitted, Guid.NewGuid()));
        Assert.False(epochs.RolePermissionsChangedSince(epochs.Capture(), role));
    }

    [Fact]
    public void EndedSessionsKeepTheEntriesBoundedAndACallFromBeforeADroppedChangeIsStillRefused()
    {
        var epochs = new AccessEpochs();
        var anna = Guid.NewGuid();
        var oldCall = epochs.Capture();
        epochs.SessionEnded("s-first");

        for (var i = 0; i < AccessEpochs.MaxEntries * 3; i++)
        {
            epochs.SessionEnded("s-" + i);
        }

        Assert.InRange(epochs.Count, 1, AccessEpochs.MaxEntries);
        Assert.True(epochs.ChangedSince(oldCall, anna, "s-first", null)); // its entry is gone, the floor refuses it
        Assert.True(epochs.ChangedSince(oldCall, anna, "s-unrelated", null)); // fail closed: any call from before the floor

        // A call that began after the last change is refused only by a change after it.
        var newCall = epochs.Capture();
        Assert.False(epochs.ChangedSince(newCall, anna, "s-0", "group"));
        epochs.UserChanged(anna);
        Assert.True(epochs.ChangedSince(newCall, anna, null, null));
        Assert.False(epochs.ChangedSince(newCall, Guid.NewGuid(), "s-new", "group"));
        epochs.ScopeChanged("group");
        Assert.True(epochs.ChangedSince(newCall, Guid.NewGuid(), null, "group"));
    }
}
