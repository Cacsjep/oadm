using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Pki.Ca;

/// <summary>The plugin settings of the PKI: <c>ca</c>, <c>previousCas</c>, <c>issued</c> (written by the task plugins), <c>config</c>.</summary>
public sealed class PkiStore(IPluginSettings settings)
{
    public const string CaKey = "ca";
    public const string PreviousCasKey = "previousCas";
    public const string IssuedKey = "issued";
    public const string ConfigKey = "config";

    private readonly IPluginSettings _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    public async Task<StoredCa?> LoadCaAsync(CancellationToken ct) =>
        PkiJson.TryDeserialize<StoredCa>(await _settings.GetAsync(CaKey, ct).ConfigureAwait(false)) is { Id.Length: > 0 } ca ? ca : null;

    public Task SaveCaAsync(StoredCa ca, CancellationToken ct) => _settings.SetAsync(CaKey, PkiJson.Serialize(ca), ct);

    public async Task<List<StoredPreviousCa>> LoadPreviousAsync(CancellationToken ct) =>
        PkiJson.TryDeserialize<List<StoredPreviousCa>>(await _settings.GetAsync(PreviousCasKey, ct).ConfigureAwait(false)) ?? [];

    public Task SavePreviousAsync(IReadOnlyList<StoredPreviousCa> previous, CancellationToken ct) =>
        _settings.SetAsync(PreviousCasKey, PkiJson.Serialize(previous), ct);

    /// <summary>The registry of issued device certificates (see <see cref="IssuedRegistry"/>).</summary>
    public async Task<List<IssuedCertificate>> LoadIssuedAsync(CancellationToken ct) =>
        PkiJson.TryDeserialize<List<IssuedCertificate>>(await _settings.GetAsync(IssuedKey, ct).ConfigureAwait(false)) ?? [];

    public Task SaveIssuedAsync(IReadOnlyList<IssuedCertificate> issued, CancellationToken ct) =>
        _settings.SetAsync(IssuedKey, PkiJson.Serialize(issued), ct);

    public async Task<PkiConfig> LoadConfigAsync(CancellationToken ct) =>
        PkiJson.TryDeserialize<PkiConfig>(await _settings.GetAsync(ConfigKey, ct).ConfigureAwait(false)) ?? new PkiConfig();

    public Task SaveConfigAsync(PkiConfig config, CancellationToken ct) => _settings.SetAsync(ConfigKey, PkiJson.Serialize(config), ct);
}

/// <summary>Counts of the issued registry for the page (O(n) over the registry).</summary>
public sealed record IssuedCounts(int Devices, int WithCurrentCa, int WithPreviousCa, int ExpiringSoon, IReadOnlyDictionary<string, int> DevicesPerCa)
{
    /// <summary>
    /// Per device and purpose only the newest certificate counts. A device counts once: "with the current CA" when any of
    /// its newest certificates comes from <paramref name="currentCaId"/>, "from a previous CA" when any comes from another CA,
    /// "expiring soon" when any ends within <paramref name="warningDays"/> (or ended).
    /// </summary>
    public static IssuedCounts From(IReadOnlyList<IssuedCertificate> issued, string? currentCaId, DateTime nowUtc, int warningDays)
    {
        ArgumentNullException.ThrowIfNull(issued);
        var newest = new Dictionary<(Guid, string), IssuedCertificate>();
        foreach (var entry in issued)
        {
            var key = (entry.DeviceId, entry.Purpose ?? string.Empty);
            if (!newest.TryGetValue(key, out var known) || entry.IssuedUtc >= known.IssuedUtc)
            {
                newest[key] = entry;
            }
        }

        var devices = new HashSet<Guid>();
        var current = new HashSet<Guid>();
        var previous = new HashSet<Guid>();
        var expiring = new HashSet<Guid>();
        var perCa = new Dictionary<string, HashSet<Guid>>(StringComparer.OrdinalIgnoreCase);
        var limit = nowUtc.AddDays(warningDays);
        foreach (var entry in newest.Values)
        {
            devices.Add(entry.DeviceId);
            if (string.Equals(entry.CaId, currentCaId, StringComparison.OrdinalIgnoreCase))
            {
                current.Add(entry.DeviceId);
            }
            else
            {
                previous.Add(entry.DeviceId);
            }

            if (entry.NotAfterUtc <= limit)
            {
                expiring.Add(entry.DeviceId);
            }

            if (!perCa.TryGetValue(entry.CaId ?? string.Empty, out var set))
            {
                perCa[entry.CaId ?? string.Empty] = set = [];
            }

            set.Add(entry.DeviceId);
        }

        return new IssuedCounts(devices.Count, current.Count, previous.Count, expiring.Count, perCa.ToDictionary(p => p.Key, p => p.Value.Count, StringComparer.OrdinalIgnoreCase));
    }
}
