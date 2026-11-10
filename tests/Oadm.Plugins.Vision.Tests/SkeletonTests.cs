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

    // Every field says what it does in its info icon: one short sentence, never the label again.
    [Fact]
    public void Every_node_field_has_a_short_info()
    {
        foreach (var field in NodeKinds.All.SelectMany(k => k.Fields))
        {
            Assert.False(string.IsNullOrWhiteSpace(field.Info), $"{field.Key} has no info");
            Assert.True(field.Info!.Split(' ').Length <= 10, $"{field.Key}: {field.Info}");
            Assert.NotEqual(field.Label, field.Info.TrimEnd('.'));
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
