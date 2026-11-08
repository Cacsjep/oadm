using Oadm.Plugins.Vision.Model;

namespace Oadm.Plugins.Vision.Tests;

public sealed class SkeletonTests
{
    [Fact]
    public void Every_node_kind_has_a_unique_id_and_unique_field_keys()
    {
        Assert.Equal(NodeKinds.All.Count, NodeKinds.All.Select(k => k.Id).Distinct().Count());
        foreach (var kind in NodeKinds.All)
        {
            Assert.Equal(kind.Fields.Count, kind.Fields.Select(f => f.Key).Distinct().Count());
        }
    }

    [Fact]
    public void Limits_follow_the_cores()
    {
        Assert.Equal(4, VisionLimits.MaxWatchingFor(2));
        Assert.Equal(24, VisionLimits.MaxWatchingFor(24));
        Assert.Equal(50, VisionLimits.MaxWatchingFor(128));
    }
}
