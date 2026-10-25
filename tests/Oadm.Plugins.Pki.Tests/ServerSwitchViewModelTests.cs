using Oadm.Plugins.Pki.Client.Servers;
using Oadm.Plugins.Pki.Servers;
using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>Turn on / Turn off of the 802.1X and EST servers on their tabs (docs/spec/pki.md "One plugin").</summary>
public sealed class ServerSwitchViewModelTests
{
    [Fact]
    public void A_server_that_is_on_but_does_not_run_can_be_turned_off_from_its_off_state()
    {
        var vm = new ServerSwitchViewModel(new Context((_, _) => Task.FromResult<string?>(null)), "oadm.est", "EST server", "Cameras stop renewing.");

        vm.Apply(new ServerSwitchState(On: true, Running: false, ProActive: false, "Needs an OADM PRO license or trial."));
        Assert.True(vm.ShowOffState);
        Assert.True(vm.ShowOffTurnOff); // the license ended: it can still be turned off
        Assert.False(vm.ShowOffTurnOn);

        vm.Apply(new ServerSwitchState(On: true, Running: false, ProActive: true, ServerSwitchMethods.StartFailed("port 9454 is in use")));
        Assert.True(vm.ShowOffTurnOff); // a start that failed
        vm.Apply(new ServerSwitchState(On: false, Running: false, ProActive: true, ServerSwitchMethods.OffText));
        Assert.False(vm.ShowOffTurnOff);
        Assert.True(vm.ShowOffTurnOn);
    }

    [Fact]
    public async Task Turn_on_shows_its_progress_while_the_server_starts()
    {
        var started = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = new ServerSwitchViewModel(new Context((method, _) => method == ServerSwitchMethods.TurnOn ? started.Task : Task.FromResult<string?>(null)),
            "oadm.est", "EST server", "Cameras stop renewing.");
        vm.Apply(new ServerSwitchState(On: false, Running: false, ProActive: true, ServerSwitchMethods.OffText));

        var turningOn = vm.TurnOnCommand.ExecuteAsync(null);

        Assert.True(vm.IsBusy);
        Assert.Equal("Starting the EST server", vm.BusyText);
        Assert.False(vm.TurnOnCommand.CanExecute(null)); // the button that started it waits
        started.SetResult(ServerSwitchMethods.Serialize(new ServerSwitchState(On: true, Running: true, ProActive: true, "Running")));
        await turningOn;
        Assert.False(vm.IsBusy);
        Assert.True(vm.Running);
    }

    private sealed class Context(Func<string, string?, Task<string?>> answer) : ICorePluginClientContext
    {
        public IReadOnlyList<IDeviceInfo> Devices { get; } = [];

        public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => answer(method, payloadJson);
    }
}
