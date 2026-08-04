using Cccxa.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Cccxa.Services;

/// <summary>
/// עוקב אחרי החלון הפעיל (איזו תוכנה בפוקוס + כותרת החלון), וכשהחלון הפעיל הוא דפדפן -
/// קורא גם את הכתובת מתוך שורת הכתובת (כולל גלישה בסתר / מצב אורח).
///
/// קריאת ה-URL (שהיא היקרה ביותר) מתבצעת רק כשהחלון או הכותרת משתנים, ועם דה-דופ,
/// כדי לשמור על עומס נמוך.
/// </summary>
public sealed class ForegroundBrowserService : BackgroundService
{
    private readonly IOptionsMonitor<CccxaOptions> _opt;
    private readonly SnapshotStore _store;
    private BrowserUrlReader? _reader;

    public ForegroundBrowserService(IOptionsMonitor<CccxaOptions> opt, SnapshotStore store)
    {
        _opt = opt;
        _store = store;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        IntPtr lastHwnd = IntPtr.Zero;
        string lastTitle = string.Empty;
        string lastUrl = string.Empty;

        while (!ct.IsCancellationRequested)
        {
            var o = _opt.CurrentValue;

            try
            {
                var hwnd = Win32.GetForegroundWindow();
                var title = Win32.GetWindowTitle(hwnd);

                if (hwnd != lastHwnd || title != lastTitle)
                {
                    Win32.GetWindowThreadProcessId(hwnd, out var pid);
                    var proc = Win32.ProcessName(pid);

                    if (o.CaptureForegroundWindows && !string.IsNullOrEmpty(proc))
                        _store.AddEvent("foreground", proc, title, null, null);

                    if (o.CaptureBrowserUrls && IsBrowser(proc, o))
                    {
                        _reader ??= new BrowserUrlReader();
                        var info = _reader.TryRead(hwnd, o.BrowserAddressBarNames, o.BrowserPrivateMarkers);
                        if (info is not null && !string.IsNullOrEmpty(info.Url) && info.Url != lastUrl)
                        {
                            // detail שומר את דגל הבסתר; שם הדפדפן נשמר ב-app.
                            _store.AddEvent("browser_url", proc, title, info.Url, info.Incognito ? "incognito" : null);
                            lastUrl = info.Url;
                        }
                    }

                    lastHwnd = hwnd;
                    lastTitle = title;
                }
            }
            catch
            {
                // ממשיכים לדגום גם אם סבב בודד נכשל.
            }

            try { await Task.Delay(Math.Max(250, o.ForegroundPollMs), ct); }
            catch (TaskCanceledException) { }
        }

        _reader?.Dispose();
    }

    private static bool IsBrowser(string proc, CccxaOptions o)
        => !string.IsNullOrEmpty(proc) &&
           o.BrowserProcessNames.Any(n => string.Equals(n, proc, StringComparison.OrdinalIgnoreCase));
}
