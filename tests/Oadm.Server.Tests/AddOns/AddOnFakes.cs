using System.IO.Compression;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Oadm.Core.Plugins;
using Oadm.Server.AddOns;
using Oadm.Server.Updates;

namespace Oadm.Server.Tests.AddOns;

/// <summary>The release files of the tests: never a real download.</summary>
internal sealed class FakeAddOnFiles : IAddOnFiles
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly Lock _sync = new();

    public bool Reachable { get; set; } = true;

    /// <summary>The download's SHA-256 does not match the release's list.</summary>
    public bool WrongHash { get; set; }

    /// <summary>Holds every download until it completes (concurrency tests).</summary>
    public TaskCompletionSource? Hold { get; set; }

    /// <summary>Set once a held download has started.</summary>
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<string> Downloads { get; } = [];

    public int ReachCalls { get; private set; }

    /// <summary>Holds every reach check until it completes (one check at a time).</summary>
    public TaskCompletionSource? ReachHold { get; set; }

    public void Publish(string version, string runtime, byte[] zip, string id = AddOnCatalog.TextRecognitionId) =>
        _files[AddOnCatalog.ReleaseFileName(id, version, runtime)] = zip;

    public async Task<bool> CanReachAsync(string version, CancellationToken ct)
    {
        lock (_sync)
        {
            ReachCalls++;
        }

        if (ReachHold is { } hold)
        {
            await hold.Task.WaitAsync(ct);
        }

        return Reachable;
    }

    public async Task DownloadAsync(string version, string fileName, string targetPath, IProgress<int>? progress, CancellationToken ct)
    {
        lock (_sync)
        {
            Downloads.Add(fileName);
        }

        Started.TrySetResult();
        if (Hold is { } hold)
        {
            await hold.Task.WaitAsync(ct);
        }

        if (!Reachable || !_files.TryGetValue(fileName, out var bytes))
        {
            throw new AddOnException(AddOnTexts.DownloadFailed);
        }

        if (WrongHash)
        {
            throw new AddOnException(AddOnTexts.Damaged);
        }

        progress?.Report(50);
        await File.WriteAllBytesAsync(targetPath, bytes, ct);
        progress?.Report(100);
    }
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
        Restart();
    }

    public string DataDirectory { get; }

    public string Root => AddOnFolders.Root(DataDirectory);

    public string Folder => Path.Combine(Root, AddOnCatalog.TextRecognitionId);

    public FakeAddOnFiles Files { get; } = new();

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
            new AddOnInstallerOptions(DataDirectory, _version, Runtime),
            Files,
            List,
            Activation,
            Loader,
            Gate,
            NullLogger<AddOnInstaller>.Instance,
            Time);
    }

    /// <summary>
    /// The release ZIP of an add-on: plugin.json, a server part that loads, its dependency, a client part, a native library and
    /// the version file of <paramref name="version"/> (none when null).
    /// </summary>
    public static byte[] Zip(string id = AddOnCatalog.TextRecognitionId, string minSdk = "0.10.0", string? topFolder = null, string? extraEntry = null, string? version = Version)
    {
        var prefix = topFolder is null ? string.Empty : topFolder + "/";
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, prefix + "plugin.json", Encoding.UTF8.GetBytes(
                $$"""{ "id": "{{id}}", "version": "0.1.0", "minSdkVersion": "{{minSdk}}", "displayName": "Text recognition", "enabledByDefault": false }"""));
            // A real server part: the Restart plugin's assembly under the add-on's name.
            Add(zip, prefix + "Oadm.Plugins.Ocr.Server.dll", File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Oadm.Plugins.Restart.Server.dll")));
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
