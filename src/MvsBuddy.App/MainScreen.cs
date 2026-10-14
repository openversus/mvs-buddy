using System.Text;
using Microsoft.Extensions.Logging;
using MvsBuddy.Catalog;
using MvsBuddy.Changes;
using MvsBuddy.Games;
using MvsBuddy.Installs;
using RmlUi;
using RmlUi.Desktop;

namespace MvsBuddy.App;

/// <summary>
/// The buddy's one window: finding the game, what is installed there, and the changes it offers. Each view is RML put
/// into <c>#content</c>; its buttons carry a <c>data-action</c> that names a handler registered for that render, so one
/// click listener on the document serves every view and no element handle outlives the render that made it.
/// </summary>
internal sealed class MainScreen
{
    private readonly UiThread ui;
    private readonly Buddy buddy;
    private readonly ILogger log;
    private readonly ElementDocument document;
    private readonly Dictionary<string, Action> actions = [];
    private IReadOnlyList<GameInstall> found = [];
    private GameInstall? game;
    private InstallState? state;
    private (string Text, bool Bad)? message;
    private readonly Dictionary<Channel, IReadOnlyList<Release>> releases = [];
    private readonly HashSet<Channel> fetching = [];
    private Channel channel = Channel.Prod;
    private bool otherVersions;
    private bool onGame;
    private string? cleanStartUnavailable;
    private readonly HashSet<ChangePlan> logged = [];

    // Steps a confirm screen lists before pointing at the log for the rest: a clean start can move tens of thousands.
    private const int StepsShown = 200;

    public MainScreen(RmlWindow window, UiThread ui, Buddy buddy, string version, ILogger log)
    {
        this.ui = ui;
        this.buddy = buddy;
        this.log = log;
        document = window.Context.LoadDocument("ui/main.rml");
        document.GetElementById("version").SetInnerRML(Html.Text(version));
        document.Show();
        window.Listen(document, "click", OnClick);
        ui.OnError = Failed;
    }

    /// <summary>Looks for the game where the stores keep it, or uses <paramref name="copy"/>.</summary>
    public void Start(GameInstall? copy = null)
    {
        if (copy != null)
        {
            found = [copy];
            Select(copy);
            return;
        }
        Show(Working("Looking for MultiVersus..."));
        ui.Run(() => Task.FromResult(buddy.Games.FindKnown()), games =>
        {
            found = games;
            if (games.Count == 1)
            {
                Select(games[0]);
            }
            else
            {
                ShowGames();
            }
        }, Failed);
    }

    // The click's element or the nearest one above it with a data-action. The handler runs after this event is done,
    // since it replaces the elements the event is still passing through.
    private void OnClick(Event e)
    {
        for (var element = e.GetTargetElement(); !element.IsNull; element = element.GetParentNode())
        {
            if (element.GetAttribute("data-action")?.ToString() is string id && actions.TryGetValue(id, out var action))
            {
                ui.Post(action);
                return;
            }
        }
    }

    private string Button(string label, Action action, string kind = "")
    {
        string id = $"a{actions.Count}";
        actions[id] = action;
        return $"<button class=\"{kind}\" data-action=\"{id}\">{Html.Text(label)}</button>";
    }

    private void Show(string rml)
    {
        onGame = false;
        document.GetElementById("content").SetInnerRML(rml);
    }

    private string Working(string what)
    {
        actions.Clear();
        return $"<p class=\"working\">{Html.Text(what)}</p>";
    }

    private string Message()
    {
        if (message is not { } m)
        {
            return "";
        }
        return $"<p class=\"message {(m.Bad ? "bad" : "good")}\">{Html.Text(m.Text)}</p>";
    }

    // Finding the game -------------------------------------------------------------------------------------------------

