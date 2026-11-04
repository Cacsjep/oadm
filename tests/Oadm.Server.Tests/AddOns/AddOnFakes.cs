using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Oadm.Contracts.Updates;
using Oadm.Core.Plugins;
using Oadm.Server.AddOns;
using Oadm.Server.Updates;

namespace Oadm.Server.Tests.AddOns;

/// <summary>The hashes of the tests: what a release build has built in (no real release needed).</summary>
internal sealed class FakeAddOnHashes : IAddOnHashes
{
    private readonly Dictionary<string, string> _hashes = new(StringComparer.OrdinalIgnoreCase);

    public string? Find(string id) => _hashes.GetValueOrDefault(id);

    public void Allow(byte[] zip, string id = AddOnCatalog.TextRecognitionId) => _hashes[id] = Convert.ToHexStringLower(SHA256.HashData(zip));

    public void Clear() => _hashes.Clear();
}

internal sealed class FakeDiskSpace : IDiskSpace
{
    public long? Free { get; set; }

    public long? AvailableBytes(string folder) => Free;
}

internal sealed class MemoryAddOnList : IAddOnListStore
{
    public List<InstalledAddOn> Items { get; set; } = [];

    /// <summary>Every write fails (a database that is gone).</summary>
    public bool FailWrites { get; set; }

    public Task<IReadOnlyList<InstalledAddOn>> ReadAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<InstalledAddOn>>([.. Items]);

    public Task WriteAsync(IReadOnlyList<InstalledAddOn> list, CancellationToken ct)
    {
        if (FailWrites)
        {
            throw new IOException("The database is gone.");
        }

        Items = [.. list];
        return Task.CompletedTask;
    }
}

internal sealed class RecordingActivation : IAddOnActivation
{
    public List<string> Calls { get; } = [];

    /// <summary>The packages with a stored choice (on or off).</summary>
    public HashSet<string> Choices { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool HasChoice(string packageId) => Choices.Contains(packageId);

    public Task TurnOffAsync(string packageId, CancellationToken ct)
    {
        Calls.Add("off " + packageId);
        Choices.Add(packageId);
        return Task.CompletedTask;
    }

    /// <summary>The packages whose follow the installer waited for, in order.</summary>
    public List<string> Followed { get; } = [];

    public Task PackageFollowedAsync(string packageId, CancellationToken ct)
    {
        Followed.Add(packageId);
        return Task.CompletedTask;
    }
}

/// <summary>One server with a data folder of its own; <see cref="Restart"/> is the next start on the same folder and list.</summary>
internal sealed class AddOnRig : IDisposable
{
    public const string Runtime = "linux-x64";
    public const string Version = "4.11.0";

    private readonly string _version;

    public AddOnRig(string version = Version)
    {
        _version = version;
        DataDirectory = Path.Combine(Path.GetTempPath(), "oadm-addons-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DataDirectory);
        GenuineZip = Zip(version: version);
        Hashes.Allow(GenuineZip);
        Restart();
    }

    /// <summary>The ZIP whose hash this server has built in.</summary>
    public byte[] GenuineZip { get; }

    public string DataDirectory { get; }

    public string Root => AddOnFolders.Root(DataDirectory);

    public string Folder => Path.Combine(Root, AddOnCatalog.TextRecognitionId);

    public FakeAddOnHashes Hashes { get; } = new();

    public FakeDiskSpace Disk { get; } = new();

    /// <summary>The server's limits (tests lower them).</summary>
    public Func<AddOnInstallerOptions, AddOnInstallerOptions> Limits { get; set; } = o => o;

    public MemoryAddOnList List { get; } = new();

    public UpdateGate Gate { get; } = new();

    public FakeTimeProvider Time { get; } = new();

    public RecordingActivation Activation { get; private set; } = null!;

    public PluginRegistry Registry { get; private set; } = null!;

    public PluginLoader Loader { get; private set; } = null!;

    public AddOnInstaller Installer { get; private set; } = null!;

    /// <summary>The packages the installer waited to be followed (<see cref="IAddOnActivation.PackageFollowedAsync"/>).</summary>
    public List<string> Added => Activation.Followed;

    /// <summary>A new process on the same data folder and stored list (the bundled plugins loaded by <paramref name="bundled"/>).</summary>
    public void Restart(Action<PluginLoader>? bundled = null)
    {
        Installer?.Dispose();
        Registry = new PluginRegistry();
        Loader = new PluginLoader(Registry);
        bundled?.Invoke(Loader);
        Activation = new RecordingActivation();
        Installer = new AddOnInstaller(
            Limits(new AddOnInstallerOptions(DataDirectory, _version, Runtime)),
            Hashes,
            List,
            Activation,
            Loader,
            Gate,
            NullLogger<AddOnInstaller>.Instance,
            Time,
            Disk);
    }

    /// <summary>Installs <paramref name="zip"/> (default the genuine one) under the release file's name, as a client sends it.</summary>
    public Task<AddOnStatus> InstallAsync(byte[]? zip = null, ProgressLog? progress = null, string? fileName = null) =>
        Installer.InstallFileAsync(UploadOf(zip ?? GenuineZip, fileName), progress ?? new ProgressLog(), CancellationToken.None);

    /// <summary>The upload of <paramref name="zip"/> in chunks.</summary>
    public AddOnUpload UploadOf(byte[] zip, string? fileName = null, long? size = null) =>
        new(AddOnCatalog.TextRecognitionId, fileName ?? AddOnCatalog.ReleaseFileName(AddOnCatalog.TextRecognitionId, _version, Runtime), size ?? zip.Length, ChunksOf(zip));

    /// <summary>The bytes in 64 KB chunks.</summary>
    public static async IAsyncEnumerable<ReadOnlyMemory<byte>> ChunksOf(byte[] data, [EnumeratorCancellation] CancellationToken ct = default)
    {
        for (var at = 0; at < data.Length; at += 64 * 1024)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return data.AsMemory(at, Math.Min(64 * 1024, data.Length - at));
        }
    }

