using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace Cccxa;

/// <summary>תוצאת קריאה מחלון דפדפן: הכתובת והאם זו גלישה בסתר/אורח.</summary>
public sealed class UrlInfo
{
    public string Url { get; init; } = "";
    public bool Incognito { get; init; }
}

/// <summary>
/// קורא את הכתובת מתוך שורת הכתובת של דפדפן כרומיום (Chrome/Edge/Brave...) בעזרת UI Automation,
/// וגם מזהה האם החלון הוא גלישה בסתר / מצב אורח.
///
/// למה UI Automation ולא קריאת קובץ ההיסטוריה: גלישה בסתר / אורח לא נשמרת כלל בקובץ ההיסטוריה,
/// אבל שורת הכתובת החיה נגישה דרך עץ הנגישות - ולכן זו הדרך היחידה ללכוד גם גלישה נסתרת.
///
/// זיהוי הבסתר מוגבל לסרגל הכלים בלבד (לא לתוכן הדף) כדי לשמור על עומס נמוך.
/// </summary>
public sealed class BrowserUrlReader : IDisposable
{
    private readonly UIA3Automation _automation = new();

    public UrlInfo? TryRead(IntPtr hwnd, IEnumerable<string> addressBarNames, IEnumerable<string> privateMarkers)
    {
        try
        {
            var root = _automation.FromHandle(hwnd);
            if (root is null) return null;

            AutomationElement? addr = null;

            // ניסיון 1: לפי שם שורת הכתובת (מהיר ומדויק, תלוי שפה).
            foreach (var name in addressBarNames)
            {
                addr = root.FindFirstDescendant(cf =>
                    cf.ByControlType(ControlType.Edit).And(cf.ByName(name)));
                if (ReadValue(addr) is not null) break;
                addr = null;
            }

            string? url = ReadValue(addr);

            // ניסיון 2 (fallback): הראשון מבין תיבות ה-Edit שהערך בו נראה ככתובת.
            if (url is null)
            {
                foreach (var e in root.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit)))
                {
                    var v = ReadValue(e);
                    if (v is not null && LooksLikeUrl(v)) { addr = e; url = v; break; }
                }
            }

            if (url is null) return null;

            var markers = privateMarkers as string[] ?? privateMarkers.ToArray();
            return new UrlInfo { Url = url, Incognito = DetectIncognito(addr, markers) };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// מחפש סימון בסתר/אורח. מאתר את סרגל הכלים (ToolBar) שמכיל את שורת הכתובת וסורק את כל צאצאיו -
    /// תת-עץ קטן של סרגל הכלים בלבד (לא כולל את תוכן הדף), כך שהעומס נשאר נמוך. תג ה-Incognito של Chrome
    /// מקונן עמוק בתוך סרגל הכלים, ולכן חיפוש צאצאים ישירים בלבד פספס אותו.
    /// </summary>
    private static bool DetectIncognito(AutomationElement? addr, string[] markers)
    {
        try
        {
            if (addr is null) return false;

            // עולים משורת הכתובת עד שמוצאים את ה-ToolBar המכיל אותה.
            AutomationElement scope = addr;
            bool foundToolbar = false;
            var node = addr;
            for (int i = 0; i < 6 && node is not null; i++)
            {
                try { if (node.ControlType == ControlType.ToolBar) { scope = node; foundToolbar = true; break; } }
                catch { }
                try { node = node.Parent; } catch { break; }
            }
            // fallback מוגבל: אם אין ToolBar, מסתפקים בהורה הישיר של שורת הכתובת (תת-עץ קטן).
            if (!foundToolbar) scope = TryParent(addr) ?? addr;

            // סורקים את כל צאצאי סרגל הכלים - שם יושב תג הבסתר/אורח.
            if (MatchesAny(SafeDescendants(scope), markers)) return true;

            // ליתר ביטחון גם את השכנים הישירים של סרגל הכלים (התג עשוי לשבת לצדו).
            var parent = TryParent(scope);
            if (parent is not null && MatchesAny(SafeChildren(parent), markers)) return true;
        }
        catch { }
        return false;
    }

    private static bool MatchesAny(AutomationElement[] els, string[] markers)
    {
        foreach (var e in els)
        {
            string n;
            try { n = e.Name ?? ""; } catch { continue; }
            if (n.Length == 0) continue;
            foreach (var m in markers)
                if (!string.IsNullOrEmpty(m) && n.Contains(m, StringComparison.OrdinalIgnoreCase))
                    return true;
        }
        return false;
    }

    private static AutomationElement? TryParent(AutomationElement? e)
    {
        try { return e?.Parent; } catch { return null; }
    }

    private static AutomationElement[] SafeChildren(AutomationElement e)
    {
        try { return e.FindAllChildren(); } catch { return Array.Empty<AutomationElement>(); }
    }

    private static AutomationElement[] SafeDescendants(AutomationElement e)
    {
        try { return e.FindAllDescendants(); } catch { return Array.Empty<AutomationElement>(); }
    }

    private static string? ReadValue(AutomationElement? el)
    {
        try
        {
            var pattern = el?.Patterns.Value.PatternOrDefault;
            if (pattern is null) return null;
            var v = pattern.Value.ValueOrDefault;
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static bool LooksLikeUrl(string s)
        => s.Contains("://") || (s.Contains('.') && !s.Contains(' '));

    public void Dispose() => _automation.Dispose();
}