    private void ShowGames(string? searching = null, CancellationTokenSource? stop = null)
    {
        actions.Clear();
        var rml = new System.Text.StringBuilder();
        rml.Append(Message());
        if (found.Count == 0)
        {
            rml.Append("<h2>MultiVersus was not found</h2>");
            rml.Append("<p>It is not where Steam or the Epic Games Launcher keep their games. The buddy can look through every drive for it.</p>");
        }
        else
        {
            rml.Append("<h2>Which copy of MultiVersus?</h2>");
            foreach (var copy in found)
            {
                var chosen = copy;
                rml.Append($"<div class=\"card\"><p class=\"strong\">{Html.Text(StoreName(copy))}</p><p class=\"path\">{Html.Text(GameFolder(copy))}</p>")
                    .Append(Button("Use this one", () => Select(chosen))).Append("</div>");
            }
        }
        rml.Append("<div class=\"actions\">");
        if (searching != null)
        {
            rml.Append($"<p class=\"working\">{Html.Text(searching)}</p>");
            if (stop != null)
            {
                rml.Append(Button("Stop searching", stop.Cancel));
            }
        }
        else
        {
            rml.Append(Button("Search all drives", Search, "primary"));
        }
        rml.Append("</div>");
        rml.Append("<h3>Or type the folder the game is in</h3>");
        rml.Append("<div class=\"row\"><input type=\"text\" id=\"folder\" size=\"40\" value=\"\"/>")
            .Append(Button("Use this folder", ChooseTyped)).Append("</div>");
        Show(rml.ToString());
    }

    private void Search()
    {
        message = null;
        var stop = CancellationTokenSource.CreateLinkedTokenSource(ui.Closing);
        ShowGames("Searching the drives...", stop);
        var progress = new Throttled(ui, p =>
        {
            if (!stop.IsCancellationRequested)
            {
                ShowGames($"Searching the drives... {p.FoldersSearched:N0} folders so far.", stop);
            }
        });
        ui.Run(() => buddy.Games.SearchAsync(progress, stop.Token), games =>
        {
            found = games;
            message = games.Count == 0 ? ("MultiVersus was not found on any drive.", true) : null;
            if (games.Count == 1)
            {
                Select(games[0]);
            }
            else
            {
                ShowGames();
            }
        }, e =>
        {
            if (e is OperationCanceledException)
            {
                message = ("The search was stopped.", false);
                ShowGames();
            }
            else
            {
                Failed(e);
            }
        });
    }

    private void ChooseTyped()
    {
        string typed = ElementFormControlInput.From(document.GetElementById("folder")).GetValue().Trim().Trim('"');
        if (typed.Length == 0)
        {
            return;
        }
        var chosen = buddy.Games.Choose(typed);
        if (chosen.Verified)
        {
            Select(chosen);
            return;
        }
        // Not the game itself: often a folder or two above it, such as the Steam library.
        Show(Working("Looking inside that folder for MultiVersus..."));
        ui.Run(() => Task.FromResult(buddy.Games.FindUnder(typed, ui.Closing)), inside =>
        {
            if (inside.Count > 0)
            {
                ShowFoundInside(chosen, inside);
            }
            else
            {
                AskAsChosen(chosen);
            }
        }, Failed);
    }

    private void ShowFoundInside(GameInstall chosen, IReadOnlyList<GameInstall> inside)
    {
        actions.Clear();
        var rml = new StringBuilder("<h2>MultiVersus is inside the folder you chose</h2>")
            .Append("<p>The folder you chose is not the game's own, but the game is in it. Use this instead?</p>");
        foreach (var copy in inside)
        {
            var use = copy;
            rml.Append($"<div class=\"card\"><p class=\"path\">{Html.Text(GameFolder(copy))}</p>").Append(Button("Use this one", () => Select(use), "primary")).Append("</div>");
        }
        rml.Append($"<div class=\"actions\">{Button("No", () => AskAsChosen(chosen))}</div>");
        Show(rml.ToString());
    }

