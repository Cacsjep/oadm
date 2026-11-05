using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Grpc.Core;

using NSubstitute;

using Oadm.Client.Api;
using Oadm.Client.Devices;
using Oadm.Client.Dialogs;
using Oadm.Client.EventRules;
using Oadm.Client.Infrastructure;
using Oadm.Contracts.V1;
using Oadm.Sdk.Actions;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Testing;

namespace Oadm.Client.Tests;

/// <summary>
/// Persistent recipients in the client (docs/spec/recipient-sessions.md): the certificate selects of the recipient dialog, its
/// Test, and the live status in the Recipients list. Set OADM_SCREENSHOT_DIR for recipient-sessions-*.png.
/// </summary>
public sealed class RecipientSessionTests : IDisposable
{
    private readonly UiThread _ui = new();

    public void Dispose() => _ui.Dispose();

    private static async Task<(FakeOadmApi Api, DeviceStore Store)> ApiAsync()
    {
        var api = new FakeOadmApi(TimeSpan.FromMilliseconds(1));
        var store = new DeviceStore();
        store.Reset(await api.ListDevicesAsync(CancellationToken.None));
        store.Tags.Reset(await api.ListTagsAsync(CancellationToken.None));
        return (api, store);
    }

    private static async Task<RecipientEntry> ByNameAsync(FakeOadmApi api, string name) =>
        (await api.ListRecipientsAsync(CancellationToken.None)).Single(r => r.Name == name);

    private static async Task<RecipientEditorViewModel> EditorAsync(FakeOadmApi api, RecipientEntry? existing = null, string? typeId = null)
    {
        var catalog = await api.GetEventCatalogAsync(CancellationToken.None);
        var recipients = await api.ListRecipientsAsync(CancellationToken.None);
        var editor = new RecipientEditorViewModel(api, catalog.RecipientTypes, existing, [.. recipients.Where(r => r.Id != existing?.Id).Select(r => r.Name)]);
        if (typeId is not null)
        {
            editor.SelectedType = editor.Types.Single(t => t.Entry.Id == typeId);
        }

        return editor;
    }

    private static ActionFieldViewModel Field(RecipientEditorViewModel editor, string key) => editor.Fields.Single(f => f.Key == key);

    private static async Task<EventRulesViewModel> PageAsync(FakeOadmApi api, DeviceStore store, IUiDispatcher ui)
    {
        var page = new EventRulesViewModel(api, Substitute.For<IDialogService>(), store, ui);
        page.Activate();
        await Eventually.TrueAsync(() => page.Recipients.Count(r => r.HasStatus) == 3, "the statuses arrived");
        return page;
    }

    [Fact]
    public Task The_certificate_selects_fill_from_PKI_with_None_when_optional() => _ui.RunAsync(async () =>
    {
        var (api, _) = await ApiAsync();
        var editor = await EditorAsync(api, typeId: FakeOadmApi.FakeMqttRecipientType);
        Field(editor, "tls").IsChecked = true;

        var client = Field(editor, "clientCertificate");
        Assert.Equal(["None"], client.Choices.Select(c => c.Text));
        await editor.LoadCertificatesAsync(CancellationToken.None);

        Assert.Equal(["None", "Broker client (OADM Root CA SERVER01), until 2028-06-01"], client.Choices.Select(c => c.Text));
        Assert.Equal(3, Field(editor, "trustedCa").Choices.Count);
        Assert.Equal("None", client.SelectedChoice?.Text);
        Assert.Null(client.Error);

        client.SelectChoice(FakeOadmApi.FakeClientCertificate);
        Assert.Equal(FakeOadmApi.FakeClientCertificate, ActionFieldViewModel.ToSettings(editor.Fields)["clientCertificate"]?.ToString());
    });

    [Fact]
    public Task A_required_certificate_is_checked_like_any_required_select() => _ui.RunAsync(async () =>
    {
        var (api, _) = await ApiAsync();
        var entry = new ActionFieldEntry { Key = "certificate", Label = "Client certificate", Kind = ActionFieldKind.ActionFieldClientCertificate, Required = true };
        var field = new ActionFieldViewModel(entry);
        Assert.Equal(ActionFieldType.ClientCertificate, field.Field.Type);
        Assert.Empty(field.Choices); // no None
        field.SetCertificates((await api.ListRecipientCertificatesAsync(CancellationToken.None)).Client);

        ActionFieldViewModel.Refresh([field], statefulRule: true);
        Assert.Equal("Choose client certificate.", field.Problem);

        field.SelectChoice(FakeOadmApi.FakeClientCertificate);
        ActionFieldViewModel.Refresh([field], statefulRule: true);
        Assert.Null(field.Problem);
    });

