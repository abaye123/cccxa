using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cccxa.Report;

/// <summary>
/// שרת מקומי קצר-חיים (loopback בלבד) שמגיש את הדשבורד ואת ממשק ההגדרות, ומאפשר שמירה חיה של
/// ההגדרות. אינו תהליך רקע קבוע: הדף שולח פינג כל כמה שניות, וכשהוא נסגר (הפינגים נפסקים או
/// נשלח sendBeacon) השרת מכבה את עצמו. מיועד להרצה על-פי דרישה מהקיצור בשולחן העבודה.
/// </summary>
public static class DashboardServer
{
    private const int IdleTimeoutSeconds = 45;

    public static int Run(string dataRoot, string configPath, int port)
    {
        HttpListener? listener = null;
        string? prefix = null;
        foreach (var p in CandidatePorts(port))
        {
            try
            {
                var l = new HttpListener();
                var pre = $"http://127.0.0.1:{p}/";
                l.Prefixes.Add(pre);
                l.Start();
                listener = l; prefix = pre;
                break;
            }
            catch { listener = null; }
        }

        if (listener is null)
        {
            Console.Error.WriteLine("[cccxa] לא ניתן לפתוח שרת מקומי (כל הפורטים תפוסים).");
            return 1;
        }

        Console.WriteLine($"[cccxa] הדשבורד פעיל בכתובת {prefix}  (נסגר אוטומטית עם סגירת הדף)");

        var gate = new object();
        var lastSeen = DateTime.UtcNow;
        var stopping = false;

        var watchdog = new Thread(() =>
        {
            while (true)
            {
                Thread.Sleep(5000);
                bool idle;
                lock (gate) { idle = stopping || (DateTime.UtcNow - lastSeen).TotalSeconds > IdleTimeoutSeconds; }
                if (idle) { try { listener.Stop(); } catch { } break; }
            }
        })
        { IsBackground = true };
        watchdog.Start();

        TryOpen(prefix!);

        while (true)
        {
            HttpListenerContext ctx;
            try { ctx = listener.GetContext(); }
            catch { break; } // listener.Stop() נקרא

            lock (gate) { lastSeen = DateTime.UtcNow; }

            bool shutdown = false;
            try { shutdown = Handle(ctx, dataRoot, configPath); }
            catch { try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { } }

            if (shutdown)
            {
                lock (gate) { stopping = true; }
                try { listener.Stop(); } catch { }
                break;
            }
        }

        return 0;
    }

    private static IEnumerable<int> CandidatePorts(int port)
        => port > 0 ? new[] { port } : new[] { 8733, 8734, 8735, 8891, 8912 };

    private static bool Handle(HttpListenerContext ctx, string dataRoot, string configPath)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        var path = req.Url?.AbsolutePath ?? "/";
        res.Headers["Cache-Control"] = "no-store";

        switch (path)
        {
            case "/":
            case "/index.html":
                WriteText(res, ReportBuilder.BuildHtmlAll(dataRoot, configPath), "text/html; charset=utf-8");
                return false;

            case "/img":
                ServeImage(res, dataRoot, req.QueryString["p"]);
                return false;

            case "/api/ping":
                WriteJson(res, "{\"ok\":true}");
                return false;

            case "/api/shutdown":
                WriteJson(res, "{\"ok\":true}");
                return true;

            case "/api/settings" when req.HttpMethod == "POST":
                string body;
                using (var r = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
                    body = r.ReadToEnd();
                var ok = SaveSettings(configPath, body, out var err);
                WriteJson(res, ok ? "{\"ok\":true}" : "{\"ok\":false,\"error\":" + JsonString(err) + "}");
                return false;

            default:
                res.StatusCode = 404;
                res.Close();
                return false;
        }
    }

    private static bool SaveSettings(string configPath, string body, out string error)
    {
        error = "";
        try
        {
            if (JsonNode.Parse(body) is not JsonObject incoming)
            {
                error = "invalid json";
                return false;
            }

            JsonObject root = File.Exists(configPath)
                ? (JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject ?? new JsonObject())
                : new JsonObject();

            root["Cccxa"] = incoming;
            File.WriteAllText(configPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void ServeImage(HttpListenerResponse res, string dataRoot, string? p)
    {
        try
        {
            if (string.IsNullOrEmpty(p)) { res.StatusCode = 400; res.Close(); return; }

            // מגישים רק קבצי JPG שנמצאים בתוך תיקיית הנתונים (הגנה מפני path traversal).
            var baseDir = Path.GetFullPath(Directory.GetParent(dataRoot)?.FullName ?? dataRoot);
            var full = Path.GetFullPath(p);
            if (!full.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase) ||
                !full.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(full))
            {
                res.StatusCode = 404; res.Close(); return;
            }

            res.ContentType = "image/jpeg";
            res.Headers["Cache-Control"] = "max-age=3600";
            using var fs = File.OpenRead(full);
            fs.CopyTo(res.OutputStream);
            res.Close();
        }
        catch
        {
            try { res.StatusCode = 500; res.Close(); } catch { }
        }
    }

    private static void WriteText(HttpListenerResponse res, string text, string contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        res.ContentType = contentType;
        res.ContentLength64 = bytes.Length;
        res.OutputStream.Write(bytes, 0, bytes.Length);
        res.Close();
    }

    private static void WriteJson(HttpListenerResponse res, string json)
        => WriteText(res, json, "application/json; charset=utf-8");

    private static string JsonString(string s) => JsonSerializer.Serialize(s);

    private static void TryOpen(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { }
    }
}
