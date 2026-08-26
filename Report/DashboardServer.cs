using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cccxa.Report;

/// <summary>
/// שרת מקומי קצר-חיים (loopback בלבד) שמגיש את הדשבורד ואת ממשק ההגדרות, ומאפשר שמירה חיה של
/// ההגדרות. אינו תהליך רקע קבוע: הדף שולח פינג כל כמה שניות, וכשהוא נסגר (הפינגים נפסקים או
/// נשלח sendBeacon) השרת מכבה את עצמו. מיועד להרצה על-פי דרישה מהקיצור בשולחן העבודה.
///
/// אבטחה אופציונלית: אם מוגדר גיבוב סיסמה (Cccxa:DashboardPasswordHash), הגישה לדשבורד,
/// לתמונות ולהגדרות נחסמת עד הזנת הסיסמה הנכונה ב-/api/login (מקבלים עוגיית סשן חד-פעמית).
/// </summary>
public static class DashboardServer
{
    private const int IdleTimeoutSeconds = 45;

    // אסימון סשן חד-פעמי לכל הרצת שרת. עוגייה תקפה = הערך הזה. אינו נשמר לדיסק.
    private static readonly string AuthToken = Guid.NewGuid().ToString("N");

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

        var pwHash = ReadPasswordHash(configPath);
        bool locked = !string.IsNullOrEmpty(pwHash);
        bool authed = !locked || IsAuthed(req);

        // התחברות זמינה תמיד (זה השער כשנעול).
        if (path == "/api/login" && req.HttpMethod == "POST")
        {
            var pw = ReadStringField(req, "password");
            if (locked && VerifyPassword(pwHash!, pw))
            {
                res.Headers["Set-Cookie"] = $"cccxa_auth={AuthToken}; Path=/; HttpOnly; SameSite=Strict";
                WriteJson(res, "{\"ok\":true}");
            }
            else
            {
                res.StatusCode = 401;
                WriteJson(res, "{\"ok\":false}");
            }
            return false;
        }

        // כשנעול ולא מאומת: הדף הראשי מגיש מסך התחברות, פינג מותר, וכל השאר 401.
        if (locked && !authed)
        {
            if (path == "/api/ping") { WriteJson(res, "{\"ok\":true}"); return false; }
            if (path == "/" || path == "/index.html")
            {
                WriteText(res, LoginHtml(), "text/html; charset=utf-8");
                return false;
            }
            res.StatusCode = 401;
            res.Close();
            return false;
        }

        switch (path)
        {
            case "/":
            case "/index.html":
                WriteText(res, ReportBuilder.BuildHtmlAll(dataRoot, configPath), "text/html; charset=utf-8");
                return false;

            case "/img":
                ServeImage(res, dataRoot, req.QueryString["p"]);
                return false;

            case "/api/users":
                WriteJson(res, JsonSerializer.Serialize(Win32.LocalUserNames()));
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

    // ---------------- authentication ----------------

    private static bool IsAuthed(HttpListenerRequest req)
    {
        var cookie = req.Headers["Cookie"];
        if (string.IsNullOrEmpty(cookie)) return false;
        foreach (var part in cookie.Split(';'))
        {
            var kv = part.Trim();
            const string key = "cccxa_auth=";
            if (kv.StartsWith(key, StringComparison.Ordinal))
                return CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(kv.Substring(key.Length)),
                    Encoding.UTF8.GetBytes(AuthToken));
        }
        return false;
    }