    [Fact]
    public Task The_dialog_shows_that_it_reads_and_waits_for_the_list() => _ui.RunAsync(async () =>
    {
        var (api, _) = await ApiAsync();
        api.HoldRecipientCertificates = new TaskCompletionSource();
        var editor = await EditorAsync(api, typeId: FakeOadmApi.FakeMqttRecipientType);
        editor.Name = "New broker";
        Field(editor, "host").Text = "broker.example.com";

        var load = editor.LoadCertificatesAsync(CancellationToken.None);
        Assert.True(editor.ShowCertificatesLoading);
        Assert.False(editor.SaveCommand.CanExecute(null));
        Assert.False(editor.TestCommand.CanExecute(null));

        editor.SelectedType = editor.Types.Single(t => t.Entry.Id == FakeOadmApi.FakeHttpRecipientType);
        Assert.False(editor.ShowCertificatesLoading); // this type waits for nothing
        editor.SelectedType = editor.Types.Single(t => t.Entry.Id == FakeOadmApi.FakeMqttRecipientType);

        api.HoldRecipientCertificates.SetResult();
        await load;
        Assert.False(editor.ShowCertificatesLoading);
        Assert.True(editor.TestCommand.CanExecute(null));
    });

    [Fact]
    public Task A_type_chosen_after_the_read_has_its_certificates() => _ui.RunAsync(async () =>
    {
        var (api, _) = await ApiAsync();
        var editor = await EditorAsync(api);
        await editor.LoadCertificatesAsync(CancellationToken.None);

        editor.SelectedType = editor.Types.Single(t => t.Entry.Id == FakeOadmApi.FakeOpcUaRecipientType);
        Assert.Equal(3, Field(editor, "trustedCa").Choices.Count);
    });

    [Fact]
    public Task Nothing_is_read_when_no_type_has_a_certificate() => _ui.RunAsync(async () =>
    {
        var (real, _) = await ApiAsync();
        var api = Substitute.For<IOadmApi>();
        var catalog = await real.GetEventCatalogAsync(CancellationToken.None);
        var editor = new RecipientEditorViewModel(api, [catalog.RecipientTypes.Single(t => t.Id == FakeOadmApi.FakeHttpRecipientType)], null, []);

        await editor.LoadCertificatesAsync(CancellationToken.None);

        await api.DidNotReceive().ListRecipientCertificatesAsync(Arg.Any<CancellationToken>());
    });

    [Fact]
    public Task An_empty_list_says_where_certificates_come_from() => _ui.RunAsync(async () =>
    {
        var (api, _) = await ApiAsync();
        api.FakeRecipientCertificates = new RecipientCertificateList();
        var editor = await EditorAsync(api, typeId: FakeOadmApi.FakeMqttRecipientType);
        var client = Field(editor, "clientCertificate");
        Assert.Equal("Optional", client.Hint); // before the read nothing is known

        await editor.LoadCertificatesAsync(CancellationToken.None);

        Assert.Equal("Import or create one on the PKI page.", client.Hint);
        Assert.Equal(ActionFieldViewModel.NoCertificatesHint, Field(editor, "trustedCa").Hint);
        Assert.Null(client.Error);
        Assert.Equal("Optional. One per recipient on a broker.", Field(editor, "clientId").Hint);
    });

    [Fact]
    public Task A_certificate_no_longer_in_the_list_shows_why_below_the_field_and_blocks_Save() => _ui.RunAsync(async () =>
    {
        var (api, _) = await ApiAsync();
        api.FakeRecipientCertificates = new RecipientCertificateList { Trusted = { new RecipientCertificateChoice { Id = FakeOadmApi.FakeTrustedCa, Text = "Root" } } };
        var editor = await EditorAsync(api, await ByNameAsync(api, "Plant broker"));

        await editor.LoadCertificatesAsync(CancellationToken.None);

        var client = Field(editor, "clientCertificate");
        Assert.Equal(ActionFieldViewModel.CertificateUnusableText, client.Error);
        Assert.Null(client.SelectedChoice);
        Assert.Equal(FakeOadmApi.FakeTrustedCa, Field(editor, "trustedCa").SelectedChoice?.Value);
        Assert.False(editor.SaveCommand.CanExecute(null));
        Assert.Contains(ActionFieldViewModel.CertificateUnusableText, editor.FormError, StringComparison.Ordinal);

        client.SelectedChoice = client.Choices.Single(c => c.Text == "None"); // the user chooses: the error goes
        Assert.Null(client.Error);
        Assert.True(editor.SaveCommand.CanExecute(null));
    });

