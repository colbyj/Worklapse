using Microsoft.Data.Sqlite;

namespace Worklapse.Core.Storage;

/// <summary>
/// Opens the activity database in a data folder and keeps its schema current. Each instance wraps one
/// connection and must only be used from one thread at a time; the recorder and the UI open their own.
/// </summary>
public sealed class WorklapseDatabase : IDisposable
{
    public const string FileName = "worklapse.db";
    private const int SchemaVersion = 9;

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
        // secure_delete: what is deleted (window titles, app names of deleted captures) is overwritten with
        // zeros instead of lingering in free space, where anyone opening the file could still read it.
        db.Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA secure_delete=ON;");
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

        if (version < 4)
        {
            // Removing deleted captures' video from disk. Segment frames can be dropped once no capture refers
            // to them; the batch encoder then removes or rebuilds their segment.
            using var transaction = BeginTransaction();
            Execute("""
                -- Finds who still refers to a segment frame (also what the foreign key check on deleting one uses).
                CREATE INDEX ix_frames_segment ON frames (segment_id, frame_index);

                -- Finds the apps left without captures after a delete.
                CREATE INDEX ix_captures_app ON captures (app_id);

                -- Segments that may hold frames nothing refers to any more. generation goes up each time a
                -- segment is queued again, so a check that started before a newer delete doesn't clear it.
                CREATE TABLE reclaim_queue (
                    segment_id  INTEGER PRIMARY KEY REFERENCES segments (id) ON DELETE CASCADE,
                    generation  INTEGER NOT NULL DEFAULT 0
                );

                -- Segments whose files are to be deleted: one no longer in segments, or one being written that
                -- isn't yet. Recorded in the same transaction that lets go of the files (or before writing them),
                -- so a crash in between leaves a note of what to clean up rather than an orphan. The path is the
                -- segment's (relative, ".ivf"); its ".staging" and ".tmp" files go too.
                CREATE TABLE dead_segment_files (
                    path  TEXT PRIMARY KEY
                );

                -- Deletes made before this version left their frames behind.
                INSERT INTO reclaim_queue (segment_id)
                SELECT DISTINCT sf.segment_id FROM segment_frames sf
                WHERE NOT EXISTS (SELECT 1 FROM frames f WHERE f.segment_id = sf.segment_id AND f.frame_index = sf.frame_index);
                """);
            Execute("PRAGMA user_version = 4;");
            transaction.Commit();
        }

        if (version < 5)
        {
            // Websites, from the browser's address bar at capture time (domain only). A capture with a site is
            // productive if the site is; otherwise it follows its app. Older captures have no site.
            using var transaction = BeginTransaction();
            Execute("""
                CREATE TABLE sites (
                    id          INTEGER PRIMARY KEY,
                    domain      TEXT NOT NULL UNIQUE COLLATE NOCASE,
                    productive  INTEGER NOT NULL DEFAULT 0
                );
                ALTER TABLE captures ADD COLUMN site_id INTEGER REFERENCES sites (id);
                CREATE INDEX ix_captures_site ON captures (site_id) WHERE site_id IS NOT NULL;
                """);
            Execute("PRAGMA user_version = 5;");
            transaction.Commit();
        }

        if (version < 6)
        {
            // Which monitors were attached at each capture, captured or not, so Day review shows the arrangement
            // as it was. Plugging monitors in or out adds monitors rows, and without this every monitor of the day
            // was drawn at once, on top of each other. Older captures have none (Day review works it out).
            using var transaction = BeginTransaction();
            Execute("""
                CREATE TABLE layouts (
                    id        INTEGER PRIMARY KEY,
                    monitors  TEXT NOT NULL UNIQUE      -- monitor ids, ascending, comma-separated
                );
                ALTER TABLE captures ADD COLUMN layout_id INTEGER REFERENCES layouts (id);
                """);
            Execute("PRAGMA user_version = 6;");
            transaction.Commit();
        }

        if (version < 7)
        {
            // Recording as images (JPEG or WebP) instead of video: each image is a segment with one frame, in its own
            // file. Staged means the recorder may still show it again for a monitor that hasn't changed.
            using var transaction = BeginTransaction();
            Execute("""
                ALTER TABLE segments ADD COLUMN format INTEGER NOT NULL DEFAULT 0;   -- 0 = AV1 video, 1 = JPEG, 2 = WebP

                -- Finds a segment by its file. Images make a segment per changed capture, too many to search through.
                CREATE INDEX ix_segments_path ON segments (path);
                """);
            Execute("PRAGMA user_version = 7;");
            transaction.Commit();
        }

        if (version < 8)
        {
            // Apps (by exe name) and sites nothing is recorded for while they have the focus: no capture at all,
            // so that time shows as not recorded.
            using var transaction = BeginTransaction();
            Execute("""
                ALTER TABLE apps ADD COLUMN excluded INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE sites ADD COLUMN excluded INTEGER NOT NULL DEFAULT 0;
                """);
            Execute("PRAGMA user_version = 8;");
            transaction.Commit();
        }

        if (version < 9)
        {
            // Projects. An app or site linked to a project marks its active time with it; ranges set by hand override
            // the links (a NULL project there means "no project"). Notes belong to a project and a moment, and are shown
            // on the block of that project around it. The timesheet holds the time to bill per project per day.
            using var transaction = BeginTransaction();
            Execute("""
                CREATE TABLE categories (
                    id     INTEGER PRIMARY KEY,
                    name   TEXT NOT NULL,
                    color  INTEGER NOT NULL                         -- 0xRRGGBB
                );

                CREATE TABLE projects (
                    id           INTEGER PRIMARY KEY,
                    name         TEXT NOT NULL,
                    category_id  INTEGER REFERENCES categories (id) ON DELETE SET NULL,
                    color        INTEGER,                           -- 0xRRGGBB; NULL = the category's
                    archived     INTEGER NOT NULL DEFAULT 0
                );

                ALTER TABLE apps ADD COLUMN project_id INTEGER REFERENCES projects (id) ON DELETE SET NULL;
                ALTER TABLE sites ADD COLUMN project_id INTEGER REFERENCES projects (id) ON DELETE SET NULL;

                -- Never overlap; adjacent ranges with the same project are merged.
                CREATE TABLE project_spans (
                    id          INTEGER PRIMARY KEY,
                    start_ms    INTEGER NOT NULL,                   -- UTC, like captures.ts_ms
                    end_ms      INTEGER NOT NULL,
                    project_id  INTEGER REFERENCES projects (id) ON DELETE CASCADE
                );
                CREATE INDEX ix_project_spans_start ON project_spans (start_ms);

                CREATE TABLE notes (
                    id          INTEGER PRIMARY KEY,
                    project_id  INTEGER NOT NULL REFERENCES projects (id) ON DELETE CASCADE,
                    ts_ms       INTEGER NOT NULL,
                    text        TEXT NOT NULL
                );
                CREATE INDEX ix_notes_ts ON notes (ts_ms);

                CREATE TABLE day_notes (
                    day   TEXT PRIMARY KEY,                         -- local date, yyyy-MM-dd
                    text  TEXT NOT NULL
                );

                CREATE TABLE timesheet (
                    day         TEXT NOT NULL,                      -- local date, yyyy-MM-dd
                    project_id  INTEGER NOT NULL REFERENCES projects (id) ON DELETE CASCADE,
                    logged_min  INTEGER NOT NULL,
                    PRIMARY KEY (day, project_id)
                ) WITHOUT ROWID;
                """);
            Execute("PRAGMA user_version = 9;");
            transaction.Commit();
        }
    }
}
