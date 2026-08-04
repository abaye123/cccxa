using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Cccxa.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Cccxa.Services;

/// <summary>
/// מצלם את המסך במרווחים קבועים, מקטין את הרזולציה ושומר JPEG דחוס.
/// המרווח נקרא מחדש בכל סבב, כך שהוא דינאמי (שינוי ב-appsettings.json משפיע מיד).
/// </summary>
public sealed class ScreenshotService : BackgroundService
{
    private readonly IOptionsMonitor<CccxaOptions> _opt;
    private readonly SnapshotStore _store;

    public ScreenshotService(IOptionsMonitor<CccxaOptions> opt, SnapshotStore store)
    {
        _opt = opt;
        _store = store;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var s = _opt.CurrentValue.Screenshot;
            int interval = Math.Max(5, s.IntervalSeconds);

            try
            {
                bool idleSkip = s.SkipWhenIdleSeconds > 0 &&
                                Win32.GetIdleSeconds() >= s.SkipWhenIdleSeconds;

                if (s.Enabled && !idleSkip)
                    Capture(s);
            }
            catch
            {
                // צילום בודד שנכשל לא מפיל את הלולאה.
            }

            try { await Task.Delay(TimeSpan.FromSeconds(interval), ct); }
            catch (TaskCanceledException) { }
        }
    }

    private void Capture(ScreenshotOptions s)
    {
        var (x, y, w, h) = Win32.VirtualScreen();
        if (w <= 0 || h <= 0) return;

        using var full = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(full))
            g.CopyFromScreen(x, y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);

        double scale = s.Scale <= 0 ? 1.0 : s.Scale;
        int nw = Math.Max(1, (int)(w * scale));
        int nh = Math.Max(1, (int)(h * scale));

        if (s.MaxWidth > 0 && nw > s.MaxWidth)
        {
            double f = (double)s.MaxWidth / nw;
            nw = s.MaxWidth;
            nh = Math.Max(1, (int)(nh * f));
        }

        using var scaled = new Bitmap(nw, nh, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(scaled))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(full, 0, 0, nw, nh);
        }

        var now = DateTimeOffset.Now;
        var dir = Path.Combine(_store.ScreenshotDir, now.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, now.ToString("HH-mm-ss") + ".jpg");

        SaveJpeg(scaled, file, Math.Clamp(s.JpegQuality, 10, 95));

        long bytes = 0;
        try { bytes = new FileInfo(file).Length; } catch { }
        _store.AddScreenshot(now, file, nw, nh, bytes);
    }

    private static void SaveJpeg(Bitmap bmp, string path, int quality)
    {
        var encoder = ImageCodecInfo.GetImageEncoders()
            .First(e => e.FormatID == ImageFormat.Jpeg.Guid);
        using var ep = new EncoderParameters(1);
        ep.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
        bmp.Save(path, encoder, ep);
    }
}