    [Fact]
    public Task With_PKI_stopped_a_chosen_certificate_gets_the_same_neutral_text() => _ui.RunAsync(async () =>
    {
        var (api, _) = await ApiAsync();
        api.FakeRecipientCertificates = new RecipientCertificateList(); // what the server answers while PKI is stopped
        var editor = await EditorAsync(api, await ByNameAsync(api, "Plant broker"));

        await editor.LoadCertificatesAsync(CancellationToken.None);

        Assert.Equal("This certificate cannot be used now. Choose another one.", Field(editor, "clientCertificate").Error);
        Assert.DoesNotContain("no longer exists", Field(editor, "clientCertificate").Error, StringComparison.Ordinal);
    });

    [Fact]
    public Task A_certificate_of_a_hidden_field_stays_when_the_recipient_is_saved() => _ui.RunAsync(async () =>
    {
        var (api, _) = await ApiAsync();
        api.FakeRecipientCertificates = new RecipientCertificateList();
        var editor = await EditorAsync(api, await ByNameAsync(api, "Plant broker"));
        await editor.LoadCertificatesAsync(CancellationToken.None);

        Field(editor, "tls").IsChecked = false; // hides the selects: nothing to choose, nothing to fix

        Assert.True(editor.SaveCommand.CanExecute(null));
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Contains(FakeOadmApi.FakeClientCertificate, (await ByNameAsync(api, "Plant broker")).ValuesJson, StringComparison.Ordinal);
    });

    [Fact]
    public Task A_list_that_cannot_be_read_stands_below_the_field() => _ui.RunAsync(async () =>
    {
        var api = Substitute.For<IOadmApi>();
        var (real, _) = await ApiAsync();
        api.ListRecipientCertificatesAsync(Arg.Any<CancellationToken>()).Returns<RecipientCertificateList>(_ => throw new RpcException(new Status(StatusCode.Unavailable, "The server is gone.")));
        var catalog = await real.GetEventCatalogAsync(CancellationToken.None);
        var editor = new RecipientEditorViewModel(api, catalog.RecipientTypes, null, []);
        editor.SelectedType = editor.Types.Single(t => t.Entry.Id == FakeOadmApi.FakeMqttRecipientType);
        Field(editor, "tls").IsChecked = true;

        await editor.LoadCertificatesAsync(CancellationToken.None);

        Assert.Equal("The list could not be read: The server is gone.", Field(editor, "clientCertificate").Error);
    });

    [Fact]
    public void The_persistent_flag_reaches_the_type_option()
    {
        Assert.True(new RecipientTypeOption(new RecipientTypeEntry { Persistent = true }).Persistent);
        Assert.False(new RecipientTypeOption(new RecipientTypeEntry()).Persistent);
    }

    [Fact]
    public Task Test_of_a_persistent_type_says_Connecting_then_the_result() => _ui.RunAsync(async () =>
    {
        var (api, _) = await ApiAsync();
        var editor = await EditorAsync(api, await ByNameAsync(api, "Line 2 PLC"));
        Assert.Equal("Connects once now", editor.TestTip);
        var gate = new TaskCompletionSource<TestActionReply>();
        var slow = Substitute.For<IOadmApi>();
        slow.TestRecipientAsync(default!, default).ReturnsForAnyArgs(gate.Task);
        var catalog = await api.GetEventCatalogAsync(CancellationToken.None);
        var held = new RecipientEditorViewModel(slow, catalog.RecipientTypes, await ByNameAsync(api, "Line 2 PLC"), []);

        var test = held.TestCommand.ExecuteAsync(null);
        Assert.Equal("Connecting", held.TestResult);
        gate.SetResult(new TestActionReply { Ok = true });
        await test;
        Assert.True(held.TestOk);
        Assert.Equal("Connected", held.TestResult);

        await editor.TestCommand.ExecuteAsync(null);
        Assert.Equal(RecipientSessionTexts.Connected, editor.TestResult); // the text of the server
    });

