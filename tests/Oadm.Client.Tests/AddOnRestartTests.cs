using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Oadm.Client.Api;
using Oadm.Client.Dialogs;
using Oadm.Client.Plugins;
using Oadm.Client.Plugins.AddOns;
using Oadm.Contracts.V1;

namespace Oadm.Client.Tests;

/// <summary>Restart server on the Optional plugins card: a new version waits for the next restart (docs/spec/add-ons.md).</summary>
public sealed class AddOnRestartTests
{
    private static async Task<(AddOnsViewModel Vm, AddOnRowViewModel Row, IDialogService Dialogs)> RigAsync(FakeOadmApi api, bool confirm = true)
    {
        api.TextRecognitionState = AddOnState.AddOnInstalled;
        api.AddOnUpdateWaits = true;
        var dialogs = Substitute.For<IDialogService>();
        dialogs.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(confirm);
        var store = new PluginPackageStore(api, NullLogger<PluginPackageStore>.Instance);
        var vm = new AddOnsViewModel(api, store, NullLogger<AddOnsViewModel>.Instance, dialogs);
        await vm.LoadAsync(CancellationToken.None);
        return (vm, Assert.Single(vm.AddOns), dialogs);
    }

    [Fact]
    public async Task The_row_says_a_restart_is_needed_and_the_button_asks_first()
    {
        using var api = new FakeOadmApi(TimeSpan.Zero);
        var (vm, row, dialogs) = await RigAsync(api);
        Assert.True(row.ShowRestart);
        Assert.False(row.ShowInstalledChip);

        await vm.RestartCommand.ExecuteAsync(row);

        await dialogs.Received(1).ConfirmAsync(OptionalPluginTexts.RestartTitle, OptionalPluginTexts.RestartQuestion("Text recognition"), OptionalPluginTexts.RestartServer, OptionalPluginTexts.Later);
        Assert.Equal(1, api.AddOnRestarts);
        Assert.False(row.IsBusy);
    }

    [Fact]
    public async Task Later_restarts_nothing()
    {
        using var api = new FakeOadmApi(TimeSpan.Zero);
        var (vm, row, _) = await RigAsync(api, confirm: false);

        await vm.RestartCommand.ExecuteAsync(row);

        Assert.Equal(0, api.AddOnRestarts);
    }

    [Fact]
    public async Task A_refusal_stands_in_the_row()
    {
        using var api = new FakeOadmApi(TimeSpan.Zero) { AddOnRestartError = "1 task is running." };
        var (vm, row, _) = await RigAsync(api);

        await vm.RestartCommand.ExecuteAsync(row);

        Assert.Equal("1 task is running.", row.Error);
    }
}
