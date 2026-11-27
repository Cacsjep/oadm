using System.Globalization;
using System.Text;
using System.Threading.Channels;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Oadm.Plugins.LicensePlates.Log;

/// <summary>A row to store: a read or a gap, with the plate crop to keep as a file (null: none).</summary>
internal sealed record LogInsert(PlateRead Row, byte[]? Picture);

/// <summary>A later change of a stored read: its direction, a better plate crop.</summary>
internal sealed record LogUpdate(long Id, PlateDirection? Direction, byte[]? Picture);

/// <summary>The cameras a call may see in the log: null = every camera (users with all devices).</summary>
internal sealed record LogScope(IReadOnlySet<Guid>? Cameras)
{
    public static LogScope All { get; } = new((IReadOnlySet<Guid>?)null);

    public static LogScope None { get; } = new(new HashSet<Guid>());
}

/// <summary>
/// The plate log (docs/spec/license-plate-center.md "The log store"): one SQLite database in the chosen folder (WAL), one row
/// per read with the plate's name and tags at the time of the read, indexes on time, plate and time, camera and time, result
/// and time; the plate crops as JPEG files in a folder per day next to it, never in the database. Writes are batched (one
/// transaction per burst, one writer); reads run on their own connections beside it. Counts for the Stats tab come from two
/// small tables kept with every write (per hour and camera, per day, camera and plate), never from a scan of the reads.
/// Never part of a backup.
/// </summary>
internal sealed partial class PlateLogStore : IAsyncDisposable
{
    public const string DatabaseName = "plate-log.db";
    public const string PicturesFolder = "pictures";
    private const int MaxPictureBytes = 512 * 1024;
    private const long HourMs = 3_600_000;

    private const string Columns =
        "id, time, camera, camera_name, plate, read_as, result, reason, reason_time, name, tags, direction, area, country, confidence, view, vehicle_type, vehicle_color, vehicle_make, vehicle_model, picture, gap_until, state, vehicle_confidence, list_entry";

    private readonly string _connection;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Channel<object> _writes = Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _writer;
    private long _nextId;
    private long _pictureBytes;
    private volatile string? _problem;

    private PlateLogStore(string folder, TimeProvider time, ILogger logger, long nextId, long pictureBytes)
    {
        Folder = folder;
        _connection = ConnectionString(folder);
        _time = time;
        _logger = logger;
        _nextId = nextId;
        _pictureBytes = pictureBytes;
        using (ExecutionContext.SuppressFlow())
        {
            _writer = Task.Run(WriteLoopAsync);
        }
    }

    /// <summary>The log's folder.</summary>
    public string Folder { get; }

    /// <summary>Keep the plate crops (the setting).</summary>
    public bool KeepPictures { get; set; } = true;

    /// <summary>Why storing stopped (a full disk, the folder gone); null while it works.</summary>
    public string? Problem => _problem;

    /// <summary>Raised (without a caller) when <see cref="Problem"/> changes.</summary>
    public event Action? ProblemChanged;

    /// <summary>Rows written so far (tests wait for it).</summary>
    public long Written => Interlocked.Read(ref _written);

    private long _written;

    /// <summary>The next id of a row: the log hands it out before the row is written, so a later change can name it.</summary>
    public long NextId() => Interlocked.Increment(ref _nextId);

