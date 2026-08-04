using Cccxa.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Cccxa.Services;

/// <summary>
/// מחיקה אוטומטית של נתונים ישנים לפי מדיניות שמירה: צילומי מסך מעל ScreenshotRetentionDays,
/// ושאר הנתונים מעל DataRetentionDays. הערכים דינאמיים (נטענים מחדש בכל סבב מקובץ ההגדרות).
/// רץ פעם אחת בהתחלה ואז מדי RetentionCheckHours שעות.
/// </summary>
public sealed class RetentionService : BackgroundService
{
    private readonly IOptionsMonitor<CccxaOptions> _opt;
    private readonly SnapshotStore _store;

    public RetentionService(IOptionsMonitor<CccxaOptions> opt, SnapshotStore store)
    {
        _opt = opt;
        _store = store;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var o = _opt.CurrentValue;
            try
            {
                if (o.ScreenshotRetentionDays > 0 || o.DataRetentionDays > 0)
                    _store.PurgeOld(o.ScreenshotRetentionDays, o.DataRetentionDays);
            }
            catch
            {
                // סבב מחיקה שנכשל לא מפיל את השירות.
            }

            int hours = Math.Max(1, o.RetentionCheckHours);
            try { await Task.Delay(TimeSpan.FromHours(hours), ct); }
            catch (TaskCanceledException) { }
        }
    }
}
