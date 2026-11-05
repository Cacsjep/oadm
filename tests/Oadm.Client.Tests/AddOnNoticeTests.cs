using NSubstitute;

using Oadm.Client.Api;
using Oadm.Client.Dialogs;
using Oadm.Client.Infrastructure;
using Oadm.Client.Plugins.AddOns;
using Oadm.Client.Shell;
using Oadm.Contracts.V1;

namespace Oadm.Client.Tests;

/// <summary>The dialog about optional plugins after a connect (docs/spec/add-ons.md "Plugins page").</summary>
public sealed class AddOnNoticeTests
{
    private static AddOnInfo Row(bool update = false, AddOnState state = AddOnState.AddOnInstalled, string version = "4.10.0", string url = "zip-4.11.0") => new()
    {
        Id = "oadm.ocr",
        DisplayName = "Text recognition",
        State = state,
        Version = version,
        UpdateAvailable = update,
        DownloadUrl = url,
    };

    private static (AddOnNotice Notice, IOadmApi Api, IDialogService Dialogs, InMemoryClientSettingsStore Settings) Rig(UserRole role = UserRole.Admin, params AddOnInfo[] rows)
    {
        var api = Substitute.For<IOadmApi>();
        Answer(api, rows);
        var dialogs = Substitute.For<IDialogService>();
        dialogs.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        var session = new UserSession();
        session.SignIn(new UserInfo { UserName = "acs", Role = role, RoleName = role.ToString() }, "fake");
        var settings = new InMemoryClientSettingsStore();
        return (new AddOnNotice(api, settings, session, dialogs), api, dialogs, settings);
    }

    private static void Answer(IOadmApi api, params AddOnInfo[] rows)
    {
        var list = new AddOnList();
        list.AddOns.AddRange(rows);
        api.ListAddOnsAsync(Arg.Any<CancellationToken>()).Returns(list);
    }

    [Fact]
    public async Task An_older_plugin_shows_the_dialog_once_for_one_state()
    {
        var (notice, _, dialogs, _) = Rig(rows: Row(update: true));

        Assert.True(await notice.ShowIfNeededAsync(CancellationToken.None));
        Assert.False(await notice.ShowIfNeededAsync(CancellationToken.None));

        await dialogs.Received(1).ConfirmAsync(
            OptionalPluginTexts.NoticeTitle,
            "Text recognition V4.10.0 is older than this OADM. It keeps running. Install the new version.",
            OptionalPluginTexts.OpenPlugins,
            OptionalPluginTexts.Later);
    }

    [Fact]
    public async Task It_comes_back_when_the_next_update_needs_another_zip()
    {
        var (notice, api, dialogs, _) = Rig(rows: Row(update: true));
        await notice.ShowIfNeededAsync(CancellationToken.None);

        Answer(api, Row(update: true, url: "zip-4.12.0"));
        await notice.ShowIfNeededAsync(CancellationToken.None);

        await dialogs.Received(2).ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task A_plugin_that_is_up_to_date_shows_nothing_whatever_its_version()
    {
        var (notice, _, dialogs, _) = Rig(rows: Row());

        Assert.False(await notice.ShowIfNeededAsync(CancellationToken.None));

        await dialogs.DidNotReceive().ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task A_plugin_that_is_not_loaded_says_so()
    {
        var (notice, _, dialogs, _) = Rig(rows: Row(state: AddOnState.AddOnFailed));

        await notice.ShowIfNeededAsync(CancellationToken.None);

        await dialogs.Received(1).ConfirmAsync(Arg.Any<string>(), "Text recognition is not loaded. Install it again.", Arg.Any<string>(), OptionalPluginTexts.Later);
    }

    [Fact]
    public async Task Only_Administrators_see_it()
    {
        var (notice, api, dialogs, _) = Rig(UserRole.Operator, Row(update: true));

        Assert.False(await notice.ShowIfNeededAsync(CancellationToken.None));

        await api.DidNotReceive().ListAddOnsAsync(Arg.Any<CancellationToken>());
        await dialogs.DidNotReceive().ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task A_dialog_that_cannot_show_comes_back()
    {
        var (notice, _, dialogs, settings) = Rig(rows: Row(update: true));
        dialogs.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns<bool>(_ => throw new InvalidOperationException("no window"));

        Assert.False(await notice.ShowIfNeededAsync(CancellationToken.None));

        Assert.Empty(settings.Current.AddOnNotices);
    }

    [Fact]
    public async Task An_install_that_runs_keeps_what_was_told()
    {
        var (notice, api, dialogs, settings) = Rig(rows: Row(update: true));
        await notice.ShowIfNeededAsync(CancellationToken.None);

        Answer(api, Row(state: AddOnState.AddOnInstalling));
        await notice.ShowIfNeededAsync(CancellationToken.None);
        Assert.NotEmpty(settings.Current.AddOnNotices);

        Answer(api, Row(update: true));
        await notice.ShowIfNeededAsync(CancellationToken.None);
        await dialogs.Received(1).ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task A_failed_read_says_nothing()
    {
        var (notice, api, dialogs, _) = Rig();
        api.ListAddOnsAsync(Arg.Any<CancellationToken>()).Returns<AddOnList>(_ => throw new InvalidOperationException("no"));

        Assert.False(await notice.ShowIfNeededAsync(CancellationToken.None));

        await dialogs.DidNotReceive().ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }
}
