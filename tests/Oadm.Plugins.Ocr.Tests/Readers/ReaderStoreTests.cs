using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Ocr.Readers;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Ocr.Tests.Readers;

/// <summary>The saved readers: checks with the spec's texts, limits, storage (docs/spec/ocr.md "Readers", "Rules that never break").</summary>
public sealed class ReaderStoreTests
{
    [Fact]
    public async Task ASavedReaderIsStoredListedAndKeptOverARestart()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var area = new Area { Name = " Container number ", Box = new RelativeBox(0.1, 0.1, 0.4, 0.2), Expected = ExpectedText.WithFormat(" @@@U ###### # ") };

        var saved = await ocr.SaveAsync(ocr.NewReader(" Gate 2 ", areas: area) with { OwnerScopeKey = "forged" });

        Assert.False(string.IsNullOrEmpty(saved.Id));
        Assert.Equal("Gate 2", saved.Name);
        Assert.Equal("Container number", saved.Areas[0].Name);
        Assert.Equal("@@@U ###### #", saved.Areas[0].Expected.Format);
        Assert.Null(saved.OwnerScopeKey); // never from the page, never to the page
        var list = await ocr.ListAsync();
        var row = Assert.Single(list.Readers);
        Assert.Equal(("Gate 2", ocr.Inside.Id, ReaderMode.OnAction, ""), (row.Name, row.DeviceId, row.Mode, row.LastText));
        Assert.Equal([1, 2, 4, 8, 12, 15], list.FpsChoices);
        Assert.Equal(16, list.MaxWatching); // the harness's 8 cores: 4 workers
        var got = OcrJson.Deserialize<Reader>(await ocr.CallAsync(OcrMethods.GetReader, new ReaderRequest { ReaderId = saved.Id }));
        Assert.Equal(OcrJson.Serialize(saved), OcrJson.Serialize(got));