    [Fact]
    public Task Test_of_a_one_shot_type_still_says_Sending() => _ui.RunAsync(async () =>
    {
        var (api, _) = await ApiAsync();
        var gate = new TaskCompletionSource<TestActionReply>();
        var slow = Substitute.For<IOadmApi>();
        slow.TestRecipientAsync(default!, default).ReturnsForAnyArgs(gate.Task);
        var catalog = await api.GetEventCatalogAsync(CancellationToken.None);
        var held = new RecipientEditorViewModel(slow, catalog.RecipientTypes, await ByNameAsync(api, "Alarm panel"), []);
        Assert.Equal("Sends it once now, with sample event values", held.TestTip);

        var test = held.TestCommand.ExecuteAsync(null);
        Assert.Equal("Sending", held.TestResult);
        gate.SetResult(new TestActionReply { Ok = true });
        await test;
        Assert.Equal("Sent", held.TestResult);
    });

    /// <summary>A dialog whose Test is running and waits for the reply the test sets.</summary>
    private static async Task<(RecipientEditorViewModel Editor, TaskCompletionSource<TestActionReply> Reply, Task Test)> HeldTestAsync(FakeOadmApi api, string recipient)
    {
        var reply = new TaskCompletionSource<TestActionReply>();
        var slow = Substitute.For<IOadmApi>();
        slow.TestRecipientAsync(default!, default).ReturnsForAnyArgs(reply.Task);
        var catalog = await api.GetEventCatalogAsync(CancellationToken.None);
        var editor = new RecipientEditorViewModel(slow, catalog.RecipientTypes, await ByNameAsync(api, recipient), []);
        var test = editor.TestCommand.ExecuteAsync(null);
        Assert.True(editor.IsTesting);
        return (editor, reply, test);
    }

    [Fact]
    public Task An_edit_during_a_Test_drops_its_reply() => _ui.RunAsync(async () =>
    {
        var (api, _) = await ApiAsync();
        var (editor, reply, test) = await HeldTestAsync(api, "Line 2 PLC");

        Field(editor, "host").Text = "10.0.0.71";
        Assert.Null(editor.TestResult);

        reply.SetResult(new TestActionReply { Ok = true, Message = "Connected" });
        await test;

        Assert.Null(editor.TestResult);
        Assert.False(editor.HasTestResult);
        Assert.False(editor.IsTesting);
        Assert.True(editor.TestCommand.CanExecute(null));
    });

    [Fact]
    public Task A_new_name_during_a_Test_drops_its_reply() => _ui.RunAsync(async () =>
    {
        var (api, _) = await ApiAsync();
        var (editor, reply, test) = await HeldTestAsync(api, "Line 2 PLC");

        editor.Name = "Line 3 PLC";
        reply.SetResult(new TestActionReply { Ok = false, Message = "No answer" });
        await test;

        Assert.Null(editor.TestResult);
    });

    [Fact]
    public Task A_type_change_during_a_Test_drops_its_failure() => _ui.RunAsync(async () =>
    {
        var (api, _) = await ApiAsync();
        var (editor, reply, test) = await HeldTestAsync(api, "Line 2 PLC");

        editor.SelectedType = editor.Types.Single(t => t.Entry.Id == FakeOadmApi.FakeHttpRecipientType);
        reply.SetException(new RpcException(new Status(StatusCode.Unavailable, "Gone")));
        await test;

        Assert.Null(editor.TestResult);
        Assert.False(editor.IsTesting);
    });

    [Fact]
    public Task A_Test_after_a_dropped_one_shows_its_own_result() => _ui.RunAsync(async () =>
    {
        var (api, _) = await ApiAsync();
        var replies = new Queue<TaskCompletionSource<TestActionReply>>([new(), new()]);
        var slow = Substitute.For<IOadmApi>();
        var first = replies.Peek();
        slow.TestRecipientAsync(default!, default).ReturnsForAnyArgs(_ => replies.Dequeue().Task);
        var second = replies.ElementAt(1);
        var catalog = await api.GetEventCatalogAsync(CancellationToken.None);
        var editor = new RecipientEditorViewModel(slow, catalog.RecipientTypes, await ByNameAsync(api, "Line 2 PLC"), []);

        var dropped = editor.TestCommand.ExecuteAsync(null);
        Field(editor, "host").Text = "10.0.0.71";
        first.SetResult(new TestActionReply { Ok = false, Message = "The old form" });
        await dropped;

        var test = editor.TestCommand.ExecuteAsync(null);
        Assert.Equal("Connecting", editor.TestResult);
        second.SetResult(new TestActionReply { Ok = true });
        await test;

        Assert.Equal("Connected", editor.TestResult);
        Assert.True(editor.TestOk);
    });