    // The folder as it was chosen; "No" ends here, since the person most likely picked the wrong folder.
    private void AskAsChosen(GameInstall chosen)
    {
        actions.Clear();
        Show($"<h2>Use the folder you chose?</h2><p>{Html.Text(chosen.UnverifiedReason)}</p><p class=\"path\">{Html.Text(chosen.Win64Directory)}</p>"
            + "<p>The buddy can use it anyway; it will install there and say what it could not check.</p>"
            + $"<div class=\"actions\">{Button("Yes, use it", () => Select(chosen), "primary")}{Button("No", () => ShowGames())}</div>");
    }

    // One copy of the game ---------------------------------------------------------------------------------------------

    private void Select(GameInstall chosen)
    {
        game = chosen;
        Reload();
    }

    private void Reload()
    {
        var copy = game!;
        logged.Clear();
        Show(Working("Reading what is installed..."));
        ui.Run(() =>
        {
            var read = buddy.Installs.Read(copy);
            return Task.FromResult((read, buddy.Stock.Unavailable(read)));
        }, read =>
        {
            (state, cleanStartUnavailable) = read;
            ShowGame();
        }, Failed);
    }

    private void ShowGame()
    {
        actions.Clear();
        var s = state!;
        var rml = new System.Text.StringBuilder();
        rml.Append($"<div class=\"card\"><p class=\"strong\">{Html.Text(StoreName(s.Game))}</p><p class=\"path\">{Html.Text(GameFolder(s.Game))}</p>");
        if (!s.Game.Verified)
        {
            rml.Append($"<p class=\"bad\">Not recognised as MultiVersus: {Html.Text(s.Game.UnverifiedReason)}</p>");
        }
        rml.Append("</div>");
        rml.Append($"<h2>{Html.Text(Status(s))}</h2>");
        rml.Append(ChannelLine(s));
        if (s.Mod.Settings?.GameServerUrl is string server)
        {
            rml.Append($"<p class=\"muted\">Server: {Html.Text(server)}</p>");
        }
        rml.Append(Message());

        rml.Append("<div class=\"actions\">");
        if (s.Status == InstallStatus.Installed || s.Status == InstallStatus.LoaderMissing)
        {
            rml.Append(Button("Turn OpenVersus off", () => Confirm("Turn OpenVersus off?",
                "Its files and paks are set aside in the buddy's own folder, and so is the loader if the buddy installed it and no "
                + "other ASI plugin loads through it; the game runs without them until you turn it back on.",
                buddy.Changes.HoldOpenVersus(s), "OpenVersus is off.")));
        }
        if (s.Status == InstallStatus.Disabled)
        {
            foreach (var held in s.Held.Where(h => h.IsRecorded && InstallChanges.IsOpenVersusSet(h.Name)))
            {
                string name = held.Name;
                rml.Append(Button($"Turn {name} on", () => Confirm($"Turn {name} on?", "Puts back the files that were set aside.",
                    buddy.Changes.Restore(s, name), $"{name} is on."), "primary"));
            }
        }
        if (s.Held.Where(h => h.IsRecorded && h.Placed.Count > 0).OrderByDescending(h => h.HeldAt).FirstOrDefault() is { } undo)
        {
            rml.Append(Button("Undo the last install", () => Confirm("Undo the last install?",
                $"Puts this copy back the way it was before that install ({undo.Name}): the files it added are deleted, and "
                + "so are any changes made to its settings file since.",
                buddy.Changes.Restore(s, undo.Name), "The install was undone.")));
        }
        if (s.Status != InstallStatus.NotInstalled && s.Status != InstallStatus.Disabled)
        {
            rml.Append(Button("Uninstall", () => Confirm("Uninstall OpenVersus?",
                "Takes OpenVersus out: its files, its paks and the loader are set aside in the buddy's own folder as one set, "
                + "which can be put back.", buddy.Installer.Uninstall(s), "OpenVersus was uninstalled.")));
        }
        rml.Append(Button("Check again", () =>
        {
            message = null;
            releases.Clear();
            Reload();
        }));
        rml.Append(Button("Another copy", () =>
        {
            message = null;
            ShowGames();
        }));
        rml.Append("</div>");
        rml.Append(InstallSection(s));
        rml.Append(CleanStartSection());
        rml.Append(HeldSection(s));

        if (s.Notes.Count > 0)
        {
            rml.Append("<h3>Worth knowing</h3><ul>");
            foreach (string note in s.Notes)
            {
                rml.Append($"<li>{Html.Text(note)}</li>");
            }
            rml.Append("</ul>");
        }
        Show(rml.ToString());
        onGame = true;
    }

