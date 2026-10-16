using System.Reflection;
using Microsoft.Extensions.Logging;
using MvsBuddy;
using MvsBuddy.App;
using RmlUi;
using RmlUi.Desktop;
using Serilog;
using Serilog.Extensions.Logging;

// MVS Buddy: finds MultiVersus and installs, turns off and undoes OpenVersus there, for people who should never need to
// open a folder. Logs go to MVS Buddy's own folder (logs/), one file a day. For looking at it without a person:
// --game=<Win64 folder> uses that copy instead of looking for one, --click="Label;Label" clicks those buttons in turn, and
// --screenshot=<file.ppm> then saves the window and exits (OpenGL; Linux); with --screenshot-when=<text> it saves instead
// on the first frame the content shows that text, for a screen that is only up while work runs. --here runs this copy
// where it is instead of from MVS Buddy's folder (SelfInstall); --version prints the build number and exits.

string version = typeof(MainScreen).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
// How one copy asks another what it is (SelfInstall): the build number alone, nothing else started.
if (args.Contains("--version"))
{
    Console.WriteLine(version.Split('+')[0]);
    return 0;
}
string? screenshot = Option("--screenshot");
string? gameFolder = Option("--game");
string? clicks = Option("--click");
string? screenshotWhen = Option("--screenshot-when");

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

// MVS Buddy runs from its one known place: a copy started from anywhere else puts itself there (or hands over to a newer
// one already there) before any window. --here runs in place, for building and checking it. A failure to copy or to
// start the installed one is logged and this copy runs on: never a reason not to start.
// Only the single-file build is one file to copy: a build run from its output folder (its assemblies beside it) stays
// where it is, since a copy of its host alone could not start.
bool bundled = string.IsNullOrEmpty(typeof(MainScreen).Assembly.Location);
if (bundled && !args.Contains("--here") && Environment.ProcessPath is string running)
{
    try
    {
        switch (SelfInstall.Decide(running, version, exe =>
        {
            string? theirs = SelfInstall.VersionOf(exe);
            if (theirs == null)
            {
                log.LogWarning("The copy at {Path} did not say its version (--version), so it is replaced", exe);
            }
            return theirs;
        }))
        {
            case SelfInstall.Outcome.StartInstalled:
                log.LogInformation("Starting the installed copy at {Path}", SelfInstall.InstalledPath());
                SelfInstall.Start(SelfInstall.InstalledPath()!, args);
                return 0;
            case SelfInstall.Outcome.Install:
                string installed = SelfInstall.Install(running);
                log.LogInformation("Installed this copy at {Path} and starting it", installed);
                SelfInstall.Start(installed, args);
                return 0;
        }
    }
    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is System.ComponentModel.Win32Exception)
    {
        log.LogWarning(e, "Could not install or start the copy in MVS Buddy's folder; running from {Path}", running);
    }
}
EventListener.UnhandledException = e => log.LogError(e, "A UI handler failed");
OwnedElement.Leaked = address => log.LogWarning("An element made in code was never attached ({Address})", address);

try
{
    using var buddy = new BuddyBuilder().Logging(loggerFactory).Build();
    // For a person who asked for a menu entry (Linux) or a desktop shortcut (Windows), it follows MVS Buddy wherever it
    // was unpacked; a failure here is not worth stopping for.
    try
    {
        if (buddy.Preferences.MenuEntry == true)
        {
            string? entry = OperatingSystem.IsWindows() ? Shortcut.Install(Environment.ProcessPath ?? "") : DesktopEntry.Install(Environment.ProcessPath ?? "", Icons.Embedded());
            if (entry != null)
            {
                log.LogInformation("Wrote {Entry}", entry);
            }
        }
    }
    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
    {
        log.LogWarning(e, "Could not write the menu entry or shortcut");
    }
    // The app id is the menu entry's name, so a Wayland desktop shows the entry's icon on the window; elsewhere the
    // window carries the icon itself.
    var builder = new RmlWindowBuilder("MVS Buddy", 1024, 768)
        .Resizable()
        .AppId(DesktopEntry.IconName)
        .Icon(Icons.Embedded()[256])
        .Assets(typeof(MainScreen).Assembly)
        .AddFont("fonts/LatoLatin-Regular.ttf", "Lato")
        .AddFont("fonts/LatoLatin-Bold.ttf", "Lato", weight: Style.FontWeight.Bold);
    if (screenshot != null)
    {
        builder.Renderer(RmlRenderer.OpenGL);
    }
    using var window = builder.Build();
    log.LogInformation("Window: {Renderer}, icon {Icon}", window.Renderer, window.HasIcon ? "on the window" : "from the desktop entry");
    var ui = new UiThread(window);
    var screen = new MainScreen(window, ui, buddy, version.Split('+')[0], loggerFactory.CreateLogger<MainScreen>());
    screen.Start(gameFolder == null ? null : buddy.Games.Choose(gameFolder));
    Action? afterFrame = screenshot == null || screenshotWhen != null ? null : Screenshot.When(window, ui, screenshot);
    if (clicks != null)
    {
        afterFrame = new Autopilot(window, ui, clicks.Split(';', StringSplitOptions.RemoveEmptyEntries), afterFrame).AfterFrame;
    }
    if (screenshot != null && screenshotWhen != null)
    {
        // Watched every frame beside the clicks, since the screen it waits for is up only while the clicked work runs.
        var clicking = afterFrame;
        var shown = Screenshot.WhenShown(window, ui, screenshot, screenshotWhen);
        afterFrame = () =>
        {
            clicking?.Invoke();
            shown();
        };
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
