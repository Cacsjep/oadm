using System.Text.Json.Nodes;

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
using Oadm.Sdk.Events;
using Oadm.Sdk.Testing;

namespace Oadm.Client.Tests;

/// <summary>
/// Recipients on the Event rules page, the recipient dialog and the select of the rule dialog (docs/spec/recipients.md). Set
/// OADM_SCREENSHOT_DIR for recipients-*.png.
/// </summary>
public sealed class RecipientTests : IDisposable
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

    private static async Task<RecipientEntry> ByNameAsync(FakeOadmApi api, string name) =>
        (await api.ListRecipientsAsync(CancellationToken.None)).Single(r => r.Name == name);

    private static void FillEmail(RecipientEditorViewModel editor, string name = "Night shift")
    {
        editor.Name = name;
        Field(editor, "host").Text = "mail.example.com";
        Field(editor, "from").Text = "oadm@example.com";
        Field(editor, "to").Text = "night@example.com";
    }

    [Fact]
    public Task The_page_lists_the_recipients_with_type_target_and_rules_and_searches_them() => _ui.RunAsync(async () =>
    {
        var (api, store) = await ApiAsync();
        using var page = new EventRulesViewModel(api, Substitute.For<IDialogService>(), store, _ui);
        page.Activate();
        await Eventually.TrueAsync(() => page.Recipients.Count == 3 && page.Rules.Count == 3, "the lists loaded");

        var gate = page.Recipients.Single(r => r.Name == "Gate team");
        Assert.Equal("Email", gate.TypeText);
        Assert.Equal("smtp.office365.com:587", gate.Target);

        page.RecipientSearchText = "hooks";
        Assert.Equal("Teams webhook", Assert.Single(page.Recipients).Name);
        page.RecipientSearchText = "nothing like this";
        Assert.True(page.ShowRecipientsEmpty);
        Assert.Equal("No recipient matches the search.", page.RecipientsEmptyText);
    });

    [Fact]
    public Task An_empty_list_says_what_to_do_first() => _ui.RunAsync(async () =>
    {
        var api = Substitute.For<IOadmApi>();
        api.GetEventCatalogAsync(Arg.Any<CancellationToken>()).Returns(new EventCatalog());
        api.ListRecipientsAsync(Arg.Any<CancellationToken>()).Returns([]);
        api.WatchRulesAsync(Arg.Any<CancellationToken>()).Returns(AsyncEnumerable.Empty<RuleList>());
        api.WatchEventLogAsync(Arg.Any<CancellationToken>()).Returns(AsyncEnumerable.Empty<EventLogPage>());
        api.ListEventLogAsync(Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new EventLogPage());
        using var page = new EventRulesViewModel(api, Substitute.For<IDialogService>(), new DeviceStore(), _ui);
        page.Activate();
        await Eventually.TrueAsync(() => page.ShowRecipientsEmpty, "the list loaded");
        Assert.Equal("Create a recipient, then choose it in a rule.", page.RecipientsEmptyText);
    });

    [Fact]
    public Task A_recipient_made_in_a_cancelled_rule_dialog_shows_in_the_list() => _ui.RunAsync(async () =>
    {
        var (api, store) = await ApiAsync();
        var dialogs = Substitute.For<IDialogService>();
        dialogs.ShowRuleEditorAsync(default!).ReturnsForAnyArgs(async _ =>
        {
            var type = (await api.GetEventCatalogAsync(CancellationToken.None)).RecipientTypes[0];
            await api.SaveRecipientAsync(new SaveRecipientRequest { Name = "Made in the dialog", TypeId = type.Id, SettingsJson = """{"url":"https://new.example.com"}""" }, CancellationToken.None);
            return false;
        });
        using var page = new EventRulesViewModel(api, dialogs, store, _ui);
        page.Activate();
        await Eventually.TrueAsync(() => page.Recipients.Count == 3, "the list loaded");

        await page.NewRuleCommand.ExecuteAsync(null);

        Assert.Contains(page.Recipients, r => r.Name == "Made in the dialog");
    });

    [Fact]
    public Task Delete_of_a_recipient_in_use_says_which_rules_and_keeps_it() => _ui.RunAsync(async () =>
    {
        var (api, store) = await ApiAsync();
        var dialogs = Substitute.For<IDialogService>();
        dialogs.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        using var page = new EventRulesViewModel(api, dialogs, store, _ui);
        page.Activate();
        await Eventually.TrueAsync(() => page.Recipients.Count == 3, "the list loaded");

        page.SelectedRecipient = page.Recipients.Single(r => r.Name == "Gate team");
        await page.DeleteRecipientCommand.ExecuteAsync(null);
        Assert.Equal("Used by Camera offline in Building A. Change those rules first.", page.RecipientsErrorText);
        Assert.Equal(3, page.Recipients.Count);

        page.SelectedRecipient = page.Recipients.Single(r => r.Name == "Alarm panel");
        await page.DeleteRecipientCommand.ExecuteAsync(null);
        Assert.Null(page.RecipientsErrorText);
        Assert.DoesNotContain(page.Recipients, r => r.Name == "Alarm panel");
    });

    [Fact]
    public Task Without_the_right_to_change_rules_the_commands_are_off() => _ui.RunAsync(async () =>
    {
        var (api, store) = await ApiAsync();
        var session = new Shell.UserSession();
        session.SignIn(new UserInfo { UserName = "op", Role = UserRole.Operator, RoleId = FakeOadmApi.OperatorRoleId, Permissions = { "events.view" } }, "fake");
        using var page = new EventRulesViewModel(api, Substitute.For<IDialogService>(), store, _ui, session);
        page.Activate();
        await Eventually.TrueAsync(() => page.Recipients.Count == 3, "the list loaded");
        page.SelectedRecipient = page.Recipients[0];

        Assert.True(page.ShowRecipients);
        Assert.False(page.NewRecipientCommand.CanExecute(null));
        Assert.False(page.EditRecipientCommand.CanExecute(null));
        Assert.False(page.DeleteRecipientCommand.CanExecute(null));
        Assert.Equal("Your role does not allow changing event rules.", page.ReadOnlyHint);
    });

    [Fact]
    public Task A_new_recipient_starts_quiet_then_shows_errors_below_its_fields() => _ui.RunAsync(async () =>
    {
        var (api, _) = await ApiAsync();
        var editor = await EditorAsync(api, typeId: FakeOadmApi.FakeEmailRecipientType);

        Assert.Equal("New recipient", editor.Title);
        Assert.Equal(["HTTP", "Email", "TCP"], editor.Types.Select(t => t.Text));
        Assert.False(editor.SaveCommand.CanExecute(null));
        Assert.All(editor.Fields, f => Assert.Null(f.Error));
        Assert.NotNull(editor.FormError);

        Field(editor, "port").Text = "70000";
        Assert.Equal("Must be 1 to 65535.", Field(editor, "port").Error);
        Field(editor, "port").Text = "587";
        FillEmail(editor);
        Assert.True(editor.SaveCommand.CanExecute(null));

        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Night shift", editor.Saved!.Name);
        Assert.Equal("mail.example.com:587", (await ByNameAsync(api, "Night shift")).Target);
    });

    [Fact]
    public Task A_name_in_use_shows_below_the_name() => _ui.RunAsync(async () =>
    {
        var (api, _) = await ApiAsync();
        var editor = await EditorAsync(api, typeId: FakeOadmApi.FakeEmailRecipientType);
        FillEmail(editor, "Gate team");
        Assert.Equal("A recipient with this name already exists.", editor.ErrorOf(nameof(editor.Name)));
        Assert.False(editor.SaveCommand.CanExecute(null));
    });

    [Fact]
    public Task A_server_refusal_stands_below_the_field_it_names() => _ui.RunAsync(async () =>
    {
        var (fake, _) = await ApiAsync();
        var api = Substitute.For<IOadmApi>();
        api.SaveRecipientAsync(Arg.Any<SaveRecipientRequest>(), Arg.Any<CancellationToken>()).Returns<RecipientEntry>(_ => throw new RpcException(
            new Status(StatusCode.InvalidArgument, "Enter email addresses separated by commas."), new Metadata { { RecipientErrors.FieldTrailer, "to" } }));
        var catalog = await fake.GetEventCatalogAsync(CancellationToken.None);
        var editor = new RecipientEditorViewModel(api, catalog.RecipientTypes, null, []);
        editor.SelectedType = editor.Types.Single(t => t.Entry.Id == FakeOadmApi.FakeEmailRecipientType);
        FillEmail(editor);

        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Enter email addresses separated by commas.", Field(editor, "to").Error);
        Assert.False(editor.SaveCommand.CanExecute(null));

        Field(editor, "to").Text = "day@example.com";
        Assert.Null(Field(editor, "to").Error);
        Assert.True(editor.SaveCommand.CanExecute(null));
    });

    [Fact]
    public Task Changing_a_recipient_keeps_its_values_and_says_the_password_is_stored() => _ui.RunAsync(async () =>
    {
        var (api, _) = await ApiAsync();
        var gate = await ByNameAsync(api, "Gate team");
        var editor = await EditorAsync(api, gate);

        Assert.False(editor.IsNew);
        Assert.Equal("Change recipient", editor.Title);
        Assert.Equal("smtp.office365.com", Field(editor, "host").Text);
        Assert.Equal("Stored. Leave empty to keep it.", Field(editor, "password").Hint);
        Assert.Equal(string.Empty, Field(editor, "password").Text);
        Assert.True(editor.SaveCommand.CanExecute(null));
    });

    [Fact]
    public Task The_login_fields_follow_the_login_of_an_http_recipient() => _ui.RunAsync(async () =>
    {
        var (api, _) = await ApiAsync();
        var editor = await EditorAsync(api, typeId: FakeOadmApi.FakeHttpRecipientType);
        Assert.False(Field(editor, "token").IsVisible);
        Field(editor, "auth").SelectChoice("bearer");
        Assert.True(Field(editor, "token").IsVisible);
        Assert.False(Field(editor, "password").IsVisible);
    });

    [Fact]
    public Task A_wrong_value_shows_below_its_field_while_typing_and_keeps_save_off() => _ui.RunAsync(async () =>
    {
        var (api, _) = await ApiAsync();
        var http = await EditorAsync(api, typeId: FakeOadmApi.FakeHttpRecipientType);
        http.Name = "Hook";

        Field(http, "url").Text = "http://127.0.0.1/acs asdasd";
        Assert.Equal("Enter a URL without spaces.", Field(http, "url").Error);
        Assert.False(http.SaveCommand.CanExecute(null));

        Field(http, "url").Text = "http://127.0.0.1/acs";
        Assert.Null(Field(http, "url").Error);
        Assert.True(http.SaveCommand.CanExecute(null));

        var email = await EditorAsync(api, typeId: FakeOadmApi.FakeEmailRecipientType);
        Field(email, "from").Text = "abc";
        Field(email, "to").Text = "a@example.com, nope";
        Field(email, "host").Text = "mail server";
        Assert.Equal("Enter an email address.", Field(email, "from").Error);
        Assert.Equal("Enter email addresses separated by commas.", Field(email, "to").Error);
        Assert.Equal("Enter a host name or IP address.", Field(email, "host").Error);

        var tcp = await EditorAsync(api, typeId: FakeOadmApi.FakeTcpRecipientType);
        Field(tcp, "host").Text = "panel:8000";
        Assert.Equal("Enter a host name or IP address.", Field(tcp, "host").Error);
    });

    [Fact]
    public Task Test_shows_the_reason_as_text_and_sends_nothing_with_errors() => _ui.RunAsync(async () =>
    {
        var (api, _) = await ApiAsync();
        var editor = await EditorAsync(api, typeId: FakeOadmApi.FakeEmailRecipientType);

        await editor.TestCommand.ExecuteAsync(null);
        Assert.Equal("Fix the settings first.", editor.TestResult);
        Assert.True(editor.TestFailed);
        Assert.Equal("Enter a host name or IP address.", Field(editor, "host").Error);

        FillEmail(editor);
        api.NextRecipientTest = new TestActionReply { Ok = false, Message = "The mail server refused the user name or password: 535 5.7.3" };
        await editor.TestCommand.ExecuteAsync(null);
        Assert.Equal("The mail server refused the user name or password: 535 5.7.3", editor.TestResult);
        Assert.True(editor.TestFailed);

        Field(editor, "to").Text = "other@example.com";
        Assert.Null(editor.TestResult); // an old result never stands next to new values

        api.NextRecipientTest = null;
        await editor.TestCommand.ExecuteAsync(null);
        Assert.True(editor.TestOk);
    });

    [Fact]
    public Task The_rule_dialog_offers_the_recipients_of_the_type_and_New_selects_the_result() => _ui.RunAsync(async () =>
    {
        var (api, store) = await ApiAsync();
        var catalog = await api.GetEventCatalogAsync(CancellationToken.None);
        var editor = new RuleEditorViewModel(api, catalog, null, store, await api.ListRecipientsAsync(CancellationToken.None));
        var action = editor.RuleActions[0];
        action.SelectedAction = action.Actions.Single(a => a.Id == "oadm.notifications.email");
        var field = action.Fields.Single(f => f.IsRecipient);

        Assert.True(field.IsChoice);
        Assert.Equal(["Gate team"], field.Choices.Select(c => c.Text));
        Assert.Equal("Choose a recipient", field.ChoicePlaceholder);
        Assert.Equal("Send email: Choose a recipient.", action.FieldProblem);

        RecipientEditorViewModel? shown = null;
        editor.ShowRecipientEditor = async dialog =>
        {
            shown = dialog;
            FillEmail(dialog, "Another team");
            await dialog.SaveCommand.ExecuteAsync(null);
            return true;
        };
        await field.NewRecipientCommand.ExecuteAsync(null);

        Assert.Equal(["Email"], shown!.Types.Select(t => t.Text)); // only the types of the field
        Assert.Equal(["Another team", "Gate team"], field.Choices.Select(c => c.Text)); // sorted by name
        Assert.Equal("Another team", field.SelectedChoice?.Text);
        Assert.Null(action.FieldProblem);
    });

    [Fact]
    public Task An_old_action_shows_the_field_empty_with_its_error_and_blocks_the_step() => _ui.RunAsync(async () =>
    {
        var (api, store) = await ApiAsync();
        var catalog = await api.GetEventCatalogAsync(CancellationToken.None);
        var saved = await api.SaveRuleAsync(OldRule().ToJson(), CancellationToken.None);
        Assert.Equal("Send email: Choose a recipient.", saved.Summary.Problem);

        var editor = new RuleEditorViewModel(api, catalog, saved, store, await api.ListRecipientsAsync(CancellationToken.None));
        var field = editor.RuleActions[0].Fields.Single(f => f.IsRecipient);
        editor.RuleActions[0].RefreshFields(true);

        Assert.Null(field.SelectedChoice);
        Assert.Equal(ActionSettings.ChooseRecipient, field.Error);
        Assert.Equal("Send email: Choose a recipient.", editor.Problem);

        field.SelectChoice(field.Choices[0].Value);
        Assert.Null(editor.ProblemOf(RuleEditorViewModel.ThenStep));
        var settings = editor.ToDefinition().Actions[0].Settings;
        Assert.Equal(field.Choices[0].Value, settings["recipient"]?.ToString());
        Assert.Null(settings["host"]); // the old values go with this save
        Assert.Null(settings["password"]);
    });

    private static RuleDefinition OldRule() => new()
    {
        Name = "Old mail",
        Enabled = true,
        Conditions = [new RuleCondition { Kind = RuleConditionKind.Event, EventTypeId = "oadm.device.unreachable", Devices = new DeviceSelector { Mode = DeviceSelectorMode.All } }],
        Actions = [new RuleAction { ActionId = "oadm.notifications.email", Settings = new JsonObject { ["host"] = "old.example.com", ["to"] = "old@example.com", ["subject"] = "{rule}", ["format"] = "text", ["body"] = "{event}" } }],
    };

    [Fact]
    public Task The_page_and_the_dialogs_render() => _ui.RunAsync(async () =>
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            var (api, store) = await ApiAsync();
            await api.SaveRuleAsync(OldRule().ToJson(), CancellationToken.None);
            // The theme and fonts of the headless session draw the page like the client.
            using var page = new EventRulesViewModel(api, Substitute.For<IDialogService>(), store, new AvaloniaUiDispatcher());
            var view = new EventRulesView { DataContext = page };
            var window = new Window { Width = 1320, Height = 760, Content = new Border { Padding = new Thickness(24), Child = view } };
            Oadm.Client.App.ApplyCrispText(window);
            window.Show();
            await PumpUntilAsync(() => page.Recipients.Count == 3 && page.Rules.Count == 4 && page.LogEntries.Count > 0);
            Capture(window, outDir, "recipients-page.png");
            Assert.False(view.IsStacked);
            Assert.Contains(view.GetVisualDescendants().OfType<GridSplitter>(), s => s.Classes.Contains("vertical") && s.IsEffectivelyVisible);

            // Both columns at their minimum: the page just wide enough for 560 + splitter + 280.
            window.Width = 960;
            window.Height = 672;
            await PumpUntilAsync(() => true);
            Assert.False(view.IsStacked);
            Capture(window, outDir, "recipients-page-min.png");

            window.Width = 880;
            await PumpUntilAsync(() => view.IsStacked);
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<GridSplitter>(), s => s.Classes.Contains("vertical"));
            Capture(window, outDir, "recipients-page-narrow.png");
            window.Close();

            foreach (var (typeId, name) in new[] { (FakeOadmApi.FakeHttpRecipientType, "http"), (FakeOadmApi.FakeEmailRecipientType, "email"), (FakeOadmApi.FakeTcpRecipientType, "tcp") })
            {
                var editor = await EditorAsync(api, typeId: typeId);
                var dialog = new RecipientEditorWindow();
                dialog.Attach(editor);
                Oadm.Client.App.ApplyCrispText(dialog);
                dialog.Show();
                await PumpUntilAsync(() => dialog.GetVisualDescendants().OfType<SettingsFormView>().Any());
                Capture(dialog, outDir, $"recipients-dialog-{name}.png");
                if (name == "http")
                {
                    editor.Name = "local";
                    Field(editor, "url").Text = "http://127.0.0.1/acs asdasd";
                    await PumpUntilAsync(() => true);
                    Capture(dialog, outDir, "recipients-dialog-url-error.png");
                }
                if (typeId == FakeOadmApi.FakeEmailRecipientType)
                {
                    FillEmail(editor);
                    Field(editor, "user").Text = "oadm@example.com";
                    Field(editor, "password").Text = "secret";
                    api.NextRecipientTest = new TestActionReply { Ok = false, Message = "The mail server refused the user name or password: 535 5.7.3 Authentication unsuccessful" };
                    await editor.TestCommand.ExecuteAsync(null);
                    await PumpUntilAsync(() => true);
                    Capture(dialog, outDir, "recipients-dialog-test-failed.png");
                    api.NextRecipientTest = null;
                    await editor.TestCommand.ExecuteAsync(null);
                    await PumpUntilAsync(() => true);
                    Capture(dialog, outDir, "recipients-dialog-test-ok.png");

                    editor.Name = string.Empty;
                    Field(editor, "to").Text = string.Empty;
                    await PumpUntilAsync(() => true);
                    Capture(dialog, outDir, "recipients-dialog-errors.png");
                }

                dialog.Close();
            }

            var catalog = await api.GetEventCatalogAsync(CancellationToken.None);
            var rule = await api.GetRuleAsync((await api.ListRulesAsync(CancellationToken.None)).Single(r => r.Name == "Old mail").Id, CancellationToken.None);
            var ruleEditor = new RuleEditorViewModel(api, catalog, rule, store, await api.ListRecipientsAsync(CancellationToken.None));
            var ruleWindow = new RuleEditorWindow();
            ruleWindow.Attach(ruleEditor);
            Oadm.Client.App.ApplyCrispText(ruleWindow);
            ruleWindow.Show();
            ruleEditor.GoTo(RuleEditorViewModel.ThenStep);
            await PumpUntilAsync(() => ruleWindow.GetVisualDescendants().OfType<SettingsFormView>().Any(v => v.IsEffectivelyVisible));
            Assert.Contains(ruleWindow.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "New" && t.IsEffectivelyVisible);
            Capture(ruleWindow, outDir, "recipients-rule-old-action.png");

            var field = ruleEditor.RuleActions[0].Fields.Single(f => f.IsRecipient);
            field.SelectChoice(field.Choices[0].Value);
            await PumpUntilAsync(() => true);
            Capture(ruleWindow, outDir, "recipients-rule-select.png");
            ruleWindow.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    });

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