    // Installing -------------------------------------------------------------------------------------------------------

    // The channel to install from, its newest release on the main button, and the others on request.
    private string InstallSection(InstallState s)
    {
        var rml = new StringBuilder("<h3>Install</h3><div class=\"row\">");
        foreach (var (choice, label) in new[] { (Channel.Prod, "Prod"), (Channel.Testing, "Testing") })
        {
            rml.Append(Button(label, () =>
            {
                channel = choice;
                otherVersions = false;
                ShowGame();
            }, choice == channel ? "tab on" : "tab"));
        }
        rml.Append("</div>");
        if (!releases.TryGetValue(channel, out var list))
        {
            Fetch(channel);
            return rml.Append("<p class=\"working\">Checking GitHub for releases...</p>").ToString();
        }
        if (list.Count == 0)
        {
            return rml.Append("<p class=\"muted\">No releases to install here right now.</p>").ToString();
        }
        if (s.NeedsFirstLaunch)
        {
            rml.Append("<p class=\"bad\">Start the game once and close it first: the paks go in a folder the game makes when it first starts.</p>");
        }
        rml.Append("<div class=\"actions\">").Append(Button(InstallLabel(s, list[0]), () => Prepare(list[0]), "primary"));
        if (!otherVersions && list.Count > 1)
        {
            rml.Append(Button("Other versions", () =>
            {
                otherVersions = true;
                ShowGame();
            }, "small"));
        }
        rml.Append("</div>");
        if (otherVersions)
        {
            foreach (var release in list.Skip(1))
            {
                rml.Append($"<div class=\"release\"><span class=\"strong\">{Html.Text(release.Tag)}</span> "
                    + $"<span class=\"muted\">{release.PublishedAt:yyyy-MM-dd}</span> ")
                    .Append(Button(InstallLabel(s, release), () => Prepare(release), "small")).Append("</div>");
            }
        }
        return rml.ToString();
    }

    private void Fetch(Channel which)
    {
        if (!fetching.Add(which))
        {
            return;
        }
        ui.Run(() => buddy.Releases.ListAsync(which, ui.Closing), list =>
        {
            fetching.Remove(which);
            releases[which] = list;
            if (onGame)
            {
                ShowGame();
            }
        }, e =>
        {
            fetching.Remove(which);
            releases[which] = [];
            message = ($"Could not get the list of releases from GitHub: {e.Message}", true);
            if (onGame)
            {
                ShowGame();
            }
        });
    }

    private static string InstallLabel(InstallState s, Release release)
    {
        if (s.Mod.Version is { } have && release.Version is { } offered)
        {
            return offered > have ? $"Update to {release.Tag}" : offered == have ? $"Reinstall {release.Tag}" : $"Install {release.Tag}";
        }
        return $"Install {release.Tag}";
    }

    // Downloads and checks everything first; nothing in the game changes until the person confirms.
    private void Prepare(Release release)
    {
        var s = state!;
        message = null;
        var stop = CancellationTokenSource.CreateLinkedTokenSource(ui.Closing);
        string line = "Starting...";
        void Render()
        {
            actions.Clear();
            Show($"<h2>Getting {Html.Text(release.Tag)} ready</h2><p class=\"working\">{Html.Text(line)}</p>"
                + "<p class=\"muted\">Nothing in the game changes until you say so.</p>"
                + $"<div class=\"actions\">{Button("Cancel", stop.Cancel)}</div>");
        }
        Render();
        var progress = new Lines(ui, text =>
        {
            line = text;
            if (!stop.IsCancellationRequested)
            {
                Render();
            }
        });
        ui.Run(() => buddy.Installer.PrepareAsync(s, release, null, progress, stop.Token), prepared => ConfirmInstall(prepared), e =>
        {
            if (e is OperationCanceledException)
            {
                message = ("The install was cancelled; nothing was changed.", false);
                ShowGame();
            }
            else if (e is ChangeException || e is DownloadException || e is ReleaseCatalogException)
            {
                message = (e.Message, true);
                ShowGame();
            }
            else
            {
                Failed(e);
            }
        });
    }

