using Microsoft.Data.Sqlite;

namespace Worklapse.Core.Storage;

/// <summary>
/// Opens the activity database in a data folder and keeps its schema current. Each instance wraps one
/// connection and must only be used from one thread at a time; the recorder and the UI open their own.
/// </summary>
public sealed class WorklapseDatabase : IDisposable
{
    public const string FileName = "worklapse.db";
    private const int SchemaVersion = 3;

    private WorklapseDatabase(string dataFolder, SqliteConnection connection)
    {
        DataFolder = dataFolder;
        Connection = connection;
    }

    public string DataFolder { get; }
    public SqliteConnection Connection { get; }

    public static WorklapseDatabase Open(string dataFolder)
    {
        Directory.CreateDirectory(dataFolder);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dataFolder, FileName),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();

        var db = new WorklapseDatabase(dataFolder, connection);
        db.Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;");
        db.Migrate();
        return db;
    }

    public SqliteCommand Command(string sql)
    {
        var command = Connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }

    public int Execute(string sql)
    {
        using var command = Command(sql);
        return command.ExecuteNonQuery();
    }

    public SqliteTransaction BeginTransaction() => Connection.BeginTransaction();

    public void Dispose() => Connection.Dispose();

    private void Migrate()
    {
        using var versionCommand = Command("PRAGMA user_version;");
        var version = Convert.ToInt32(versionCommand.ExecuteScalar());
        if (version > SchemaVersion)
            throw new InvalidOperationException(
                $"The database in '{DataFolder}' was created by a newer version of Worklapse (schema {version}).");

        if (version < 1)
        {
            using var transaction = BeginTransaction();
            Execute("""
                CREATE TABLE apps (
                    id        INTEGER PRIMARY KEY,
                    exe_name  TEXT NOT NULL COLLATE NOCASE,
                    exe_path  TEXT COLLATE NOCASE,
                    UNIQUE (exe_name, exe_path)
                );

                CREATE TABLE monitors (
                    id          INTEGER PRIMARY KEY,
                    device_name TEXT NOT NULL,
                    x           INTEGER NOT NULL,
                    y           INTEGER NOT NULL,
                    width       INTEGER NOT NULL,
                    height      INTEGER NOT NULL,
                    is_primary  INTEGER NOT NULL,
                    UNIQUE (device_name, x, y, width, height, is_primary)
                );

                -- One row per capture tick. Idle ticks have no frames; time with no rows was not recorded.
                CREATE TABLE captures (
                    id              INTEGER PRIMARY KEY,
                    ts_ms           INTEGER NOT NULL,          -- UTC, milliseconds since the Unix epoch
                    utc_offset_min  INTEGER NOT NULL,          -- local offset at capture time
                    kind            INTEGER NOT NULL,          -- 0 = active, 1 = idle
                    app_id          INTEGER REFERENCES apps (id),
                    window_title    TEXT,
                    window_x        INTEGER,
                    window_y        INTEGER,
                    window_width    INTEGER,
                    window_height   INTEGER,
                    pending_delete  INTEGER NOT NULL DEFAULT 0
                );
                CREATE INDEX ix_captures_ts ON captures (ts_ms);

                -- One AV1 stream per file; each segment starts with a key frame.
                CREATE TABLE segments (
                    id          INTEGER PRIMARY KEY,
                    monitor_id  INTEGER NOT NULL REFERENCES monitors (id),
                    path        TEXT NOT NULL,                 -- relative to the data folder
                    width       INTEGER NOT NULL,
                    height      INTEGER NOT NULL,
                    quality     INTEGER NOT NULL,
                    created_ms  INTEGER NOT NULL
                );

                -- Where each encoded frame sits inside its segment file.
                CREATE TABLE segment_frames (
                    segment_id   INTEGER NOT NULL REFERENCES segments (id),
                    frame_index  INTEGER NOT NULL,
                    file_offset  INTEGER NOT NULL,             -- start of the frame's payload
                    length       INTEGER NOT NULL,
                    is_key       INTEGER NOT NULL,
                    PRIMARY KEY (segment_id, frame_index)
                ) WITHOUT ROWID;

                -- Which encoded frame shows each monitor at each capture. Captures where a monitor did not
                -- change point at the earlier frame instead of storing a new one.
                CREATE TABLE frames (
                    capture_id   INTEGER NOT NULL REFERENCES captures (id) ON DELETE CASCADE,
                    monitor_id   INTEGER NOT NULL REFERENCES monitors (id),
                    segment_id   INTEGER NOT NULL,
                    frame_index  INTEGER NOT NULL,
                    PRIMARY KEY (capture_id, monitor_id),
                    FOREIGN KEY (segment_id, frame_index) REFERENCES segment_frames (segment_id, frame_index)
                ) WITHOUT ROWID;
                """);
            Execute("PRAGMA user_version = 1;");
            transaction.Commit();
        }

        if (version < 2)
        {
            // Segments are first written to a staging file and encoded to AV1 when complete. While staged,
            // segment_frames point into the staging file (path + ".staging").
            using var transaction = BeginTransaction();
            Execute("""
                ALTER TABLE segments ADD COLUMN state INTEGER NOT NULL DEFAULT 1;   -- 0 = staged, 1 = encoded
                CREATE INDEX ix_segments_state ON segments (state) WHERE state = 0;
                """);
            Execute("PRAGMA user_version = 2;");
            transaction.Commit();
        }

        if (version < 3)
        {
            using var transaction = BeginTransaction();
            Execute("ALTER TABLE apps ADD COLUMN productive INTEGER NOT NULL DEFAULT 0;");
            Execute("PRAGMA user_version = 3;");
            transaction.Commit();
        }
    }
}
