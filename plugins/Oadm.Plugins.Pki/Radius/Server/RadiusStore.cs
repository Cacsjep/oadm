using System.Text.Json;

using Oadm.Plugins.Radius.Api;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Radius.Server;

/// <summary>A switch as stored (plugin setting <c>switches</c>): the shared secret only encrypted with the master key.</summary>
public sealed record StoredSwitch
{
    public Guid Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Address { get; init; } = string.Empty;

    public Guid? SiteId { get; init; }

    /// <summary><c>Secrets.Protect(secret, "radius:switch:&lt;id&gt;")</c>; never logged, never sent to a page.</summary>
    public string SecretProtected { get; init; } = string.Empty;
}

/// <summary>The server's own certificate as stored (plugin setting <c>serverCertificate</c>): the key only encrypted.</summary>
public sealed record StoredServerCertificate
{
    /// <summary>The certificate and its CA chain, leaf first, base64 DER.</summary>
    public IReadOnlyList<string> Chain { get; init; } = [];

    /// <summary><c>Secrets.Protect(PKCS#8 base64, "radius:server-key")</c>.</summary>
    public string KeyProtected { get; init; } = string.Empty;

    /// <summary>The names it was made for (addresses and host name), to make it again when they change.</summary>
    public IReadOnlyList<string> Names { get; init; } = [];
}

/// <summary>
/// The plugin settings of the 802.1X server (<see cref="IPluginSettings"/>): <c>config</c> (the Server card),
/// <c>switches</c> (at most 500, secrets encrypted) and <c>serverCertificate</c>. Each a small JSON value written on Save; the
/// log lives in the plugin records (<see cref="RadiusLog"/>). The rules are PKI's policies since OADM 4.5: the Advanced
/// fields of <c>config</c>, <c>trustedCas</c>, <c>policies</c> and the older <c>vlans</c> are only read once, when PKI makes
/// policies of them (<see cref="RadiusPolicies.ConvertAsync"/>), and are kept as they are.
/// </summary>
public sealed class RadiusStore(IPluginSettings settings)
{
    public const string ConfigKey = "config";
    public const string SwitchesKey = "switches";
    public const string VlansKey = "vlans";
    public const string ServerCertificateKey = "serverCertificate";
    public const string PoliciesKey = "policies";
    public const string TrustedCasKey = "trustedCas";

    /// <summary>The policies of development builds (read once for the conversion).</summary>
    public async Task<LegacyPolicySettings?> PoliciesAsync(CancellationToken ct) => Read<LegacyPolicySettings>(await settings.GetAsync(PoliciesKey, ct).ConfigureAwait(false));

    /// <summary>The Trusted CAs of development builds (read once for the conversion).</summary>
    public async Task<TrustedCaSettings?> TrustedCasAsync(CancellationToken ct) => Read<TrustedCaSettings>(await settings.GetAsync(TrustedCasKey, ct).ConfigureAwait(false));

    /// <summary>The Advanced fields of <c>config</c> (read once for the conversion).</summary>
    public async Task<LegacyAdvanced?> LegacyAdvancedAsync(CancellationToken ct) => Read<LegacyAdvanced>(await settings.GetAsync(ConfigKey, ct).ConfigureAwait(false));

    public async Task<RadiusSettings?> ConfigAsync(CancellationToken ct) => Read<RadiusSettings>(await settings.GetAsync(ConfigKey, ct).ConfigureAwait(false));

    public Task SaveConfigAsync(RadiusSettings config, CancellationToken ct) => settings.SetAsync(ConfigKey, RadiusJson.Serialize(config), ct);

    public async Task<IReadOnlyList<StoredSwitch>> SwitchesAsync(CancellationToken ct) =>
        Read<List<StoredSwitch>>(await settings.GetAsync(SwitchesKey, ct).ConfigureAwait(false)) ?? [];

    public Task SaveSwitchesAsync(IReadOnlyList<StoredSwitch> switches, CancellationToken ct) => settings.SetAsync(SwitchesKey, RadiusJson.Serialize(switches), ct);

    /// <summary>The VLAN rules of the development builds before 4.5 (read once for the conversion).</summary>
    public async Task<VlanSettings?> VlansAsync(CancellationToken ct) => Read<VlanSettings>(await settings.GetAsync(VlansKey, ct).ConfigureAwait(false));


    public async Task<StoredServerCertificate?> ServerCertificateAsync(CancellationToken ct) =>
        Read<StoredServerCertificate>(await settings.GetAsync(ServerCertificateKey, ct).ConfigureAwait(false));

    public Task SaveServerCertificateAsync(StoredServerCertificate? certificate, CancellationToken ct) =>
        settings.SetAsync(ServerCertificateKey, certificate is null ? null : RadiusJson.Serialize(certificate), ct);

    /// <summary>A stored value; null when missing or unreadable (the defaults apply).</summary>
    internal static T? Read<T>(string? json)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, RadiusJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