    private void ConfirmInstall(PreparedInstall prepared, bool showFiles = false)
    {
        actions.Clear();
        string tag = prepared.Release.Tag;
        var rml = new StringBuilder($"<h2>Install {Html.Text(tag)}?</h2><ul>");
        foreach (string line in prepared.Summary)
        {
            rml.Append($"<li>{Html.Text(line)}</li>");
        }
        rml.Append("</ul>");
        rml.Append($"<div class=\"actions\">{Button("Install", () => Apply(prepared.Plan, $"OpenVersus {tag} is installed."), "primary")}{Button("Cancel", ShowGame)}</div>");
        rml.Append(Files(prepared.Plan, showFiles, () => ConfirmInstall(prepared, showFiles: true)));
        Show(rml.ToString());
    }

    // How many files a plan moves, and the list of its steps when asked for: the first few hundred, and the rest in the log.
    private string Files(ChangePlan plan, bool show, Action showThem)
    {
        var steps = plan.Steps;
        int files = steps.Count(s => s.StartsWith("Move ", StringComparison.Ordinal));
        var rml = new StringBuilder($"<p class=\"muted\">{files:N0} {(files == 1 ? "file is" : "files are")} moved.</p>");
        if (!show)
        {
            return rml.Append(Button("Show the files", showThem, "small")).ToString();
        }
        rml.Append("<div class=\"steps\">");
        foreach (string step in steps.Take(StepsShown))
        {
            rml.Append($"<p>{Html.Text(Shorten(step))}</p>");
        }
        if (steps.Count <= StepsShown)
        {
            return rml.Append("</div>").ToString();
        }
        rml.Append($"<p>...and {steps.Count - StepsShown:N0} more.</p></div>");
        if (logged.Add(plan))
        {
            ui.Run(() =>
            {
                log.LogInformation("Planned, not done yet ({Count} steps):{NewLine}{Steps}", steps.Count, Environment.NewLine, string.Join(Environment.NewLine, steps));
                return Task.FromResult(true);
            }, _ => { }, Failed);
        }
        return rml.Append("<p class=\"muted\">Every step is in the buddy's log:</p>")
            .Append($"<p class=\"path\">{Html.Text(LogFile)}</p>").Append(Button("Open the log", OpenLog, "small")).ToString();
    }

    // Today's log; Program names it the same way.
    private static string LogFile => Path.Combine(BuddyBuilder.DefaultDataDirectory, "logs", $"mvs-buddy-{DateTime.Now:yyyyMMdd}.log");

