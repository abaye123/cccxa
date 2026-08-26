using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Cccxa;

/// <summary>
/// עטיפות דקות סביב Win32 API: חלון פעיל, כותרת, מזהה תהליך, מדדי מסך וירטואלי,
/// זמן חוסר פעילות ו-DPI awareness.
/// </summary>
public static class Win32
{
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int maxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("kernel32.dll")]
    private static extern uint GetTickCount();

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(uint dwProcessId);

    private const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

    // -- רשימת חשבונות המשתמש המקומיים (שמות SAM), לבורר המשתמשים בהגדרות --
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserEnum(string? servername, int level, int filter,
        out IntPtr bufptr, int prefmaxlen, out int entriesread, out int totalentries, ref int resumeHandle);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct USER_INFO_0 { public string usri0_name; }

    private const int FILTER_NORMAL_ACCOUNT = 0x0002;
    private const int MAX_PREFERRED_LENGTH = -1;
    private const int NERR_Success = 0;
    private const int ERROR_MORE_DATA = 234;

    /// <summary>
    /// שמות חשבונות המשתמש המקומיים (SAM), ממויינים וללא כפילויות. חשבונות מערכת שאינם
    /// אינטראקטיביים מסוננים. משמש את בורר המשתמשים בשדות הסינון בדשבורד.
    /// </summary>
    public static List<string> LocalUserNames()
    {
        var result = new List<string>();
        try
        {
            int resume = 0, status;
            do
            {
                status = NetUserEnum(null, 0, FILTER_NORMAL_ACCOUNT, out var buf, MAX_PREFERRED_LENGTH,
                    out var read, out _, ref resume);
                if (status != NERR_Success && status != ERROR_MORE_DATA) break;
                try
                {
                    var size = Marshal.SizeOf<USER_INFO_0>();
                    var ptr = buf;
                    for (int i = 0; i < read; i++)
                    {
                        var info = Marshal.PtrToStructure<USER_INFO_0>(ptr);
                        if (!string.IsNullOrWhiteSpace(info.usri0_name)) result.Add(info.usri0_name);
                        ptr = IntPtr.Add(ptr, size);
                    }
                }
                finally { NetApiBufferFree(buf); }
            }
            while (status == ERROR_MORE_DATA);
        }
        catch { /* אם המנייה נכשלה - מחזירים מה שנאסף (אולי ריק) */ }

        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "WDAGUtilityAccount", "DefaultAccount" };
        return result.Where(n => !skip.Contains(n))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                     .ToList();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    /// <summary>הופך את התהליך למודע-DPI כדי שצילום המסך יתפוס את כל הפיקסלים במסכי 4K/סקיילינג.</summary>
    public static void SetDpiAware()
    {
        try
        {
            // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4
            if (!SetProcessDpiAwarenessContext(new IntPtr(-4)))
                SetProcessDPIAware();
        }
        catch
        {
            try { SetProcessDPIAware(); } catch { /* לא קריטי */ }
        }
    }

    public static string GetWindowTitle(IntPtr hWnd)
    {
        int len = GetWindowTextLength(hWnd);
        if (len <= 0) return string.Empty;
        var sb = new StringBuilder(len + 1);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static int GetIdleSeconds()
    {
        var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref lii)) return 0;
        return (int)((GetTickCount() - lii.dwTime) / 1000);
    }

    /// <summary>גבולות המסך הווירטואלי הכולל (כל המסכים יחד).</summary>
    public static (int X, int Y, int Width, int Height) VirtualScreen()
        => (GetSystemMetrics(SM_XVIRTUALSCREEN),
            GetSystemMetrics(SM_YVIRTUALSCREEN),
            GetSystemMetrics(SM_CXVIRTUALSCREEN),
            GetSystemMetrics(SM_CYVIRTUALSCREEN));

    /// <summary>
    /// מכיוון שהאפליקציה היא WinExe (ללא קונסולה), פקודות CLI מתחברות לקונסולה של תהליך האב
    /// (הטרמינל) כדי שהפלט יופיע. אם אין קונסולת אב (הופעל בלחיצה כפולה) - פשוט אין פלט, וזה בסדר.
    /// יש לקרוא רק במצב CLI, ולעולם לא במצב הרקע - כדי שלא תיווצר קונסולה גלויה.
    /// </summary>
    public static void AttachParentConsole()
    {
        try
        {
            if (AttachConsole(ATTACH_PARENT_PROCESS))
            {
                var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
                Console.SetOut(stdout);
                var stderr = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
                Console.SetError(stderr);
            }
        }
        catch { /* פלט CLI הוא נוחות בלבד */ }
    }

    public static string ProcessName(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }
}
