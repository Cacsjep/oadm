using System.Runtime.CompilerServices;

using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;

using Grpc.Core;

using Oadm.Client.Api;
using Oadm.Client.Assistant;
using Oadm.Client.Controls;
using Oadm.Contracts.V1;

using static Oadm.Client.Tests.AssistantTests;

namespace Oadm.Client.Tests;

/// <summary>
/// The Assistant panel like the chat apps (docs/spec/assistant.md "The panel"): Thinking until the answer's first event,
/// following the end only while the user is there, Try again and Edit (replace_last, the old entries gone in one step),
/// files pasted or dropped, Escape and Up arrow, and the headless screenshots of the composer.
/// </summary>
public sealed class AssistantChatUxTests
{
    private static async IAsyncEnumerable<AssistantEvent> Gated(TaskCompletionSource gate, IEnumerable<AssistantEvent> before, IEnumerable<AssistantEvent> after,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var e in before)
        {
            await Task.Yield();
            yield return e;
        }

        await gate.Task.WaitAsync(ct);
        foreach (var e in after)
        {
            await Task.Yield();
            yield return e;
        }
    }

    private static AssistantEvent Text(string text) => new() { Text = text };

    private static AssistantEvent Done(string error = "", bool stopped = false) => new() { Done = new AssistantDone { Error = error, Stopped = stopped } };

    [Fact]
    public async Task ThinkingShowsFromSendBelowTheNoticeUntilTheAnswersFirstEvent()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        api.AssistantScript = (_, ct) => Gated(gate, [new AssistantEvent { Notice = "Your messages go to Anthropic." }], [Text("Hello."), Done()], ct);
        var panel = await PanelAsync(api);
        panel.Draft = "Hi";
        var sending = panel.SendCommand.ExecuteAsync(null);
        await Oadm.Sdk.Testing.Eventually.TrueAsync(() => panel.Items.OfType<AssistantNoticeItem>().Any(), "the notice arrived");

        Assert.True(panel.IsThinking);
        Assert.Equal([typeof(AssistantUserItem), typeof(AssistantNoticeItem), typeof(AssistantStatusItem)], panel.Items.Select(i => i.GetType()));
        Assert.Equal(AssistantTexts.Thinking, ((AssistantStatusItem)panel.Items[2]).Text);

        gate.SetResult();
        await sending.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(panel.IsThinking);
        Assert.Equal([typeof(AssistantUserItem), typeof(AssistantNoticeItem), typeof(AssistantAnswerItem)], panel.Items.Select(i => i.GetType()));
    }

    [Fact]
    public async Task ThinkingGoesWithAnErrorOrARefusal()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        api.AssistantScript = (_, ct) => Script([Done("The provider did not answer. Try again.")], ct);
        var panel = await PanelAsync(api);
        panel.Draft = "Hi";
        await panel.SendCommand.ExecuteAsync(null);
        Assert.False(panel.IsThinking);
        Assert.DoesNotContain(panel.Items, i => i is AssistantStatusItem);

        api.AssistantScript = (_, _) => Refused();
        panel.Draft = "Again";
        await panel.SendCommand.ExecuteAsync(null);
        Assert.False(panel.IsThinking);
        Assert.DoesNotContain(panel.Items, i => i is AssistantStatusItem);
    }

    private static async IAsyncEnumerable<AssistantEvent> Refused()
    {
        await Task.Yield();
        throw new RpcException(new Status(StatusCode.ResourceExhausted, "The assistant reached today's limit. It works again tomorrow."));
#pragma warning disable CS0162 // an iterator needs a yield
        yield break;
#pragma warning restore CS0162
    }

    [Fact]
    public async Task TryAgainSendsTheLastMessageAgainInPlaceOfItsAnswer()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        var answers = 0;
        api.AssistantScript = (_, ct) => Script([Text($"Answer {++answers}."), Done()], ct);
        var panel = await PanelAsync(api);
        panel.Draft = "First";
        await panel.SendCommand.ExecuteAsync(null);
        panel.Draft = "Second";
        await panel.SendCommand.ExecuteAsync(null);

        var last = (AssistantAnswerItem)panel.Items[^1];
        Assert.True(last.ShowsRetry);
        Assert.Single(panel.Items, i => i.ShowsRetry); // only under the last answer
        Assert.True(((AssistantUserItem)panel.Items[2]).CanEdit);
        Assert.False(((AssistantUserItem)panel.Items[0]).CanEdit);

        var changes = 0;
        panel.Items.CollectionChanged += (_, e) => changes += e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset ? 1 : 0;
        await panel.RetryCommand.ExecuteAsync(last);

        var sent = api.AssistantMessages[^1];
        Assert.Equal(("Second", true), (sent.Text, sent.ReplaceLast));
        Assert.False(api.AssistantMessages[1].ReplaceLast);
        Assert.Equal(1, changes); // the old exchange left in one step
        Assert.Equal(["First", "Answer 1.", "Second", "Answer 3."], panel.Items.Select(i => i switch
        {
            AssistantUserItem u => u.Text,
            AssistantAnswerItem a => a.Text,
            _ => "?",
        }));
    }

    [Fact]
    public async Task TryAgainShowsOnAnErrorAndAStopAndAMessageTheServerNeverHadIsSentAsANewOne()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        api.AssistantScript = (_, ct) => Script([Text("Half"), Done(stopped: true)], ct);
        var panel = await PanelAsync(api);
        panel.Draft = "Tell me";
        await panel.SendCommand.ExecuteAsync(null);
        var stopped = Assert.IsType<AssistantNoticeItem>(panel.Items[^1]);
        Assert.True(stopped.ShowsRetry);
        Assert.False(panel.Items.OfType<AssistantAnswerItem>().Single().ShowsRetry);

        api.AssistantScript = (_, _) => Refused();
        await panel.RetryCommand.ExecuteAsync(stopped);
        Assert.True(api.AssistantMessages[^1].ReplaceLast); // the stopped exchange was on the server
        var refusal = Assert.IsType<AssistantNoticeItem>(panel.Items[^1]);
        Assert.True(refusal.IsError && refusal.ShowsRetry);

        api.AssistantScript = (_, ct) => Script([Text("Done."), Done()], ct);
        await panel.RetryCommand.ExecuteAsync(refusal);
        Assert.False(api.AssistantMessages[^1].ReplaceLast); // the refused one never reached the conversation
        Assert.Equal([typeof(AssistantUserItem), typeof(AssistantAnswerItem)], panel.Items.Select(i => i.GetType()));
    }

    [Fact]
    public async Task NothingToTryAgainOnTheServerSendsTheMessageAsANewOne()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        api.AssistantScript = (m, ct) => m.ReplaceLast ? NothingToRetry() : Script([Text("Yes."), Done()], ct);
        var panel = await PanelAsync(api);
        panel.Draft = "Hi";
        await panel.SendCommand.ExecuteAsync(null);
        await panel.RetryCommand.ExecuteAsync(panel.Items[^1]);

        Assert.Equal([false, true, false], api.AssistantMessages.Select(m => m.ReplaceLast));
        Assert.Equal([typeof(AssistantUserItem), typeof(AssistantAnswerItem)], panel.Items.Select(i => i.GetType()));

        static async IAsyncEnumerable<AssistantEvent> NothingToRetry()
        {
            await Task.Yield();
            throw new RpcException(new Status(StatusCode.FailedPrecondition, AssistantTexts.NothingToRetry));
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    [Fact]
    public async Task EditPutsTheLastMessageBackAndSendingReplacesThatExchange()
    {
        var path = Path.Combine(Path.GetTempPath(), $"oadm-edit-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(path, new byte[100]);
        try
        {
            using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5), seedSampleData: false);
            api.AssistantScript = (m, ct) => Script([Text("About " + m.Text), Done()], ct);
            var panel = await PanelAsync(api);
            await panel.AttachAsync(path);
            panel.Draft = "the lobby";
            await panel.SendCommand.ExecuteAsync(null);
            var user = (AssistantUserItem)panel.Items[0];
            var focus = 0;
            panel.FocusMessageRequested += (_, _) => focus++;

            panel.EditCommand.Execute(user);
            Assert.Equal("the lobby", panel.Draft);
            Assert.Single(panel.Attachments); // its files come back too
            Assert.Equal(1, focus);

            panel.Draft = "the yard";
            await panel.SendCommand.ExecuteAsync(null);
            var sent = api.AssistantMessages[^1];
            Assert.Equal(("the yard", true, 1), (sent.Text, sent.ReplaceLast, sent.Attachments.Count));
            Assert.Equal(["the yard", "About the yard"], panel.Items.Select(i => i is AssistantUserItem u ? u.Text : ((AssistantAnswerItem)i).Text));

            // An emptied box drops the edit: the next message is a new one.
            panel.EditCommand.Execute(panel.Items[0]);
            panel.Draft = string.Empty;
            panel.Draft = "the gate";
            await panel.SendCommand.ExecuteAsync(null);
            Assert.False(api.AssistantMessages[^1].ReplaceLast);
            Assert.Equal(4, panel.Items.Count);

            // Up arrow: only in an empty box, and only the last message.
            panel.Draft = "typing";
            Assert.False(panel.EditLast());
            panel.Draft = string.Empty;
            Assert.True(panel.EditLast());
            Assert.Equal("the gate", panel.Draft);
            panel.EditCommand.Execute(panel.Items[0]); // not the last one: nothing
            Assert.Equal("the gate", panel.Draft);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ScrolledUpTheListStopsFollowingAndGoToTheLatestFollowsAgain()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        api.AssistantScript = (_, ct) => Script([Text("Yes."), Done()], ct);
        var panel = await PanelAsync(api);
        Assert.False(panel.ShowJumpToLatest); // empty
        panel.IsFollowing = false;
        Assert.False(panel.ShowJumpToLatest);

        panel.Draft = "Hi";
        await panel.SendCommand.ExecuteAsync(null);
        Assert.True(panel.IsFollowing); // a new message goes to the end
        panel.IsFollowing = false; // the user scrolled up
        Assert.True(panel.ShowJumpToLatest);

        var scrolls = 0;
        panel.ScrollToEndRequested += (_, _) => scrolls++;
        panel.JumpToLatestCommand.Execute(null);
        Assert.True(panel.IsFollowing);
        Assert.False(panel.ShowJumpToLatest);
        Assert.Equal(1, scrolls);
    }

    [Fact]
    public async Task DroppedOrPastedFilesAttachLikeAttachFileWithItsLimitAndAPastedPictureBecomesAPng()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"oadm-drop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var files = Enumerable.Range(1, 11).Select(i => Path.Combine(folder, $"file{i}.bin")).ToList();
            foreach (var file in files)
            {
                await File.WriteAllBytesAsync(file, new byte[10]);
            }

            using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5), seedSampleData: false);
            var panel = await PanelAsync(api);
            var pastes = Directory.CreateDirectory(Path.Combine(folder, "pastes")).FullName;
            panel.PasteRoot = pastes; // this panel's own paste folders, never another program's in the temp folder
            await panel.AttachFilesAsync([files[0], folder, files[1]]); // a folder is skipped
            Assert.Equal(["file1.bin", "file2.bin"], panel.Attachments.Select(a => a.Name));

            await panel.AttachPastedImageAsync(Jpeg(), CancellationToken.None);
            await panel.AttachPastedImageAsync(Jpeg(), CancellationToken.None);
            Assert.Equal(["Pasted image 1.png", "Pasted image 2.png"], panel.Attachments.Skip(2).Select(a => a.Name));
            Assert.Empty(Directory.GetFileSystemEntries(pastes)); // the local copy and its folder are gone

            await panel.AttachFilesAsync(files.Skip(2));
            Assert.Equal(AssistantTexts.MaxAttachments, panel.Attachments.Count);
            Assert.Equal((AssistantTexts.TooManyFiles, true), (panel.Message, panel.MessageIsError));
            await panel.AttachPastedImageAsync(Jpeg(), CancellationToken.None);
            Assert.Equal(AssistantTexts.MaxAttachments, panel.Attachments.Count);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task EscapeStopsTheAnswerAndUpArrowInTheEmptyBoxEditsTheLastMessage()
    {
        var done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            api.AssistantScript = (_, ct) => Gated(gate, [Text("Let me look")], [Done()], ct);
            var panel = await PanelAsync(api);
            var window = PanelWindow(panel);
            window.Show();
            Pump();
            var view = window.GetVisualDescendants().OfType<AssistantPanelView>().Single();
            var box = view.FindControl<TextBox>("MessageBox")!;
            box.Focus();
            panel.Draft = "Tell me everything";
            var sending = panel.SendCommand.ExecuteAsync(null);
            await Oadm.Sdk.Testing.Eventually.TrueAsync(() => panel.Items.OfType<AssistantAnswerItem>().Any(), "the answer began");
            Pump();

            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            await sending.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal([panel.ConversationId!], api.AssistantStops);

            box.Focus();
            Pump();
            window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
            Pump();
            Assert.Equal("Tell me everything", panel.Draft);
            window.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    [Fact]
    public async Task TheComposerThinkingAnswerScrollStreamingDropAndAttachmentsLookLikeTheChatApps()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var folder = Path.Combine(Path.GetTempPath(), $"oadm-shots-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var done = await HeadlessSession.Shared.Dispatch(async () =>
            {
                var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5), seedSampleData: false);
                using var f = new DevicesFixture(api);
                var devices = AssistantTests.Devices(5);
                f.SeedDevices(devices);
                var panel = await PanelAsync(api, f);
                var window = PanelWindow(panel);
                window.Show();
                Pump();
                var view = window.GetVisualDescendants().OfType<AssistantPanelView>().Single();
                var send = view.FindControl<Button>("Send")!;
                Assert.Contains("send", send.Classes);
                Assert.False(send.IsEffectivelyEnabled); // nothing written: dimmed
                Assert.Equal(AssistantTexts.EnterMessage, ToolTip.GetTip(send));
                Assert.Equal(AssistantTexts.AttachFile, ToolTip.GetTip(view.FindControl<Button>("AttachFile")!));
                Save(window, outDir, "assistant-ux-empty.png");

                // Thinking until the first event.
                f.Select(devices[0].Id, devices[1].Id, devices[2].Id);
                var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                api.AssistantScript = (_, ct) => Gated(gate, [], [Text("**3 devices** are selected. Two of them run AXIS OS 11.11, one runs 12.6.97.\n\n- 10.0.0.1\n- 10.0.0.2\n- 10.0.0.3"), Done()], ct);
                panel.Draft = "What runs on the selected cameras?";
                var sending = panel.SendCommand.ExecuteAsync(null);
                Pump();
                Assert.Contains(AssistantTexts.Thinking, VisibleTexts(window));
                Assert.True(view.FindControl<Button>("Stop")!.IsEffectivelyVisible);
                Save(window, outDir, "assistant-ux-thinking.png");
                gate.SetResult();
                await sending;
                Pump();
                Assert.DoesNotContain(AssistantTexts.Thinking, VisibleTexts(window));
                Assert.Contains(view.GetVisualDescendants().OfType<Button>(), b => b.Name == "RetryAnswer" && b.IsEffectivelyVisible);
                Assert.Contains("3 devices selected", VisibleTexts(window));
                Save(window, outDir, "assistant-ux-answer.png");

                // Streaming: Stop in place of the send arrow (a new chat: its first piece shows at once).
                panel.NewChatCommand.Execute(null);
                var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                api.AssistantScript = (_, ct) => Gated(hold, [Text("Checking the firmware of the three cameras. ")], [Done()], ct);
                panel.Draft = "And are they up to date?";
                sending = panel.SendCommand.ExecuteAsync(null);
                await Oadm.Sdk.Testing.Eventually.TrueAsync(() => panel.Items.OfType<AssistantAnswerItem>().Any(a => a.Text.Length > 0), "the answer began");
                Pump();
                Assert.True(view.FindControl<Button>("Stop")!.IsEffectivelyVisible);
                Assert.False(send.IsEffectivelyVisible);
                Assert.Equal(AssistantTexts.Stop, ToolTip.GetTip(view.FindControl<Button>("Stop")!));
                Save(window, outDir, "assistant-ux-streaming.png");
                hold.SetResult();
                await sending;

                // Scrolled up: Go to the latest; new content leaves the place alone.
                api.AssistantScript = (_, ct) => Script([Text(string.Join("\n\n", Enumerable.Range(1, 40).Select(i => $"Paragraph {i} of a long answer about the cameras and their firmware."))), Done()], ct);
                panel.Draft = "Tell me more";
                await panel.SendCommand.ExecuteAsync(null);
                Pump();
                var scroller = view.FindControl<ListBox>("Conversation")!.GetVisualDescendants().OfType<ScrollViewer>().First();
                Assert.True(scroller.Extent.Height > scroller.Viewport.Height + 200, "the answer is longer than the panel");
                Assert.True(scroller.Offset.Y >= scroller.Extent.Height - scroller.Viewport.Height - AssistantPanelView.FollowDistance);
                Assert.True(panel.IsFollowing);
                scroller.Offset = new Avalonia.Vector(0, 0);
                Pump();
                Assert.False(panel.IsFollowing);
                Assert.True(view.FindControl<Button>("JumpToLatest")!.IsEffectivelyVisible);
                Save(window, outDir, "assistant-ux-scrolled-up.png");
                panel.JumpToLatestCommand.Execute(null);
                Pump();
                Assert.True(scroller.Offset.Y >= scroller.Extent.Height - scroller.Viewport.Height - AssistantPanelView.FollowDistance);
                Assert.False(view.FindControl<Button>("JumpToLatest")!.IsEffectivelyVisible);

                // Dragging files over the panel.
                panel.IsDragOver = true;
                Pump();
                Assert.Contains(AssistantTexts.DropFiles, VisibleTexts(window));
                Save(window, outDir, "assistant-ux-drop.png");
                panel.IsDragOver = false;

                // Chips inside the box.
                var first = Path.Combine(folder, "AXIS_OS_12.6.97.bin");
                var second = Path.Combine(folder, "lobby-config.txt");
                await File.WriteAllBytesAsync(first, new byte[48_000]);
                await File.WriteAllBytesAsync(second, new byte[900]);
                await panel.AttachFilesAsync([first, second]);
                await panel.AttachPastedImageAsync(Jpeg(), CancellationToken.None);
                panel.Draft = "Upgrade the selected cameras with this file";
                Pump();
                var composer = view.FindControl<Border>("Composer")!;
                Assert.All(view.FindControl<ItemsControl>("AttachmentList")!.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("chatChip")),
                    chip => Assert.Contains(composer, chip.GetVisualAncestors()));
                Assert.True(scroller.Offset.Y >= scroller.Extent.Height - scroller.Viewport.Height - 1, "the grown box keeps the end in view");
                Save(window, outDir, "assistant-ux-attachments.png");
                window.Close();
                return true;
            }, CancellationToken.None);
            Assert.True(done);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static AssistantEvent Tool(string text, bool failed = false) => new() { Tool = new AssistantToolLine { Text = text, Failed = failed } };

    private static AssistantEvent Sources(params string[] chips) => new() { Sources = new AssistantSources { Chips = { chips } } };

    private static AssistantEvent Next(params string[] questions) => new() { FollowUps = new AssistantFollowUps { Questions = { questions } } };

    [Fact]
    public async Task OneLiveStatusLineFollowsTheToolsAndTheStepsFoldIntoOneRowAtTheEnd()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        api.AssistantScript = (_, ct) => Gated(gate, [Tool("Looking at devices")],
            [Tool("Checking compliance"), Text("All 7 pass."), Sources("7 devices", "Compliance"), Next("Which fail HTTPS?"), Done()], ct);
        var panel = await PanelAsync(api);
        panel.Draft = "Do my devices pass?";
        var sending = panel.SendCommand.ExecuteAsync(null);
        await Oadm.Sdk.Testing.Eventually.TrueAsync(() => panel.Items.OfType<AssistantStatusItem>().Any(s => s.Text == "Looking at devices"), "the first tool runs");

        // While it runs: one line, no list of tool lines.
        Assert.Equal([typeof(AssistantUserItem), typeof(AssistantStatusItem)], panel.Items.Select(i => i.GetType()));
        Assert.True(panel.IsThinking);

        gate.SetResult();
        await sending.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal([typeof(AssistantUserItem), typeof(AssistantStepsItem), typeof(AssistantAnswerItem), typeof(AssistantSourcesItem), typeof(AssistantFollowUpsItem)],
            panel.Items.Select(i => i.GetType()));
        var steps = (AssistantStepsItem)panel.Items[1];
        Assert.Equal(("Steps (2)", false, false), (steps.Header, steps.IsExpanded, steps.HasFailed)); // folded: the answer ended well
        Assert.Equal(["7 devices", "Compliance"], ((AssistantSourcesItem)panel.Items[3]).Chips);
        Assert.True(((AssistantAnswerItem)panel.Items[2]).ShowsRetry); // Try again stays under the answer
        Assert.Contains("_Checking compliance_", panel.ChatMarkdown(), StringComparison.Ordinal);
        Assert.Contains("Sources: 7 devices, Compliance", panel.ChatMarkdown(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheAnswersTextShowsAboveTheCardsThatCameBeforeItAndSourcesAndNextStayLast()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        var device = AssistantTests.Devices(1)[0];
        var table = new AssistantTable { Total = 1, Rows = { new AssistantDeviceRow { DeviceId = device.Id, Name = device.Address, Address = device.Address } } };
        var plan = new AssistantPreviewCard { Handle = "h1", Title = "Restart", DeviceCount = 1 };
        api.AssistantScript = (_, ct) => Script(
        [
            Text("Let me look."), // text before any card stays where it is
            Tool("Looking at devices"),
            new AssistantEvent { Table = table },
            new AssistantEvent { Picture = new AssistantPicture { DeviceId = device.Id, DeviceName = device.Address, Jpeg = Google.Protobuf.ByteString.CopyFrom(Jpeg()) } },
            Text("1 camera is offline."), // above the table and the picture
            new AssistantEvent { Preview = plan },
            Text("Restarting stops recording for about a minute."), // above the plan only
            Sources("1 device"),
            Next("Why is it offline?"),
            Done(),
        ], ct);
        var panel = await PanelAsync(api);
        panel.Draft = "Is anything offline?";
        await panel.SendCommand.ExecuteAsync(null);

        Assert.Equal(
            [typeof(AssistantUserItem), typeof(AssistantStepsItem), typeof(AssistantAnswerItem), typeof(AssistantAnswerItem), typeof(AssistantTableItem),
                typeof(AssistantPictureItem), typeof(AssistantAnswerItem), typeof(AssistantPreviewItem), typeof(AssistantSourcesItem), typeof(AssistantFollowUpsItem)],
            panel.Items.Select(i => i.GetType()));
        Assert.Equal(["Let me look.", "1 camera is offline.", "Restarting stops recording for about a minute."],
            panel.Items.OfType<AssistantAnswerItem>().Select(a => a.Text));
        Assert.True(panel.Items.OfType<AssistantTableItem>().Single().ShowsLabel); // still the answer's first result card
        Assert.True(((AssistantAnswerItem)panel.Items[6]).ShowsRetry); // Try again under the answer's last text
        var markdown = panel.ChatMarkdown(); // Copy chat follows the order shown
        Assert.True(markdown.IndexOf("1 camera is offline.", StringComparison.Ordinal) < markdown.IndexOf("Sources: 1 device", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TextAfterANoticeStaysBelowIt()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        var device = AssistantTests.Devices(1)[0];
        var table = new AssistantTable { Total = 1, Rows = { new AssistantDeviceRow { DeviceId = device.Id, Name = device.Address, Address = device.Address } } };
        api.AssistantScript = (_, ct) => Script(
        [
            new AssistantEvent { Table = table },
            new AssistantEvent { Notice = "Your messages go to Anthropic." },
            Text("Done reading."),
            Done(),
        ], ct);
        var panel = await PanelAsync(api);
        panel.Draft = "Hi";
        await panel.SendCommand.ExecuteAsync(null);

        Assert.Equal([typeof(AssistantUserItem), typeof(AssistantTableItem), typeof(AssistantNoticeItem), typeof(AssistantAnswerItem)],
            panel.Items.Select(i => i.GetType()));
    }

    [Fact]
    public async Task AFailedStepKeepsItsMarkAndTheStepsOpenOnlyWhenTheAnswerFailed()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        api.AssistantScript = (_, ct) => Script([Tool("Looking at devices"), Tool("Reading the logs: Not allowed", failed: true), Text("I cannot read the logs."), Done()], ct);
        var panel = await PanelAsync(api);
        panel.Draft = "What failed?";
        await panel.SendCommand.ExecuteAsync(null);
        var steps = panel.Items.OfType<AssistantStepsItem>().Single();
        Assert.True(steps.HasFailed);
        Assert.False(steps.IsExpanded);

        api.AssistantScript = (_, ct) => Script([Tool("Looking at devices"), Done("The provider did not answer. Try again.")], ct);
        panel.Draft = "Again";
        await panel.SendCommand.ExecuteAsync(null);
        Assert.True(panel.Items.OfType<AssistantStepsItem>().Last().IsExpanded);

        // An answer without tools has no Steps row.
        api.AssistantScript = (_, ct) => Script([Text("Hello."), Done()], ct);
        panel.NewChatCommand.Execute(null);
        panel.Draft = "Hi";
        await panel.SendCommand.ExecuteAsync(null);
        Assert.DoesNotContain(panel.Items, i => i is AssistantStepsItem);
    }

    [Fact]
    public async Task ANextQuestionIsSentAsTheUsersMessageAndOnlyTheNewestAnswerKeepsItsQuestions()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        var answers = 0;
        api.AssistantScript = (_, ct) => Script([Text($"Answer {++answers}."), Next("Which ones are offline?", "Upgrade them"), Done()], ct);
        var panel = await PanelAsync(api);
        panel.Draft = "How are my cameras?";
        await panel.SendCommand.ExecuteAsync(null);
        var next = panel.Items.OfType<AssistantFollowUpsItem>().Single();
        Assert.Equal(["Which ones are offline?", "Upgrade them"], next.Questions);

        await panel.SendSuggestionCommand.ExecuteAsync(next.Questions[0]);

        Assert.Equal("Which ones are offline?", api.AssistantMessages[^1].Text);
        Assert.False(api.AssistantMessages[^1].ReplaceLast);
        var single = Assert.Single(panel.Items.OfType<AssistantFollowUpsItem>()); // the older questions went
        Assert.NotSame(next, single);
        Assert.Equal("Which ones are offline?", panel.Items.OfType<AssistantUserItem>().Last().Text);
    }

    [Fact]
    public async Task TheAnswerStepsResultCardSourcesNextQuestionsAndComposerLookCleanInDarkAndLight()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        // The light ones: OADM_TEST_APPEARANCE=Light (the appearance is chosen once per test process).
        var appearance = string.Equals(Environment.GetEnvironmentVariable("OADM_TEST_APPEARANCE"), "Light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark";
        var folder = Path.Combine(Path.GetTempPath(), $"oadm-shots-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var done = await HeadlessSession.Shared.Dispatch(async () =>
            {
                var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5), seedSampleData: false);
                using var f = new DevicesFixture(api);
                var devices = AssistantTests.Devices(12);
                f.SeedDevices(devices);
                var panel = await PanelAsync(api, f);
                var window = PanelWindow(panel);
                window.Show();
                Pump();
                var view = window.GetVisualDescendants().OfType<AssistantPanelView>().Single();

                // The live status line while a tool runs.
                var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var table = new AssistantTable { Total = 12 };
                table.Rows.AddRange(devices.Take(10).Select(d => new AssistantDeviceRow { DeviceId = d.Id, Name = d.Address, Address = d.Address, Model = d.Model, Status = "OK" }));
                table.AllDeviceIds.AddRange(devices.Select(d => d.Id));
                api.AssistantScript = (_, ct) => Gated(gate, [Tool("Looking at devices")],
                [
                    new AssistantEvent { Table = table },
                    Tool("Checking compliance"), Tool("Reading the settings of 12 devices"), Tool("Searching the docs"),
                    Text("**12 cameras** are online. 2 of them still run AXIS OS 11.11."),
                    Sources("12 devices", "Compliance", "Settings", "Docs"),
                    Next("Which cameras run AXIS OS 11.11?", "Upgrade the two old cameras", "Show a picture of the lobby camera"),
                    Done(),
                ], ct);
                panel.Draft = "How are my cameras?";
                var sending = panel.SendCommand.ExecuteAsync(null);
                await Oadm.Sdk.Testing.Eventually.TrueAsync(() => panel.Items.OfType<AssistantStatusItem>().Any(s => s.Text == "Looking at devices"), "the tool runs");
                Pump();
                Assert.Contains("Looking at devices", VisibleTexts(window));
                Assert.Single(view.GetVisualDescendants().OfType<StackPanel>(), s => s.Name == "Thinking" && s.IsEffectivelyVisible);
                Save(window, outDir, $"assistant-design-status-{appearance}.png");

                // The end: steps folded, the result card with its actions, the sources and the next questions.
                gate.SetResult();
                await sending;
                Pump();
                var texts = VisibleTexts(window);
                Assert.DoesNotContain("Checking compliance", texts);
                Assert.Contains(AssistantTexts.SourcesLabel, texts);
                Assert.Contains(AssistantTexts.NextLabel, texts);
                Assert.Contains("12 devices", texts);
                Assert.Contains("+4 more", texts); // the card shows 8 of the 12
                Assert.Contains(AssistantTexts.Footnote, texts);
                var card = view.GetVisualDescendants().OfType<ResultCard>().Single(c => c.Name == "DeviceTable");
                Assert.Contains(card.GetVisualDescendants().OfType<Button>(), b => ToolTip.GetTip(b) as string == AssistantTexts.SaveCsv && b.IsEffectivelyVisible);
                Assert.Contains(card.GetVisualDescendants().OfType<Oadm.Sdk.Client.Controls.ToolbarButton>(), b => b.Text == AssistantTexts.SelectAllInGrid);
                Assert.Equal(3, view.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("followUp") && b.IsEffectivelyVisible));
                Save(window, outDir, $"assistant-design-result-{appearance}.png");

                var steps = panel.Items.OfType<AssistantStepsItem>().Single();
                var scroller = view.FindControl<ListBox>("Conversation")!.GetVisualDescendants().OfType<ScrollViewer>().First();
                scroller.Offset = new Avalonia.Vector(0, 0);
                Pump();
                Assert.Contains("Steps (4)", VisibleTexts(window));
                Save(window, outDir, $"assistant-design-steps-folded-{appearance}.png");
                view.GetVisualDescendants().OfType<ToggleButton>().Single(b => b.Name == "StepsHeader").IsChecked = true;
                Pump();
                Assert.True(steps.IsExpanded);
                Assert.Contains("Checking compliance", VisibleTexts(window));
                Save(window, outDir, $"assistant-design-steps-expanded-{appearance}.png");


                // An answer set for reading: headings, a bold lead-in, lists, code.
                panel.NewChatCommand.Execute(null);
                api.AssistantScript = (_, ct) => Script(
                [
                    Text("# Firmware report\n\n**In short:** 10 of 12 cameras run the newest AXIS OS. Two need an upgrade before the end of the month.\n\n"
                        + "## What to do\n\n1. Upgrade `10.0.0.2` and `10.0.0.3` to 12.6.97.\n2. Check that both come back online.\n3. Run the compliance check again.\n\n"
                        + "### Why it matters\n\n- AXIS OS 11.11 has known vulnerabilities.\n- The LTS track ends in March, after that no fixes come.\n- An upgrade takes about 5 minutes per camera and needs a restart.\n\n"
                        + "```json\n{\"firmware\": \"12.6.97\"}\n```\n"),
                    Done(),
                ], ct);
                panel.Draft = "Write a firmware report";
                await panel.SendCommand.ExecuteAsync(null);
                Pump();
                scroller.Offset = new Avalonia.Vector(0, 0);
                Pump();
                var markdown = view.GetVisualDescendants().OfType<MarkdownText>().Single();
                Assert.Contains(markdown.GetVisualDescendants().OfType<SelectableTextBlock>(), t => t.Classes.Contains("h1"));
                Assert.Contains(markdown.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "01.");
                Assert.Equal(3, markdown.GetVisualDescendants().OfType<Border>().Count(b => b.Classes.Contains("mdBullet")));
                Save(window, outDir, $"assistant-design-answer-{appearance}.png");

                // The composer: the chosen devices and the files inside the box, the model as a chip, the line below the box;
                // the files of a sent message as small cards.
                var file = Path.Combine(folder, "AXIS_OS_12.6.97.bin");
                await File.WriteAllBytesAsync(file, new byte[48_000]);
                await panel.AttachFilesAsync([file]);
                api.AssistantScript = (_, ct) => Script([Text("I will prepare the upgrade with this file."), Done()], ct);
                panel.Draft = "Use this file";
                await panel.SendCommand.ExecuteAsync(null);
                Pump();
                Assert.Contains(view.GetVisualDescendants().OfType<FileChip>(), c => c.FileName == "AXIS_OS_12.6.97.bin" && c.RemoveCommand is null && c.IsEffectivelyVisible);
                await panel.AttachFilesAsync([file]);
                f.Select(devices[0].Id, devices[1].Id);
                panel.Draft = "Upgrade the selected cameras with this file";
                Pump();
                var composer = view.FindControl<Border>("Composer")!;
                Assert.Contains(composer.GetVisualDescendants().OfType<FileChip>(), c => c.RemoveCommand is not null);
                Assert.Contains(composer.GetVisualDescendants().OfType<Oadm.Sdk.Client.Controls.OadmIcon>(), i => i.Classes.Contains("modelIcon") && i.IsEffectivelyVisible);
                Save(window, outDir, $"assistant-design-composer-{appearance}.png");
                window.Close();
                return true;
            }, CancellationToken.None);
            Assert.True(done);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
