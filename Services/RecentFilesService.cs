using Cccxa.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Cccxa.Services;

/// <summary>
/// עוקב אחרי מסמכים וקבצים שנפתחו, ע"י האזנה לתיקיית ה-Recent של Windows
/// (שם המערכת יוצרת קיצור .lnk לכל קובץ שנפתח). מבוסס-אירועים, כמעט ללא עומס.
/// </summary>
public sealed class RecentFilesService : BackgroundService
{
    private readonly IOptionsMonitor<CccxaOptions> _opt;
    private readonly SnapshotStore _store;
    private FileSystemWatcher? _watcher;
    private readonly Dictionary<string, DateTime> _lastSeen = new(StringComparer.OrdinalIgnoreCase);

    public RecentFilesService(IOptionsMonitor<CccxaOptions> opt, SnapshotStore store)
    {
        _opt = opt;
        _store = store;
    }

    protected override Task ExecuteAsync(CancellationToken ct)
    {
        if (!_opt.CurrentValue.CaptureRecentFiles)
            return Task.CompletedTask;

        var recent = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        if (string.IsNullOrEmpty(recent) || !Directory.Exists(recent))
            return Task.CompletedTask;

        _watcher = new FileSystemWatcher(recent, "*.lnk")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            EnableRaisingEvents = true
        };
        _watcher.Created += (_, e) => Handle(e.FullPath);
        _watcher.Changed += (_, e) => Handle(e.FullPath);

        ct.Register(() =>
        {
            if (_watcher is not null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
            }
        });

        return Task.CompletedTask;
    }

    private void Handle(string lnkPath)
    {
        try
        {
            // דה-דופ: FileSystemWatcher יורה לעיתים כמה אירועים לאותו קובץ.
            var now = DateTime.UtcNow;
            lock (_lastSeen)
            {
                if (_lastSeen.TryGetValue(lnkPath, out var t) && (now - t).TotalSeconds < 2)
                    return;
                _lastSeen[lnkPath] = now;
            }

            var name = Path.GetFileNameWithoutExtension(lnkPath);
            var target = ResolveShortcut(lnkPath);
            _store.AddEvent("recent_file", null, name, null, target);
        }
        catch
        {
            // מתעלמים מקיצור בעייתי בודד.
        }
    }

    /// <summary>מפענח את יעד קיצור ה-.lnk דרך WScript.Shell (COM).</summary>
    private static string? ResolveShortcut(string lnkPath)
    {
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t is null) return null;
            dynamic? shell = Activator.CreateInstance(t);
            if (shell is null) return null;
            dynamic sc = shell.CreateShortcut(lnkPath);
            string target = sc.TargetPath;
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch
        {
            return null;
        }
    }
}