    /// <summary>קורא את גיבוב הסיסמה מקובץ ההגדרות (ריק אם אין / לא ניתן לקרוא).</summary>
    private static string? ReadPasswordHash(string configPath)
    {
        try
        {
            if (!File.Exists(configPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            if (doc.RootElement.TryGetProperty("Cccxa", out var c) &&
                c.TryGetProperty("DashboardPasswordHash", out var h) &&
                h.ValueKind == JsonValueKind.String)
            {
                var s = h.GetString();
                return string.IsNullOrEmpty(s) ? null : s;
            }
        }
        catch { }
        return null;
    }

    internal static string HashPassword(string pw)
    {
        const int iter = 100_000;
        var salt = RandomNumberGenerator.GetBytes(16);
        using var kdf = new Rfc2898DeriveBytes(pw, salt, iter, HashAlgorithmName.SHA256);
        var hash = kdf.GetBytes(32);
        return $"pbkdf2${iter}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    private static bool VerifyPassword(string stored, string pw)
    {
        try
        {
            var parts = stored.Split('$');
            if (parts.Length != 4 || parts[0] != "pbkdf2") return false;
            int iter = int.Parse(parts[1]);
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            using var kdf = new Rfc2898DeriveBytes(pw, salt, iter, HashAlgorithmName.SHA256);
            var actual = kdf.GetBytes(expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch { return false; }
    }

    private static string ReadStringField(HttpListenerRequest req, string field)
    {
        try
        {
            string body;
            using (var r = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
                body = r.ReadToEnd();
            if (JsonNode.Parse(body) is JsonObject o && o[field] is JsonValue v &&
                v.TryGetValue<string>(out var s))
                return s;
        }
        catch { }
        return "";
    }

    // ---------------- settings persistence ----------------

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

            // טיפול בסיסמה: שדות בקרה נפרדים כדי שהגיבוב לעולם לא יסתובב דרך הדף.
            //  _newPassword   - קובע סיסמה חדשה (מגובב בשרת).
            //  _clearPassword - מסיר סיסמה קיימת.
            //  אחרת - שומרים על הגיבוב הקיים כפי שהוא.
            string existingHash = ReadPasswordHash(configPath) ?? "";
            string newPw = TryGetString(incoming, "_newPassword");
            bool clear = TryGetBool(incoming, "_clearPassword");
            incoming.Remove("_newPassword");
            incoming.Remove("_clearPassword");
            incoming.Remove("HasPassword"); // דגל תצוגה בלבד, לא הגדרה

            string finalHash = clear ? "" : (!string.IsNullOrEmpty(newPw) ? HashPassword(newPw) : existingHash);
            if (!string.IsNullOrEmpty(finalHash)) incoming["DashboardPasswordHash"] = finalHash;
            else incoming.Remove("DashboardPasswordHash");

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

    private static string TryGetString(JsonObject o, string key)
        => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    private static bool TryGetBool(JsonObject o, string key)
        => o[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    // ---------------- static assets ----------------

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

    /// <summary>מסך התחברות מינימלי (עצמאי, ללא תלות חיצונית) המוגש כשהדשבורד נעול בסיסמה.</summary>
    private static string LoginHtml()
    {
        return """
<!DOCTYPE html>
<html lang="he" dir="rtl"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1"><title>cccxa</title>
<style>
  :root { color-scheme: light dark; }
  * { box-sizing: border-box; }
  body { margin:0; min-height:100vh; display:flex; align-items:center; justify-content:center;
    background:#0b0b0f; color:#e7e7ea;
    font-family: ui-sans-serif, system-ui, "Segoe UI", "Noto Sans Hebrew", Arial, sans-serif; }
  .box { width:320px; max-width:92vw; background:#16161c; border:1px solid #26262e; border-radius:14px;
    padding:26px 24px; box-shadow:0 10px 40px rgba(0,0,0,.4); }
  h1 { font-size:18px; margin:0 0 4px; }
  p { font-size:13px; color:#9a9aa4; margin:0 0 18px; }
  label { font-size:13px; display:block; margin-bottom:6px; }
  input { width:100%; padding:10px 12px; border-radius:9px; border:1px solid #33333d;
    background:#0e0e13; color:#e7e7ea; font-size:14px; outline:none; }
  input:focus { border-color:#4f7cff; box-shadow:0 0 0 3px rgba(79,124,255,.25); }
  button { width:100%; margin-top:14px; padding:10px 12px; border:0; border-radius:9px; cursor:pointer;
    background:#4f7cff; color:#fff; font-size:14px; font-weight:600; }
  button:hover { opacity:.92; }
  .err { color:#ff6b6b; font-size:13px; margin-top:10px; min-height:18px; }
</style></head><body>
  <form class="box" id="f" autocomplete="off">
    <h1>cccxa</h1>
    <p>הגישה לדשבורד מוגנת בסיסמה.</p>
    <label for="pw">סיסמה</label>
    <input type="password" id="pw" autofocus>
    <button type="submit">כניסה</button>
    <div class="err" id="err"></div>
  </form>
<script>
  var f = document.getElementById('f'), pw = document.getElementById('pw'), err = document.getElementById('err');
  f.addEventListener('submit', function (e) {
    e.preventDefault(); err.textContent = '';
    fetch('/api/login', { method:'POST', headers:{'Content-Type':'application/json'},
      body: JSON.stringify({ password: pw.value }) })
      .then(function (r) { return r.json().then(function (j) { return { ok:r.ok, j:j }; }); })
      .then(function (res) {
        if (res.ok && res.j && res.j.ok) { location.reload(); }
        else { err.textContent = 'סיסמה שגויה'; pw.value = ''; pw.focus(); }
      })
      .catch(function () { err.textContent = 'שגיאה'; });
  });
</script>
</body></html>
""";
    }

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
