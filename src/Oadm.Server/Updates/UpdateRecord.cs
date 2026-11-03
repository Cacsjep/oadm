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

/// <summary>Reads and writes <c>update/state.json</c> (written whole through a temporary file, never half).</summary>
public sealed class UpdateRecordFile(string dataDirectory)
{
    public const string FolderName = "update";
    public const string FileName = "state.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>The update folder: installers, the database copy, the installer log, the helper.</summary>
    public string Folder { get; } = Path.Combine(dataDirectory, FolderName);

    public string FilePath => System.IO.Path.Combine(Folder, FileName);

    /// <summary>
    /// The record; null without one (or a damaged file: an update that cannot be read is no update). A file that is being
    /// replaced (in use) is read again a few times.
    /// </summary>
    public UpdateRecord? Read()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    return null;
                }

                return JsonSerializer.Deserialize<UpdateRecord>(File.ReadAllText(FilePath), Json);
            }
            catch (IOException) when (attempt < ReadAttempts)
            {
                Thread.Sleep(ReadRetryPause);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    private const int ReadAttempts = 5;
    private static readonly TimeSpan ReadRetryPause = TimeSpan.FromMilliseconds(50);

    /// <summary>Writes the whole record through a temporary file of its own (server and helper may write at once).</summary>
    public void Write(UpdateRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Directory.CreateDirectory(Folder);
        var temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(record, Json));
            File.Move(temp, FilePath, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    /// <summary>Changes a running record; a finished one stays (the server or the helper got there first).</summary>
    public UpdateRecord? UpdateRunning(Func<UpdateRecord, UpdateRecord> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (Read() is not { IsRunning: true } record)
        {
            return null;
        }

        var next = change(record);
        Write(next);
        return next;
    }
}
