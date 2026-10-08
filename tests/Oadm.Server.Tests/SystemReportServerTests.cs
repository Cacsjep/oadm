using Oadm.Core.Auth;
using Oadm.Core.Plugins;
using Oadm.Plugins.SystemReport;
using Oadm.Sdk.Plugins;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests;

/// <summary>The System report core plugin inside the real server: no rail page, operator calls, audited start, data folder.</summary>
public sealed class SystemReportServerTests
{
    [Fact]
    public async Task AToolbarOnlyPluginIsListedWithoutAPageAndOperatorsRunAuditedJobs()
    {
        await using var host = await TestServerHost.StartAsync();
        Assert.True(host.Get<PluginRegistry>().RegisterCorePlugin(new SystemReportPlugin(), new PluginOrigin(SystemReportPluginInfo.PluginId, "0.1.0", null)));
        await host.Get<CorePluginHost>().StartAllAsync(CancellationToken.None);

        var listed = Assert.Single((await host.Plugins.ListCorePluginsAsync(new Proto.Empty())).Plugins);
        Assert.Equal((SystemReportPluginInfo.PluginId, "System report", true), (listed.Id, listed.DisplayName, listed.NoPage));

        var op = new Proto.PluginService.PluginServiceClient(await host.InvokerForUserAsync("operator1", UserRole.Operator));
        async Task<JobStatus> Call(string method, object request) =>
            SystemReportJson.Deserialize<JobStatus>((await op.InvokeAsync(new Proto.InvokeRequest
            {
                PluginId = SystemReportPluginInfo.PluginId,
                Method = method,
                PayloadJson = SystemReportJson.Serialize(request),
            })).PayloadJson);

        var started = await Call(SystemReportMethods.Start, new StartRequest { DeviceIds = [Guid.NewGuid()] });
        var jobFolder = Path.Combine(host.DataDirectory, "plugin-data", SystemReportPluginInfo.PluginId, "jobs", started.JobId);
        Assert.True(Directory.Exists(jobFolder));
        var status = started;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (status.State != JobStates.Done && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
            status = await Call(SystemReportMethods.Status, new StatusRequest { JobId = started.JobId });
        }

        Assert.Equal((JobStates.Done, 1), (status.State, status.Failed));
        Assert.Equal("The device is no longer managed", status.Devices.Single().Error);
        var chunk = SystemReportJson.Deserialize<ReportChunk>((await op.InvokeAsync(new Proto.InvokeRequest
        {
            PluginId = SystemReportPluginInfo.PluginId,
            Method = SystemReportMethods.Read,
            PayloadJson = SystemReportJson.Serialize(new ReadRequest { JobId = started.JobId }),
        })).PayloadJson);
        Assert.True(chunk.Eof);

        var audit = (await host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries.Where(e => e.Action == AuditActions.PluginCall).ToList();
        var entry = Assert.Single(audit);
        Assert.Equal(("operator1", "System report", "start"), (entry.UserName, entry.Target, entry.Detail));

        await host.Get<CorePluginHost>().StopAllAsync(CancellationToken.None);
        Assert.False(Directory.Exists(jobFolder));
    }
}