    private void OpenLog()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(LogFile) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception || e is InvalidOperationException || e is IOException)
        {
            log.LogWarning(e, "Could not open {Log}", LogFile);
        }
    }

    private static string Status(InstallState s) => s.Status switch
    {
        InstallStatus.Installed => $"OpenVersus {Version(s)}{ChannelName(s)} is installed.",
        InstallStatus.Disabled => "OpenVersus is turned off.",
        InstallStatus.LoaderOnly => "OpenVersus is not installed; only its loader is here.",
        InstallStatus.LoaderMissing => $"OpenVersus {Version(s)}{ChannelName(s)} is here, but the loader it needs is missing.",
        _ => "OpenVersus is not installed.",
    };

    private static string ChannelName(InstallState s) => s.Channel is { } c ? $" ({c.Channel})" : "";

    // How the buddy knows the channel, and the person's way to say otherwise.
    private string ChannelLine(InstallState s)
    {
        if (s.Channel is not { } c)
        {
            return "";
        }
        string how = c.Source switch
        {
            ChannelSource.Install => $"{c.Channel}: the buddy installed it.",
            ChannelSource.Person => $"{c.Channel}: as you said.",
            _ => $"{c.Channel}, going by the server in its settings; the buddy did not install this copy.",
        };
        var rml = new StringBuilder($"<p class=\"muted\">{Html.Text(how)} ");
        if (s.Mod.Layout == ModLayout.Current)
        {
            var other = c.Channel == Channel.Prod ? Channel.Testing : Channel.Prod;
            rml.Append(Button($"It's {other}", () => Apply(buddy.Changes.SetChannel(s, other), $"The buddy counts this copy as {other} now."), "small"));
        }
        return rml.Append("</p>").ToString();
    }

    private static string Version(InstallState s) => s.Mod.Version is { } v ? Catalog.ModVersion.Text(v) : "";

    // Clean start ------------------------------------------------------------------------------------------------------

    private string CleanStartSection()
    {
        var rml = new StringBuilder("<h3>Clean start</h3>");
        rml.Append("<p class=\"muted\">Sets aside what the game did not ship, so it runs as the store installed it. Everything set aside can be put back.</p>");
        if (cleanStartUnavailable is string reason)
        {
            return rml.Append("<button class=\"off\">Clean start...</button>").Append($"<p class=\"muted\">{Html.Text(reason)}</p>").ToString();
        }
        return rml.Append(Button("Clean start...", StartCleanStart)).ToString();
    }

    private void StartCleanStart()
    {
        var s = state!;
        message = null;
        Show(Working("Looking through the game's folder..."));
        ui.Run(() => Task.FromResult(buddy.Stock.Read(s)), report => ShowCleanStart(report,
            [ExtraKind.MvsInfinite, ExtraKind.Other]), Failed);
    }

    // The files the game did not ship, by whose they seem to be, each kind kept or set aside as the person picks.
    private void ShowCleanStart(StockReport report, HashSet<ExtraKind> setAside)
    {
        actions.Clear();
        var s = state!;
        var rml = new StringBuilder("<h2>Clean start</h2>");
        if (!report.HasManifests)
        {
            rml.Append($"<p>{Html.Text(string.Join(" ", report.Notes))}</p>")
                .Append($"<div class=\"actions\">{Button("Back", ShowGame)}</div>");
            Show(rml.ToString());
            return;
        }
        var kinds = new[]
        {
            (ExtraKind.OpenVersus, "OpenVersus" + ChannelName(s)),
            (ExtraKind.MvsInfinite, "MVS Infinite"),
            (ExtraKind.Other, "Everything else: other mods, tools, backups"),
        }.Where(k => report.Extras.Any(e => e.Kind == k.Item1)).ToList();
        if (kinds.Count == 0)
        {
            rml.Append("<p>Nothing here needs setting aside: the game's folder holds only what the game shipped.</p>");
        }
        else
        {
            rml.Append("<p>The game's own files stay. Pick what to set aside:</p>");
        }
        foreach (var (kind, label) in kinds)
        {
            var extras = report.Extras.Where(e => e.Kind == kind).ToList();
            bool aside = setAside.Contains(kind);
            rml.Append($"<div class=\"card\"><p class=\"strong\">{Html.Text(label)}</p><p class=\"muted\">{extras.Count:N0} "
                + $"{(extras.Count == 1 ? "file" : "files")}, {Size(extras.Sum(e => e.Size))}</p>");
            foreach (var (keep, word) in new[] { (true, "Keep"), (false, "Set aside") })
            {
                rml.Append(Button(word, () =>
                {
                    if (keep)
                    {
                        setAside.Remove(kind);
                    }
                    else
                    {
                        setAside.Add(kind);
                    }
                    ShowCleanStart(report, setAside);
                }, keep != aside ? "tab on" : "tab"));
            }
            rml.Append("</div>");
        }
        if (report.Extras.Any(e => e.Kind == ExtraKind.Loader))
        {
            rml.Append("<p class=\"muted\">Ultimate ASI Loader goes too, but only when nothing left needs it.</p>");
        }
        foreach (string note in report.Notes)
        {
            rml.Append($"<p class=\"muted\">{Html.Text(note)}</p>");
        }
        rml.Append("<div class=\"actions\">");
        if (setAside.Any(k => kinds.Any(x => x.Item1 == k)))
        {
            rml.Append(Button("Continue", () => ConfirmCleanStart(buddy.Changes.CleanStart(s,
                report.Extras.Where(e => setAside.Contains(e.Kind) || e.Kind == ExtraKind.Loader))), "primary"));
        }
        else
        {
            rml.Append("<button class=\"off\">Continue</button>");
        }
        rml.Append(Button("Cancel", ShowGame)).Append("</div>");
        Show(rml.ToString());
    }

    private void ConfirmCleanStart(CleanStartPlan clean, bool showFiles = false)
    {
        actions.Clear();
        var rml = new StringBuilder("<h2>Clean start?</h2><ul>");
        foreach (string line in clean.Summary)
        {
            rml.Append($"<li>{Html.Text(line)}</li>");
        }
        rml.Append("</ul><p>Each set can be put back from the game's screen, under \"Set aside\".</p>");
        rml.Append($"<div class=\"actions\">{Button("Go ahead", () => Apply(clean.Plan, "The clean start is done."), "primary")}{Button("Cancel", ShowGame)}</div>");
        rml.Append(Files(clean.Plan, showFiles, () => ConfirmCleanStart(clean, showFiles: true)));
        Show(rml.ToString());
    }

    // Sets the buddy holds aside, apart from what undoes an install: each can be put back or deleted for good.
    private string HeldSection(InstallState s)
    {
        // A hold that did not finish is only told about (Notes): which of its files are the only copies is not known.
        var sets = s.Held.Where(h => h.IsRecorded && h.Placed.Count == 0).ToList();
        if (sets.Count == 0)
        {
            return "";
        }
        var rml = new StringBuilder("<h3>Set aside</h3>");
        foreach (var held in sets)
        {
            string name = held.Name;
            int count = held.Entries.Count(e => e.IsHeld);
            rml.Append($"<div class=\"release\"><span class=\"strong\">{Html.Text(name)}</span> <span class=\"muted\">{held.HeldAt:yyyy-MM-dd}, "
                + $"{count:N0} {(count == 1 ? "file" : "files")}</span> ");
            // OpenVersus has its own "turn on" above.
            if (!InstallChanges.IsOpenVersusSet(name))
            {
                rml.Append(Button("Put back", () => Confirm($"Put {name} back?", "Puts every file of the set back where it was.",
                    buddy.Changes.Restore(s, name), $"{name} is back."), "small"));
            }
            rml.Append(Button("Delete for good", () => Confirm($"Delete {name} for good?",
                $"Deletes the {count:N0} {(count == 1 ? "file" : "files")} held as {name} from the buddy's folder. They cannot be put back after this.",
                buddy.Changes.Discard(s, name), $"{name} was deleted."), "small")).Append("</div>");
        }
        return rml.ToString();
    }

    private static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} bytes",
    };

    // Changes ----------------------------------------------------------------------------------------------------------

    private void Confirm(string title, string sentence, ChangePlan plan, string done, bool showFiles = false)
    {
        actions.Clear();
        var rml = new System.Text.StringBuilder();
        rml.Append($"<h2>{Html.Text(title)}</h2><p>{Html.Text(sentence)}</p>");
        rml.Append($"<div class=\"actions\">{Button("Go ahead", () => Apply(plan, done), "primary")}{Button("Cancel", ShowGame)}</div>");
        rml.Append(Files(plan, showFiles, () => Confirm(title, sentence, plan, done, showFiles: true)));
        Show(rml.ToString());
    }

    // A step with its long folders named instead: the game's folder, where the paks go, and the buddy's own.
    private string Shorten(string step)
    {
        var roots = new List<(string Folder, string Name)> { (BuddyBuilder.DefaultDataDirectory, "buddy's folder") };
        if (state?.SavedPaksDirectory is string paks)
        {
            roots.Add((paks, "paks folder"));
        }
        if (game != null)
        {
            roots.Add((GameFolder(game), "game folder"));
        }
        foreach (var (folder, name) in roots.OrderByDescending(r => r.Folder.Length))
        {
            step = step.Replace(folder + Path.DirectorySeparatorChar, name + " \u203a ", StringComparison.Ordinal);
        }
        return step;
    }

    private void Apply(ChangePlan plan, string done)
    {
        Show(Working("Working... Leave the window open until this is done."));
        ui.Run(() =>
        {
            plan.Apply();
            return Task.FromResult(true);
        }, _ =>
        {
            message = (done, false);
            Reload();
        }, e =>
        {
            if (e is ChangeException { DeniedFolders.Count: > 0 } denied && buddy.Permissions.CanAsk)
            {
                ShowDenied(denied, plan, done);
            }
            else if (e is ChangeException change)
            {
                message = (change.Message, true);
                Reload();
            }
            else
            {
                Failed(e);
            }
        });
    }

    // Windows refused the change in some folders: ask for exactly those, then make the same change again.
    private void ShowDenied(ChangeException denied, ChangePlan plan, string done)
    {
        actions.Clear();
        Show($"<p class=\"message bad\">{Html.Text(denied.Message)}</p>"
            + "<p>Windows can allow this account to change files there. It asks an administrator once, then the buddy does this again.</p>"
            + $"<div class=\"actions\">{Button("Allow and try again", () => Allow(denied.DeniedFolders, plan, done), "primary")}{Button("Cancel", Reload)}</div>");
    }

    private void Allow(IReadOnlyList<string> folders, ChangePlan plan, string done)
    {
        Show(Working("Asking Windows for permission... Answer the prompt Windows shows."));
        ui.Run(() => buddy.Permissions.AskAsync(folders), result =>
        {
            if (result.Outcome == PermissionOutcome.Granted)
            {
                Apply(plan, done);
                return;
            }
            message = (result.Message, true);
            Reload();
        }, Failed);
    }

    private void Failed(Exception e)
    {
        log.LogError(e, "Something went wrong");
        message = ($"Something went wrong: {e.Message}", true);
        if (state != null)
        {
            ShowGame();
        }
        else
        {
            ShowGames();
        }
    }

    // Words ------------------------------------------------------------------------------------------------------------

    private static string StoreName(GameInstall copy) => copy.Store switch
    {
        GameStore.Steam => "MultiVersus on Steam",
        GameStore.Epic => "MultiVersus from the Epic Games Launcher",
        _ => copy.FoundBy == FoundBy.Chosen ? "MultiVersus, in the folder you chose" : "MultiVersus",
    };

    // The game's own folder (where the store put it), not Binaries/Win64 inside it.
    private static string GameFolder(GameInstall copy) =>
        copy.Verified ? Path.GetFullPath(Path.Combine(copy.Win64Directory, "..", "..", "..")) : copy.Win64Directory;

    /// <summary>Progress lines from running work, each shown on the UI thread.</summary>
    private sealed class Lines(UiThread ui, Action<string> show) : IProgress<string>
    {
        public void Report(string value) => ui.Post(() => show(value));
    }

    /// <summary>Progress from a search, shown at most every few hundred folders and whenever it finds a copy.</summary>
    private sealed class Throttled(UiThread ui, Action<SearchProgress> show) : IProgress<SearchProgress>
    {
        private int lastShown;

        public void Report(SearchProgress value)
        {
            if (value.Found != null || value.FoldersSearched - Volatile.Read(ref lastShown) >= 250)
            {
                Volatile.Write(ref lastShown, value.FoldersSearched);
                ui.Post(() => show(value));
            }
        }
    }
}
