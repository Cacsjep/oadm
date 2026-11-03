using System.Text.Json;
using System.Text.Json.Serialization;

namespace Oadm.Server.Updates;

/// <summary>
/// The record of one server update (docs/spec/self-update.md rule 5), <c>update/state.json</c> in the data folder: written by
/// the server before its helper starts, carried on by the helper (as SYSTEM / root) and completed by the server that starts
/// afterwards, old or new.
/// </summary>
public sealed record UpdateRecord
{
    public const string Running = "running";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string NotBack = "not-back";

    public const string StepCopy = "copy";
    public const string StepInstall = "install";
    public const string StepWait = "wait";

    /// <summary>The helper started the installer (written before it runs: a restarted helper never runs it twice).</summary>
    public const string StepInstalling = "installing";

    public required string From { get; init; }

    public required string To { get; init; }

    public required DateTimeOffset StartedUtc { get; init; }

    public DateTimeOffset? FinishedUtc { get; init; }

    public string Step { get; init; } = StepCopy;

    public string Result { get; init; } = Running;

    /// <summary>Why it failed; on success a note such as "Restart the computer to finish the update".</summary>
    public string? Reason { get; init; }

    public string? InstallerLog { get; init; }

    public string? DatabaseCopy { get; init; }

    /// <summary>"admin@PC-1".</summary>
    public string RequestedBy { get; init; } = string.Empty;

    public string ClientAddress { get; init; } = string.Empty;

    /// <summary>The installer this update runs (in the update folder).</summary>
    public string? Installer { get; init; }

    /// <summary>The installer's SHA-256 (hex) as checked against the release; the helper checks it again before it runs.</summary>
    public string? InstallerSha256 { get; init; }

    /// <summary>The result is in the audit log (written once by the server that completed it).</summary>
    public bool Audited { get; init; }

    [JsonIgnore]
    public bool IsRunning => Result == Running;
}

/// <summary>
/// Reads and writes <c>update/state.json</c> (written whole through a temporary file, never half). Server and helper both
/// write it: a change re-reads the record under <c>state.json.lock</c> and decides on what it holds then, and a file the
/// other process holds open is tried again a few times (short pauses on the given clock, never a blocked thread).
/// </summary>
public sealed class UpdateRecordFile(string dataDirectory, TimeProvider? time = null)
{
    public const string FolderName = "update";
    public const string FileName = "state.json";

    /// <summary>How often a file in use is tried, and the pause between two tries.</summary>
    internal const int Attempts = 20;
    internal static readonly TimeSpan RetryPause = TimeSpan.FromMilliseconds(50);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>The update folder: installers, the database copy, the installer log, the helper.</summary>
    public string Folder { get; } = Path.Combine(dataDirectory, FolderName);

    public string FilePath => System.IO.Path.Combine(Folder, FileName);

    private string LockPath => FilePath + ".lock";

    /// <summary>
    /// The record now, without waiting; null without one (or a damaged file: an update that cannot be read is no update; a
    /// file being replaced right now also reads as null). Tests and quick looks; the server and the helper use
    /// <see cref="ReadAsync"/>.
    /// </summary>
    public UpdateRecord? Read() => TryRead(out var record) ? record : null;

    /// <summary>The record; a file that is being replaced (in use) is read again a few times, without blocking a thread.</summary>
    public async Task<UpdateRecord?> ReadAsync(CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            if (TryRead(out var record) || attempt >= Attempts)
            {
                return record;
            }

            await Task.Delay(RetryPause, _time, ct).ConfigureAwait(false);
        }
    }

    /// <summary>False while the file is in use (try again); true with the record, or null without a readable one.</summary>
    private bool TryRead(out UpdateRecord? record)
    {
        record = null;
        try
        {
            if (!File.Exists(FilePath))
            {
                return true;
            }

            record = JsonSerializer.Deserialize<UpdateRecord>(File.ReadAllText(FilePath), Json);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    /// <summary>Writes the whole record at once, without a pause between tries (test seeds); the server and the helper use the async calls.</summary>
    public void Write(UpdateRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var temp = WriteTemp(record);
        try
        {
            var attempt = 1;
            while (!TryReplace(temp, last: attempt >= Attempts))
            {
                attempt++;
            }
        }
        finally
        {
            File.Delete(temp);
        }
    }

    /// <summary>Writes the whole record (whatever the file holds now); a file in use is tried again a few times.</summary>
    public async Task WriteAsync(UpdateRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        await ChangeAsync(_ => record, ct).ConfigureAwait(false);
    }

    /// <summary>Changes a running record; a finished one stays (the server or the helper got there first).</summary>
    public Task<UpdateRecord?> UpdateRunningAsync(Func<UpdateRecord, UpdateRecord> change, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(change);
        return ChangeAsync(r => r is { IsRunning: true } ? change(r) : null, ct);
    }

    /// <summary>
    /// Read, change and write as one step against the other process: under the lock the record is read again and
    /// <paramref name="change"/> decides on what it holds now (null: leave it). Returns the record written, or null.
    /// </summary>
    public async Task<UpdateRecord?> ChangeAsync(Func<UpdateRecord?, UpdateRecord?> change, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(change);
        Directory.CreateDirectory(Folder);
        var held = await LockAsync(ct).ConfigureAwait(false);
        try
        {
            var next = change(await ReadAsync(ct).ConfigureAwait(false));
            if (next is null)
            {
                return null;
            }

            var temp = WriteTemp(next);
            try
            {
                for (var attempt = 1; !TryReplace(temp, last: attempt >= Attempts); attempt++)
                {
                    await Task.Delay(RetryPause, _time, ct).ConfigureAwait(false);
                }
            }
            finally
            {
                File.Delete(temp);
            }

            return next;
        }
        finally
        {
            if (held is not null)
            {
                await held.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The lock of a change (the other process waits for it); null when it cannot be had in time (e.g. a lock file another
    /// account left without rights): the change then goes on without it rather than never.
    /// </summary>
    private async Task<FileStream?> LockAsync(CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.Read, FileShare.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= Attempts)
                {
                    return null;
                }
            }

            await Task.Delay(RetryPause, _time, ct).ConfigureAwait(false);
        }
    }

    private string WriteTemp(UpdateRecord record)
    {
        Directory.CreateDirectory(Folder);
        var temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(record, Json));
        return temp;
    }

    /// <summary>Replaces the record with <paramref name="temp"/>; false while the other process holds it open (the last try throws).</summary>
    private bool TryReplace(string temp, bool last)
    {
        try
        {
            File.Move(temp, FilePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (!last && ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
