using Microsoft.Data.Sqlite;

namespace Oadm.Server.Updates;

/// <summary>
/// The copy of the database before an update (docs/spec/self-update.md rule 11): the SQLite backup API writes a consistent
/// copy while the server keeps working, to <c>update/oadm-&lt;from version&gt;.db</c>. Only the copy of the last update is
/// kept; putting it back is a manual step of the user docs.
/// </summary>
public static class DatabaseCopy
{
    public static string FileNameFor(string fromVersion) => $"oadm-{fromVersion}.db";

    /// <summary>Copies <paramref name="connectionString"/>'s database into <paramref name="folder"/>; returns the copy's path.</summary>
    public static async Task<string> CopyAsync(string connectionString, string folder, string fromVersion, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(fromVersion);
        Directory.CreateDirectory(folder);
        foreach (var old in Directory.EnumerateFiles(folder, "oadm-*.db"))
        {
            File.Delete(old);
        }

        var target = Path.Combine(folder, FileNameFor(fromVersion));
        var part = target + ".part";
        File.Delete(part);
        await Task.Run(
            () =>
            {
                using var source = new SqliteConnection(connectionString + ";Pooling=False");
                using var copy = new SqliteConnection($"Data Source={part};Pooling=False");
                source.Open();
                copy.Open();
                source.BackupDatabase(copy);
            },
            ct).ConfigureAwait(false);
        File.Move(part, target, overwrite: true);
        return target;
    }
}