    /// <summary>The database, its journal and the plate crops, in bytes.</summary>
    public long UsedBytes
    {
        get
        {
            long size = Interlocked.Read(ref _pictureBytes);
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                var file = new FileInfo(Path.Combine(Folder, DatabaseName + suffix));
                if (file.Exists)
                {
                    size += file.Length;
                }
            }

            return size;
        }
    }

    private static string ConnectionString(string folder) =>
        new SqliteConnectionStringBuilder { DataSource = Path.Combine(folder, DatabaseName), Pooling = true, DefaultTimeout = 30 }.ToString();

    /// <summary>Opens (or creates) the log in <paramref name="folder"/>; throws when the folder cannot be used.</summary>
    public static async Task<PlateLogStore> OpenAsync(string folder, TimeProvider time, ILogger? logger, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(time);
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(folder, PicturesFolder));
        await using var db = new SqliteConnection(ConnectionString(folder));
        await db.OpenAsync(ct).ConfigureAwait(false);
        await ExecuteAsync(db, "PRAGMA auto_vacuum = INCREMENTAL;", ct).ConfigureAwait(false);
        await ExecuteAsync(db, "PRAGMA journal_mode = WAL;", ct).ConfigureAwait(false);
        await ExecuteAsync(db, """
            CREATE TABLE IF NOT EXISTS reads(
              id INTEGER PRIMARY KEY, time INTEGER NOT NULL, camera TEXT NOT NULL, camera_name TEXT NOT NULL, plate TEXT NOT NULL,
              read_as TEXT, result INTEGER NOT NULL, reason INTEGER NOT NULL, reason_time TEXT, name TEXT NOT NULL, tags TEXT NOT NULL,
              direction INTEGER NOT NULL, area INTEGER, country TEXT, confidence INTEGER NOT NULL, view TEXT, vehicle_type TEXT,
              vehicle_color TEXT, vehicle_make TEXT, vehicle_model TEXT, picture TEXT, gap_until INTEGER, state TEXT, vehicle_confidence INTEGER, list_entry TEXT);
            CREATE INDEX IF NOT EXISTS reads_time ON reads(time);
            CREATE INDEX IF NOT EXISTS reads_plate_time ON reads(plate, time);
            CREATE INDEX IF NOT EXISTS reads_camera_time ON reads(camera, time);
            CREATE INDEX IF NOT EXISTS reads_result_time ON reads(result, time);
            CREATE TABLE IF NOT EXISTS hourly(
              hour INTEGER NOT NULL, camera TEXT NOT NULL, result INTEGER NOT NULL, direction INTEGER NOT NULL, reads INTEGER NOT NULL,
              confidence INTEGER NOT NULL, low INTEGER NOT NULL, last INTEGER NOT NULL, mid INTEGER NOT NULL DEFAULT 0, good INTEGER NOT NULL DEFAULT 0,
              vehicle INTEGER NOT NULL DEFAULT 0, vehicles INTEGER NOT NULL DEFAULT 0,
              PRIMARY KEY(hour, camera, result, direction)) WITHOUT ROWID;
            DROP TABLE IF EXISTS daily_plates;
            CREATE TABLE IF NOT EXISTS picture_days(day TEXT PRIMARY KEY, bytes INTEGER NOT NULL) WITHOUT ROWID;
            """, ct).ConfigureAwait(false);
        // The confidence bands 60 to 74 (mid) and 75 to 89 (good) came after the first stores: added once; the hours before count
        // as 90 and above.
        if (Convert.ToInt64(await ScalarAsync(db, "SELECT COUNT(*) FROM pragma_table_info('hourly') WHERE name = 'mid';", ct).ConfigureAwait(false), CultureInfo.InvariantCulture) == 0)
        {
            await ExecuteAsync(db, "ALTER TABLE hourly ADD COLUMN mid INTEGER NOT NULL DEFAULT 0; ALTER TABLE hourly ADD COLUMN good INTEGER NOT NULL DEFAULT 0;", ct).ConfigureAwait(false);
        }

        // The vehicle confidence (sum and count per hour) and the plate's state came on 2026-11-27: added once; older reads have none.
        if (Convert.ToInt64(await ScalarAsync(db, "SELECT COUNT(*) FROM pragma_table_info('hourly') WHERE name = 'vehicle';", ct).ConfigureAwait(false), CultureInfo.InvariantCulture) == 0)
        {
            await ExecuteAsync(db, "ALTER TABLE hourly ADD COLUMN vehicle INTEGER NOT NULL DEFAULT 0; ALTER TABLE hourly ADD COLUMN vehicles INTEGER NOT NULL DEFAULT 0;", ct).ConfigureAwait(false);
        }

        if (Convert.ToInt64(await ScalarAsync(db, "SELECT COUNT(*) FROM pragma_table_info('reads') WHERE name = 'state';", ct).ConfigureAwait(false), CultureInfo.InvariantCulture) == 0)
        {
            await ExecuteAsync(db, "ALTER TABLE reads ADD COLUMN state TEXT; ALTER TABLE reads ADD COLUMN vehicle_confidence INTEGER;", ct).ConfigureAwait(false);
        }

        // The plate of the list that decided a read (a wildcard plate's pattern) came with the wildcard plates: older reads have none.
        if (Convert.ToInt64(await ScalarAsync(db, "SELECT COUNT(*) FROM pragma_table_info('reads') WHERE name = 'list_entry';", ct).ConfigureAwait(false), CultureInfo.InvariantCulture) == 0)
        {
            await ExecuteAsync(db, "ALTER TABLE reads ADD COLUMN list_entry TEXT;", ct).ConfigureAwait(false);
        }

        // A search by name finds the reads of a wildcard plate by its list entry.
        await ExecuteAsync(db, "CREATE INDEX IF NOT EXISTS reads_list_entry_time ON reads(list_entry, time);", ct).ConfigureAwait(false);

        var nextId = Convert.ToInt64(await ScalarAsync(db, "SELECT COALESCE(MAX(id), 0) FROM reads;", ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        var pictures = Convert.ToInt64(await ScalarAsync(db, "SELECT COALESCE(SUM(bytes), 0) FROM picture_days;", ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        return new PlateLogStore(folder, time, logger ?? NullLogger.Instance, nextId, pictures);
    }

    // ---- writes ----

    /// <summary>Queues a row; written with the next burst. Dropped while <see cref="Problem"/> is set.</summary>
    public void Insert(LogInsert insert)
    {
        ArgumentNullException.ThrowIfNull(insert);
        if (_problem is null)
        {
            _writes.Writer.TryWrite(insert);
        }
    }

    /// <summary>Queues a change of a stored read.</summary>
    public void Update(LogUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (_problem is null)
        {
            _writes.Writer.TryWrite(update);
        }
    }

    /// <summary>Waits until everything queued before the call is written (tests, the export).</summary>
    public Task FlushAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_writes.Writer.TryWrite(done))
        {
            done.TrySetResult();
        }

        return done.Task;
    }

    /// <summary>Queues the cleanup: reads older than <paramref name="keepDays"/>, then the oldest days until the log is under <paramref name="maxBytes"/>.</summary>
    public Task CleanupAsync(int keepDays, long maxBytes)
    {
        var cleanup = new Cleanup(keepDays, maxBytes);
        if (!_writes.Writer.TryWrite(cleanup))
        {
            cleanup.Done.TrySetResult();
        }

        return cleanup.Done.Task;
    }

    private sealed record Cleanup(int KeepDays, long MaxBytes)
    {
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private async Task WriteLoopAsync()
    {
        var reader = _writes.Reader;
        var batch = new List<object>(256);
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            batch.Clear();
            while (batch.Count < 2_000 && reader.TryRead(out var item))
            {
                batch.Add(item);
            }

            var rows = batch.Where(b => b is LogInsert or LogUpdate).ToList();
            if (rows.Count > 0 && _problem is null)
            {
                try
                {
                    await WriteBatchAsync(rows).ConfigureAwait(false);
                    Interlocked.Add(ref _written, rows.Count(r => r is LogInsert));
                }
#pragma warning disable CA1031 // A full disk or a folder that is gone stops storing; the reads still fire their events.
                catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
#pragma warning restore CA1031
                {
                    SetProblem(Describe(ex));
                    LogWriteFailed(_logger, Folder, ex);
                }
            }

            foreach (var item in batch)
            {
                switch (item)
                {
                    case Cleanup cleanup:
                        try
                        {
                            if (_problem is null)
                            {
                                await RunCleanupAsync(cleanup.KeepDays, cleanup.MaxBytes).ConfigureAwait(false);
                            }
                        }
#pragma warning disable CA1031 // The next hour tries again.
                        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
#pragma warning restore CA1031
                        {
                            SetProblem(Describe(ex));
                            LogWriteFailed(_logger, Folder, ex);
                        }
                        finally
                        {
                            cleanup.Done.TrySetResult();
                        }

                        break;
                    case TaskCompletionSource flush:
                        flush.TrySetResult();
                        break;
                }
            }
        }
    }

    /// <summary>
    /// Fills a new log fast (the Perf test of 10 million reads): prepared inserts, one transaction per 200,000 rows, the
    /// journal off and no sync for the fill only, then the hourly counts in one pass. Never used by the plugin itself.
    /// </summary>
    internal async Task BulkFillAsync(IEnumerable<PlateRead> rows, Action<long>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rows);
        await FlushAsync().ConfigureAwait(false);
        await using var db = new SqliteConnection(_connection);
        await db.OpenAsync(ct).ConfigureAwait(false);
        await ExecuteAsync(db, "PRAGMA journal_mode = OFF; PRAGMA synchronous = OFF;", ct).ConfigureAwait(false);
        try
        {
            long count = 0;
            SqliteTransaction? tx = null;
            SqliteCommand? cmd = null;
            try
            {
                foreach (var row in rows)
                {
                    if (tx is null)
                    {
                        tx = (SqliteTransaction)await db.BeginTransactionAsync(ct).ConfigureAwait(false);
                        cmd = db.CreateCommand();
                        cmd.Transaction = tx;
                        cmd.CommandText = "INSERT INTO reads(id, time, camera, camera_name, plate, result, reason, name, tags, direction, confidence) VALUES (@id, @time, @camera, @camera_name, @plate, @result, @reason, @name, @tags, @direction, @confidence);";
                        foreach (var name in new[] { "@id", "@time", "@camera", "@camera_name", "@plate", "@result", "@reason", "@name", "@tags", "@direction", "@confidence" })
                        {
                            cmd.Parameters.Add(new SqliteParameter { ParameterName = name });
                        }

                        cmd.Prepare();
                    }

                    var p = cmd!.Parameters;
                    p["@id"].Value = row.Id;
                    p["@time"].Value = row.Time.ToUnixTimeMilliseconds();
                    p["@camera"].Value = CameraKey(row.CameraId);
                    p["@camera_name"].Value = row.Camera;
                    p["@plate"].Value = row.Plate;
                    p["@result"].Value = (int)row.Result;
                    p["@reason"].Value = (int)row.Reason;
                    p["@name"].Value = row.Name;
                    p["@tags"].Value = TagsKey(row.Tags);
                    p["@direction"].Value = (int)row.Direction;
                    p["@confidence"].Value = row.Confidence;
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    if (++count % 200_000 == 0)
                    {
                        await tx.CommitAsync(ct).ConfigureAwait(false);
                        await cmd.DisposeAsync().ConfigureAwait(false);
                        await tx.DisposeAsync().ConfigureAwait(false);
                        (tx, cmd) = (null, null);
                        progress?.Invoke(count);
                    }
                }

                if (tx is not null)
                {
                    await tx.CommitAsync(ct).ConfigureAwait(false);
                }
            }
            finally
            {
                if (cmd is not null)
                {
                    await cmd.DisposeAsync().ConfigureAwait(false);
                }

                if (tx is not null)
                {
                    await tx.DisposeAsync().ConfigureAwait(false);
                }
            }

            await ExecuteAsync(db, $"""
                INSERT INTO hourly(hour, camera, result, direction, reads, confidence, low, last, mid, good)
                SELECT time / {HourMs}, camera, result, direction, COUNT(*), SUM(confidence), SUM(confidence < {ConfidenceLevels.Error}), MAX(time),
                  SUM(confidence >= {ConfidenceLevels.Error} AND confidence < {ConfidenceLevels.Warning}), SUM(confidence >= {ConfidenceLevels.Warning} AND confidence < {ConfidenceLevels.High})
                FROM reads WHERE gap_until IS NULL GROUP BY time / {HourMs}, camera, result, direction;
                """, ct).ConfigureAwait(false);
            Interlocked.Add(ref _written, count);
        }
        finally
        {
            await ExecuteAsync(db, "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;", CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>The problem text of a failed write.</summary>
    internal static string Describe(Exception ex) => ex switch
    {
        SqliteException { SqliteErrorCode: 13 } => "The disk of the log folder is full. Reads are not stored.",
        SqliteException { SqliteErrorCode: 8 } or UnauthorizedAccessException => "The log folder cannot be written. Reads are not stored.",
        IOException when IsDiskFull(ex) => "The disk of the log folder is full. Reads are not stored.",
        _ => "The log folder cannot be used. Reads are not stored.",
    };

    private static bool IsDiskFull(Exception ex) => (ex.HResult & 0xFFFF) is 0x70 or 0x27 || ex.Message.Contains("space", StringComparison.OrdinalIgnoreCase);

    private void SetProblem(string? problem)
    {
        if (_problem == problem)
        {
            return;
        }

        _problem = problem;
        ProblemChanged?.Invoke();
    }

    private async Task WriteBatchAsync(List<object> rows)
    {
        await using var db = new SqliteConnection(_connection);
        await db.OpenAsync().ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await db.BeginTransactionAsync().ConfigureAwait(false);
        var zone = _time.LocalTimeZone;
        foreach (var item in rows)
        {
            switch (item)
            {
                case LogInsert insert:
                    await InsertAsync(db, tx, insert, zone).ConfigureAwait(false);
                    break;
                case LogUpdate update:
                    await UpdateAsync(db, tx, update, zone).ConfigureAwait(false);
                    break;
            }
        }

        await tx.CommitAsync().ConfigureAwait(false);
    }

    private async Task InsertAsync(SqliteConnection db, SqliteTransaction tx, LogInsert insert, TimeZoneInfo zone)
    {
        var row = insert.Row;
        var ms = row.Time.ToUnixTimeMilliseconds();
        var picture = row.Gap || !KeepPictures || insert.Picture is null ? null : await SavePictureAsync(db, tx, row.Id, row.Time, insert.Picture, zone).ConfigureAwait(false);
        await using (var cmd = db.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"INSERT INTO reads({Columns}) VALUES (@id, @time, @camera, @camera_name, @plate, @read_as, @result, @reason, @reason_time, @name, @tags, @direction, @area, @country, @confidence, @view, @vehicle_type, @vehicle_color, @vehicle_make, @vehicle_model, @picture, @gap_until, @state, @vehicle_confidence, @list_entry);";
            cmd.Parameters.AddWithValue("@id", row.Id);
            cmd.Parameters.AddWithValue("@time", ms);
            cmd.Parameters.AddWithValue("@camera", CameraKey(row.CameraId));
            cmd.Parameters.AddWithValue("@camera_name", row.Camera);
            cmd.Parameters.AddWithValue("@plate", row.Plate);
            cmd.Parameters.AddWithValue("@read_as", (object?)row.ReadAs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@result", row.Gap ? GapResult : (int)row.Result);
            cmd.Parameters.AddWithValue("@reason", (int)row.Reason);
            cmd.Parameters.AddWithValue("@reason_time", row.ReasonTime is { } rt ? rt.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture) : DBNull.Value);
            cmd.Parameters.AddWithValue("@name", row.Name);
            cmd.Parameters.AddWithValue("@tags", TagsKey(row.Tags));
            cmd.Parameters.AddWithValue("@direction", (int)row.Direction);
            cmd.Parameters.AddWithValue("@area", (object?)row.Area ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@country", (object?)row.Country ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@confidence", row.Confidence);
            cmd.Parameters.AddWithValue("@view", (object?)row.View ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@vehicle_type", (object?)row.VehicleType ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@vehicle_color", (object?)row.VehicleColor ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@vehicle_make", (object?)row.VehicleMake ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@vehicle_model", (object?)row.VehicleModel ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@picture", (object?)picture ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@gap_until", row.Until is { } until ? until.ToUnixTimeMilliseconds() : DBNull.Value);
            cmd.Parameters.AddWithValue("@state", (object?)row.State ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@vehicle_confidence", (object?)row.VehicleConfidence ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@list_entry", (object?)row.ListEntry ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        if (!row.Gap)
        {
            await CountAsync(db, tx, ms, row.CameraId, row.Result, row.Direction, row.Confidence, row.VehicleConfidence, +1).ConfigureAwait(false);
        }
    }

    /// <summary>The result column of a gap row.</summary>
    internal const int GapResult = 9;

    private async Task UpdateAsync(SqliteConnection db, SqliteTransaction tx, LogUpdate update, TimeZoneInfo zone)
    {
        long ms;
        Guid camera;
        ReadResult result;
        PlateDirection direction;
        int confidence;
        string plate;
        string? picture;
        await using (var read = db.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT time, camera, result, direction, confidence, plate, picture FROM reads WHERE id = @id AND gap_until IS NULL;";
            read.Parameters.AddWithValue("@id", update.Id);
            await using var reader = await read.ExecuteReaderAsync().ConfigureAwait(false);
            if (!await reader.ReadAsync().ConfigureAwait(false))
            {
                return; // removed by the cleanup meanwhile
            }

            ms = reader.GetInt64(0);
            camera = Guid.Parse(reader.GetString(1));
            result = (ReadResult)reader.GetInt32(2);
            direction = (PlateDirection)reader.GetInt32(3);
            confidence = reader.GetInt32(4);
            plate = reader.GetString(5);
            picture = reader.IsDBNull(6) ? null : reader.GetString(6);
        }

        var time = DateTimeOffset.FromUnixTimeMilliseconds(ms);
        if (update.Picture is not null && KeepPictures)
        {
            if (picture is not null)
            {
                DeletePicture(picture);
            }

            picture = await SavePictureAsync(db, tx, update.Id, time, update.Picture, zone).ConfigureAwait(false) ?? picture;
        }

        var newDirection = update.Direction ?? direction;
        await using (var cmd = db.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE reads SET direction = @direction, picture = @picture WHERE id = @id;";
            cmd.Parameters.AddWithValue("@direction", (int)newDirection);
            cmd.Parameters.AddWithValue("@picture", (object?)picture ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@id", update.Id);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        if (newDirection != direction)
        {
            // The vehicle confidence stays with the hour and the camera: only the direction of the count moves.
            await CountAsync(db, tx, ms, camera, result, direction, confidence, null, -1).ConfigureAwait(false);
            await CountAsync(db, tx, ms, camera, result, newDirection, confidence, null, +1).ConfigureAwait(false);
        }
    }

    private static async Task CountAsync(SqliteConnection db, SqliteTransaction tx, long ms, Guid camera, ReadResult result, PlateDirection direction, int confidence, int? vehicle, int delta)
    {
        await using (var cmd = db.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO hourly(hour, camera, result, direction, reads, confidence, low, last, mid, good, vehicle, vehicles) VALUES (@hour, @camera, @result, @direction, @delta, @confidence, @low, @time, @mid, @good, @vehicle, @vehicles)
                ON CONFLICT(hour, camera, result, direction) DO UPDATE SET reads = reads + @delta, confidence = confidence + @confidence, low = low + @low, last = MAX(last, @time),
                  mid = mid + @mid, good = good + @good, vehicle = vehicle + @vehicle, vehicles = vehicles + @vehicles;
                """;
            cmd.Parameters.AddWithValue("@hour", ms / HourMs);
            cmd.Parameters.AddWithValue("@camera", CameraKey(camera));
            cmd.Parameters.AddWithValue("@result", (int)result);
            cmd.Parameters.AddWithValue("@direction", (int)direction);
            cmd.Parameters.AddWithValue("@delta", delta);
            cmd.Parameters.AddWithValue("@confidence", delta * confidence);
            cmd.Parameters.AddWithValue("@low", confidence < ConfidenceLevels.Error ? delta : 0);
            cmd.Parameters.AddWithValue("@mid", confidence is >= ConfidenceLevels.Error and < ConfidenceLevels.Warning ? delta : 0);
            cmd.Parameters.AddWithValue("@good", confidence is >= ConfidenceLevels.Warning and < ConfidenceLevels.High ? delta : 0);
            cmd.Parameters.AddWithValue("@time", ms);
            cmd.Parameters.AddWithValue("@vehicle", vehicle is { } v ? delta * v : 0);
            cmd.Parameters.AddWithValue("@vehicles", vehicle is null ? 0 : delta);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

    }

    private async Task<string?> SavePictureAsync(SqliteConnection db, SqliteTransaction tx, long id, DateTimeOffset time, byte[] picture, TimeZoneInfo zone)
    {
        if (picture.Length is < 4 or > MaxPictureBytes || picture[0] != 0xFF || picture[1] != 0xD8)
        {
            return null; // not a JPEG: nothing is converted
        }

        var day = TimeZoneInfo.ConvertTime(time, zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var relative = Path.Combine(PicturesFolder, day, id.ToString(CultureInfo.InvariantCulture) + ".jpg");
        var path = Path.Combine(Folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, picture).ConfigureAwait(false);
        Interlocked.Add(ref _pictureBytes, picture.Length);
        await using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO picture_days(day, bytes) VALUES (@day, @bytes) ON CONFLICT(day) DO UPDATE SET bytes = bytes + @bytes;";
        cmd.Parameters.AddWithValue("@day", day);
        cmd.Parameters.AddWithValue("@bytes", picture.Length);
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        return relative.Replace('\\', '/');
    }

    private long DeletePicture(string relative)
    {
        var path = PicturePath(relative);
        if (path is null)
        {
            return 0;
        }

        var file = new FileInfo(path);
        if (!file.Exists)
        {
            return 0;
        }

        var length = file.Length;
        file.Delete();
        Interlocked.Add(ref _pictureBytes, -length);
        return length;
    }

    /// <summary>The file of a stored picture path; null for a path outside the pictures folder.</summary>
    private string? PicturePath(string relative)
    {
        var root = Path.GetFullPath(Path.Combine(Folder, PicturesFolder));
        var path = Path.GetFullPath(Path.Combine(Folder, relative));
        return path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? path : null;
    }

    // ---- retention ----

    private async Task RunCleanupAsync(int keepDays, long maxBytes)
    {
        await using var db = new SqliteConnection(_connection);
        await db.OpenAsync().ConfigureAwait(false);
        var cutoff = (_time.GetUtcNow() - TimeSpan.FromDays(Math.Max(1, keepDays))).ToUnixTimeMilliseconds();
        await DeleteBeforeAsync(db, cutoff).ConfigureAwait(false);
        await ExecuteAsync(db, "PRAGMA incremental_vacuum;", CancellationToken.None).ConfigureAwait(false);
        await ExecuteAsync(db, "PRAGMA wal_checkpoint(TRUNCATE);", CancellationToken.None).ConfigureAwait(false);
        for (var round = 0; round < 3_660 && UsedBytes > maxBytes; round++)
        {
            var oldest = await ScalarAsync(db, "SELECT MIN(time) FROM reads;", CancellationToken.None).ConfigureAwait(false);
            if (oldest is null or DBNull)
            {
                break;
            }

            // The oldest day of the server's zone, as a whole.
            var ms = Convert.ToInt64(oldest, CultureInfo.InvariantCulture);
            var zone = _time.LocalTimeZone;
            var local = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(ms), zone);
            var nextDay = new DateTimeOffset(local.Date.AddDays(1), zone.GetUtcOffset(local.Date.AddDays(1)));
            await DeleteBeforeAsync(db, nextDay.ToUnixTimeMilliseconds()).ConfigureAwait(false);
            await ExecuteAsync(db, "PRAGMA incremental_vacuum;", CancellationToken.None).ConfigureAwait(false);
            await ExecuteAsync(db, "PRAGMA wal_checkpoint(TRUNCATE);", CancellationToken.None).ConfigureAwait(false);
        }

        RemoveEmptyDayFolders();
    }

    /// <summary>Deletes the reads before <paramref name="before"/> (ms) with their crops and counts, in chunks.</summary>
    private async Task DeleteBeforeAsync(SqliteConnection db, long before)
    {
        while (true)
        {
            var chunk = new List<(long Id, string? Picture)>();
            await using (var select = db.CreateCommand())
            {
                select.CommandText = "SELECT id, picture FROM reads WHERE time < @before ORDER BY time LIMIT 20000;";
                select.Parameters.AddWithValue("@before", before);
                await using var reader = await select.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    chunk.Add((reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
                }
            }

            if (chunk.Count == 0)
            {
                break;
            }

            var freed = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var (_, picture) in chunk)
            {
                if (picture is not null)
                {
                    var day = picture.Split('/') is { Length: 3 } parts ? parts[1] : string.Empty;
                    freed[day] = freed.GetValueOrDefault(day) + DeletePicture(picture);
                }
            }

            await using var tx = (SqliteTransaction)await db.BeginTransactionAsync().ConfigureAwait(false);
            await using (var delete = db.CreateCommand())
            {
                delete.Transaction = tx;
                delete.CommandText = "DELETE FROM reads WHERE id IN (SELECT value FROM json_each(@ids));";
                delete.Parameters.AddWithValue("@ids", "[" + string.Join(",", chunk.Select(c => c.Id.ToString(CultureInfo.InvariantCulture))) + "]");
                await delete.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            foreach (var (day, bytes) in freed)
            {
                await using var cmd = db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE picture_days SET bytes = MAX(0, bytes - @bytes) WHERE day = @day;";
                cmd.Parameters.AddWithValue("@day", day);
                cmd.Parameters.AddWithValue("@bytes", bytes);
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            await tx.CommitAsync().ConfigureAwait(false);
        }

        await using (var counts = db.CreateCommand())
        {
            counts.CommandText = "DELETE FROM hourly WHERE hour < @hour; DELETE FROM picture_days WHERE bytes <= 0;";
            counts.Parameters.AddWithValue("@hour", before / HourMs);
            await counts.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
    }

    private void RemoveEmptyDayFolders()
    {
        var root = Path.Combine(Folder, PicturesFolder);
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var day in Directory.EnumerateDirectories(root))
        {
            if (!Directory.EnumerateFileSystemEntries(day).Any())
            {
                Directory.Delete(day);
            }
        }
    }

    // ---- reads ----

    private async Task<SqliteConnection> OpenReadAsync(CancellationToken ct)
    {
        var db = new SqliteConnection(_connection);
        await db.OpenAsync(ct).ConfigureAwait(false);
        return db;
    }

    /// <summary>A page of the log, newest first.</summary>
    public async Task<LogPage> PageAsync(LogPageRequest request, LogScope scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(scope);
        var limit = Math.Clamp(request.Limit, 1, LicensePlateLimits.MaxLogPage);
        var rows = await RowsAsync(request.Query, scope, request.Before, request.After, limit + 1, ct).ConfigureAwait(false);
        var more = rows.Count > limit;
        var page = more ? rows.Take(limit).ToList() : rows;
        return new LogPage(page, more && request.After is null ? new LogCursor(page[^1].Time.ToUnixTimeMilliseconds(), page[^1].Id) : null);
    }

    /// <summary>The newest <paramref name="limit"/> rows of a query (the export).</summary>
    public Task<IReadOnlyList<PlateRead>> NewestAsync(LogQuery query, LogScope scope, int limit, CancellationToken ct) =>
        RowsAsync(query, scope, null, null, limit, ct);

    private async Task<IReadOnlyList<PlateRead>> RowsAsync(LogQuery query, LogScope scope, LogCursor? before, LogCursor? after, int limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (scope.Cameras is { Count: 0 })
        {
            return [];
        }

        await using var db = await OpenReadAsync(ct).ConfigureAwait(false);
        await using var cmd = db.CreateCommand();
        var where = Where(query, scope, cmd);
        if (before is not null)
        {
            where.Add("(time < @btime OR (time = @btime AND id < @bid))");
            cmd.Parameters.AddWithValue("@btime", before.TimeMs);
            cmd.Parameters.AddWithValue("@bid", before.Id);
        }

        if (after is not null)
        {
            // Stored later (ids grow with every stored row), whatever the read's time.
            where.Add("id > @aid");
            cmd.Parameters.AddWithValue("@aid", after.Id);
        }

        cmd.CommandText = Select(query, cmd, where, $"SELECT {Columns}") + " ORDER BY time DESC, id DESC LIMIT @limit;";
        cmd.Parameters.AddWithValue("@limit", limit);
        var rows = new List<PlateRead>(Math.Min(limit, 1024));
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(ReadRow(reader));
        }

        return rows;
    }

    /// <summary>How many rows a query matches.</summary>
    public async Task<int> CountAsync(LogQuery query, LogScope scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.Cameras is { Count: 0 })
        {
            return 0;
        }

        await using var db = await OpenReadAsync(ct).ConfigureAwait(false);
        await using var cmd = db.CreateCommand();
        var where = Where(query, scope, cmd);
        cmd.CommandText = "SELECT COUNT(*) FROM (" + Select(query, cmd, where, "SELECT id") + ");";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The select of a query: with a search, the plates that start with it and the plates of the list its name found
    /// (<see cref="LogQuery.NamedPlates"/>; a wildcard plate by its list entry), each through an index, as parts of one union;
    /// never a scan of the stored names. Without a search one select.
    /// </summary>
    private static string Select(LogQuery query, SqliteCommand cmd, List<string> where, string select)
    {
        var common = where.Count == 0 ? string.Empty : string.Join(" AND ", where);
        var search = query.Search?.Trim();
        if (string.IsNullOrEmpty(search))
        {
            return select + " FROM reads" + (common.Length == 0 ? string.Empty : " WHERE " + common);
        }

        var prefix = Plates.PlateText.OfRead(search);
        cmd.Parameters.AddWithValue("@plate_from", prefix);
        cmd.Parameters.AddWithValue("@plate_to", prefix + "￿");
        var and = common.Length == 0 ? string.Empty : common + " AND ";
        var parts = new List<string>(3);
        if (prefix.Length > 0)
        {
            parts.Add($"{select} FROM reads WHERE {and}plate >= @plate_from AND plate < @plate_to");
        }

        var named = query.NamedPlates.Where(p => !Plates.PlateText.IsWildcard(p)).ToList();
        var entries = query.NamedPlates.Where(Plates.PlateText.IsWildcard).ToList();
        if (named.Count > 0)
        {
            parts.Add($"{select} FROM reads WHERE {and}plate IN (SELECT value FROM json_each(@named))");
            cmd.Parameters.AddWithValue("@named", System.Text.Json.JsonSerializer.Serialize(named));
        }

        if (entries.Count > 0)
        {
            parts.Add($"{select} FROM reads WHERE {and}list_entry IN (SELECT value FROM json_each(@entries))");
            cmd.Parameters.AddWithValue("@entries", System.Text.Json.JsonSerializer.Serialize(entries));
        }

        return parts.Count == 0
            ? $"{select} FROM reads WHERE 0"
            : "SELECT * FROM (" + string.Join(" UNION ", parts) + ")";
    }

    /// <summary>The conditions of a query and the caller's cameras (gap rows only without value filters).</summary>
    private static List<string> Where(LogQuery query, LogScope scope, SqliteCommand cmd)
    {
        var where = new List<string>();
        if (query.From is { } from)
        {
            where.Add("time >= @from");
            cmd.Parameters.AddWithValue("@from", from.ToUnixTimeMilliseconds());
        }

        if (query.To is { } to)
        {
            where.Add("time < @to");
            cmd.Parameters.AddWithValue("@to", to.ToUnixTimeMilliseconds());
        }

        IReadOnlyCollection<Guid>? cameras = scope.Cameras;
        if (query.CameraIds is { } chosen)
        {
            cameras = cameras is null ? chosen : [.. chosen.Where(cameras.Contains)];
        }

        if (cameras is not null)
        {
            where.Add("camera IN (SELECT value FROM json_each(@cameras))");
            cmd.Parameters.AddWithValue("@cameras", "[" + string.Join(",", cameras.Select(c => "\"" + CameraKey(c) + "\"")) + "]");
        }

        var valueFilter = false;
        if (query.Results is { Count: > 0 } results)
        {
            where.Add("result IN (" + string.Join(",", results.Select(r => ((int)r).ToString(CultureInfo.InvariantCulture))) + ")");
            valueFilter = true;
        }

        if (query.Tags is { Count: > 0 } tags)
        {
            var parts = new List<string>();
            for (var i = 0; i < tags.Count; i++)
            {
                parts.Add($"tags LIKE @tag{i} ESCAPE '\\'");
                cmd.Parameters.AddWithValue($"@tag{i}", "%," + tags[i].Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + ",%");
            }

            where.Add("(" + string.Join(" OR ", parts) + ")");
            valueFilter = true;
        }

        if (query.Area is { } area)
        {
            where.Add("area = @area");
            cmd.Parameters.AddWithValue("@area", area);
            valueFilter = true;
        }

        if (query.Direction is { } direction)
        {
            where.Add("direction = @direction");
            cmd.Parameters.AddWithValue("@direction", (int)direction);
            valueFilter = true;
        }

        valueFilter |= Text(where, cmd, "country", query.Country);
        valueFilter |= Text(where, cmd, "view", query.View);
        valueFilter |= Text(where, cmd, "vehicle_type", query.VehicleType);
        valueFilter |= Text(where, cmd, "vehicle_color", query.VehicleColor);
        valueFilter |= Text(where, cmd, "vehicle_make", query.VehicleMake);
        valueFilter |= Text(where, cmd, "vehicle_model", query.VehicleModel);
        if (valueFilter || !string.IsNullOrWhiteSpace(query.Search))
        {
            where.Add("gap_until IS NULL");
        }

        return where;
    }

    private static bool Text(List<string> where, SqliteCommand cmd, string column, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        where.Add($"{column} = @{column} COLLATE NOCASE");
        cmd.Parameters.AddWithValue("@" + column, value.Trim());
        return true;
    }

    private static PlateRead ReadRow(SqliteDataReader r)
    {
        var gap = !r.IsDBNull(21);
        return new PlateRead
        {
            Id = r.GetInt64(0),
            Time = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(1)),
            CameraId = Guid.TryParse(r.GetString(2), out var camera) ? camera : Guid.Empty,
            Camera = r.GetString(3),
            Plate = r.GetString(4),
            ReadAs = r.IsDBNull(5) ? null : r.GetString(5),
            Result = gap ? ReadResult.Unknown : (ReadResult)r.GetInt32(6),
            Reason = (ReadReason)r.GetInt32(7),
            ReasonTime = r.IsDBNull(8) ? null : DateTime.ParseExact(r.GetString(8), "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture),
            Name = r.GetString(9),
            Tags = r.GetString(10).Split(',', StringSplitOptions.RemoveEmptyEntries),
            Direction = (PlateDirection)r.GetInt32(11),
            Area = r.IsDBNull(12) ? null : r.GetInt32(12),
            Country = r.IsDBNull(13) ? null : r.GetString(13),
            Confidence = r.GetInt32(14),
            View = r.IsDBNull(15) ? null : r.GetString(15),
            VehicleType = r.IsDBNull(16) ? null : r.GetString(16),
            VehicleColor = r.IsDBNull(17) ? null : r.GetString(17),
            VehicleMake = r.IsDBNull(18) ? null : r.GetString(18),
            VehicleModel = r.IsDBNull(19) ? null : r.GetString(19),
            HasPicture = !r.IsDBNull(20),
            Gap = gap,
            Until = gap ? DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(21)) : null,
            State = r.IsDBNull(22) ? null : r.GetString(22),
            VehicleConfidence = r.IsDBNull(23) ? null : r.GetInt32(23),
            ListEntry = r.IsDBNull(24) ? null : r.GetString(24),
        };
    }

    /// <summary>The plate crop of a read the scope sees; null when there is none.</summary>
    public async Task<byte[]?> PictureAsync(long id, LogScope scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await using var db = await OpenReadAsync(ct).ConfigureAwait(false);
        await using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT camera, picture FROM reads WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.IsDBNull(1)
            || !Guid.TryParse(reader.GetString(0), out var camera) || (scope.Cameras is { } cameras && !cameras.Contains(camera)))
        {
            return null;
        }

        var path = PicturePath(reader.GetString(1));
        return path is not null && File.Exists(path) ? await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false) : null;
    }

    /// <summary>The cameras the log has reads of (from the hourly counts).</summary>
    public async Task<IReadOnlyList<Guid>> CamerasAsync(CancellationToken ct)
    {
        await using var db = await OpenReadAsync(ct).ConfigureAwait(false);
        await using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT camera FROM hourly;";
        var cameras = new List<Guid>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (Guid.TryParse(reader.GetString(0), out var id))
            {
                cameras.Add(id);
            }
        }

        return cameras;
    }

    /// <summary>Reads of the scope since <paramref name="since"/> (from the hourly counts: whole hours).</summary>
    public async Task<int> ReadsSinceAsync(DateTimeOffset since, LogScope scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.Cameras is { Count: 0 })
        {
            return 0;
        }

        await using var db = await OpenReadAsync(ct).ConfigureAwait(false);
        await using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(SUM(reads), 0) FROM hourly WHERE hour >= @hour" + CameraCondition(scope.Cameras, cmd) + ";";
        cmd.Parameters.AddWithValue("@hour", since.ToUnixTimeMilliseconds() / HourMs);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static string CameraCondition(IReadOnlyCollection<Guid>? cameras, SqliteCommand cmd)
    {
        if (cameras is null)
        {
            return string.Empty;
        }

        cmd.Parameters.AddWithValue("@cameras", "[" + string.Join(",", cameras.Select(c => "\"" + CameraKey(c) + "\"")) + "]");
        return " AND camera IN (SELECT value FROM json_each(@cameras))";
    }

    /// <summary>
    /// The Stats tab from the hourly counts (docs/spec/license-plate-center.md "Stats"): the top cards (the last hour, 24 hours
    /// and 7 days exactly: whole hours from the counts plus the reads of the first, partial hour; Total: everything the log
    /// holds) and for the chosen time the series of <see cref="StatsSeries"/>.
    /// </summary>
    public async Task<StatsReply> StatsAsync(StatsRequest request, LogScope scope, Func<Guid, string> cameraName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(cameraName);
        IReadOnlyCollection<Guid>? cameras = scope.Cameras;
        if (request.CameraIds is { } chosen)
        {
            cameras = cameras is null ? chosen : [.. chosen.Where(cameras.Contains)];
        }

        var zone = StatsSeries.Zone(request.TimeZone, request.UtcOffsetMinutes);
        if (cameras is { Count: 0 })
        {
            return StatsSeries.Build([], request.From, request.To, request.Step, zone, cameraName, new ReadTotals(0, 0, 0, 0));
        }

        var (start, steps) = StatsSeries.Frame(request.From, request.To, request.Step, zone);
        var fromHour = start.ToUnixTimeMilliseconds() / HourMs;
        var toHour = request.Step == StatsStep.Day
            ? start.AddDays(steps + 1).ToUnixTimeMilliseconds() / HourMs // a day of a zone change has 23 or 25 hours: StepOf drops the rest
            : fromHour + steps;
        await using var db = await OpenReadAsync(ct).ConfigureAwait(false);
        var rows = new List<HourRow>();
        await using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT hour, camera, result, SUM(reads), SUM(confidence), SUM(vehicle), SUM(vehicles) FROM hourly WHERE hour >= @from AND hour < @to"
                + CameraCondition(cameras, cmd) + " GROUP BY hour, camera, result;";
            cmd.Parameters.AddWithValue("@from", fromHour);
            cmd.Parameters.AddWithValue("@to", toHour);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                rows.Add(new HourRow(reader.GetInt64(0), Guid.TryParse(reader.GetString(1), out var id) ? id : Guid.Empty, reader.GetInt32(2), reader.GetInt32(3),
                    reader.GetInt64(4), reader.GetInt64(5), reader.GetInt32(6)));
            }
        }

        var now = _time.GetUtcNow();
        var totals = new ReadTotals(
            await CountSinceAsync(db, cameras, now - TimeSpan.FromHours(1), ct).ConfigureAwait(false),
            await CountSinceAsync(db, cameras, now - TimeSpan.FromHours(24), ct).ConfigureAwait(false),
            await CountSinceAsync(db, cameras, now - TimeSpan.FromDays(7), ct).ConfigureAwait(false),
            await CountSinceAsync(db, cameras, null, ct).ConfigureAwait(false));
        return StatsSeries.Build(rows, request.From, request.To, request.Step, zone, cameraName, totals);
    }

    /// <summary>The reads since <paramref name="since"/> (null: all): whole hours from the counts, the partial first hour from the reads.</summary>
    private static async Task<long> CountSinceAsync(SqliteConnection db, IReadOnlyCollection<Guid>? cameras, DateTimeOffset? since, CancellationToken ct)
    {
        var sinceMs = since?.ToUnixTimeMilliseconds() ?? 0;
        var fullHour = (sinceMs + HourMs - 1) / HourMs;
        long count;
        await using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT COALESCE(SUM(reads), 0) FROM hourly WHERE hour >= @hour" + CameraCondition(cameras, cmd) + ";";
            cmd.Parameters.AddWithValue("@hour", fullHour);
            count = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }

        if (since is null || fullHour * HourMs == sinceMs)
        {
            return count;
        }

        await using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM reads WHERE time >= @from AND time < @to AND gap_until IS NULL" + CameraCondition(cameras, cmd) + ";";
            cmd.Parameters.AddWithValue("@from", sinceMs);
            cmd.Parameters.AddWithValue("@to", fullHour * HourMs);
            count += Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }

        return count;
    }

    // ---- helpers ----

    internal static string CameraKey(Guid camera) => camera.ToString("D");

    /// <summary>",Garage A,Staff," (searchable with LIKE '%,name,%'); empty without tags.</summary>
    internal static string TagsKey(IReadOnlyList<string> tags)
    {
        if (tags.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(",");
        foreach (var tag in tags)
        {
            sb.Append(tag.Replace(",", " ", StringComparison.Ordinal)).Append(',');
        }

        return sb.ToString();
    }

    private static async Task ExecuteAsync(SqliteConnection db, string sql, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<object?> ScalarAsync(SqliteConnection db, string sql, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _writes.Writer.TryComplete();
        try
        {
            await _writer.ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Closing: the writer's own failures were reported.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        using (var db = new SqliteConnection(_connection))
        {
            SqliteConnection.ClearPool(db);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "License Plate Center: the log in {Folder} cannot be written")]
    private static partial void LogWriteFailed(ILogger logger, string folder, Exception ex);
}
