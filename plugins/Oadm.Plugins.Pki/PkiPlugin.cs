using Oadm.Plugins.Pki.Tasks;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Pki;

/// <summary>
/// Core plugin "PKI" (spec <c>docs/specs/pki.md</c>): one active certificate authority that issues device certificates for
/// HTTPS and IEEE 802.1X. Part 1: the CA (generated on first start or imported), previous CAs, export and backup, the
/// server's trust store, trust anchors for the device grid's certificate rating, settings. Part 2: the contributed Security
/// task plugins that deploy certificates (<see cref="TaskPlugins"/>). Page backend methods: <see cref="PkiMethods"/>; live event: state.
/// </summary>
public sealed class PkiPlugin : ICorePlugin, IAsyncDisposable
{
    private readonly PkiOptions _options;
    private volatile PkiService? _service;

    public PkiPlugin()
        : this(new PkiOptions())
    {
    }

    public PkiPlugin(PkiOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        PkiService? Current() => _service;
        TaskPlugins =
        [
            new HttpsEnableTask(Current),
            new HttpsDisableTask(Current),
            new Dot1xEnableTask(Current),
            new Dot1xDisableTask(Current),
            new ViewCertificatesTask(Current),
            new DeleteCertificatesTask(Current),
            new InstallCertificatesTask(Current),
            new RenewTask(Current),
        ];
    }

    public string Id => PkiPluginInfo.PluginId;

    public string DisplayName => PkiPluginInfo.DisplayName;

    public string? IconKey => PkiPluginInfo.IconKey;

    /// <summary>The Security tasks (part 2): HTTPS and IEEE 802.1X enable / disable, view, delete, manual install, renew.</summary>
    public IReadOnlyList<ITaskPlugin> TaskPlugins { get; }

    /// <summary>The running service (null while stopped).</summary>
    public PkiService? Service => _service;

    public async Task StartAsync(ICorePluginContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        await StopAsync(ct).ConfigureAwait(false);
        var service = new PkiService(_options, ctx.Settings, ctx.Secrets, ctx.Events, ctx.TrustAnchors, ctx.Logger);
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

    /// <summary>
    /// Reading the state is for everyone; generate, import, export, backup, install in the server root store and the
    /// settings are Admin only (and audited). The Security tasks are task plugins and need no more than Operator.
    /// </summary>
    public UserRole RequiredRole(string method) => method == PkiMethods.GetState ? UserRole.Operator : UserRole.Admin;

    public async Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct)
    {
        var service = _service ?? throw new InvalidOperationException("The PKI plugin is not running.");
        return method switch
        {
            PkiMethods.GetState => PkiJson.Serialize(await service.GetStateAsync(ct).ConfigureAwait(false)),
            PkiMethods.Generate => PkiJson.Serialize(await service.GenerateAsync(PkiJson.Deserialize<GenerateRequest>(payloadJson), ct).ConfigureAwait(false)),
            PkiMethods.Import => PkiJson.Serialize(await service.ImportAsync(PkiJson.Deserialize<ImportRequest>(payloadJson), ct).ConfigureAwait(false)),
            PkiMethods.PreviewReplace => PkiJson.Serialize(await service.PreviewReplaceAsync(ct).ConfigureAwait(false)),
            PkiMethods.ExportPublic => PkiJson.Serialize(service.ExportPublic(PkiJson.Deserialize<ExportRequest>(payloadJson))),
            PkiMethods.ExportPrevious => PkiJson.Serialize(service.ExportPrevious(PkiJson.Deserialize<ExportPreviousRequest>(payloadJson))),
            PkiMethods.RemovePrevious => PkiJson.Serialize(await service.RemovePreviousAsync(PkiJson.Deserialize<RemovePreviousRequest>(payloadJson), ct).ConfigureAwait(false)),
            PkiMethods.Backup => PkiJson.Serialize(service.Backup(PkiJson.Deserialize<BackupRequest>(payloadJson))),
            PkiMethods.InstallServerTrust => PkiJson.Serialize(await service.InstallServerTrustAsync(ct).ConfigureAwait(false)),
            PkiMethods.SaveSettings => PkiJson.Serialize(await service.SaveSettingsAsync(PkiJson.Deserialize<SaveSettingsRequest>(payloadJson), ct).ConfigureAwait(false)),
            PkiMethods.ImportRadiusCa => PkiJson.Serialize(PkiService.ImportRadiusCa(PkiJson.Deserialize<ImportRadiusCaRequest>(payloadJson))),
            _ => throw new ArgumentException($"Unknown method '{method}'.", nameof(method)),
        };
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);
}
