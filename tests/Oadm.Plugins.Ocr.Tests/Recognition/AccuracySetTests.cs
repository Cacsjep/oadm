using System.Globalization;
using Oadm.Plugins.Ocr.Recognition;
using Xunit.Abstractions;

namespace Oadm.Plugins.Ocr.Tests.Recognition;

/// <summary>
/// The accuracy set and its score (docs/spec/ocr.md "Accuracy", rule 6): every picture of
/// <c>Fixtures/AccuracySet</c> read on the CPU, the exact-value rate and character error rate per group written to the output,
/// and a group below its baseline (<c>baseline.json</c>, measured when the set was made) fails. One value of slack per group
/// absorbs the last-digit differences of the CPU maths between platforms.
/// </summary>
[Collection(RecognitionGroup.Name)]
public sealed class AccuracySetTests(RecognizerFixture fixture, ITestOutputHelper output)
{
    /// <summary>Set to the source folder <c>tests/Oadm.Plugins.Ocr.Tests/Fixtures/AccuracySet</c> to make the generated pictures anew.</summary>
    private const string RegenerateVariable = "OADM_OCR_REGENERATE";

    [Fact]
    public async Task The_accuracy_set_reads_at_least_its_baseline()
    {
        var pictures = AccuracySet.Load();
        var baseline = AccuracySet.Baseline();

        var (scores, misses) = await AccuracySet.ScoreAsync(fixture.Recognizer, pictures, CancellationToken.None);

        output.WriteLine($"Accuracy set on {fixture.Recognizer.RunsOn.Name}, {pictures.Count} pictures:");
        output.WriteLine(AccuracySet.Table(scores));
        var withFormat = Total(scores, pictures, format: true);
        var anyText = Total(scores, pictures, format: false);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"With a format: {withFormat:P1} exact, any text: {anyText:P1} exact"));
        foreach (var miss in misses)
        {
            output.WriteLine("Miss: " + miss);
        }

        Assert.Equal(baseline.Keys.Order(StringComparer.Ordinal), scores.Select(s => s.Group).Order(StringComparer.Ordinal));
        foreach (var score in scores)
        {
            var floor = baseline[score.Group];
            var slack = 1.0 / score.Values;
            Assert.True(score.ExactRate >= floor.Exact - slack - 1e-9, $"{score.Group}: {score.ExactRate:P1} exact, baseline {floor.Exact:P1}");
            Assert.True(score.CharacterErrorRate <= floor.Cer + slack + 1e-9, $"{score.Group}: {score.CharacterErrorRate:P2} character errors, baseline {floor.Cer:P2}");
        }
    }

    [Fact]
    public void The_set_is_complete_and_small()
    {
        var pictures = AccuracySet.Load();
        var total = 0L;
        foreach (var picture in pictures.DistinctBy(p => p.File))
        {
            var file = new FileInfo(Path.Combine(AccuracySet.Folder, picture.File));
            Assert.True(file.Exists, picture.File);
            total += file.Length;
            Assert.All(picture.Areas, a => Assert.Equal(4, a.Box.Length));
        }

        Assert.True(total < 15 * 1024 * 1024, $"{total} bytes");

        // Every container number of the set has a valid check digit.
        Assert.All(pictures.SelectMany(p => p.Areas).Where(a => a.Format == "@@@U ###### #"), a => Assert.True(Oadm.Plugins.Ocr.Matching.ContainerCheck.IsValid(a.Expected), a.Expected));
    }

    [Fact]
    public void The_generated_pictures_are_made_anew_only_on_request()
    {
        var folder = Environment.GetEnvironmentVariable(RegenerateVariable);
        if (string.IsNullOrEmpty(folder))
        {
            output.WriteLine($"{RegenerateVariable} is not set: the committed pictures are used.");
            return;
        }

        var pictures = AccuracySetGenerator.CameraPictures();
        pictures.AddRange(AccuracySetGenerator.Generate(folder));
        File.WriteAllText(Path.Combine(folder, "set.json"), AccuracySetGenerator.ToJson(pictures));
        output.WriteLine($"{pictures.Count} pictures written to {folder}. Measure the new baseline before committing.");
    }

    private static double Total(IEnumerable<GroupScore> scores, IReadOnlyList<AccuracyPicture> pictures, bool format)
    {
        var groups = pictures.Where(p => p.Areas.Any(a => (a.Format is not null) == format)).Select(p => p.Group).ToHashSet();
        var chosen = scores.Where(s => groups.Contains(s.Group)).ToList();
        var values = chosen.Sum(s => s.Values);
        return values == 0 ? 1 : chosen.Sum(s => s.Exact) / (double)values;
    }
}