        // A restart reads the stored record again.
        await using var again = new OcrPlugin(ocr.Parts);
        await again.StartAsync(ocr.Context, CancellationToken.None);
        Assert.Equal(OcrJson.Serialize(saved), OcrJson.Serialize(again.FindReader(saved.Id)));
        Assert.Single(await ocr.Context.PluginRecords.ListAsync(ReaderStore.Collection, CancellationToken.None));
    }

    [Fact]
    public async Task AChangeKeepsTheIdAndADeleteRemovesEverything()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var saved = await ocr.SaveAsync(ocr.NewReader());
        ocr.Frames.Single = "MSCU 123456 7";
        await ocr.ReadOnceAsync(saved.Id);

        var renamed = await ocr.SaveAsync(saved with { Name = "Gate 3" });
        Assert.Equal(saved.Id, renamed.Id);
        Assert.NotNull(ocr.Plugin.Runtime!.Last(saved.Id)); // a new name keeps the picture
        var moved = await ocr.SaveAsync(renamed with { Resolution = new Resolution(1280, 720) });
        Assert.Null(ocr.Plugin.Runtime.Last(moved.Id)); // another picture: the old reading no longer fits

        await ocr.CallAsync(OcrMethods.DeleteReader, new ReaderRequest { ReaderId = saved.Id });
        Assert.Empty((await ocr.ListAsync()).Readers);
        Assert.Empty(await ocr.Context.PluginRecords.ListAsync(ReaderStore.Collection, CancellationToken.None));
        Assert.Equal(OcrInfo.AnyReaderProblemTypeId, Assert.Single(ocr.Plugin.EventTypes).Id);
        var gone = await Assert.ThrowsAsync<ArgumentException>(() => ocr.CallAsync(OcrMethods.GetReader, new ReaderRequest { ReaderId = saved.Id }));
        Assert.Equal(ReaderRules.Gone, gone.Message);
        await ocr.CallAsync(OcrMethods.DeleteReader, new ReaderRequest { ReaderId = saved.Id }); // twice: nothing to do
    }

    [Fact]
    public async Task ASavedReaderKeepsItsCamera()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var other = ocr.Context.Add(new FakeDevice { Address = "10.0.0.3" });
        var saved = await ocr.SaveAsync(ocr.NewReader());

        // The camera is chosen once, with New: a save with another camera is refused below the camera key and changes nothing.
        var refused = await Assert.ThrowsAsync<ReaderFieldException>(() => ocr.SaveAsync(saved with { DeviceId = other.Id, Name = "Gate 9" }));
        Assert.Equal(new FieldError(OcrFields.Camera, ReaderRules.CameraFixed), Assert.Single(refused.Errors));
        Assert.Equal("A saved reader keeps its camera. Create a new reader for another camera.", ReaderRules.CameraFixed);
        Assert.Equal((ocr.Inside.Id, "Gate 2"), (ocr.Plugin.FindReader(saved.Id)!.DeviceId, ocr.Plugin.FindReader(saved.Id)!.Name));

        // A new reader on that camera is fine.
        Assert.Equal(other.Id, (await ocr.SaveAsync(ocr.NewReader("Dock", device: other.Id))).DeviceId);
    }

    [Fact]
    public async Task ABadReaderIsRefusedWithItsTextAndNothingIsStored()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var whole = new Area { Name = "Number" };
        var cases = new (Reader Reader, string Field, string Text)[]
        {
            (ocr.NewReader(" "), "name", ReaderRules.NameMissing),
            (ocr.NewReader(new string('x', 65)), "name", ReaderRules.NameTooLong),
            (ocr.NewReader() with { DeviceId = Guid.Empty }, "camera", ReaderRules.CameraMissing),
            (ocr.NewReader() with { VideoSource = 0 }, "videoSource", ReaderRules.SourceMissing),
            (ocr.NewReader() with { Resolution = new Resolution(0, 0) }, "resolution", ReaderRules.ResolutionMissing),
            (ocr.NewReader() with { Mode = (ReaderMode)7 }, "mode", ReaderRules.ModeMissing),
            (ocr.NewReader(mode: ReaderMode.Watching) with { Fps = 5 }, "fps", "Choose 1, 2, 4, 8, 12 or 15 frames per second."),
            (ocr.NewReader("Gate\n2"), "name", ReaderRules.NameInvalid),
            (ocr.NewReader(areas: whole with { Box = new RelativeBox(0.1, 0.1, 0.01, 0.5) }), "areas[0].box", ReaderRules.AreaTooSmall),
            (ocr.NewReader(areas: [.. Enumerable.Range(1, 9).Select(i => whole with { Name = $"Area {i}" })]), "areas", "A reader has at most 8 areas."),
            (ocr.NewReader(areas: [whole, whole with { Name = "number" }]), "areas[1].name", "Two areas are named number."),
            (ocr.NewReader(areas: whole with { Name = "" }), "areas[0].name", ReaderRules.AreaNameMissing),
            (ocr.NewReader(areas: whole with { Box = new RelativeBox(0.8, 0, 0.5, 1) }), "areas[0].box", ReaderRules.AreaOutside),
            (ocr.NewReader(areas: whole with { Expected = new ExpectedText { Kind = ExpectedTextKind.Format } }), "areas[0].format", ReaderRules.FormatMissing),
            (ocr.NewReader(areas: whole with { Expected = ExpectedText.WithFormat("XYZ") }), "areas[0].format", ReaderRules.FormatWithoutPlaceholder),
        };

        foreach (var (reader, field, text) in cases)
        {
            var refused = await Assert.ThrowsAsync<ReaderFieldException>(() => ocr.SaveAsync(reader));
            Assert.Equal(new FieldError(field, text), Assert.Single(refused.Errors));
        }

        // Every field's problem at once, each with its key.
        var all = await Assert.ThrowsAsync<ReaderFieldException>(() => ocr.SaveAsync(ocr.NewReader(" ") with { DeviceId = Guid.Empty, Areas = [whole with { Name = "" }] }));
        Assert.Equal(["name", "camera", "areas[0].name"], all.Errors.Select(e => e.Field));

        await ocr.SaveAsync(ocr.NewReader());
        var taken = await Assert.ThrowsAsync<ReaderFieldException>(() => ocr.SaveAsync(ocr.NewReader("GATE 2")));
        Assert.Equal(new FieldError(OcrFields.Name, "A reader named GATE 2 exists already."), Assert.Single(taken.Errors));
        Assert.Single(await ocr.Context.PluginRecords.ListAsync(ReaderStore.Collection, CancellationToken.None));
    }

    [Fact]
    public async Task AtMost50ReadersAndTheWatchingOnesByTheCores()
    {
        await using var ocr = await OcrHarness.StartAsync(); // 8 cores: 4 workers, 16 watching
        for (var i = 0; i < OcrLimits.MaxReaders; i++)
        {
            await ocr.SaveAsync(ocr.NewReader($"Reader {i:00}", i < 16 ? ReaderMode.Watching : ReaderMode.OnAction));
        }

        var tooMany = await Assert.ThrowsAsync<ArgumentException>(() => ocr.SaveAsync(ocr.NewReader("One more")));
        Assert.Equal("OADM keeps at most 50 readers. Delete one first.", tooMany.Message);

        var onDemand = ocr.Plugin.AllReaders.First(r => r.Mode == ReaderMode.OnAction);
        var watching = await Assert.ThrowsAsync<ReaderFieldException>(() => ocr.SaveAsync(onDemand with { Mode = ReaderMode.Watching }));
        Assert.Equal(new FieldError(OcrFields.Mode, "At most 16 readers can watch at once. Set another reader to On action first."), Assert.Single(watching.Errors));

        // A watching reader may be changed; one set to On action makes room.
        var first = ocr.Plugin.AllReaders.First(r => r.Mode == ReaderMode.Watching);
        await ocr.SaveAsync(first with { Fps = 4 });
        await ocr.SaveAsync(first with { Mode = ReaderMode.OnAction });
        await ocr.SaveAsync(onDemand with { Mode = ReaderMode.Watching });
        await Eventually.TrueAsync(() => ocr.Frames.Open == 16, "16 readers watch");
    }

    [Fact]
    public async Task ManyCoresLet50WatchAndEveryServerOffersTheHigherRates()
    {
        // A graphics card no longer changes the limits: the CPU reads every fixed area.
        await using var ocr = await OcrHarness.StartAsync(onGpu: false, processorCount: 24);
        for (var i = 0; i < 48; i++)
        {
            await ocr.SaveAsync(ocr.NewReader($"Reader {i:00}", ReaderMode.Watching) with { Fps = i % 2 == 0 ? 15 : 8 });
        }

        var list = await ocr.ListAsync();
        Assert.Equal([1, 2, 4, 8, 12, 15], list.FpsChoices);
        Assert.Equal(48, list.MaxWatching);
        Assert.Equal(48, list.Readers.Count);
        await Eventually.TrueAsync(() => ocr.Frames.Open == 48, "48 readers watch");
        Assert.Equal("Choose 1, 2, 4, 8, 12 or 15 frames per second.", ReaderRules.FpsNotAllowed());
    }

    [Fact]
    public async Task AReaderBeyondTheWatchingLimitSaysWhyAndAnOddRateReadsAtTheNextBelow()
    {
        // Readers stored by a server with more cores: this one watches 16, the others say why they read nothing.
        await using var ocr = new OcrHarness(processorCount: 8);
        var stored = Enumerable.Range(0, 18).Select(i => ocr.NewReader($"Reader {i:00}", ReaderMode.Watching) with { Fps = i == 0 ? 10 : 1 }).ToList();
        await ocr.Context.PluginRecords.SetManyAsync(
            ReaderStore.Collection,
            [.. stored.Select((r, i) => new KeyValuePair<string, string?>($"r{i:00}", OcrJson.Serialize(r)))],
            CancellationToken.None);
        await ocr.Plugin.StartAsync(ocr.Context, CancellationToken.None);

        await Eventually.TrueAsync(() => ocr.Frames.Open == 16, "16 readers watch");
        var list = await ocr.ListAsync();
        Assert.Equal(2, list.Readers.Count(r => r.Problem == ReaderRules.TooManyWatching(16)));
        Assert.Equal(8, ocr.Plugin.Runtime!.LoopFps("r00")); // 10 is no choice: the next one below
    }

    [Fact]
    public async Task OnlyTheReaderDefinitionsAreStored()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var saved = await ocr.SaveAsync(ocr.NewReader("Meter", ReaderMode.Watching));
        await Eventually.TrueAsync(() => ocr.Frames.Open == 1, "the reader watches");
        var written = ocr.Context.PluginRecords.WrittenRecords;

        ocr.Frames.Single = "12345";
        await ocr.ReadOnceAsync(saved.Id);
        foreach (var text in new[] { "1", "1", "2", "2" })
        {
            await ocr.SendAndWaitAsync(ocr.Inside.Id, text);
        }

        // Readings, frames and events never reach the database or the settings (rule 2).
        Assert.Equal(written, ocr.Context.PluginRecords.WrittenRecords);
        Assert.Empty(ocr.Context.PluginSettings.Values);
        var stored = Assert.Single(await ocr.Context.PluginRecords.ListAsync(ReaderStore.Collection, CancellationToken.None));
        Assert.DoesNotContain("12345", stored.Value, StringComparison.Ordinal);
    }
}
