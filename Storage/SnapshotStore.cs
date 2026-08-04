using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace Cccxa.Storage;

/// <summary>
/// שכבת האחסון המקומית. כל האירועים נכתבים ל-SQLite (cccxa.db) וצילומי המסך כקבצי JPEG.
///
/// כל הכתיבות עוברות דרך תור (Channel) עם כותב יחיד, כך שריבוי אוספים (collectors)
/// לא מתנגשים על החיבור ל-DB ואין נעילות. זה גם שומר על עומס נמוך.
///
/// סכימה נוחה ל-AI מקומי: טבלת events אחת עם עמודות type/app/title/url/detail,
/// וטבלת screenshots המצביעה על הקבצים בדיסק.
/// </summary>
public sealed class SnapshotStore : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly Channel<Action<SqliteConnection>> _channel;
    private readonly Task _writerTask;

    public string Root { get; }
    public string ScreenshotDir { get; }

    public SnapshotStore(string root)
    {
        Root = root;
        Directory.CreateDirectory(Root);
        ScreenshotDir = Path.Combine(Root, "screenshots");
        Directory.CreateDirectory(ScreenshotDir);

        _conn = new SqliteConnection($"Data Source={Path.Combine(Root, "cccxa.db")}");
        _conn.Open();

        Exec("PRAGMA journal_mode=WAL;");
        Exec("PRAGMA synchronous=NORMAL;");
        Exec(@"CREATE TABLE IF NOT EXISTS events(
                 id     INTEGER PRIMARY KEY AUTOINCREMENT,
                 ts     TEXT NOT NULL,
                 type   TEXT NOT NULL,
                 app    TEXT,
                 title  TEXT,
                 url    TEXT,
                 detail TEXT);");
        Exec(@"CREATE TABLE IF NOT EXISTS screenshots(
                 id     INTEGER PRIMARY KEY AUTOINCREMENT,
                 ts     TEXT NOT NULL,
                 path   TEXT NOT NULL,
                 width  INTEGER,
                 height INTEGER,
                 bytes  INTEGER);");
        Exec("CREATE INDEX IF NOT EXISTS ix_events_ts ON events(ts);");
        Exec("CREATE INDEX IF NOT EXISTS ix_events_type ON events(type);");

        _channel = Channel.CreateUnbounded<Action<SqliteConnection>>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        _writerTask = Task.Run(WriterLoopAsync);
    }

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private async Task WriterLoopAsync()
    {
        await foreach (var action in _channel.Reader.ReadAllAsync())
        {
            try { action(_conn); }
            catch { /* לא מפילים את הכותב בגלל כתיבה בודדת שנכשלה */ }
        }
    }

    public void AddEvent(string type, string? app, string? title, string? url, string? detail)
    {
        var ts = DateTimeOffset.UtcNow.ToString("o");
        _channel.Writer.TryWrite(conn =>
        {
            using var c = conn.CreateCommand();
            c.CommandText =
                "INSERT INTO events(ts,type,app,title,url,detail) VALUES($ts,$type,$app,$title,$url,$detail)";
            c.Parameters.AddWithValue("$ts", ts);
            c.Parameters.AddWithValue("$type", type);
            c.Parameters.AddWithValue("$app", (object?)app ?? DBNull.Value);
            c.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
            c.Parameters.AddWithValue("$url", (object?)url ?? DBNull.Value);
            c.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);
            c.ExecuteNonQuery();
        });
    }

    public void AddScreenshot(DateTimeOffset ts, string path, int width, int height, long bytes)
    {
        var s = ts.ToUniversalTime().ToString("o");
        _channel.Writer.TryWrite(conn =>
        {
            using var c = conn.CreateCommand();
            c.CommandText =
                "INSERT INTO screenshots(ts,path,width,height,bytes) VALUES($ts,$path,$w,$h,$b)";
            c.Parameters.AddWithValue("$ts", s);
            c.Parameters.AddWithValue("$path", path);
            c.Parameters.AddWithValue("$w", width);
            c.Parameters.AddWithValue("$h", height);
            c.Parameters.AddWithValue("$b", bytes);
            c.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// מוחק נתונים ישנים: צילומי מסך מעל screenshotDays וקבצי התמונה שלהם, ואירועים מעל dataDays.
    /// ערך 0 עבור קטגוריה = לא מוחקים אותה. הכל רץ על תור הכתיבה היחיד, כך שאין התנגשות עם כתיבות.
    /// </summary>
    public void PurgeOld(int screenshotDays, int dataDays)
    {
        _channel.Writer.TryWrite(conn =>
        {
            if (screenshotDays > 0)
            {
                var cutoff = DateTimeOffset.UtcNow.AddDays(-screenshotDays).ToString("o");

                // אוספים את נתיבי הקבצים למחיקה, מוחקים מהדיסק, ואז מוחקים מה-DB.
                var paths = new List<string>();
                using (var c = conn.CreateCommand())
                {
                    c.CommandText = "SELECT path FROM screenshots WHERE ts < $c";
                    c.Parameters.AddWithValue("$c", cutoff);
                    using var r = c.ExecuteReader();
                    while (r.Read())
                        if (!r.IsDBNull(0)) paths.Add(r.GetString(0));
                }
                foreach (var p in paths)
                    try { if (File.Exists(p)) File.Delete(p); } catch { }

                using (var c = conn.CreateCommand())
                {
                    c.CommandText = "DELETE FROM screenshots WHERE ts < $c";
                    c.Parameters.AddWithValue("$c", cutoff);
                    c.ExecuteNonQuery();
                }

                RemoveEmptyDayFolders();
            }

            if (dataDays > 0)
            {
                var cutoff = DateTimeOffset.UtcNow.AddDays(-dataDays).ToString("o");
                using var c = conn.CreateCommand();
                c.CommandText = "DELETE FROM events WHERE ts < $c";
                c.Parameters.AddWithValue("$c", cutoff);
                c.ExecuteNonQuery();
            }

            // מכווץ את קובץ ה-WAL כדי לשחרר מקום בדיסק אחרי מחיקות.
            try
            {
                using var c = conn.CreateCommand();
                c.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                c.ExecuteNonQuery();
            }
            catch { }
        });
    }

    private void RemoveEmptyDayFolders()
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(ScreenshotDir))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                        Directory.Delete(dir);
                }
                catch { }
            }
        }
        catch { }
    }

    public void Dispose()
    {
        try { _channel.Writer.Complete(); } catch { }
        try { _writerTask.Wait(TimeSpan.FromSeconds(3)); } catch { }
        try { _conn.Dispose(); } catch { }
    }
}
