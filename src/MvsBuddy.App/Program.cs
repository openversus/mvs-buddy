using System.Reflection;
using Microsoft.Extensions.Logging;
using MvsBuddy;
using MvsBuddy.App;
using RmlUi;
using RmlUi.Desktop;
using Serilog;
using Serilog.Extensions.Logging;

// MVS Buddy: finds MultiVersus and installs, turns off and undoes OpenVersus there, for people who should never need to
// open a folder. Logs go to the buddy's own folder (logs/), one file a day. For looking at it without a person:
// --game=<Win64 folder> uses that copy instead of looking for one, --click="Label;Label" clicks those buttons in turn, and
// --screenshot=<file.ppm> then saves the window and exits (OpenGL; Linux).

string version = typeof(MainScreen).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
string? screenshot = Option("--screenshot");
string? gameFolder = Option("--game");
string? clicks = Option("--click");

// Configured in code: a trimmed single-file executable cannot resolve sinks by name from a settings file.
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.Console()
    // Blocking when the queue is full: a plan of thousands of steps logs every one of them, where dropping would lose most.
    .WriteTo.Async(to => to.File(Path.Combine(BuddyBuilder.DefaultDataDirectory, "logs", "mvs-buddy-.log"),
        rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14), blockWhenFull: true)
    .CreateLogger();
using var loggerFactory = new SerilogLoggerFactory(Log.Logger, dispose: true);
var log = loggerFactory.CreateLogger("MvsBuddy");
log.LogInformation("MVS Buddy {Version} on {OS}", version, System.Runtime.InteropServices.RuntimeInformation.OSDescription);
EventListener.UnhandledException = e => log.LogError(e, "A UI handler failed");
OwnedElement.Leaked = address => log.LogWarning("An element made in code was never attached ({Address})", address);

try
{
    using var buddy = new BuddyBuilder().Logging(loggerFactory).Build();
    var builder = new RmlWindowBuilder("MVS Buddy", 780, 560)
        .Resizable()
        .Assets(typeof(MainScreen).Assembly)
        .AddFont("fonts/LatoLatin-Regular.ttf", "Lato")
        .AddFont("fonts/LatoLatin-Bold.ttf", "Lato", weight: Style.FontWeight.Bold);
    if (screenshot != null)
    {
        builder.Renderer(RmlRenderer.OpenGL);
    }
    using var window = builder.Build();
    log.LogInformation("Window: {Renderer}", window.Renderer);
    var ui = new UiThread(window);
    var screen = new MainScreen(window, ui, buddy, version.Split('+')[0], loggerFactory.CreateLogger<MainScreen>());
    screen.Start(gameFolder == null ? null : buddy.Games.Choose(gameFolder));
    Action? afterFrame = screenshot == null ? null : Screenshot.When(window, ui, screenshot);
    if (clicks != null)
    {
        afterFrame = new Autopilot(window, ui, clicks.Split(';', StringSplitOptions.RemoveEmptyEntries), afterFrame).AfterFrame;
    }
    ui.Loop(afterFrame);
    return 0;
}
catch (Exception e)
{
    log.LogCritical(e, "MVS Buddy stopped");
    return 1;
}

string? Option(string name) => args.FirstOrDefault(a => a.StartsWith(name + "=", StringComparison.Ordinal))?[(name.Length + 1)..];
