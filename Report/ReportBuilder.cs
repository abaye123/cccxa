using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Cccxa.Report;

/// <summary>
/// בונה דשבורד HTML עצמאי (ללא שרת, ללא אינטרנט) מתוך מסד הנתונים המקומי,
/// עם כל הנתונים מוטמעים ישירות בקובץ - כך שהוא מציג הכל אוטומטית, ללא ייבוא.
/// תבנית ה-HTML מוטמעת ב-exe כמשאב, כך שאין תלות בקבצים חיצוניים.
/// </summary>
public static class ReportBuilder
{
    private const int TopN = 15;
    // מגבלות בטיחות גבוהות - מציגים למעשה את כל ההיסטוריה, אך מונעים קובץ ענק בלתי סביר.
    private const int MaxRows = 200_000;
    private const int MaxShots = 20_000;

    // camelCase כדי להתאים למפתחות ש-JS בתבנית מצפה להם.
    // ברירת המחדל של המקודד ממירה < > & -> בטוח להטמעה בתוך תגית script.
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static int Build(string dbRoot, string outHtml, string? configPath, bool open)
    {
        var user = new UserReport { Name = Path.GetFileName(dbRoot.TrimEnd('\\', '/')) };
        FillUser(dbRoot, user);
        WriteFile(outHtml, RenderHtml(new Report { Users = { user } }, configPath));
        Console.WriteLine($"[cccxa] נוצר דשבורד: {outHtml}");
        if (open) TryOpen(outHtml);
        return 0;
    }

    public static int BuildAll(string dataRoot, string outHtml, string? configPath, bool open)
    {
        var html = BuildHtmlAll(dataRoot, configPath, out int count);
        WriteFile(outHtml, html);
        Console.WriteLine($"[cccxa] נוצר דשבורד ({count} משתמשים): {outHtml}");
        if (open) TryOpen(outHtml);
        return 0;
    }

    /// <summary>בונה את ה-HTML של כל המשתמשים ומחזיר אותו כמחרוזת (משמש את השרת המקומי).</summary>
    public static string BuildHtmlAll(string dataRoot, string? configPath)
        => BuildHtmlAll(dataRoot, configPath, out _);

    private static string BuildHtmlAll(string dataRoot, string? configPath, out int userCount)
    {
        var report = new Report();
        if (Directory.Exists(dataRoot))
        {
            foreach (var dir in Directory.EnumerateDirectories(dataRoot))
            {
                if (!File.Exists(Path.Combine(dir, "cccxa.db"))) continue;
                var user = new UserReport { Name = Path.GetFileName(dir) };
                try { FillUser(dir, user); } catch { }
                report.Users.Add(user);
            }
        }
        userCount = report.Users.Count;
        return RenderHtml(report, configPath);
    }

