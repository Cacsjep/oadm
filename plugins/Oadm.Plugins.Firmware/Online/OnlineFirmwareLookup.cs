namespace Oadm.Plugins.Firmware.Online;

/// <summary>
/// Firmware online on the server (README "Firmware online"): reads the public listings of axis.com for the models of the
/// selection only, never the whole site, and the file of one version. Read only, no sign-in, no cache.
/// </summary>
public sealed class OnlineFirmwareLookup(IAxisSite site)
{
    /// <summary>Models read at the same time.</summary>
    public const int MaxParallelModels = 8;

    /// <summary>Models of one <c>online</c> query (the dialog asks in parts of this size; 50 models take about 3 s).</summary>
    public const int MaxModelsPerQuery = 50;

    /// <summary>Smallest .bin counted as a firmware file.</summary>
    public const long MinFileSize = FirmwareImageInspector.MinimumSize;

    private readonly IAxisSite _site = site ?? throw new ArgumentNullException(nameof(site));

    /// <summary>Per model the versions grouped by track; <see cref="OnlineLookupResult.Unreachable"/> when axis.com did not answer.</summary>
    public async Task<OnlineLookupResult> LookupAsync(IReadOnlyList<string> models, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(models);
        var wanted = models.Where(m => OnlineFirmware.Normalize(m).Length > 0)
            .DistinctBy(OnlineFirmware.Normalize, StringComparer.Ordinal).ToList();
        if (wanted.Count > MaxModelsPerQuery)
        {
            throw new ArgumentException($"At most {MaxModelsPerQuery} models per query.", nameof(models));
        }

        if (wanted.Count == 0)
        {
            return new OnlineLookupResult();
        }

        Dictionary<string, (string Source, string Folder)> products;
        try
        {
            products = await ReadProductsAsync(ct).ConfigureAwait(false);
        }
        catch (AxisSiteException)
        {
            return new OnlineLookupResult { Unreachable = true };
        }

        using var slots = new SemaphoreSlim(MaxParallelModels);
        var results = await Task.WhenAll(wanted.Select(async model =>
        {
            await slots.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return products.TryGetValue(OnlineFirmware.Normalize(model), out var product)
                    ? await ReadModelAsync(model, product.Source, product.Folder, ct).ConfigureAwait(false)
                    : new OnlineModel { Model = model };
            }
            finally
            {
                slots.Release();
            }
        })).ConfigureAwait(false);
        return new OnlineLookupResult { Models = results };
    }

    /// <summary>
    /// The .bin of one version of a product folder: its version folder, else <c>latest/</c> when its <c>ver.txt</c> names
    /// that version; null when axis.com does not list it (or without a SHA-256 or a size of at least 1 MB).
    /// </summary>
    public async Task<OnlineFileInfo?> FindVersionAsync(string? source, string? product, string? version, CancellationToken ct)
    {
        if (!OnlineFirmware.IsValidSource(source) || !OnlineFirmware.IsValidProduct(product) || OnlineListing.ParseVerTxt(version) is not { } v)
        {
            throw new ArgumentException("Not a product and version of axis.com.");
        }

        var root = OnlineFirmware.SourcePath(source!) + product + "/";
        var folder = OnlineListing.FolderOf(v);
        var page = await _site.GetAsync(root + folder + "/", ct).ConfigureAwait(false);
        if (page.Found)
        {
            return ToInfo(source!, product!, v, PickBin(OnlineListing.Parse(page.Text), root, product!, folder));
        }

        var verTxt = await _site.GetAsync(root + "latest/ver.txt", ct).ConfigureAwait(false);
        if (!verTxt.Found || OnlineListing.ParseVerTxt(verTxt.Text) != v)
        {
            return null;
        }

        var latest = await _site.GetAsync(root + "latest/", ct).ConfigureAwait(false);
        return latest.Found ? ToInfo(source!, product!, v, PickBin(OnlineListing.Parse(latest.Text), root, product!, null)) : null;
    }

    /// <summary>Both root listings: normalized name -> folder; a product in both is taken from MPQT.</summary>
    private async Task<Dictionary<string, (string Source, string Folder)>> ReadProductsAsync(CancellationToken ct)
    {
        var mpqt = _site.GetAsync(OnlineFirmware.SourcePath(OnlineFirmware.Mpqt), ct);
        var pacs = _site.GetAsync(OnlineFirmware.SourcePath(OnlineFirmware.Pacs), ct);
        var pages = await Task.WhenAll(mpqt, pacs).ConfigureAwait(false);
        if (!pages[0].Found || !pages[1].Found)
        {
            throw new AxisSiteException("A product list of axis.com is missing.");
        }

        var products = new Dictionary<string, (string Source, string Folder)>(StringComparer.Ordinal);
        foreach (var (source, page) in ((string, AxisPage)[])[(OnlineFirmware.Mpqt, pages[0]), (OnlineFirmware.Pacs, pages[1])])
        {
            foreach (var entry in OnlineListing.Parse(page.Text))
            {
                if (entry.IsFolder && OnlineFirmware.IsValidProduct(entry.Name))
                {
                    products.TryAdd(OnlineFirmware.Normalize(entry.Name), (source, entry.Name));
                }
            }
        }

        return products;
    }

    private async Task<OnlineModel> ReadModelAsync(string model, string source, string product, CancellationToken ct)
    {
        var root = OnlineFirmware.SourcePath(source) + product + "/";
        try
        {
            var page = await _site.GetAsync(root, ct).ConfigureAwait(false);
            if (!page.Found)
            {
                return new OnlineModel { Model = model };
            }

            var entries = OnlineListing.Parse(page.Text);
            var folders = new Dictionary<AxisOsVersion, string?>();
            foreach (var entry in entries.Where(e => e.IsFolder))
            {
                if (OnlineListing.VersionOfFolder(entry.Name) is { } version)
                {
                    folders.TryAdd(version, entry.Name);
                }
            }

            // latest/: the newest .bin and ver.txt with its version; a version only there counts with that file.
            OnlineListingEntry? latestFile = null;
            AxisOsVersion? verTxt = null;
            if (entries.Any(e => e.IsFolder && e.Name == "latest"))
            {
                var latest = await _site.GetAsync(root + "latest/", ct).ConfigureAwait(false);
                latestFile = latest.Found ? PickBin(OnlineListing.Parse(latest.Text), root, product, null) : null;
                var ver = await _site.GetAsync(root + "latest/ver.txt", ct).ConfigureAwait(false);
                verTxt = ver.Found ? OnlineListing.ParseVerTxt(ver.Text) : null;
                if (verTxt is not null && latestFile is not null && !folders.ContainsKey(verTxt))
                {
                    folders[verTxt] = null;
                }
            }

            var files = new Dictionary<AxisOsVersion, OnlineListingEntry>();
            var tracks = new List<OnlineTrack>();
            foreach (var track in OnlineFirmware.BuildTracks(folders.Keys))
            {
                // The newest version of each track with a usable .bin; versions without one are left out.
                var versions = track.Versions.ToList();
                while (versions.Count > 0)
                {
                    var v = AxisOsVersion.TryParse(versions[0].Version)!;
                    var file = folders[v] is { } folder
                        ? await ReadBinAsync(root, product, folder, ct).ConfigureAwait(false)
                        : latestFile;
                    if (file is not null)
                    {
                        files[v] = file;
                        versions[0] = versions[0] with { Size = file.Size, Sha256 = file.Sha256 };
                        break;
                    }

                    versions.RemoveAt(0);
                }

                if (versions.Count > 0)
                {
                    tracks.Add(track with { Versions = versions });
                }
            }

            var listed = tracks.SelectMany(t => t.Versions).Select(v => AxisOsVersion.TryParse(v.Version)!).ToHashSet();
            var newest = verTxt is not null && listed.Contains(verTxt) ? verTxt
                : latestFile is not null && files.FirstOrDefault(f => f.Value.Sha256 == latestFile.Sha256).Key is { } same ? same
                : listed.Max();
            return new OnlineModel { Model = model, Product = product, Source = source, Latest = newest?.Text, Tracks = tracks };
        }
        catch (AxisSiteException)
        {
            return new OnlineModel { Model = model, Product = product, Source = source, Failed = true };
        }
    }

    private async Task<OnlineListingEntry?> ReadBinAsync(string root, string product, string folder, CancellationToken ct)
    {
        var page = await _site.GetAsync(root + folder + "/", ct).ConfigureAwait(false);
        return page.Found ? PickBin(OnlineListing.Parse(page.Text), root, product, folder) : null;
    }

    /// <summary>
    /// The firmware file of a folder: "&lt;product&gt;_&lt;version&gt;.bin" (or "&lt;product&gt;.bin" in latest/), else the
    /// only .bin; with a SHA-256, at least 1 MB and a link inside the product folder. Null otherwise.
    /// </summary>
    private static OnlineListingEntry? PickBin(IReadOnlyList<OnlineListingEntry> entries, string root, string product, string? folder)
    {
        var bins = entries.Where(e => !e.IsFolder && e.Name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)).ToList();
        var expected = folder is null ? product + ".bin" : product + "_" + folder + ".bin";
        var file = bins.FirstOrDefault(b => string.Equals(b.Name, expected, StringComparison.OrdinalIgnoreCase)) ?? (bins.Count == 1 ? bins[0] : null);
        return file is { Sha256: not null, Size: >= MinFileSize } && file.Path.StartsWith(root, StringComparison.Ordinal) && !file.Path.Contains("..", StringComparison.Ordinal)
            ? file
            : null;
    }

    private static OnlineFileInfo? ToInfo(string source, string product, AxisOsVersion version, OnlineListingEntry? file) =>
        file is null ? null : new OnlineFileInfo
        {
            Source = source,
            Product = product,
            Version = version.Text,
            FileName = file.Name,
            Url = OnlineFirmware.Site + file.Path,
            Size = file.Size!.Value,
            Sha256 = file.Sha256!,
        };
}
