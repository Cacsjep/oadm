using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Oadm.Core.Persistence;
using Oadm.Server.Hosting;

namespace Oadm.Server.Tests;

/// <summary>Windows service / systemd hosting and the explicit data folder the service definitions pass.</summary>
public class ServiceHostingTests
{
    [Fact]
    public void ConsoleRunKeepsTheWorkingDirectoryAsContentRoot()
    {
        var options = ServiceHosting.CreateBuilderOptions(["--x=1"], runningAsService: false);

        Assert.Null(options.ContentRootPath);
        Assert.Equal(["--x=1"], options.Args!);
    }

    [Fact]
    public void ServiceUsesTheAppFolderAsContentRoot()
    {
        // Windows services start in System32, systemd units in /: appsettings.json must come from the app folder.
        var options = ServiceHosting.CreateBuilderOptions([], runningAsService: true);

        Assert.Equal(AppContext.BaseDirectory, options.ContentRootPath);
    }

    [Fact]
    public void TestProcessIsNotAService()
    {
        Assert.False(ServiceHosting.IsRunningAsService());
    }

    [Fact]
    public void ServiceLifetimesAreNoOpsOnAConsole()
    {
        var services = new ServiceCollection();

        ServiceHosting.AddServiceLifetimes(services);

        // Neither the Windows service nor the systemd lifetime replaces the console lifetime outside a service.
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IHostLifetime));
    }

    [Fact]
    public async Task DataFolderComesFromTheCommandLine()
    {
        // The MSI service passes --Oadm:DataDir="C:\ProgramData\OADM"; the systemd unit and the LaunchDaemon set OADM_DATA_DIR.
        var dir = Path.Combine(Path.GetTempPath(), "oadm-service-data", Guid.NewGuid().ToString("N"));
        var app = OadmServerHost.Build([$"--Oadm:DataDir={dir}"], new OadmServerHostOptions
        {
            PluginRoots = [],
            LogToConsole = false,
            ConfigureBuilder = b => b.WebHost.UseUrls("http://127.0.0.1:0"),
        });
        await using (app.ConfigureAwait(false))
        {
            Assert.Equal(Path.GetFullPath(dir), app.Services.GetRequiredService<OadmPaths>().DataDirectory);
        }
    }
}