    [Fact]
    public Task A_refused_Test_shows_below_Password_with_the_trailer() => _ui.RunAsync(async () =>
    {
        const string Refusal = "Enter the password again. The target changed.";
        var (api, _) = await ApiAsync();
        var editor = await EditorAsync(api, await ByNameAsync(api, "Plant broker"));
        await editor.LoadCertificatesAsync(CancellationToken.None);
        Field(editor, "host").Text = "other.plant.local";
        api.NextRecipientRefusal = new RpcException(new Status(StatusCode.InvalidArgument, Refusal), new Metadata { { RecipientErrors.FieldTrailer, "password" } });

        await editor.TestCommand.ExecuteAsync(null);

        Assert.Equal(Refusal, Field(editor, "password").Error);
        Assert.Null(editor.TestResult);
        Assert.False(editor.SaveCommand.CanExecute(null));

        Field(editor, "password").Text = "new secret"; // the refusal goes with the edit
        Assert.Null(Field(editor, "password").Error);
        Assert.True(editor.SaveCommand.CanExecute(null));
    });

    [Fact]
    public Task A_refused_Save_shows_below_Password_with_the_trailer() => _ui.RunAsync(async () =>
    {
        const string Refusal = "Enter the password again. The target changed.";
        var (api, _) = await ApiAsync();
        var editor = await EditorAsync(api, await ByNameAsync(api, "Plant broker"));
        await editor.LoadCertificatesAsync(CancellationToken.None);
        Field(editor, "host").Text = "other.plant.local";
        api.NextRecipientRefusal = new RpcException(new Status(StatusCode.InvalidArgument, Refusal), new Metadata { { RecipientErrors.FieldTrailer, "password" } });

        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Equal(Refusal, Field(editor, "password").Error);
        Assert.Null(editor.ServerError);
        Assert.Null(editor.Saved);
    });

    [Fact]
    public Task A_refused_Test_without_a_field_stays_next_to_the_button() => _ui.RunAsync(async () =>
    {
        var (api, _) = await ApiAsync();
        var editor = await EditorAsync(api, await ByNameAsync(api, "Line 2 PLC"));
        api.NextRecipientRefusal = new RpcException(new Status(StatusCode.PermissionDenied, "Not allowed."));

        await editor.TestCommand.ExecuteAsync(null);

        Assert.Equal("Not allowed.", editor.TestResult);
        Assert.False(editor.TestOk);
    });

    [Fact]
    public Task The_list_shows_a_word_per_state_with_the_reason_in_the_tip() => _ui.RunAsync(async () =>
    {
        var (api, store) = await ApiAsync();
        using var page = await PageAsync(api, store, _ui);

        var broker = page.Recipients.Single(r => r.Name == "Plant broker");
        Assert.Equal("Connected", broker.StatusText);
        Assert.True(broker.IsConnected);
        Assert.Null(broker.StatusTip);

        var plc = page.Recipients.Single(r => r.Name == "Line 2 PLC");
        Assert.Equal("Not connected", plc.StatusText);
        Assert.True(plc.IsNotConnected);
        Assert.Equal("No answer from 10.0.0.70:502.", plc.StatusTip);

        Assert.Equal("Connecting", page.Recipients.Single(r => r.Name == "Packing line").StatusText);
        Assert.False(page.Recipients.Single(r => r.Name == "Spare broker").HasStatus); // Not used
        Assert.False(page.Recipients.Single(r => r.Name == "Alarm panel").HasStatus); // one shot
    });

    [Fact]
    public Task A_one_shot_recipient_ignores_a_status() => _ui.RunAsync(async () =>
    {
        var (api, store) = await ApiAsync();
        using var page = await PageAsync(api, store, _ui);
        var panel = page.Recipients.Single(r => r.Name == "Alarm panel");
        api.SetRecipientStatus(panel.Id, RecipientConnection.RecipientConnected);
        await Eventually.TrueAsync(() => panel.Status is not null, "the status arrived");
        Assert.False(panel.HasStatus);
    });

