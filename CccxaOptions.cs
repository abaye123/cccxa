namespace Cccxa;

/// <summary>
/// כל ההגדרות של cccxa. נטענות מ-appsettings.json תחת המפתח "Cccxa".
/// שינויים בקובץ נטענים בזמן ריצה (hot reload) דרך IOptionsMonitor.
/// </summary>
public sealed class CccxaOptions
{
    /// <summary>תיקיית היעד לשמירה. תומך במשתני סביבה כמו %LOCALAPPDATA% ו-%USERNAME%.</summary>
    public string StorageRoot { get; set; } = "%LOCALAPPDATA%\\cccxa";

    /// <summary>
    /// אם הרשימה לא ריקה - התוכנה תתעד רק עבור שמות המשתמשים האלה. אחרת יוצאת מיד.
    /// </summary>
    public string[] OnlyUsers { get; set; } = Array.Empty<string>();

    /// <summary>
    /// שמות משתמשים שלעולם לא יתועדו (למשל משתמש עבודה עם חומר רגיש).
    /// גובר על OnlyUsers.
    /// </summary>
    public string[] ExcludeUsers { get; set; } = Array.Empty<string>();

    /// <summary>בדיקה אם מותר לתעד את המשתמש הנוכחי לפי OnlyUsers/ExcludeUsers.</summary>
    public bool IsUserAllowed(string userName)
    {
        var exclude = ExcludeUsers ?? Array.Empty<string>();
        var only = OnlyUsers ?? Array.Empty<string>();

        if (exclude.Any(u => string.Equals(u?.Trim(), userName, StringComparison.OrdinalIgnoreCase)))
            return false;
        if (only.Length > 0 &&
            !only.Any(u => string.Equals(u?.Trim(), userName, StringComparison.OrdinalIgnoreCase)))
            return false;
        return true;
    }

    public ScreenshotOptions Screenshot { get; set; } = new();

    public bool CaptureBrowserUrls { get; set; } = true;
    public bool CaptureProcesses { get; set; } = true;
    public bool CaptureForegroundWindows { get; set; } = true;
    public bool CaptureRecentFiles { get; set; } = true;

    /// <summary>מחיקה אוטומטית של צילומי מסך ישנים מ-N ימים (0 = לשמור לתמיד).</summary>
    public int ScreenshotRetentionDays { get; set; } = 7;

    /// <summary>מחיקה אוטומטית של שאר הנתונים (אירועים) ישנים מ-N ימים (0 = לשמור לתמיד).</summary>
    public int DataRetentionDays { get; set; } = 30;

    /// <summary>כל כמה שעות להריץ את בדיקת המחיקה האוטומטית.</summary>
    public int RetentionCheckHours { get; set; } = 6;

    /// <summary>ייבוא היסטוריית הגלישה הקיימת מקבצי ה-History של הדפדפנים.</summary>
    public bool ImportBrowserHistory { get; set; } = true;

    /// <summary>כל כמה דקות לבדוק ולייבא היסטוריית גלישה חדשה.</summary>
    public int HistoryImportIntervalMinutes { get; set; } = 5;

    /// <summary>תדירות דגימת החלון הפעיל (מילישניות). ערך נמוך = מדויק יותר אך מעט יותר עומס.</summary>
    public int ForegroundPollMs { get; set; } = 1000;

    /// <summary>תדירות דגימת רשימת התהליכים (מילישניות).</summary>
    public int ProcessPollMs { get; set; } = 3000;

    /// <summary>שמות התהליכים שנחשבים דפדפן (ללא סיומת exe).</summary>
    public string[] BrowserProcessNames { get; set; } =
        { "chrome", "msedge", "brave", "opera", "vivaldi", "chromium" };

    /// <summary>
    /// שמות אפשריים של שורת הכתובת ב-UI Automation (משתנה לפי שפת המערכת).
    /// אם כתובות לא נלכדות - הוסף כאן את השם המדויק לפי המערכת שלך.
    /// </summary>
    public string[] BrowserAddressBarNames { get; set; } =
        { "Address and search bar" };

    /// <summary>
    /// מחרוזות שמזהות חלון גלישה בסתר / אורח (השוואת "מכיל", לא תלוי רישיות).
    /// משתנה לפי שפה - הוסף כאן ערכים אם זיהוי הבסתר לא עובד במערכת שלך.
    /// </summary>
    public string[] BrowserPrivateMarkers { get; set; } =
        { "Incognito", "InPrivate", "Guest", "גלישה בסתר", "מצב פרטי", "אורח" };
}

public sealed class ScreenshotOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>מרווח בין צילומים בשניות. דינאמי - שינוי בקובץ ההגדרות משפיע מיד.</summary>
    public int IntervalSeconds { get; set; } = 60;

    /// <summary>גורם הקטנה (0.5 = חצי מהרזולציה). קטן יותר = קבצים קטנים ופחות עומס.</summary>
    public double Scale { get; set; } = 0.5;

    /// <summary>רוחב מרבי בפיקסלים אחרי ההקטנה (0 = ללא הגבלה).</summary>
    public int MaxWidth { get; set; } = 1600;

    /// <summary>איכות JPEG (10-95). נמוך = קובץ קטן.</summary>
    public int JpegQuality { get; set; } = 40;

    /// <summary>דלג על צילום אם המשתמש לא פעיל יותר מ-N שניות (0 = תמיד צלם).</summary>
    public int SkipWhenIdleSeconds { get; set; } = 120;
}
