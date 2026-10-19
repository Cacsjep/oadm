using Oadm.SiteNode.Admin;
using Oadm.SiteNode.Cli;
using Oadm.SiteNode.Tests.Support;

namespace Oadm.SiteNode.Tests;

/// <summary>What the installers rely on: the firewall rule of "Wait for the server" and a command line that waits for a
/// service the installer just started (MSI property SERVER).</summary>
public sealed class InstallTests
{
    private static readonly AdminResponse Done = new() { Ok = true, State = new NodeView { ListenPort = 5081 } };

    [Fact]
    public void WaitForTheServerAddsTheRuleForDomainAndPrivate()
    {
        var commands = NodeFirewall.Commands(AdminRequest.Of(AdminCommands.SetServer, ("mode", "wait")), Done, "");

        Assert.Equal(2, commands.Count);
        Assert.Equal(["advfirewall", "firewall", "delete", "rule", "name=OADM Site Node (TCP 5081)"], commands[0]);
        Assert.Contains("add", commands[1]);
        Assert.Contains("localport=5081", commands[1]);
        Assert.Contains("protocol=TCP", commands[1]);
        Assert.Contains("dir=in", commands[1]);
        Assert.Contains("profile=domain,private", commands[1]);
        Assert.DoesNotContain(commands[1], a => a.StartsWith("program=", StringComparison.Ordinal)); // no such file here
    }

    [Fact]
    public void ConnectRemovesTheRuleAndOtherChangesLeaveIt()
    {
        var connect = NodeFirewall.Commands(AdminRequest.Of(AdminCommands.SetServer, ("mode", "connect"), ("address", "10.0.0.5")), Done, "");
        Assert.Single(connect);
        Assert.Contains("delete", connect[0]);

        Assert.Empty(NodeFirewall.Commands(AdminRequest.Of(AdminCommands.SetServer, ("mode", "wait")), AdminResponse.Fail("Refused."), ""));
        Assert.Empty(NodeFirewall.Commands(AdminRequest.Of(AdminCommands.AllowBinding), Done, ""));
    }

    [Fact]
    public async Task ARuleThatCannotBeAddedIsReportedADeleteThatFindsNoneIsNot()
    {
        var wait = AdminRequest.Of(AdminCommands.SetServer, ("mode", "wait"));
        var calls = new List<string>();
        var problem = await NodeFirewall.ApplyAsync(wait, Done, CancellationToken.None, (args, _) =>
        {
            calls.Add(args[2]);
            return Task.FromResult(args[2] == "delete" ? 1 : 0);
        });
        Assert.Null(problem);
        Assert.Equal(["delete", "add"], calls);

        problem = await NodeFirewall.ApplyAsync(wait, Done, CancellationToken.None, (_, _) => Task.FromResult(1));
        Assert.NotNull(problem);
    }

    [Theory]
    [InlineData("server 10.0.0.5", "server 10.0.0.5", 0)]
    [InlineData("server 10.0.0.5 --timeout 60", "server 10.0.0.5", 60)]
    [InlineData("--timeout 5 status", "status", 5)]
    public void TimeoutIsTakenFromTheArguments(string args, string rest, int seconds)
    {
        var split = CommandLine.TakeTimeout(args.Split(' '));
        Assert.NotNull(split);
        Assert.Equal(rest, string.Join(' ', split.Value.Arguments));
        Assert.Equal(TimeSpan.FromSeconds(seconds), split.Value.Wait);
        Assert.Null(CommandLine.TakeTimeout(["status", "--timeout"]));
        Assert.Null(CommandLine.TakeTimeout(["status", "--timeout", "-1"]));
    }

    [Fact]
    public async Task WithoutATimeoutTheCommandLineSaysTheServiceIsNotRunning()
    {
        using var folder = new TempFolder();
        var error = new StringWriter();
        var exit = await CommandLine.RunAsync("status", [], folder.Path, TextWriter.Null, error, CancellationToken.None);
        Assert.Equal(1, exit);
        Assert.Contains(AdminTexts.ServiceNotRunning, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithATimeoutTheCommandLineWaitsForTheService()
    {
        using var folder = new TempFolder();
        var endpoint = AdminEndpoint.ForDataDirectory(folder.Path);
        var output = new StringWriter();
        var run = CommandLine.RunAsync("status", ["--timeout", "60"], folder.Path, output, TextWriter.Null, CancellationToken.None, new AdminClient(endpoint));

        await using var node = await TestNode.StartAsync(options: o => o.AdminEndpoint = endpoint);

        Assert.Equal(0, await run);
        Assert.Contains("Not bound.", output.ToString(), StringComparison.Ordinal);
    }
}
