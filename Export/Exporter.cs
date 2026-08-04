using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Cccxa.Export;

/// <summary>
/// ייצוא פשוט של כל הנתונים לקבצי JSONL מקומיים, מוכן להזנה ל-AI מקומי.
/// לא דורש הרשמה, שרתים או תלות חיצונית - רק קבצים בדיסק.
///
/// נוצרים:
///   events.jsonl       - כל האירועים (גלישה, תוכנות, קבצים, חלונות).
///   browsing.jsonl     - רק אירועי גלישה (browser_url + history_visit), לשיתוף נוח.
///   screenshots.jsonl  - אינדקס צילומי המסך (חותמת זמן + נתיב קובץ).
/// </summary>
public static class Exporter
{
    /// <summary>
    /// ייצוא כל המשתמשים: סורק תת-תיקייה לכל משתמש תחת dataRoot ומייצא כל אחת ל-outDir\&lt;user&gt;.
    /// מיועד למנהל שרוצה לאסוף את הנתונים של כל המשתמשים במחשב במכה אחת.
    /// </summary>
    public static int RunAll(string dataRoot, string outDir)
    {
        if (!Directory.Exists(dataRoot))
        {
            Console.Error.WriteLine($"[cccxa] לא נמצאה תיקיית נתונים: {dataRoot}");
            return 1;
        }

        var userDirs = Directory.EnumerateDirectories(dataRoot)
            .Where(d => File.Exists(Path.Combine(d, "cccxa.db")))
            .ToList();

        if (userDirs.Count == 0)
        {
            Console.Error.WriteLine($"[cccxa] לא נמצאו נתוני משתמשים תחת: {dataRoot}");
            return 1;
        }

        Directory.CreateDirectory(outDir);
        int failures = 0;
        foreach (var dir in userDirs)
        {
            var user = Path.GetFileName(dir);
            Console.WriteLine($"[cccxa] מייצא משתמש: {user}");
            try { Run(dir, Path.Combine(outDir, user)); }
            catch (Exception ex) { failures++; Console.Error.WriteLine($"  שגיאה: {ex.Message}"); }
        }
        return failures == 0 ? 0 : 2;
    }

    public static int Run(string dbRoot, string outDir)
    {
        var dbPath = Path.Combine(dbRoot, "cccxa.db");
        if (!File.Exists(dbPath))
        {
            Console.Error.WriteLine($"[cccxa] לא נמצא מסד נתונים: {dbPath}");
            return 1;
        }

        Directory.CreateDirectory(outDir);

        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Cache=Shared");
        conn.Open();

        int events = ExportQuery(conn,
            "SELECT ts,type,app,title,url,detail FROM events ORDER BY ts ASC",
            Path.Combine(outDir, "events.jsonl"),
            r => new
            {
                ts = r.GetString(0),
                type = r.GetString(1),
                app = Nullable(r, 2),
                title = Nullable(r, 3),
                url = Nullable(r, 4),
                detail = Nullable(r, 5)
            });

        int browsing = ExportQuery(conn,
            "SELECT ts,type,app,title,url,detail FROM events " +
            "WHERE type IN ('browser_url','history_visit') AND url IS NOT NULL ORDER BY ts ASC",
            Path.Combine(outDir, "browsing.jsonl"),
            r => new
            {
                ts = r.GetString(0),
                source = r.GetString(1),
                browser = Nullable(r, 2),
                title = Nullable(r, 3),
                url = Nullable(r, 4),
                visited_at = Nullable(r, 5)
            });

        int shots = ExportQuery(conn,
            "SELECT ts,path,width,height,bytes FROM screenshots ORDER BY ts ASC",
            Path.Combine(outDir, "screenshots.jsonl"),
            r => new
            {
                ts = r.GetString(0),
                path = r.GetString(1),
                width = r.GetInt32(2),
                height = r.GetInt32(3),
                bytes = r.GetInt64(4)
            });

        Console.WriteLine($"[cccxa] יוצא ל: {outDir}");
        Console.WriteLine($"  events.jsonl      : {events}");
        Console.WriteLine($"  browsing.jsonl    : {browsing}");
        Console.WriteLine($"  screenshots.jsonl : {shots}");
        return 0;
    }

    private static int ExportQuery(SqliteConnection conn, string sql, string outFile,
        Func<SqliteDataReader, object> project)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();

        using var sw = new StreamWriter(outFile, false, new UTF8Encoding(false));
        int count = 0;
        while (r.Read())
        {
            sw.WriteLine(JsonSerializer.Serialize(project(r)));
            count++;
        }
        return count;
    }

    private static string? Nullable(SqliteDataReader r, int i)
        => r.IsDBNull(i) ? null : r.GetString(i);
}
