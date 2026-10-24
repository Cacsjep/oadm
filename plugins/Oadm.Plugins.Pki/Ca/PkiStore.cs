using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Pki.Ca;

/// <summary>The plugin settings of the PKI: <c>ca</c>, <c>previousCas</c>, <c>importedCas</c>, <c>config</c> (and <c>issued</c> of OADM before 4.2, moved to the records).</summary>
public sealed class PkiStore(IPluginSettings settings)
{
    public const string CaKey = "ca";
    public const string PreviousCasKey = "previousCas";
    public const string IssuedKey = "issued";
    public const string ConfigKey = "config";
    public const string ImportedCasKey = "importedCas";

    private readonly IPluginSettings _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    public async Task<StoredCa?> LoadCaAsync(CancellationToken ct) =>
        PkiJson.TryDeserialize<StoredCa>(await _settings.GetAsync(CaKey, ct).ConfigureAwait(false)) is { Id.Length: > 0 } ca ? ca : null;

    public Task SaveCaAsync(StoredCa ca, CancellationToken ct) => _settings.SetAsync(CaKey, PkiJson.Serialize(ca), ct);

    public async Task<List<StoredPreviousCa>> LoadPreviousAsync(CancellationToken ct) =>
        PkiJson.TryDeserialize<List<StoredPreviousCa>>(await _settings.GetAsync(PreviousCasKey, ct).ConfigureAwait(false)) ?? [];

    public Task SavePreviousAsync(IReadOnlyList<StoredPreviousCa> previous, CancellationToken ct) =>
        _settings.SetAsync(PreviousCasKey, PkiJson.Serialize(previous), ct);

    /// <summary>
    /// The issued certificates as OADM before 4.2 kept them (one settings value); <see cref="IssuedRegistry.LoadAsync"/> moves
    /// them into the plugin records once and deletes the value.
    /// </summary>
    public async Task<List<IssuedCertificate>> LoadIssuedAsync(CancellationToken ct) =>
        PkiJson.TryDeserialize<List<IssuedCertificate>>(await _settings.GetAsync(IssuedKey, ct).ConfigureAwait(false)) ?? [];

    /// <summary>Deletes the settings value of OADM before 4.2 (after the move).</summary>
    public Task DeleteIssuedSettingAsync(CancellationToken ct) => _settings.SetAsync(IssuedKey, null, ct);

    public async Task<List<StoredImportedCa>> LoadImportedAsync(CancellationToken ct) =>
        PkiJson.TryDeserialize<List<StoredImportedCa>>(await _settings.GetAsync(ImportedCasKey, ct).ConfigureAwait(false)) ?? [];

    public Task SaveImportedAsync(IReadOnlyList<StoredImportedCa> imported, CancellationToken ct) =>
        _settings.SetAsync(ImportedCasKey, PkiJson.Serialize(imported), ct);

    public async Task<PkiConfig> LoadConfigAsync(CancellationToken ct) =>
        PkiJson.TryDeserialize<PkiConfig>(await _settings.GetAsync(ConfigKey, ct).ConfigureAwait(false)) ?? new PkiConfig();

    public Task SaveConfigAsync(PkiConfig config, CancellationToken ct) => _settings.SetAsync(ConfigKey, PkiJson.Serialize(config), ct);

    /// <summary>An OADM before the issuing policies ran here: a CA or the settings of the page are stored.</summary>
    public async Task<bool> UsedBeforeAsync(CancellationToken ct) =>
        !string.IsNullOrWhiteSpace(await _settings.GetAsync(ConfigKey, ct).ConfigureAwait(false))
        || !string.IsNullOrWhiteSpace(await _settings.GetAsync(CaKey, ct).ConfigureAwait(false));
}

/// <summary>Counts of the issued registry for the page (O(n) over the registry).</summary>
public sealed record IssuedCounts(int Devices, int WithCurrentCa, int WithPreviousCa, int ExpiringSoon, IReadOnlyDictionary<string, int> DevicesPerCa)
{
    /// <summary>
    /// Only certificates still in use count: not ended, not revoked (<paramref name="isRevoked"/>), not pending (not confirmed on
    /// the camera yet), not pushed out of their camera's current place and not deleted from the
    /// device (the store keeps those 30 days after their end, which would inflate the counts). Per device and purpose only the
    /// newest of them counts. A device counts once: "with the current CA" when any of its newest certificates comes from
    /// <paramref name="currentCaId"/>, "from a previous CA" when any comes from another CA, "expiring soon" when any ends within
    /// <paramref name="warningDays"/>.
    /// </summary>
    public static IssuedCounts From(IReadOnlyList<IssuedCertificate> issued, string? currentCaId, DateTime nowUtc, int warningDays, Func<string, bool>? isRevoked = null) =>
        From(issued, currentCaId, nowUtc, _ => warningDays, isRevoked);

    /// <summary>The counts with the warning days of each device (its Expiry warning policy).</summary>
    public static IssuedCounts From(IReadOnlyList<IssuedCertificate> issued, string? currentCaId, DateTime nowUtc, Func<Guid, int> warningDays, Func<string, bool>? isRevoked = null)
    {
        ArgumentNullException.ThrowIfNull(warningDays);
        ArgumentNullException.ThrowIfNull(issued);
        var newest = new Dictionary<(Guid, string), IssuedCertificate>();
        foreach (var entry in issued)
        {
            if (entry.NotAfterUtc <= nowUtc || entry.RemovedFromDeviceUtc is not null || entry.Pending || entry.NotCurrent || (isRevoked?.Invoke(entry.SerialNumber) ?? false))
            {
                continue;
            }

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

            if (entry.NotAfterUtc <= nowUtc.AddDays(warningDays(entry.DeviceId)))
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
