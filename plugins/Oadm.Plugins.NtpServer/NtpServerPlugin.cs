using Oadm.Plugins.NtpServer.Tasks;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.NtpServer;

/// <summary>
/// Core plugin "NTP server" (spec <c>docs/specs/ntp-server.md</c>): an RFC 5905 server-mode responder on the selected
/// interface, answering from the server clock (stratum 10, "LOCL") or an optional upstream (stratum + 1). Settings are
/// stored server side and restored on start. Contributes the task "Use OADM as NTP server".
/// Page backend methods: <see cref="NtpServerMethods"/>; live events: state and request log.
/// </summary>
public sealed class NtpServerPlugin : ICorePlugin, IAsyncDisposable
{
    private readonly NtpServerOptions _options;
    private volatile NtpServerService? _service;

    public NtpServerPlugin()
        : this(new NtpServerOptions())
    {
    }

    public NtpServerPlugin(NtpServerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        TaskPlugins = [new UseOadmNtpServerTask(() => _service, _options.Time)];
    }

    public string Id => NtpServerPluginInfo.PluginId;

    public string DisplayName => NtpServerPluginInfo.DisplayName;

    public string? IconKey => NtpServerPluginInfo.IconKey;

    public IReadOnlyList<ITaskPlugin> TaskPlugins { get; }

    /// <summary>The running server (null while stopped).</summary>
    public NtpServerService? Service => _service;

    public async Task StartAsync(ICorePluginContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        await StopAsync(ct).ConfigureAwait(false);
        var service = new NtpServerService(_options, ctx.Settings, ctx.Events, ctx.Logger, ctx.Firewall);
        await service.StartAsync(ct).ConfigureAwait(false);
        _service = service;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_service is { } service)
        {
            _service = null;
            await service.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct)
    {
        var service = _service ?? throw new InvalidOperationException("The NTP server plugin is not running.");
        switch (method)
        {
            case NtpServerMethods.GetState:
                return NtpJson.Serialize(service.GetState(full: true));
            case NtpServerMethods.Save:
                return NtpJson.Serialize(await service.SaveAsync(NtpJson.Deserialize<SaveRequest>(payloadJson), ct).ConfigureAwait(false));
            default:
                throw new ArgumentException($"Unknown method '{method}'.", nameof(method));
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);
}
