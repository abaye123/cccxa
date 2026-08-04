using Cccxa;
using Cccxa.Export;
using Cccxa.Report;
using Cccxa.Services;
using Cccxa.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

// -----------------------------------------------------------------------------
// cccxa - כלי רקע שמתעד פעילות מקומית (צילומי מסך, גלישה, תוכנות, קבצים)
//         עבור AI מקומי שרץ על הנתונים שלך. הכול נשמר מקומית בלבד.
// -----------------------------------------------------------------------------

// חשוב: תיקיית הבסיס של ה-exe (ולא ספריית העבודה) - כדי שההגדרות והנתונים
// יימצאו גם כשמריצים כמשימה מתוזמנת עם ספריית עבודה שונה.
var exeDir = AppContext.BaseDirectory;

// הגדרות בשתי שכבות:
//  1) exeConfig  - ברירות המחדל שנשלחות עם ה-exe (ב-Program Files, קריאה בלבד למשתמשים).
//  2) overrideConfig - הקובץ הניתן לעריכה תחת ProgramData (שם ממשק ההגדרות כותב).
// ה-override גובר. שמירתו ב-ProgramData מאפשרת למנהל לשמור ללא הרשאות מוגברות (UAC),
// בעוד משתמשים רגילים מקבלים קריאה בלבד ולא יכולים לשבש את המעקב.
var exeConfig = Path.Combine(exeDir, "appsettings.json");
var overrideConfig = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    "cccxa", "appsettings.json");

// טעינת ההגדרות פעם אחת, משמשת את מצבי ה-CLI ואת שער הסינון.
var config = new ConfigurationBuilder()
    .SetBasePath(exeDir)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .AddJsonFile(overrideConfig, optional: true, reloadOnChange: false)
    .AddEnvironmentVariables()
    .Build();

// -------- מצבי ייצוא (CLI) --------
if (args.Length > 0)
{
    var verb = args[0].ToLowerInvariant();

    // WinExe הוא ללא קונסולה - מתחברים לקונסולת הטרמינל כדי שפלט פקודות CLI יופיע.
    Win32.AttachParentConsole();

    // ייצוא של משתמש בודד: cccxa export [outDir]
    if (verb == "export")
    {
        var root = ResolveRoot(config);
        var outDir = args.Length > 1
            ? args[1]
            : Path.Combine(root, "export_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        return Exporter.Run(root, outDir);
    }

    // ייצוא כל המשתמשים (למנהל): cccxa export-all [outDir]
    if (verb == "export-all")
    {
        var dataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "cccxa", "data");
        var outDir = args.Length > 1
            ? args[1]
            : Path.Combine(dataRoot, "..", "export_all_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        return Exporter.RunAll(dataRoot, outDir);
    }

    // לתצוגת ההגדרות: קורא את ה-override אם קיים, אחרת את ברירות המחדל.
    var configForEmbed = File.Exists(overrideConfig) ? overrideConfig : exeConfig;

    // דשבורד גרפי סטטי למשתמש הנוכחי: cccxa report [out.html]
    if (verb == "report")
    {
        var root = ResolveRoot(config);
        var outHtml = args.Length > 1 ? args[1] : Path.Combine(root, "dashboard.html");
        return ReportBuilder.Build(root, outHtml, configForEmbed, open: true);
    }

    // דשבורד גרפי סטטי לכל המשתמשים (למנהל): cccxa report-all [out.html]
    if (verb == "report-all")
    {
        var dataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "cccxa", "data");
        var outHtml = args.Length > 1
            ? args[1]
            : Path.Combine(Path.GetDirectoryName(dataRoot)!, "dashboard.html");
        return ReportBuilder.BuildAll(dataRoot, outHtml, configForEmbed, open: true);
    }

    // דשבורד חי + ממשק הגדרות על-פי דרישה: cccxa serve [port]
    if (verb == "serve")
    {
        var dataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "cccxa", "data");

        // מוודאים שקובץ ה-override קיים (זריעה מברירות המחדל) כדי שאפשר יהיה לשמור אליו.
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(overrideConfig)!);
            if (!File.Exists(overrideConfig) && File.Exists(exeConfig))
                File.Copy(exeConfig, overrideConfig);
        }
        catch { }

        int port = args.Length > 1 && int.TryParse(args[1], out var pp) ? pp : 0;
        return DashboardServer.Run(dataRoot, overrideConfig, port);
    }
}

// -------- מצב שירות/רקע --------
// שער סינון משתמשים: אם המשתמש הנוכחי לא מורשה (למשל משתמש עבודה שהוחרג) - יוצאים בשקט
// בלי לתעד דבר. זה חל גם כשהמשימה המתוזמנת מופעלת לכל המשתמשים.
{
    var gateOptions = config.GetSection("Cccxa").Get<CccxaOptions>() ?? new CccxaOptions();
    if (!gateOptions.IsUserAllowed(Environment.UserName))
        return 0;
}

// מודעות DPI חייבת להיקבע לפני כל צילום מסך.
Win32.SetDpiAware();

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = exeDir
});

// שכבת ה-override מ-ProgramData גוברת על ברירות המחדל, עם טעינה חוזרת בזמן ריצה -
// כך שעריכה מממשק ההגדרות נכנסת לתוקף מיד גם עבור האוספים שרצים ברקע.
builder.Configuration.AddJsonFile(overrideConfig, optional: true, reloadOnChange: true);

// מאפשר ריצה כ-Windows Service (וגם כקונסולה/משימה מתוזמנת לבדיקה).
builder.Services.AddWindowsService(o => o.ServiceName = "cccxa");

builder.Services.Configure<CccxaOptions>(builder.Configuration.GetSection("Cccxa"));

builder.Services.AddSingleton<SnapshotStore>(sp =>
{
    var opt = sp.GetRequiredService<IOptions<CccxaOptions>>().Value;
    var root = Environment.ExpandEnvironmentVariables(opt.StorageRoot);
    return new SnapshotStore(root);
});

builder.Services.AddHostedService<ScreenshotService>();
builder.Services.AddHostedService<ForegroundBrowserService>();
builder.Services.AddHostedService<ProcessService>();
builder.Services.AddHostedService<RecentFilesService>();
builder.Services.AddHostedService<BrowserHistoryImportService>();
builder.Services.AddHostedService<RetentionService>();

var host = builder.Build();
host.Run();
return 0;

// עוזר: מחשב את תיקיית האחסון עבור מצב הייצוא (env -> appsettings -> ברירת מחדל).
static string ResolveRoot(IConfiguration config)
{
    var raw = Environment.GetEnvironmentVariable("CCCXA_STORAGE_ROOT")
              ?? config["Cccxa:StorageRoot"]
              ?? "%LOCALAPPDATA%\\cccxa";
    return Environment.ExpandEnvironmentVariables(raw);
}
