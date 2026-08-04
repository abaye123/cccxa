using System.Diagnostics;
using Cccxa.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Cccxa.Services;

/// <summary>
/// עוקב אחרי תוכנות שנפתחות ונסגרות, ע"י דגימת רשימת התהליכים והשוואה לסבב הקודם.
/// דגימה (polling) נבחרה על פני WMI כדי לשמור על עומס נמוך ולהימנע מהרשאות מיוחדות.
/// </summary>
public sealed class ProcessService : BackgroundService
{
    private readonly IOptionsMonitor<CccxaOptions> _opt;
    private readonly SnapshotStore _store;

    public ProcessService(IOptionsMonitor<CccxaOptions> opt, SnapshotStore store)
    {
        _opt = opt;
        _store = store;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var known = new Dictionary<int, string>();
        bool first = true;

        while (!ct.IsCancellationRequested)
        {
            var o = _opt.CurrentValue;

            if (o.CaptureProcesses)
            {
                try
                {
                    var current = new Dictionary<int, string>();
                    foreach (var p in Process.GetProcesses())
                    {
                        try { current[p.Id] = p.ProcessName; }
                        catch { }
                        finally { p.Dispose(); }
                    }

                    if (!first)
                    {
                        foreach (var kv in current)
                            if (!known.ContainsKey(kv.Key))
                                _store.AddEvent("process_start", kv.Value, null, null, "pid=" + kv.Key);

                        foreach (var kv in known)
                            if (!current.ContainsKey(kv.Key))
                                _store.AddEvent("process_stop", kv.Value, null, null, "pid=" + kv.Key);
                    }

                    known = current;
                    first = false;
                }
                catch
                {
                    // ממשיכים.
                }
            }

            try { await Task.Delay(Math.Max(1000, o.ProcessPollMs), ct); }
            catch (TaskCanceledException) { }
        }
    }
}
