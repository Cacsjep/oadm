using System.Text.Json;

using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Events;
using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Ocr.Readers;
using Oadm.Plugins.Ocr.Tests.Readers;
using Oadm.Sdk.Actions;
using Oadm.Sdk.Events;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Ocr.Tests.Events;

/// <summary>The events "Reader problem" and "Any reader problem" (docs/spec/ocr.md "Events and the rule action").</summary>
public sealed class ReaderProblemEventTests
{
    private static ActionInvocation Invocation(string readerId) => new(
        Guid.NewGuid(),
        "Gate opens",
        ActionEdge.Fired,
        JsonSerializer.SerializeToElement(new Dictionary<string, string> { [OcrInfo.ReaderActionField] = readerId }),
        null,
        new ActionTrigger("oadm.test", "Test", null, null, null, null, DateTimeOffset.UnixEpoch, new Dictionary<string, string>()));

    private static Reader Saved(string name, string id = "r1") => new() { Id = id, Name = name, DeviceId = Guid.NewGuid() };

    private static List<FakePublishedRuleEvent> On(FakeEventPublisher publisher, string typeId) =>
        [.. publisher.Published.Where(e => e.TypeId == typeId && e.IsOn == true)];

    [Fact]
    public async Task EveryReaderHasItsProblemEventAndOneEventCoversThemAll()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var reader = await ocr.SaveAsync(ocr.NewReader("Gate 2"));

        var type = ocr.Plugin.EventTypes.Single(t => t.Id == OcrInfo.ReaderProblemTypeId(reader.Id));
        Assert.Equal(("Reader problem", EventKind.Stateful, EventScope.Device), (type.DisplayName, type.Kind, type.Scope));
        Assert.Equal(["Text recognition", "Gate 2"], type.Folders);
        Assert.Equal(ocr.Inside.Id, type.FixedDeviceId);
        Assert.Equal([("reader", false), ("problem", true)], type.DataFields.Select(f => (f.Name, f.Filter)));