    /// <summary>
    /// The release ZIP of an add-on: plugin.json, a server part that loads, its dependency, a client part, a native library and
    /// the version file of <paramref name="version"/> (none when null).
    /// </summary>
    public static byte[] Zip(string id = AddOnCatalog.TextRecognitionId, string minSdk = "0.10.0", string? topFolder = null, string? extraEntry = null, string? version = Version, bool brokenServerPart = false, int extraBytes = 0)
    {
        var prefix = topFolder is null ? string.Empty : topFolder + "/";
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, prefix + "plugin.json", Encoding.UTF8.GetBytes(
                $$"""{ "id": "{{id}}", "version": "0.1.0", "minSdkVersion": "{{minSdk}}", "displayName": "Text recognition", "enabledByDefault": false }"""));
            // A real server part: the Restart plugin's assembly under the add-on's name.
            Add(zip, prefix + "Oadm.Plugins.Ocr.Server.dll", brokenServerPart ? [0, 1, 2] : File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Oadm.Plugins.Restart.Server.dll")));
            Add(zip, prefix + "Oadm.Plugins.Ocr.Server.pdb", [8]);
            Add(zip, prefix + "Microsoft.ML.OnnxRuntime.dll", [6, 6]); // a dependency of the server part only
            Add(zip, prefix + "Oadm.Plugins.Ocr.Client.dll", [1, 2, 3]);
            Add(zip, prefix + "client/Ocr.Helpers.dll", [4, 5]);
            Add(zip, prefix + "onnxruntime.dll", [9, 9, 9]);
            Add(zip, prefix + "models/reader.onnx", [7]);
            if (version is not null)
            {
                Add(zip, prefix + AddOnFolders.VersionFileName, Encoding.UTF8.GetBytes(version + "\n"));
            }
            if (extraEntry is not null)
            {
                Add(zip, extraEntry, [0]);
            }

            if (extraBytes > 0)
            {
                Add(zip, prefix + "models/big.bin", new byte[extraBytes]);
            }
        }

        return buffer.ToArray();
    }

    private static void Add(ZipArchive zip, string name, byte[] content)
    {
        using var stream = zip.CreateEntry(name).Open();
        stream.Write(content);
    }

    /// <summary>Puts an add-on folder in place as if installed (unpacked from <see cref="Zip"/> with the version file of <paramref name="version"/>).</summary>
    public void PlaceFolder(string? folder = null, string? version = Version)
    {
        folder ??= Folder;
        Directory.CreateDirectory(folder);
        using var zip = new ZipArchive(new MemoryStream(Zip(version: version)), ZipArchiveMode.Read);
        zip.ExtractToDirectory(folder, overwriteFiles: true);
    }

    /// <summary>The folders and files left in <c>addons/</c>, relative.</summary>
    public IReadOnlyList<string> Leftovers() =>
        Directory.Exists(Root)
            ? [.. Directory.GetFileSystemEntries(Root, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(Root, p).Replace('\\', '/')).Order(StringComparer.Ordinal)]
            : [];

    public void Dispose()
    {
        Installer.Dispose();
        try
        {
            Directory.Delete(DataDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort.
        }
    }
}

/// <summary>Collects progress in order on the caller's thread.</summary>
internal sealed class ProgressLog : IProgress<AddOnProgress>
{
    public List<AddOnProgress> Items { get; } = [];

    public void Report(AddOnProgress value) => Items.Add(value);
}
