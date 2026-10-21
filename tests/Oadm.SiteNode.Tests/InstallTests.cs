using Oadm.SiteNode.Admin;
using Oadm.SiteNode.Cli;
using Oadm.SiteNode.Tests.Support;

namespace Oadm.SiteNode.Tests;

/// <summary>What the installers rely on: a command line that waits for a service the installer just started (MSI property
/// SERVER).</summary>
public sealed class InstallTests
{
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
