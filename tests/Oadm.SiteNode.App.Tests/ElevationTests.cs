using Oadm.SiteNode.Admin;
using Oadm.SiteNode.App.Services;
using Oadm.SiteNode.App.ViewModels;

namespace Oadm.SiteNode.App.Tests;

/// <summary>
/// The elevated copy hands its answer back over its standard output (Linux: never a file in a shared folder), and every
/// way it can end is an answer the app shows: only a dismissed prompt is quiet.
/// </summary>
public sealed class ElevationTests
{
    [Fact]
    public async Task TheCopyWritesItsAnswerToTheStandardOutputAndTheAppReadsIt()
    {
        var request = AdminRequest.Of(AdminCommands.PairingCode);
        var output = new StringWriter();

        var exit = await SystemElevation.AnswerAsync(
            [SystemElevation.RequestArgument, SystemElevation.Encode(request), SystemElevation.OutArgument, SystemElevation.StandardOutput],
            r => Task.FromResult(new AdminResponse { Ok = r.Command == AdminCommands.PairingCode, PairingCode = "ABCD-EFGH" }),
            output);

        Assert.Equal(0, exit);
        // pkexec may print its own lines around the answer.
        var answer = SystemElevation.InterpretPkexec(0, "polkit agent says hello\n" + output);
        Assert.True(answer.Ok);
        Assert.Equal("ABCD-EFGH", answer.PairingCode);
    }

    [Fact]
    public async Task ARefusalOfTheServiceIsTheAnswerWhateverTheExitCode()
    {
        var output = new StringWriter();

        var exit = await SystemElevation.AnswerAsync(
            [SystemElevation.RequestArgument, SystemElevation.Encode(AdminRequest.Of(AdminCommands.AllowBinding)), SystemElevation.OutArgument, SystemElevation.StandardOutput],
            _ => throw new IOException("pipe broke"),
            output);

        Assert.Equal(1, exit);
        Assert.Equal(AdminTexts.ServiceNotRunning, SystemElevation.InterpretPkexec(1, output.ToString()).Error);
    }

    [Theory]
    [InlineData(126, SystemElevation.Cancelled)]
    [InlineData(127, SystemElevation.Cancelled)]
    [InlineData(1, SystemElevation.NotMade)]
    [InlineData(0, SystemElevation.NotMade)]
    [InlineData(139, SystemElevation.NotMade)]
    public void WithoutAnAnswerOnlyADismissedPromptIsQuiet(int exitCode, string expected)
    {
        var answer = SystemElevation.InterpretPkexec(exitCode, "");

        Assert.False(answer.Ok);
        Assert.Equal(expected, answer.Error);
        Assert.Null(SystemElevation.ParseAnswerLine(SystemElevation.AnswerPrefix + "not base64!"));
        Assert.Equal(SystemElevation.NotMade, SystemElevation.Interpret(null).Error);
    }

    [Fact]
    public async Task AChangeThatCouldNotBeMadeIsShownAndADismissedPromptIsNot()
    {
        var channel = new FakeChannel();
        var elevation = new AnsweringElevation();
        var vm = new NodeAppViewModel(channel, elevation, new FakeDialogs());
        await vm.RefreshAsync();

        elevation.Answer = AdminResponse.Fail(SystemElevation.NotMade);
        await vm.RemovePortAsync(vm.Ports[1]);
        Assert.Equal(SystemElevation.NotMade, vm.ActionError);

        elevation.Answer = AdminResponse.Fail(SystemElevation.NoPkexec);
        await vm.AllowBindingAsync();
        Assert.Equal(SystemElevation.NoPkexec, vm.BindingError);

        elevation.Answer = AdminResponse.Fail(SystemElevation.Cancelled);
        await vm.AllowBindingAsync();
        Assert.Null(vm.BindingError);
    }

    private sealed class AnsweringElevation : IElevation
    {
        public bool IsAdministrator => false;

        public AdminResponse Answer { get; set; } = new() { Ok = true };

        public Task<AdminResponse> RunAsync(AdminRequest request, CancellationToken ct) => Task.FromResult(Answer);
    }
}