    [Fact]
    public Task The_chip_follows_the_stream_in_place() => _ui.RunAsync(async () =>
    {
        var (api, store) = await ApiAsync();
        using var page = await PageAsync(api, store, _ui);
        var plc = page.Recipients.Single(r => r.Name == "Line 2 PLC");
        var resets = 0;
        page.Recipients.CollectionChanged += (_, _) => resets++;

        api.SetRecipientStatus(plc.Id, RecipientConnection.RecipientConnecting);
        await Eventually.TrueAsync(() => plc.IsConnecting, "Connecting arrived");
        api.SetRecipientStatus(plc.Id, RecipientConnection.RecipientConnected);
        await Eventually.TrueAsync(() => plc.IsConnected, "Connected arrived");
        api.SetRecipientStatus(plc.Id, RecipientConnection.RecipientNotUsed);
        await Eventually.TrueAsync(() => !plc.HasStatus, "the session closed");

        Assert.Equal(0, resets); // the row keeps its selection and scroll position
    });

    [Fact]
    public Task A_new_read_of_the_list_keeps_the_statuses() => _ui.RunAsync(async () =>
    {
        var (api, store) = await ApiAsync();
        using var page = await PageAsync(api, store, _ui);

        page.RecipientSearchText = "plant";
        Assert.Equal("Connected", Assert.Single(page.Recipients).StatusText);
        page.RecipientSearchText = string.Empty;

        await api.SaveRecipientAsync(new SaveRecipientRequest { Name = "Made elsewhere", TypeId = FakeOadmApi.FakeTcpRecipientType, SettingsJson = """{"host":"10.0.0.90","port":8000}""" }, CancellationToken.None);
        await page.NewRuleCommand.ExecuteAsync(null); // the dialog service answers no: the list is read again
        await Eventually.TrueAsync(() => page.Recipients.Any(r => r.Name == "Made elsewhere"), "the list was read again");
        Assert.Equal(3, page.Recipients.Count(r => r.HasStatus));
    });

    [Fact]
    public Task A_stream_that_ends_clears_the_chips_and_the_page_watches_again() => _ui.RunAsync(async () =>
    {
        var (api, store) = await ApiAsync();
        using var page = await PageAsync(api, store, _ui);

        api.EndRecipientStatusStreams();
        await Eventually.TrueAsync(() => !page.Recipients.Any(r => r.HasStatus), "the chips went with the stream");

        await Eventually.TrueAsync(() => page.Recipients.Count(r => r.HasStatus) == 3, "the page watched again");
    });

    [Fact]
    public Task A_stream_that_fails_is_watched_again() => _ui.RunAsync(async () =>
    {
        var (api, store) = await ApiAsync();
        var calls = 0;
        var watching = Substitute.For<IOadmApi>();
        watching.GetEventCatalogAsync(default).ReturnsForAnyArgs(_ => api.GetEventCatalogAsync(CancellationToken.None));
        watching.ListRecipientsAsync(default).ReturnsForAnyArgs(_ => api.ListRecipientsAsync(CancellationToken.None));
        watching.WatchRulesAsync(default).ReturnsForAnyArgs(AsyncEnumerable.Empty<RuleList>());
        watching.WatchEventLogAsync(default).ReturnsForAnyArgs(AsyncEnumerable.Empty<EventLogPage>());
        watching.ListEventLogAsync(default, default, default).ReturnsForAnyArgs(new EventLogPage());
        watching.WatchRecipientStatusAsync(default).ReturnsForAnyArgs(_ => ++calls == 1 ? throw new RpcException(new Status(StatusCode.Unavailable, "Gone")) : api.WatchRecipientStatusAsync(CancellationToken.None));
        using var page = new EventRulesViewModel(watching, Substitute.For<IDialogService>(), store, _ui);

        page.Activate();

        await Eventually.TrueAsync(() => page.Recipients.Count(r => r.HasStatus) == 3, "the second watch brought the statuses");
    });

