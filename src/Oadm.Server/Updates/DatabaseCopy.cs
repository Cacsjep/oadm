using Microsoft.Data.Sqlite;

namespace Oadm.Server.Updates;

/// <summary>
/// The copy of the database before an update (docs/spec/self-update.md rule 11): the SQLite backup API writes a consistent
/// copy while the server keeps working, to <c>update/oadm-&lt;from version&gt;.db</c>. Only the copy of the last update is
/// kept (the earlier one goes once the new one is complete); putting it back is a manual step of the user docs.
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
        var target = Path.Combine(folder, FileNameFor(fromVersion));
        var part = target + ".part";
        File.Delete(part);
        var sourceText = new SqliteConnectionStringBuilder(connectionString) { Pooling = false }.ToString();
        var copyText = new SqliteConnectionStringBuilder { DataSource = part, Pooling = false }.ToString();
        try
        {
            await Task.Run(
                () =>
                {
                    ct.ThrowIfCancellationRequested();
                    using var source = new SqliteConnection(sourceText);
                    using var copy = new SqliteConnection(copyText);
                    source.Open();
                    copy.Open();
                    source.BackupDatabase(copy); // one step: SQLite's backup cannot stop halfway
                },
                ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            File.Move(part, target, overwrite: true);
        }
        catch
        {
            File.Delete(part);
            throw;
        }

        foreach (var old in Directory.EnumerateFiles(folder, "oadm-*.db").Where(f => !string.Equals(f, target, StringComparison.Ordinal)))
        {
            try
            {
                File.Delete(old);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Kept for now: the next update removes it.
            }
        }

        return target;
    }
}
