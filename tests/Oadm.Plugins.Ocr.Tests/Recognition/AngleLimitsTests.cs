using System.Globalization;
using Xunit.Abstractions;

namespace Oadm.Plugins.Ocr.Tests.Recognition;

/// <summary>
/// The readable rotation and camera angle (docs/spec/ocr.md "Rotation and perspective (measured 2026-11-03)"), pinned: the
/// four texts of <see cref="AngleSweep"/> inside the measured limits must read exactly. Only the repository's Roboto, so the
/// pictures are the same on every platform; the poses lie inside the limits with room to spare (each reading's confidence is
/// written to the output). The full measurement is <see cref="AngleSweepTests"/> (Perf).
/// </summary>
[Collection(RecognitionGroup.Name)]
public sealed class AngleLimitsTests(RecognizerFixture fixture, ITestOutputHelper output)
{
    private static readonly SweepVariant[] Pinned =
    [
        new(100, AngleSweep.RobotoBold, 28, SweepDamage.None),
        new(101, AngleSweep.RobotoRegular, 36, SweepDamage.Noise),
    ];

    [Fact]
    public async Task Text_turned_up_to_the_limit_reads_exactly()
    {
        // Every text within 10° of upright and upside down; the container number within 15°; a single line (the sign) at
        // any turn; a value turned exactly on its side (the 90° rule and the angle model).
        float[] all = [0, 5, 10, 350, 355, 180];
        var cases = (from angle in all from text in AngleSweep.Texts select (Text: text, Angle: angle))
            .Concat(from angle in new float[] { 45, 90, 135, 225, 270 } select (Text: AngleSweep.Texts[2], Angle: angle))
            .Concat(from angle in new float[] { 15, 345 } select (Text: AngleSweep.Texts[0], Angle: angle))
            .Concat(from angle in new float[] { 90, 270 } select (Text: AngleSweep.Texts[1], Angle: angle));
        await AssertExactAsync(from c in cases from v in Pinned select (c.Text, new SweepPose(c.Angle), v));
    }

    [Fact]
    public async Task Text_tilted_away_up_to_the_limit_reads_exactly()
    {
        var cases = from text in AngleSweep.Texts
                    from pose in new[] { new SweepPose(0, TiltAroundVertical: 40), new SweepPose(0, TiltAroundHorizontal: 40), new SweepPose(0, TiltAroundVertical: 60), new SweepPose(0, TiltAroundHorizontal: 60) }
                    from v in Pinned
                    select (text, pose, v);
        await AssertExactAsync(cases);
    }

    private async Task AssertExactAsync(IEnumerable<(SweepText, SweepPose, SweepVariant)> cases)
    {
        var samples = await AngleSweep.RunAsync(AngleSweep.With(fixture.Recognizer), cases, search: false, fixture.Recognizer.Workers * 2, CancellationToken.None);
        foreach (var s in samples)
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{s.Pose.Label} {s.Text.Name} {s.Variant.Font} {s.Variant.CapHeight:0} px: '{s.Value}' ({s.Lines})"));
        }

        var misses = samples.Where(s => !s.Exact).Select(s => $"{s.Pose.Label} {s.Text.Name} {s.Variant.Font}: '{s.Value}' ({s.Lines})").ToList();
        Assert.True(misses.Count == 0, string.Join(Environment.NewLine, misses));
    }
}