    [Fact]
    public Task The_page_watches_only_while_it_is_shown() => _ui.RunAsync(async () =>
    {
        var (api, store) = await ApiAsync();
        using var page = await PageAsync(api, store, _ui);
        var plc = page.Recipients.Single(r => r.Name == "Line 2 PLC");

        page.Deactivate();
        Assert.False(plc.HasStatus); // a status nobody follows would be a guess

        api.SetRecipientStatus(plc.Id, RecipientConnection.RecipientConnected);
        page.Activate();
        await Eventually.TrueAsync(() => page.Recipients.Any(r => r.Name == "Line 2 PLC" && r.IsConnected), "the status of the new watch");
    });

    [Fact]
    public Task A_burst_of_statuses_is_one_post_and_the_newest_wins() => _ui.RunAsync(async () =>
    {
        var (api, store) = await ApiAsync();
        var plc = await ByNameAsync(api, "Line 2 PLC");
        var start = new TaskCompletionSource();
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watching = Substitute.For<IOadmApi>();
        watching.GetEventCatalogAsync(default).ReturnsForAnyArgs(_ => api.GetEventCatalogAsync(CancellationToken.None));
        watching.ListRecipientsAsync(default).ReturnsForAnyArgs(_ => api.ListRecipientsAsync(CancellationToken.None));
        watching.WatchRulesAsync(default).ReturnsForAnyArgs(AsyncEnumerable.Empty<RuleList>());
        watching.WatchEventLogAsync(default).ReturnsForAnyArgs(AsyncEnumerable.Empty<EventLogPage>());
        watching.ListEventLogAsync(default, default, default).ReturnsForAnyArgs(new EventLogPage());
        watching.WatchRecipientStatusAsync(default).ReturnsForAnyArgs(call => Burst(call.Arg<CancellationToken>()));
        var posts = new HeldDispatcher(_ui);
        using var page = new EventRulesViewModel(watching, Substitute.For<IDialogService>(), store, posts);
        page.Activate();
        await Eventually.TrueAsync(() => page.Recipients.Any(r => r.Id == plc.Id), "the list loaded");

        start.SetResult();
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, posts.Posted);

        posts.Release();
        await Eventually.TrueAsync(() => page.Recipients.Single(r => r.Id == plc.Id).StatusTip == "The newest", "the newest status");

        async IAsyncEnumerable<RecipientStatusList> Burst([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await start.Task.WaitAsync(ct);
            for (var i = 0; i < 50; i++)
            {
                yield return new RecipientStatusList { Recipients = { new RecipientStatusEntry { Id = plc.Id, Connection = RecipientConnection.RecipientConnecting } } };
            }

            yield return new RecipientStatusList { Recipients = { new RecipientStatusEntry { Id = plc.Id, Connection = RecipientConnection.RecipientNotConnected, Reason = "The newest" } } };
            sent.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }
    });

    /// <summary>Keeps the posts to the UI thread back, as a busy UI thread does.</summary>
    private sealed class HeldDispatcher(IUiDispatcher inner) : IUiDispatcher
    {
        private readonly List<Action> _held = [];

        public int Posted { get; private set; }

        public void Post(Action action)
        {
            Posted++;
            _held.Add(action);
        }

        public void Release()
        {
            foreach (var action in _held)
            {
                inner.Post(action);
            }

            _held.Clear();
        }
    }