    private static void FillUser(string dbRoot, UserReport user)
    {
        var dbPath = Path.Combine(dbRoot, "cccxa.db");
        if (!File.Exists(dbPath)) return;

        using var conn = OpenReadOnly(dbPath);

        // מעבר יחיד על כל האירועים: ספירה, פילוח לפי שעה מקומית, וימי פעילות.
        var days = new HashSet<string>();
        int total = 0;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT ts FROM events";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                total++;
                if (r.IsDBNull(0)) continue;
                if (DateTimeOffset.TryParse(r.GetString(0), out var dto))
                {
                    var local = dto.ToLocalTime();
                    user.ByHour[local.Hour]++;
                    days.Add(local.ToString("yyyy-MM-dd"));
                }
            }
        }
        user.Totals.Events = total;
        user.Totals.Days = days.Count;

        user.Totals.Screenshots = ScalarInt(conn, "SELECT COUNT(*) FROM screenshots");
        user.Totals.Urls  = ScalarInt(conn, "SELECT COUNT(DISTINCT url) FROM events WHERE url IS NOT NULL AND url<>''");
        user.Totals.Apps  = ScalarInt(conn, "SELECT COUNT(DISTINCT app) FROM events WHERE type='foreground' AND app IS NOT NULL AND app<>''");
        user.Totals.Files = ScalarInt(conn, "SELECT COUNT(*) FROM events WHERE type='recent_file'");

        user.BrowsingTotal = ScalarInt(conn, "SELECT COUNT(*) FROM events WHERE url IS NOT NULL AND url<>''");
        user.ProgramsTotal = ScalarInt(conn, "SELECT COUNT(*) FROM events WHERE type='foreground' AND app IS NOT NULL AND app<>''");

        // אתרים מובילים - מקבצים לפי דומיין.
        var byHost = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT url, COUNT(*) c FROM events WHERE url IS NOT NULL AND url<>'' GROUP BY url ORDER BY c DESC LIMIT 5000";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var host = HostOf(r.GetString(0));
                int c = r.GetInt32(1);
                byHost[host] = byHost.TryGetValue(host, out var e) ? e + c : c;
            }
        }
        user.TopSites = byHost.OrderByDescending(kv => kv.Value).Take(TopN)
            .Select(kv => new LabelCount { Label = kv.Key, Count = kv.Value }).ToList();

        user.TopApps = QueryLabelCounts(conn,
            "SELECT app, COUNT(*) c FROM events WHERE type='foreground' AND app IS NOT NULL AND app<>'' GROUP BY app ORDER BY c DESC LIMIT " + TopN);

        // היסטוריית גלישה מלאה (browser_url + history_visit), כולל דפדפן וסימון בסתר.
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT ts,type,app,title,url,detail FROM events WHERE url IS NOT NULL AND url<>'' ORDER BY ts DESC LIMIT " + MaxRows;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var type = r.IsDBNull(1) ? "" : r.GetString(1);
                var app = r.IsDBNull(2) ? "" : r.GetString(2);
                var detail = r.IsDBNull(5) ? "" : r.GetString(5);

                // history_visit שומר מקור כמו "chrome:Default"; browser_url שומר שם תהליך כמו "chrome".
                string browser = type == "history_visit" && app.Contains(':')
                    ? app[..app.IndexOf(':')] : app;
                bool incognito = type == "browser_url" &&
                    string.Equals(detail, "incognito", StringComparison.OrdinalIgnoreCase);

                user.Browsing.Add(new BrowseRow
                {
                    Ts = r.GetString(0),
                    Browser = browser,
                    Incognito = incognito,
                    Title = r.IsDBNull(3) ? null : r.GetString(3),
                    Url = r.GetString(4)
                });
            }
        }

        // היסטוריית שימוש בתוכנות (חלון פעיל).
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT ts,app,title FROM events WHERE type='foreground' AND app IS NOT NULL AND app<>'' ORDER BY ts DESC LIMIT " + MaxRows;
            using var r = cmd.ExecuteReader();
            while (r.Read())
                user.Programs.Add(new ProgRow
                {
                    Ts = r.GetString(0),
                    App = r.IsDBNull(1) ? null : r.GetString(1),
                    Title = r.IsDBNull(2) ? null : r.GetString(2)
                });
        }

        // קבצים ומסמכים.
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT ts,title,detail FROM events WHERE type='recent_file' ORDER BY ts DESC LIMIT " + MaxRows;
            using var r = cmd.ExecuteReader();
            while (r.Read())
                user.Files.Add(new FileRow
                {
                    Ts = r.GetString(0),
                    Name = r.IsDBNull(1) ? null : r.GetString(1),
                    Target = r.IsDBNull(2) ? null : r.GetString(2)
                });
        }

        // צילומי מסך.
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT ts,path FROM screenshots ORDER BY ts DESC LIMIT " + MaxShots;
            using var r = cmd.ExecuteReader();
            while (r.Read())
                user.Screenshots.Add(new ShotRow { Ts = r.GetString(0), Path = r.GetString(1) });
        }
    }

    private static SqliteConnection OpenReadOnly(string dbPath)
    {
        try
        {
            var c = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Cache=Shared");
            c.Open();
            return c;
        }
        catch
        {
            var c = new SqliteConnection($"Data Source={dbPath}");
            c.Open();
            return c;
        }
    }

    private static int ScalarInt(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var o = cmd.ExecuteScalar();
        return o is null || o is DBNull ? 0 : Convert.ToInt32(o);
    }

    private static List<LabelCount> QueryLabelCounts(SqliteConnection conn, string sql)
    {
        var list = new List<LabelCount>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new LabelCount { Label = r.IsDBNull(0) ? "" : r.GetString(0), Count = r.GetInt32(1) });
        return list;
    }

    private static string HostOf(string url)
    {
        try
        {
            var u = url.Trim();
            if (!u.Contains("://")) u = "http://" + u;
            if (Uri.TryCreate(u, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
                return uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
        }
        catch { }
        return url;
    }

    private static string RenderHtml(Report report, string? configPath)
    {
        report.GeneratedAt = DateTimeOffset.Now.ToString("o");
        var json = JsonSerializer.Serialize(report, JsonOpts);
        var settings = LoadSettingsJson(configPath);
        return LoadTemplate()
            .Replace("__CCCXA_DATA__", json)
            .Replace("__CCCXA_SETTINGS__", settings);
    }

    private static void WriteFile(string outHtml, string html)
    {
        var dir = Path.GetDirectoryName(outHtml);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(outHtml, html, new System.Text.UTF8Encoding(false));
    }

    /// <summary>
    /// מחזיר את תוכן מקטע "Cccxa" מקובץ ההגדרות כ-JSON, להטמעה בטופס ההגדרות.
    /// גיבוב הסיסמה (DashboardPasswordHash) מוסר ולא נחשף לדף; במקומו מוטמע דגל בוליאני
    /// HasPassword כדי שהטופס יידע אם סיסמה כבר מוגדרת.
    /// </summary>
    private static string LoadSettingsJson(string? configPath)
    {
        try
        {
            if (!string.IsNullOrEmpty(configPath) && File.Exists(configPath))
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(configPath));
                if (node?["Cccxa"] is System.Text.Json.Nodes.JsonObject cccxa)
                {
                    var hasPw = cccxa["DashboardPasswordHash"] is System.Text.Json.Nodes.JsonValue hv &&
                                hv.TryGetValue<string>(out var hs) && !string.IsNullOrEmpty(hs);
                    cccxa.Remove("DashboardPasswordHash");
                    cccxa["HasPassword"] = hasPw;
                    return cccxa.ToJsonString(JsonOpts);
                }
            }
        }
        catch { }
        return "{}";
    }

    private static string LoadTemplate()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("dashboard.template.html", StringComparison.OrdinalIgnoreCase));
        if (name is null)
            throw new InvalidOperationException("תבנית הדשבורד לא נמצאה במשאבים.");
        using var s = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(s);
        return reader.ReadToEnd();
    }

    private static void TryOpen(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { /* פתיחה אוטומטית היא נוחות בלבד */ }
    }

    // ----- מודל הנתונים המוטמע -----
    private sealed class Report
    {
        public string GeneratedAt { get; set; } = "";
        public List<UserReport> Users { get; set; } = new();
    }

    private sealed class UserReport
    {
        public string Name { get; set; } = "";
        public Totals Totals { get; set; } = new();
        public int[] ByHour { get; set; } = new int[24];
        public List<LabelCount> TopSites { get; set; } = new();
        public List<LabelCount> TopApps { get; set; } = new();
        public List<BrowseRow> Browsing { get; set; } = new();
        public List<ProgRow> Programs { get; set; } = new();
        public List<FileRow> Files { get; set; } = new();
        public List<ShotRow> Screenshots { get; set; } = new();
        public int BrowsingTotal { get; set; }
        public int ProgramsTotal { get; set; }
    }

    private sealed class Totals
    {
        public int Events { get; set; }
        public int Screenshots { get; set; }
        public int Urls { get; set; }
        public int Apps { get; set; }
        public int Files { get; set; }
        public int Days { get; set; }
    }

    private sealed class LabelCount
    {
        public string Label { get; set; } = "";
        public int Count { get; set; }
    }

    private sealed class BrowseRow
    {
        public string Ts { get; set; } = "";
        public string Browser { get; set; } = "";
        public bool Incognito { get; set; }
        public string? Title { get; set; }
        public string Url { get; set; } = "";
    }

    private sealed class ProgRow
    {
        public string Ts { get; set; } = "";
        public string? App { get; set; }
        public string? Title { get; set; }
    }

    private sealed class FileRow
    {
        public string Ts { get; set; } = "";
        public string? Name { get; set; }
        public string? Target { get; set; }
    }

    private sealed class ShotRow
    {
        public string Ts { get; set; } = "";
        public string Path { get; set; } = "";
    }
}