        var any = ocr.Plugin.EventTypes.Single(t => t.Id == OcrInfo.AnyReaderProblemTypeId);
        Assert.Equal(("Any reader problem", EventKind.Stateful, EventScope.Server), (any.DisplayName, any.Kind, any.Scope));
        Assert.Equal(["Text recognition"], any.Folders);
        Assert.Equal("all", any.OwnerScopeKey);
    }

    [Fact]
    public async Task ThePluginReportsEveryTypeOnceSoTheRulesKnowItIsReady()
    {
        await using var ocr = await OcrHarness.StartAsync();
        Assert.Empty(ocr.Publisher.States[OcrInfo.AnyReaderProblemTypeId]);

        var reader = await ocr.SaveAsync(ocr.NewReader("Gate"));

        Assert.Empty(ocr.Publisher.States[OcrInfo.ReaderProblemTypeId(reader.Id)]);
    }

    [Fact]
    public void AProblemTurnsTheEventsOnAtOnceAndAWorkingReaderTurnsThemOff()
    {
        var publisher = new FakeEventPublisher();
        var reporter = new ReaderProblemReporter(publisher);
        var reader = Saved("Gate");
        var type = OcrInfo.ReaderProblemTypeId(reader.Id);
        string? problem = null;

        reporter.Report([reader], _ => problem);
        Assert.Empty(On(publisher, type));

        problem = "The camera sends no pictures.";
        reporter.Report([reader], _ => problem);

        var on = Assert.Single(On(publisher, type));
        Assert.Equal(reader.DeviceId, on.DeviceId);
        Assert.Equal(("Gate", problem), (on.Data[OcrInfo.ReaderField], on.Data[OcrInfo.ProblemField]));
        var any = Assert.Single(On(publisher, OcrInfo.AnyReaderProblemTypeId));
        Assert.Null(any.DeviceId);
        Assert.Equal(("1", "Gate"), (any.Data[OcrInfo.CountField], any.Data[OcrInfo.ReaderField]));

        problem = null;
        reporter.Report([reader], _ => problem);

        Assert.Empty(publisher.States[type]);
        Assert.Empty(publisher.States[OcrInfo.AnyReaderProblemTypeId]);
    }

    [Fact]
    public void AnUnchangedProblemIsReportedOnceAndANewTextOnlyUpdatesTheValues()
    {
        var publisher = new FakeEventPublisher();
        var reporter = new ReaderProblemReporter(publisher);
        var reader = Saved("Gate");
        var type = OcrInfo.ReaderProblemTypeId(reader.Id);
        var problem = "The camera sends no pictures.";

        reporter.Report([reader], _ => problem);
        reporter.Report([reader], _ => problem);
        Assert.Single(On(publisher, type));

        problem = "Timeout";
        reporter.Report([reader], _ => problem);

        Assert.Equal("Timeout", On(publisher, type)[^1].Data[OcrInfo.ProblemField]);
        Assert.DoesNotContain(publisher.Published, e => e.IsOn == false);
    }

    [Fact]
    public void AnyReaderProblemNamesEveryFailingReaderAndStaysOnUntilTheLastOneWorks()
    {
        var publisher = new FakeEventPublisher();
        var reporter = new ReaderProblemReporter(publisher);
        Reader[] readers = [Saved("Gate", "a"), Saved("Belt", "b")];
        var failing = new HashSet<string> { "a", "b" };

        reporter.Report(readers, id => failing.Contains(id) ? "Timeout" : null);
        Assert.Equal(("2", "Gate, Belt"), (On(publisher, OcrInfo.AnyReaderProblemTypeId)[^1].Data[OcrInfo.CountField], On(publisher, OcrInfo.AnyReaderProblemTypeId)[^1].Data[OcrInfo.ReaderField]));

        failing.Remove("a");
        reporter.Report(readers, id => failing.Contains(id) ? "Timeout" : null);
        Assert.Equal(("1", "Belt"), (On(publisher, OcrInfo.AnyReaderProblemTypeId)[^1].Data[OcrInfo.CountField], On(publisher, OcrInfo.AnyReaderProblemTypeId)[^1].Data[OcrInfo.ReaderField]));
        Assert.Equal([Guid.Empty], publisher.States[OcrInfo.AnyReaderProblemTypeId]);

        failing.Clear();
        reporter.Report(readers, id => failing.Contains(id) ? "Timeout" : null);
        Assert.Empty(publisher.States[OcrInfo.AnyReaderProblemTypeId]);
    }

    [Fact]
    public void AProblemFollowsTheReaderToItsNewCamera()
    {
        var publisher = new FakeEventPublisher();
        var reporter = new ReaderProblemReporter(publisher);
        var reader = Saved("Gate");
        var type = OcrInfo.ReaderProblemTypeId(reader.Id);
        reporter.Report([reader], _ => ReaderRules.NotInstalled);

        var moved = reader with { DeviceId = Guid.NewGuid() };
        reporter.Report([moved], _ => ReaderRules.NotInstalled);

        Assert.Equal(moved.DeviceId, On(publisher, type)[^1].DeviceId);
        Assert.Equal([moved.DeviceId], publisher.States[type]);
    }

    [Fact]
    public void AReaderThatIsStartingIsNoProblemAndItsTypeWaits()
    {
        var publisher = new FakeEventPublisher();
        var reporter = new ReaderProblemReporter(publisher);
        var reader = Saved("Gate");

        reporter.Report([reader], _ => ReaderRules.Starting);

        Assert.Empty(publisher.Published);
        Assert.Empty(publisher.States);
    }

    [Fact]
    public async Task AStreamThatGivesNoPicturesFiresTheEventAndPicturesTurnItOff()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var reader = await ocr.SaveAsync(ocr.NewReader("Gate", ReaderMode.Watching));
        var type = OcrInfo.ReaderProblemTypeId(reader.Id);
        await Eventually.TrueAsync(() => ocr.Frames.Open == 1, "the stream is open");

        ocr.Frames.Tell(ocr.Inside.Id, "The camera sends no pictures.");

        await Eventually.TrueAsync(() => On(ocr.Publisher, type).Count == 1, "the event turned on");
        Assert.Equal("The camera sends no pictures.", On(ocr.Publisher, type)[0].Data[OcrInfo.ProblemField]);

        ocr.Frames.Tell(ocr.Inside.Id, null);

        await Eventually.TrueAsync(() => ocr.Publisher.States[type].Count == 0 && ocr.Plugin.Runtime!.ProblemOf(reader.Id) is null, "the event turned off");
    }

    [Fact]
    public async Task ReadingsThatKeepFailingAreAProblemAndAWorkingOneEndsIt()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var reader = await ocr.SaveAsync(ocr.NewReader("Gate", ReaderMode.Watching));
        await Eventually.TrueAsync(() => ocr.Frames.Open == 1, "the stream is open");
        ocr.Recognizer.FailAll = new InvalidOperationException("The session is gone.");

        for (var i = 0; i < OcrLimits.FailedReadingsForProblem; i++)
        {
            var started = ocr.Recognizer.Started;
            Assert.Null(ocr.Plugin.Runtime!.ProblemOf(reader.Id));
            ocr.Frames.Send(ocr.Inside.Id, "FAIL" + i);
            await Eventually.TrueAsync(() => ocr.Recognizer.Started > started, "the reading started"); // the next frame cannot replace it
        }

        await Eventually.TrueAsync(() => ocr.Plugin.Runtime!.ProblemOf(reader.Id) == ReaderRules.ReadingFailed, "the problem shows");

        ocr.Recognizer.FailAll = null;
        await ocr.SendAndWaitAsync(ocr.Inside.Id, "MSCU");

        await Eventually.TrueAsync(() => ocr.Plugin.Runtime!.ProblemOf(reader.Id) is null, "the problem ended");
    }

    [Fact]
    public async Task AWatchingReaderWithoutItsModelsIsAProblem()
    {
        await using var ocr = await OcrHarness.StartWithoutRecognitionAsync();

        var reader = await ocr.SaveAsync(ocr.NewReader("Gate", ReaderMode.Watching));

        Assert.Equal(ReaderRules.NotInstalled, ocr.Plugin.Runtime!.ProblemOf(reader.Id));
    }

    [Fact]
    public async Task AProblemOfTheFirstSaveKeepsTheNewTypeReady()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var reportedFirst = false;
        ocr.Plugin.EventTypesChanged += (_, _) => reportedFirst |= ocr.Publisher.States.Keys.Any(k => k.StartsWith(OcrInfo.ReaderProblemTypePrefix, StringComparison.Ordinal));

        var reader = await ocr.SaveAsync(ocr.NewReader("Gate", ReaderMode.Watching));

        Assert.False(reportedFirst); // the rules know a type before its report: a report before it would be forgotten
        Assert.True(ocr.Publisher.States.ContainsKey(OcrInfo.ReaderProblemTypeId(reader.Id)));
    }

    [Fact]
    public async Task AFailedActionIsAProblemOfAnOnActionReaderUntilAReadingWorks()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var reader = await ocr.SaveAsync(ocr.NewReader("Scale") with { Mode = ReaderMode.OnAction });
        var type = OcrInfo.ReaderProblemTypeId(reader.Id);
        var action = Assert.Single(ocr.Plugin.ActionPlugins);

        ocr.Frames.SingleError = new InvalidOperationException("The camera sends no pictures.");
        await Assert.ThrowsAsync<InvalidOperationException>(() => action.ExecuteAsync(new FakeActionContext(), Invocation(reader.Id), CancellationToken.None));

        Assert.Equal("The camera sends no pictures.", ocr.Plugin.Runtime!.ProblemOf(reader.Id));
        Assert.Equal("The camera sends no pictures.", Assert.Single(On(ocr.Publisher, type)).Data[OcrInfo.ProblemField]);

        ocr.Frames.SingleError = null;
        ocr.Frames.Single = "MSCU";
        await action.ExecuteAsync(new FakeActionContext(), Invocation(reader.Id), CancellationToken.None);

        Assert.Null(ocr.Plugin.Runtime!.ProblemOf(reader.Id));
        Assert.Empty(ocr.Publisher.States[type]);
    }

    [Fact]
    public async Task AChangeToWatchingDropsTheProblemOfTheLastAction()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var reader = await ocr.SaveAsync(ocr.NewReader("Scale") with { Mode = ReaderMode.OnAction });
        ocr.Frames.SingleError = new InvalidOperationException("Timeout");
        await Assert.ThrowsAsync<InvalidOperationException>(() => ocr.ReadOnceAsync(reader.Id));
        Assert.NotNull(ocr.Plugin.Runtime!.ProblemOf(reader.Id));

        await ocr.SaveAsync(reader with { Mode = ReaderMode.Watching });

        Assert.Null(ocr.Plugin.Runtime!.ProblemOf(reader.Id));
    }

    [Fact]
    public async Task AWatchingReaderKeepsNoProblemFromASingleFailedAction()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var reader = await ocr.SaveAsync(ocr.NewReader("Gate", ReaderMode.Watching));
        await Eventually.TrueAsync(() => ocr.Frames.Open == 1, "the stream is open");
        ocr.Frames.SingleError = new InvalidOperationException("Timeout");

        await Assert.ThrowsAsync<InvalidOperationException>(() => ocr.ReadOnceAsync(reader.Id));

        Assert.Null(ocr.Plugin.Runtime!.ProblemOf(reader.Id));
    }

    [Fact]
    public async Task AlreadyReadingIsNoProblem()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var reader = await ocr.SaveAsync(ocr.NewReader("Scale") with { Mode = ReaderMode.OnAction });
        ocr.Frames.SingleError = new InvalidOperationException(OcrInfo.AlreadyReading);

        await Assert.ThrowsAsync<InvalidOperationException>(() => ocr.ReadOnceAsync(reader.Id));

        Assert.Null(ocr.Plugin.Runtime!.ProblemOf(reader.Id));
    }
}