    [Fact]
    public Task The_list_and_the_dialogs_render() => _ui.RunAsync(async () =>
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            var (api, store) = await ApiAsync();
            using var page = new EventRulesViewModel(api, Substitute.For<IDialogService>(), store, new AvaloniaUiDispatcher());
            var view = new EventRulesView { DataContext = page };
            var window = new Window { Width = 1320, Height = 760, Content = new Border { Padding = new Thickness(24), Child = view } };
            Oadm.Client.App.ApplyCrispText(window);
            window.Show();
            await PumpUntilAsync(() => page.Recipients.Count(r => r.HasStatus) == 3 && Chips(view).Count() == 3);
            Capture(window, outDir, "recipient-sessions-page.png");
            Measure(view, outDir, "default");
            var cells = Cells(view).ToList();
            Assert.All(cells, c => Assert.False(c.ShowsWord)); // the narrow card: the target keeps its room
            Assert.Contains(cells, c => ToolTip.GetTip(c.Children.OfType<StatusChip>().Single()) as string == "Not connected: No answer from 10.0.0.70:502.");

            window.Width = 960;
            window.Height = 672;
            await PumpUntilAsync(() => true);
            Assert.False(view.IsStacked);
            Capture(window, outDir, "recipient-sessions-page-min.png");
            Measure(view, outDir, "min");

            window.Width = 880; // stacked: the card is as wide as the page
            window.Height = 1400;
            await PumpUntilAsync(() => view.IsStacked && Cells(view).Count() == 3 && Cells(view).All(c => c.ShowsWord));
            Assert.Contains(Cells(view), c => ToolTip.GetTip(c.Children.OfType<StatusChip>().Single()) as string == "No answer from 10.0.0.70:502.");
            Capture(window, outDir, "recipient-sessions-page-stacked.png");
            Measure(view, outDir, "stacked");
            window.Close();

            await DialogAsync(await EditorAsync(api, await ByNameAsync(api, "Plant broker")), "mqtt", outDir);
            await DialogAsync(await EditorAsync(api, typeId: FakeOadmApi.FakeModbusRecipientType), "modbus", outDir);
            await DialogAsync(await EditorAsync(api, typeId: FakeOadmApi.FakeOpcUaRecipientType), "opcua", outDir);

            api.FakeRecipientCertificates = new RecipientCertificateList();
            var empty = await EditorAsync(api, typeId: FakeOadmApi.FakeMqttRecipientType);
            Field(empty, "tls").IsChecked = true;
            await DialogAsync(empty, "empty", outDir);

            await DialogAsync(await EditorAsync(api, await ByNameAsync(api, "Plant broker")), "gone", outDir);

            api.HoldRecipientCertificates = new TaskCompletionSource();
            var loading = await EditorAsync(api, typeId: FakeOadmApi.FakeMqttRecipientType);
            await DialogAsync(loading, "loading", outDir);
            api.HoldRecipientCertificates.SetResult();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    });

    private static IEnumerable<StatusChip> Chips(EventRulesView view) =>
        view.FindControl<DataGrid>("RecipientGrid")!.GetVisualDescendants().OfType<StatusChip>().Where(c => c.IsEffectivelyVisible);

    private static IEnumerable<RecipientTargetCell> Cells(EventRulesView view) =>
        view.GetVisualDescendants().OfType<RecipientTargetCell>().Where(c => c.Children.OfType<StatusChip>().Single().IsEffectivelyVisible);

    private static async Task DialogAsync(RecipientEditorViewModel editor, string name, string? outDir)
    {
        var dialog = new RecipientEditorWindow();
        dialog.Attach(editor);
        Oadm.Client.App.ApplyCrispText(dialog);
        dialog.Show();
        await PumpUntilAsync(() => dialog.GetVisualDescendants().OfType<SettingsFormView>().Any());
        Capture(dialog, outDir, $"recipient-sessions-dialog-{name}.png");
        dialog.Close();
    }

    /// <summary>The layout the screenshots were measured against, for the report; never asserted.</summary>
    private static void Measure(EventRulesView view, string? outDir, string name)
    {
        if (string.IsNullOrEmpty(outDir))
        {
            return;
        }

        var lines = new List<string>();
        foreach (var chip in Chips(view))
        {
            var cell = chip.GetVisualAncestors().OfType<DataGridCell>().First();
            var text = ((Panel)chip.Parent!).Children.OfType<TextBlock>().First();
            var origin = chip.TranslatePoint(default, cell)!.Value;
            var textOrigin = text.TranslatePoint(default, cell)!.Value;
            lines.Add(FormattableString.Invariant($"{name} {chip.Text}: cell {cell.Bounds.Width:0.#}x{cell.Bounds.Height:0.#}, target x {textOrigin.X:0.#} w {text.Bounds.Width:0.#} '{text.Text}', chip x {origin.X:0.#} w {chip.Bounds.Width:0.#} h {chip.Bounds.Height:0.#}, word shown {((RecipientTargetCell)chip.Parent!).ShowsWord}"));
        }

        Directory.CreateDirectory(outDir);
        File.AppendAllLines(Path.Combine(outDir, "recipient-sessions-measure.txt"), lines);
    }

    private static async Task PumpUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 3000; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            if (i >= 3 && condition())
            {
                return;
            }

            await Task.Yield();
        }

        Assert.Fail("The page did not get there.");
    }

    private static void Capture(Window window, string? outDir, string name)
    {
        WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
            frame.Save(Path.Combine(outDir, name), PngBitmapEncoderOptions.Default);
        }
    }
}
