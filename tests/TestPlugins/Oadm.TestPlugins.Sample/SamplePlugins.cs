using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.TestPlugins.Sample;

/// <summary>Core plugin that owns state and contributes <see cref="SampleContributedTask"/>.</summary>
public sealed class SampleCorePlugin : ICorePlugin
{
    public SampleCorePlugin()
    {
        TaskPlugins = [new SampleContributedTask(this)];
    }

    public string Id => "oadm.sample";

    public string DisplayName => "Sample core";

    public string? IconKey => null;

    public IReadOnlyList<ITaskPlugin> TaskPlugins { get; }

    public bool Started { get; private set; }

    private int _contributedRuns;

    public int ContributedRuns => Volatile.Read(ref _contributedRuns);

    internal void CountRun() => Interlocked.Increment(ref _contributedRuns);

    public Task StartAsync(ICorePluginContext ctx, CancellationToken ct)
    {
        Started = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        Started = false;
        return Task.CompletedTask;
    }

    public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct)
    {
        return Task.FromResult<string?>(method switch
        {
            "echo" => payloadJson,
            "runs" => ContributedRuns.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => throw new NotSupportedException(method),
        });
    }
}

/// <summary>Task contributed by <see cref="SampleCorePlugin"/>; reaches its owner's state through the context.</summary>
public sealed class SampleContributedTask(SampleCorePlugin owner) : ITaskPlugin
{
    public string Id => "oadm.sample.ping";

    public string DisplayName => "Sample ping";

    public string? IconKey => null;

    public bool ShowInToolbar => false;

    public bool RequiresDialog => false;

    public bool CanRun(IDeviceInfo device) => true;

    public Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (!ReferenceEquals(ctx.Owner, owner))
        {
            throw new InvalidOperationException("Owner was not passed through the execution context.");
        }

        owner.CountRun();
        return Task.CompletedTask;
    }
}

/// <summary>Standalone task plugin with a parameterless constructor.</summary>
public sealed class SampleStandaloneTask : ITaskPlugin
{
    public string Id => "oadm.sample.standalone";

    public string DisplayName => "Sample standalone";

    public string? IconKey => null;

    public bool ShowInToolbar => true;

    public bool RequiresDialog => true;

    public bool CanRun(IDeviceInfo device) => true;

    public Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct) => Task.CompletedTask;
}
