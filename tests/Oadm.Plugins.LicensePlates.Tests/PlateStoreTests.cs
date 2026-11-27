using Oadm.Plugins.LicensePlates.Plates;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.LicensePlates.Tests;

/// <summary>The plate store's records: a deleted tag is never half deleted, also when a write fails on the way.</summary>
public sealed class PlateStoreTests
{
    /// <summary>The records of the plugin; a write of <see cref="FailOn"/> throws once.</summary>
    private sealed class FailingRecords(IPluginRecords inner) : IPluginRecords
    {
        public string? FailOn { get; set; }

        public Task<IReadOnlyDictionary<string, string>> ListAsync(string collection, CancellationToken ct) => inner.ListAsync(collection, ct);

        public Task SetManyAsync(string collection, IReadOnlyCollection<KeyValuePair<string, string?>> records, CancellationToken ct)
        {
            if (collection == FailOn)
            {
                FailOn = null;
                throw new IOException("The disk is full.");
            }

            return inner.SetManyAsync(collection, records, ct);
        }

        public Task ClearAsync(string collection, CancellationToken ct) => inner.ClearAsync(collection, ct);
    }

    [Theory]
    [InlineData(PlateStore.PlatesCollection)]
    [InlineData(PlateStore.CamerasCollection)]
    public async Task A_tag_delete_that_fails_halfway_leaves_no_trace_of_the_tag_after_a_restart(string failing)
    {
        var records = new FailingRecords(new MemoryPluginRecords());
        using var store = new PlateStore(records);
        await store.LoadAsync(CancellationToken.None);
        var tag = new PlateTag { Id = Guid.NewGuid(), Name = "Staff" };
        await store.SaveTagAsync(tag, CancellationToken.None);
        await store.WritePlatesAsync([new Plate { Text = "W12345A", Tags = [tag.Id] }], [], CancellationToken.None);
        var camera = Guid.NewGuid();
        await store.WriteCamerasAsync([new CameraEntry { DeviceId = camera, AllowedTags = [tag.Id] }], [], CancellationToken.None);

        records.FailOn = failing;
        await Assert.ThrowsAsync<IOException>(() => store.DeleteTagAsync(tag.Id, CancellationToken.None));

        using var reloaded = new PlateStore(records);
        await reloaded.LoadAsync(CancellationToken.None);
        Assert.Null(reloaded.Tag(tag.Id));
        Assert.Empty(reloaded.Find("W12345A")!.Tags);
        Assert.Empty(reloaded.Camera(camera)!.AllowedTags);
    }
}
