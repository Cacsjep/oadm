using Oadm.Plugins.Vision.Api;

namespace Oadm.Plugins.Vision.Pipelines;

/// <summary>
/// Vision Center's side of the runtime stats shared with Text recognition (<see cref="StatsWindow"/>, <see cref="StatsRules"/>):
/// its texts ("pipelines") and its page enum.
/// </summary>
internal static class PipelineStats
{
    /// <inheritdoc cref="StatsRules.SampleInterval"/>
    public static readonly TimeSpan SampleInterval = StatsRules.SampleInterval;

    /// <inheritdoc cref="StatsRules.SlowShare"/>
    public const double SlowShare = StatsRules.SlowShare;

    /// <summary>The texts of Vision Center's traffic light.</summary>
    public static readonly LightTexts Texts = new(PipelineRules.KeepUp, PipelineRules.Overloaded, PipelineRules.BusySlow, PipelineRules.BusyLoad);

    /// <summary>The traffic light (<see cref="StatsRules.Light"/>) with Vision Center's texts.</summary>
    public static (LoadState State, string Text) Light(double load, bool overloaded, IEnumerable<(int Fps, double Rate, bool Judged)> pipelines)
    {
        var (level, text) = StatsRules.Light(load, overloaded, pipelines, Texts);
        return ((LoadState)(int)level, text);
    }
}
