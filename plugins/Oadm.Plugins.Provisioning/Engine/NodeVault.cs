using System.Security.Cryptography;
using System.Text.Json;

using Oadm.Plugins.Provisioning.Model;
using Oadm.Plugins.Provisioning.Nodes;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Provisioning.Engine;

/// <summary>A secret the vault keeps: encrypted with the server's master key, never sent back.</summary>
public sealed record StoredSecret(string Protected, DateTimeOffset Added);

/// <summary>A file the vault keeps (an AXIS OS file, a configuration file) in the plugin's data folder.</summary>
/// <param name="Id">The id the node settings name (<c>fileId</c>).</param>
/// <param name="Name">The file name as uploaded ("P3265-LVE_12_6_95.bin").</param>
/// <param name="Size">Bytes.</param>
/// <param name="Sha256">Upper case hex.</param>
/// <param name="Added">When it was stored.</param>
public sealed record StoredFile(Guid Id, string Name, long Size, string Sha256, DateTimeOffset Added)
{
    /// <summary>The last upload the tasks read it from; uploads go after a day, so it is added again when gone.</summary>
    public string? UploadId { get; init; }
}

/// <summary>
/// The secrets and files the nodes name by id (README "Secrets and files"): typed passwords and passphrases encrypted with
/// the server's master key (<see cref="ISecretProtector"/>, setting <c>secrets</c>), files in the plugin's data folder
/// (setting <c>files</c>), handed to a task as a system upload (<see cref="IPluginUploads"/>) when a node runs. A profile
/// never holds a secret or a file, only their ids. What no profile names goes after a day.
/// </summary>
public sealed class NodeVault(IPluginSettings settings, ISecretProtector? protector, string? dataDirectory, IPluginUploads? uploads, TimeProvider? time = null) : IDisposable
{
    public const string SecretsKey = "secrets";
    public const string FilesKey = "files";

    /// <summary>Most files kept.</summary>
    public const int MaxFiles = 200;

    /// <summary>Largest file (an AXIS OS file is below 1 GB).</summary>
    public const long MaxFileBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>Most secrets kept.</summary>
    public const int MaxSecrets = 1_000;

    /// <summary>Longest secret (the camera's SD card passphrase takes 512 bytes).</summary>
    public const int MaxSecretLength = 512;

    public const string NoSecrets = "This server cannot keep passwords for provisioning.";
    public const string NoFiles = "This server cannot keep files for provisioning.";
    public const string FileGone = "The file is no longer on the server. Choose it again.";
    public const string SecretGone = "The password is no longer on the server. Type it again.";

    /// <summary>Unnamed secrets and files are kept this long (an editor may name them in a profile it has not saved yet).</summary>
    public static readonly TimeSpan KeepUnnamed = TimeSpan.FromDays(1);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _write = new(1, 1);
    private volatile Dictionary<Guid, StoredSecret> _secrets = [];
    private volatile Dictionary<Guid, StoredFile> _files = [];

    /// <summary>The ids of the secrets kept.</summary>
    public IReadOnlySet<Guid> SecretIds => _secrets.Keys.ToHashSet();

    /// <summary>The files kept, by id.</summary>
    public IReadOnlyDictionary<Guid, StoredFile> Files => _files;

    public async Task LoadAsync(CancellationToken ct)
    {
        _secrets = PluginJson.TryParse<Dictionary<Guid, StoredSecret>>(await settings.GetAsync(SecretsKey, ct).ConfigureAwait(false), ProvisioningJson.Options) ?? [];
        _files = PluginJson.TryParse<Dictionary<Guid, StoredFile>>(await settings.GetAsync(FilesKey, ct).ConfigureAwait(false), ProvisioningJson.Options) ?? [];
    }

    /// <summary>
    /// Keeps a typed password or passphrase (1 to <see cref="MaxSecretLength"/> characters, no control characters) and answers
    /// its id. Never logged; the value leaves the vault only in the payload of the task that needs it.
    /// </summary>
    public async Task<Guid> AddSecretAsync(string value, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length is < 1 or > MaxSecretLength || value.Any(char.IsControl))
        {
            throw new ArgumentException($"Type 1 to {MaxSecretLength} characters, no line breaks.");
        }

