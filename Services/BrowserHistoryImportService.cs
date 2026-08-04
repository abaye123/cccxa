using System.Text.Json;
using Cccxa.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Cccxa.Services;

/// <summary>
/// מייבא את היסטוריית הגלישה הקיימת מקבצי ה-History של דפדפני כרומיום (Chrome/Edge/Brave)
/// אל תוך אותו מסד נתונים, כאירועים מסוג "history_visit".
///
/// זה משלים את הלכידה החיה (ForegroundBrowserService): כאן מקבלים את כל ההיסטוריה
/// שכבר נצברה, כולל ביקורים שקרו כשהכלי לא רץ. הערה: גלישה בסתר / אורח לא נשמרת
/// בקבצי ההיסטוריה, ולכן אותה לוכדים רק חיה דרך שורת הכתובת.
///
/// קובץ ה-History נעול כשהדפדפן פתוח, לכן מעתיקים אותו לתיקייה זמנית ופותחים לקריאה בלבד.
/// דה-דופ נשמר לפי חותמת הביקור האחרונה שיובאה, בקובץ history_state.json.
/// </summary>
public sealed class BrowserHistoryImportService : BackgroundService
{
    private readonly IOptionsMonitor<CccxaOptions> _opt;
    private readonly SnapshotStore _store;
    private readonly string _statePath;

    // 1601-01-01 UTC - נקודת האפס של חותמות הזמן של כרומיום (במיקרו-שניות).
    private static readonly DateTime ChromeEpoch = new(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public BrowserHistoryImportService(IOptionsMonitor<CccxaOptions> opt, SnapshotStore store)
    {
        _opt = opt;
        _store = store;
        _statePath = Path.Combine(_store.Root, "history_state.json");
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var o = _opt.CurrentValue;
            if (o.ImportBrowserHistory)
            {
                try { ImportAll(); }
                catch { /* ממשיכים בסבב הבא */ }
            }

            int minutes = Math.Max(1, o.HistoryImportIntervalMinutes);
            try { await Task.Delay(TimeSpan.FromMinutes(minutes), ct); }
            catch (TaskCanceledException) { }
        }
    }

    private void ImportAll()
    {
        var state = LoadState();

        foreach (var (source, historyPath) in DiscoverHistoryFiles())
        {
            try
            {
                long last = state.TryGetValue(source, out var v) ? v : 0L;
                long newLast = ImportFrom(source, historyPath, last);
                if (newLast > last)
                    state[source] = newLast;
            }
            catch
            {
                // מקור בעייתי בודד לא עוצר את השאר.
            }
        }

        SaveState(state);
    }

    private long ImportFrom(string source, string historyPath, long lastVisit)
    {
        var temp = Path.Combine(Path.GetTempPath(), "cccxa_hist_" + Guid.NewGuid().ToString("N") + ".db");
        long maxVisit = lastVisit;
        try
        {
            File.Copy(historyPath, temp, overwrite: true);

            using var conn = new SqliteConnection($"Data Source={temp};Mode=ReadOnly;Cache=Shared");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT v.visit_time, u.url, u.title
                FROM visits v
                JOIN urls u ON u.id = v.url
                WHERE v.visit_time > $last
                ORDER BY v.visit_time ASC
                LIMIT 50000;";
            cmd.Parameters.AddWithValue("$last", lastVisit);

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                long visitTime = r.GetInt64(0);
                string url = r.IsDBNull(1) ? "" : r.GetString(1);
                string title = r.IsDBNull(2) ? "" : r.GetString(2);
                if (string.IsNullOrEmpty(url)) continue;

                var ts = ChromeEpoch.AddTicks(visitTime * 10); // מיקרו-שניות -> ticks
                _store.AddEvent("history_visit", source, title, url, ts.ToString("o"));

                if (visitTime > maxVisit) maxVisit = visitTime;
            }
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }
        return maxVisit;
    }

    /// <summary>מאתר את כל קבצי ה-History בכל הפרופילים של הדפדפנים הנתמכים.</summary>
    private static IEnumerable<(string Source, string Path)> DiscoverHistoryFiles()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        var userDataDirs = new (string Name, string Path)[]
        {
            ("chrome", Path.Combine(local, "Google", "Chrome", "User Data")),
            ("edge",   Path.Combine(local, "Microsoft", "Edge", "User Data")),
            ("brave",  Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data")),
            ("opera",  Path.Combine(roaming, "Opera Software", "Opera Stable")),
            ("vivaldi",Path.Combine(local, "Vivaldi", "User Data")),
        };

        foreach (var (name, root) in userDataDirs)
        {
            if (!Directory.Exists(root)) continue;

            // Opera שומר History ישירות בשורש הפרופיל; כרומיום בתת-תיקיות פרופיל.
            var direct = Path.Combine(root, "History");
            if (File.Exists(direct))
                yield return ($"{name}:.", direct);

            IEnumerable<string> subdirs;
            try { subdirs = Directory.EnumerateDirectories(root); }
            catch { continue; }

            foreach (var dir in subdirs)
            {
                var h = Path.Combine(dir, "History");
                if (File.Exists(h))
                    yield return ($"{name}:{Path.GetFileName(dir)}", h);
            }
        }
    }

    private Dictionary<string, long> LoadState()
    {
        try
        {
            if (File.Exists(_statePath))
            {
                var json = File.ReadAllText(_statePath);
                return JsonSerializer.Deserialize<Dictionary<string, long>>(json)
                       ?? new Dictionary<string, long>();
            }
        }
        catch { }
        return new Dictionary<string, long>();
    }

    private void SaveState(Dictionary<string, long> state)
    {
        try
        {
            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_statePath, json);
        }
        catch { }
    }
}