        var protect = protector ?? throw new InvalidOperationException(NoSecrets);
        var id = Guid.NewGuid();
        await ChangeAsync(() =>
        {
            if (_secrets.Count >= MaxSecrets)
            {
                throw new InvalidOperationException($"OADM keeps at most {MaxSecrets:N0} passwords for provisioning.");
            }

            _secrets = new Dictionary<Guid, StoredSecret>(_secrets) { [id] = new StoredSecret(protect.Protect(value, Purpose(id)), _time.GetUtcNow()) };
            return true;
        }, ct).ConfigureAwait(false);
        return id;
    }

    /// <summary>The secret with this id; null when it is gone or the server cannot read it.</summary>
    public string? ReadSecret(Guid id)
    {
        if (protector is null || !_secrets.TryGetValue(id, out var secret))
        {
            return null;
        }

        try
        {
            return protector.Unprotect(secret.Protected, Purpose(id));
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Keeps a file (copied from <paramref name="content"/>, at most <see cref="MaxFileBytes"/>) and answers it.</summary>
    public async Task<StoredFile> AddFileAsync(string name, Stream content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        var fileName = Path.GetFileName(name?.Trim() ?? string.Empty);
        if (fileName.Length is 0 or > 255)
        {
            throw new ArgumentException("The file has no name.");
        }

        var folder = Folder ?? throw new InvalidOperationException(NoFiles);
        if (_files.Count >= MaxFiles)
        {
            throw new InvalidOperationException($"OADM keeps at most {MaxFiles} files for provisioning.");
        }

        Directory.CreateDirectory(folder);
        var id = Guid.NewGuid();
        var path = PathOf(id)!;
        long size = 0;
        string sha;
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81_920, useAsync: true))
            {
                var buffer = new byte[81_920];
                int read;
                while ((read = await content.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    size += read;
                    if (size > MaxFileBytes)
                    {
                        throw new ArgumentException("The file is larger than 2 GB.");
                    }

                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
            }

            sha = Convert.ToHexString(hash.GetHashAndReset());
        }
        catch
        {
            File.Delete(path);
            throw;
        }

        var file = new StoredFile(id, fileName, size, sha, _time.GetUtcNow());
        await ChangeAsync(() =>
        {
            _files = new Dictionary<Guid, StoredFile>(_files) { [id] = file };
            return true;
        }, ct).ConfigureAwait(false);
        return file;
    }

    /// <summary>
    /// The upload id of a kept file for a task: the last one while the server still has it, else the file added again.
    /// Throws <see cref="InvalidOperationException"/> with the reason for the user.
    /// </summary>
    public async Task<string> UploadIdAsync(Guid fileId, CancellationToken ct)
    {
        if (!_files.TryGetValue(fileId, out var file) || PathOf(fileId) is not { } path || !File.Exists(path))
        {
            throw new InvalidOperationException(FileGone);
        }

        var target = uploads ?? throw new InvalidOperationException(NoFiles);
        if (file.UploadId is { } last && await target.FindAsync(last, ct).ConfigureAwait(false) is not null)
        {
            return last;
        }

        UploadedFile upload;
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920, useAsync: true))
        {
            upload = await target.AddAsync(file.Name, stream, stream.Length, ct).ConfigureAwait(false);
        }

        if (!string.Equals(upload.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The file on the server has changed. Choose it again.");
        }

        await ChangeAsync(() =>
        {
            if (!_files.TryGetValue(fileId, out var current))
            {
                return false;
            }

            _files = new Dictionary<Guid, StoredFile>(_files) { [fileId] = current with { UploadId = upload.Id } };
            return true;
        }, ct).ConfigureAwait(false);
        return upload.Id;
    }

    /// <summary>The text (UTF-8) of a kept file of at most <paramref name="maxBytes"/>. Throws <see cref="KeyNotFoundException"/> when it is gone.</summary>
    public async Task<string> ReadTextAsync(Guid fileId, long maxBytes, CancellationToken ct)
    {
        if (!_files.ContainsKey(fileId) || PathOf(fileId) is not { } path || !File.Exists(path))
        {
            throw new KeyNotFoundException(FileGone);
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920, useAsync: true);
        if (stream.Length > maxBytes)
        {
            throw new ArgumentException("The file is too large.");
        }

        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }

    /// <summary>A file made while a node runs (a template camera's configuration) as an upload for its task; never kept.</summary>
    public async Task<string> UploadAsync(string name, byte[] content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        var target = uploads ?? throw new InvalidOperationException(NoFiles);
        using var stream = new MemoryStream(content, writable: false);
        return (await target.AddAsync(name, stream, content.Length, ct).ConfigureAwait(false)).Id;
    }

    /// <summary>Removes the secrets and files no profile names and that were added more than a day ago.</summary>
    public async Task PruneAsync(IEnumerable<Profile> named, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(named);
        var (secrets, files) = NodeRefs.Of(named);
        var before = _time.GetUtcNow() - KeepUnnamed;
        List<Guid> gone = [];
        await ChangeAsync(() =>
        {
            var keepSecrets = _secrets.Where(s => secrets.Contains(s.Key) || s.Value.Added > before).ToDictionary();
            var keepFiles = _files.Where(f => files.Contains(f.Key) || f.Value.Added > before).ToDictionary();
            gone = [.. _files.Keys.Where(k => !keepFiles.ContainsKey(k))];
            var changed = keepSecrets.Count != _secrets.Count || gone.Count > 0;
            _secrets = keepSecrets;
            _files = keepFiles;
            return changed;
        }, ct).ConfigureAwait(false);
        foreach (var id in gone)
        {
            if (PathOf(id) is { } path)
            {
                File.Delete(path);
            }
        }
    }

    public void Dispose() => _write.Dispose();

    private string? Folder => dataDirectory is null ? null : Path.Combine(dataDirectory, "files");

    private string? PathOf(Guid id) => Folder is { } folder ? Path.Combine(folder, id.ToString("N") + ".bin") : null;

    private static string Purpose(Guid id) => "oadm.provisioning.secret:" + id.ToString("N");

    /// <summary>Runs <paramref name="change"/> under the write lock and stores both settings when it says so.</summary>
    private async Task ChangeAsync(Func<bool> change, CancellationToken ct)
    {
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var secrets = _secrets;
            var files = _files;
            if (!change())
            {
                return;
            }

            try
            {
                await settings.SetAsync(SecretsKey, JsonSerializer.Serialize(_secrets, ProvisioningJson.Options), ct).ConfigureAwait(false);
                await settings.SetAsync(FilesKey, JsonSerializer.Serialize(_files, ProvisioningJson.Options), ct).ConfigureAwait(false);
            }
            catch
            {
                _secrets = secrets;
                _files = files;
                throw;
            }
        }
        finally
        {
            _write.Release();
        }
    }
}

/// <summary>The secrets (<c>secretId</c>) and files (<c>fileId</c>) node settings name, found by property name at any depth.</summary>
public static class NodeRefs
{
    public static (HashSet<Guid> Secrets, HashSet<Guid> Files) Of(IEnumerable<Profile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var secrets = new HashSet<Guid>();
        var files = new HashSet<Guid>();
        foreach (var node in profiles.SelectMany(p => p.Nodes))
        {
            if (node.Settings is { } settings)
            {
                Walk(settings, secrets, files);
            }
        }

        return (secrets, files);
    }

    private static void Walk(JsonElement element, HashSet<Guid> secrets, HashSet<Guid> files)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String && property.Value.TryGetGuid(out var id))
                    {
                        if (property.NameEquals("secretId"))
                        {
                            secrets.Add(id);
                        }
                        else if (property.NameEquals("fileId"))
                        {
                            files.Add(id);
                        }
                    }
                    else
                    {
                        Walk(property.Value, secrets, files);
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Walk(item, secrets, files);
                }

                break;
        }
    }
}
