using System.Diagnostics;
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
/// MVS Buddy's one window: finding the game, what is installed there, and the changes it offers. Each view is RML put
/// into <c>#content</c>; its buttons carry a <c>data-action</c> that names a handler registered for that render, so one
/// click listener on the document serves every view and no element handle outlives the render that made it.
/// </summary>
internal sealed class MainScreen
{
    private readonly RmlWindow window;
    private readonly UiThread ui;
    private readonly Buddy buddy;
    private readonly ILogger log;
    private readonly ElementDocument document;
    private readonly Dictionary<string, Action> actions = [];
    private IReadOnlyList<GameInstall> found = [];
    private GameInstall? game;
    private InstallState? state;
    private (string Text, bool Bad)? message;
    private readonly string version;
    // The menu entry is offered once, after the person's first change: not before they have had a reason to want it.
    private bool offerMenuEntry;
    private readonly Dictionary<Channel, IReadOnlyList<Release>> releases = [];
    private readonly HashSet<Channel> fetching = [];
    private Channel channel = Channel.Prod;
    private bool otherVersions;
    private bool onGame;
    private string? cleanStartUnavailable;
    private IReadOnlyList<SetAside> kept = [];
    private IReadOnlyList<LaunchOptionsAccount> launchOptions = [];
    private bool canUndo;
    // The update check: once per run when the person wants one, else on their button; what it found, or that it found
    // nothing newer, or how it failed.
    private Update? update;
    private string? updateNote;
    private bool checkingUpdates;
    private DateTimeOffset? firstLook;
    private bool picking;

    public MainScreen(RmlWindow window, UiThread ui, Buddy buddy, string version, ILogger log)
    {
        this.window = window;
        this.ui = ui;
        this.buddy = buddy;
        this.version = version;
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
            if (element.GetAttribute("data-action")?.ToString() is string id)
            {
                // The header's About button is in the document for good, not registered per screen.
                if (id == "about")
                {
                    ui.Post(ShowAbout);
                    return;
                }
                if (actions.TryGetValue(id, out var action))
                {
                    ui.Post(action);
                    return;
                }
            }
        }
    }

    // About ------------------------------------------------------------------------------------------------------------

    // Back from About goes to the copy's screen when one is chosen, else to the list of copies.
    private void ShowAbout()
    {
        actions.Clear();
        var rml = new StringBuilder($"<h2>MVS Buddy {Html.Text(version)}</h2>");
        rml.Append("<p>The OpenVersus community mod installer and version manager for MVS.</p>");
        rml.Append("<p>MVS Buddy can install OpenVersus, easily switch between OVS release channels, enable, disable, undo any changes that it made, "
            + "and can also restore the game to a clean, stock state.</p><p></p>");
        rml.Append($"<p>This program is authored by RosettaSt0ned and made for people like Bumpy, AP, Choji, Shiro, and Messiah, so that saints like dragoniota, RetroTrue, aid, kiwi, "
            + "and Tuggernuts can play the game too.</p>");
        rml.Append($"<p class=\"muted\">MVS Buddy keeps its own files and logs in: {Html.Text(BuddyBuilder.DefaultDataDirectory)}. Nothing the game reads during runtime is ever in there.</p>");
        rml.Append("<p class=\"muted\">Nobody is paid for any of this. OpenVersus is its own project, with its own developers (half of which happen to be me); MVS Buddy only installs it.</p>");
        rml.Append(UpdateSettings());
        rml.Append($"<div class=\"actions\">{Button("License Notices", ShowNotices)}{Button("Back", Back, "primary")}</div>");
        Show(rml.ToString());
    }

    // The two update settings, each a pair of buttons with the current one lit: looking at startup (on unless turned
    // off) and installing without asking (off unless turned on).
    private string UpdateSettings()
    {
        var rml = new StringBuilder("<h3>Updates</h3>");
        string Pair(string label, bool on, Action<bool> set) =>
            $"<div class=\"row\"><span>{Html.Text(label)}</span> {Button("On", () => Setting(() => set(true)), on ? "primary small" : "small")}{Button("Off", () => Setting(() => set(false)), on ? "small" : "primary small")}</div>";
        rml.Append(Pair("Look for a newer MVS Buddy at startup:", buddy.Preferences.CheckUpdates, buddy.Preferences.SetCheckUpdates));
        rml.Append(Pair("Download and restart as the new version without asking:", buddy.Preferences.AutoUpdate, buddy.Preferences.SetAutoUpdate));
        rml.Append($"<p class=\"muted\">Kept in {Html.Text(Preferences.FileName)} in MVS Buddy's folder. Looking is one request to GitHub; nothing is downloaded unless you say so here or on the button.</p>");
        return rml.ToString();
    }

    private void Setting(Action set)
    {
        try
        {
            set();
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            message = ($"The setting could not be written ({e.Message}).", true);
        }
        ShowAbout();
    }

    // What MVS Buddy is made of and installs, each under its license; the texts are embedded in the executable.
    private void ShowNotices()
    {
        actions.Clear();
        var rml = new StringBuilder("<h2>License Notices</h2><p class=\"muted\">MVS Buddy either uses, ships with these built-in, or installs these things: each is its authors' work under the "
        + "named and included license, provided by its authors. The \"Show License\" button displays the license as they wrote it.</p>");
        foreach (var notice in Notices.All)
        {
            rml.Append($"<div class=\"card\"><p class=\"strong\">{Html.Text(notice.Name)} <span class=\"muted\">{Html.Text(notice.License)}</span></p>");
            rml.Append($"<p class=\"muted\">{Html.Text(notice.Role)}</p><div class=\"row\">{Button("Show License", () => ShowNotice(notice), "small")}</div></div>");
        }
        rml.Append($"<div class=\"actions\">{Button("Back", ShowAbout)}</div>");
        Show(rml.ToString());
    }

    private void ShowNotice(Notice notice)
    {
        actions.Clear();
        // Line by line: RmlUi folds a run of newlines into one even when white space is kept, so each is a break of its own.
        string text = Html.Text(notice.Text().Replace("\r\n", "\n")).Replace("\n", "<br/>");
        Show($"<h2>{Html.Text(notice.Name)}</h2><p class=\"muted\">{Html.Text(notice.License)}</p><div class=\"license\">{text}</div>"
            + $"<div class=\"actions\">{Button("Back", ShowNotices)}</div>");
    }

    private void Back()
    {
        if (state != null)
        {
            ShowGame();
        }
        else
        {
            ShowGames();
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
            rml.Append("<p>It is not where Steam or the Epic Games Launcher keep their games. MVS Buddy can look through every drive for it.</p>");
        }
        else
        {
            rml.Append("<h2>Which copy of MultiVersus?</h2>");
            foreach (var copy in found)
            {
                var chosen = copy;
                rml.Append($"<div class=\"card\"><p class=\"strong\">{Html.Text(StoreName(copy))}</p><p class=\"path\">{Html.Text(GameFolder(copy))}</p>")
                    .Append(Button("Use this one", () => Select(chosen), "primary")).Append("</div>");
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
            rml.Append(Button("Search all drives", Search));
        }
        rml.Append("</div>");
        rml.Append("<h3>Or pick the folder the game is in</h3>");
        rml.Append("<div class=\"row\"><input type=\"text\" id=\"folder\" size=\"40\" value=\"\"/>")
            .Append(Button("Browse...", Browse)).Append(Button("Use this folder", ChooseTyped, "primary")).Append("</div>");
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

    private string TypedFolder() => ElementFormControlInput.From(document.GetElementById("folder")).GetValue().Trim().Trim('"');

    private void ChooseTyped()
    {
        string typed = TypedFolder();
        if (typed.Length > 0)
        {
            ChooseFolder(typed);
        }
    }

    // The system's folder picker, starting in the typed folder when there is one; a folder picked is used as if typed.
    private void Browse()
    {
        if (picking)
        {
            return;
        }
        string typed = TypedFolder();
        var answer = window.ChooseFolderAsync(typed.Length > 0 && Directory.Exists(typed) ? typed : null);
        picking = true;
        ui.Run(() => answer, folder =>
        {
            picking = false;
            if (folder != null)
            {
                ChooseFolder(folder);
            }
        }, e =>
        {
            picking = false;
            log.LogWarning(e, "No folder picker");
            message = ($"{e.Message.TrimEnd('.')}. Type the folder instead.", true);
            ShowGames();
        });
    }

    private void ChooseFolder(string typed)
    {
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
            + "<p>MVS Buddy can use it anyway; it will install there and say what it could not check.</p>"
            + $"<div class=\"actions\">{Button("Yes, use it", () => Select(chosen), "primary")}{Button("No", () => ShowGames())}</div>");
    }

    // One copy of the game ---------------------------------------------------------------------------------------------

    private void Select(GameInstall chosen)
    {
        game = chosen;
        Reload();
    }

    private void Reload() => Refresh(ShowGame);

    // Reads the copy again (what is installed, kept, undoable), then goes on: to its screen, or to the next step of a flow.
    private void Refresh(Action next)
    {
        var copy = game!;
        Show(Working("Reading what is installed..."));
        // What is kept and whether there is an undo are read here, once: both open the copy's store.
        ui.Run(() =>
        {
            var read = buddy.Installs.Read(copy);
            return Task.FromResult((read, buddy.Stock.Unavailable(read), buddy.States.SetAsideOf(read), buddy.States.CanUndo(copy), buddy.States.FirstLookOf(copy),
                buddy.States.LaunchOptionsAccounts(read)));
        }, read =>
        {
            (state, cleanStartUnavailable, kept, canUndo, firstLook, launchOptions) = read;
            next();
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
        rml.Append(MenuEntryOffer());
        rml.Append(UpdateSection());

        rml.Append("<div class=\"actions\">");
        if (s.Mod.Layout != ModLayout.None)
        {
            rml.Append(Button("Turn OpenVersus off", () => Prepare("Turn OpenVersus off?", _ => buddy.States.PrepareTurnOff(s), "OpenVersus is off.")));
        }
        else
        {
            foreach (var channel in KeptChannels())
            {
                // With MVS Infinite here, turning OpenVersus on is a switch: MVS Infinite is set aside in the same change.
                rml.Append(MvsInfiniteLive(s)
                    ? Button($"Switch to OpenVersus {channel}", () => WithPakAnswers(s, answered => Prepare($"Switch to OpenVersus {channel}?",
                        progress => buddy.States.PrepareSwitchToOpenVersus(answered, channel, progress, ui.Closing), $"Switched to OpenVersus {channel}.")), "primary")
                    : Button($"Turn OpenVersus {channel} on", () => Prepare($"Turn OpenVersus {channel} on?",
                        progress => buddy.States.PrepareTurnOn(s, channel, progress, ui.Closing), $"OpenVersus {channel} is on."), "primary"));
            }
        }
        if (s.WineOverridesMissing.Count > 0)
        {
            rml.Append(Button("Enable WINEDLLOVERRIDES for loader", () => Prepare("Enable a Wine/Proton registry DLL override for the OpenVersus mod loader?", _ => buddy.States.PrepareWineSetting(s),
                "Wine DLL override (registry) for the mod loader is now enabled."), "primary"));
        }
        if (canUndo)
        {
            rml.Append(Button("Undo the last change", () => Prepare("Undo the last change?", progress => buddy.States.PrepareUndo(s, progress, ui.Closing), "The last change was undone.",
                instead: ("Delete this undo point instead", DropUndo))));
        }
        bool openVersusHere = s.Mod.Layout != ModLayout.None || KeptChannels().Any() || s.Paks.Count + s.PaksInGame.Count > 0;
        if (openVersusHere || firstLook != null)
        {
            rml.Append(Button(openVersusHere ? "Uninstall" : "Roll back...", () => ChooseUninstall(s, openVersusHere)));
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
        rml.Append(KeptSection(s));
        rml.Append(LaunchOptionsSection(s));

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

    // Under Proton, Steam's launch options can carry the Wine setting too (the registry cannot see them). Off by default;
    // what the buttons show is read from Steam's file, so a refused change leaves them as they were.
    private string LaunchOptionsSection(InstallState s)
    {
        if (launchOptions.Count == 0)
        {
            return "";
        }
        string dlls = string.Join(", ", GameStates.LaunchOptionsDlls(s).Select(d => d + ".dll"));
        var rml = new StringBuilder("<h3>Steam launch options</h3>");
        rml.Append($"<p class=\"muted\">Steam's launch options for the game can tell Wine to load {Html.Text(dlls)} from the game's folder as well (WINEDLLOVERRIDES). "
            + "On adds that to your launch options and off takes it out; the rest of them stay as they are. Steam has to be closed while it is changed.</p>");
        foreach (var account in launchOptions)
        {
            rml.Append($"<div class=\"card\"><p class=\"strong\">{Html.Text(account.Name)}</p><p class=\"muted\">{Html.Text(account.Options.Length == 0 ? "No launch options." : account.Options)}</p><div class=\"row\">");
            foreach (var (on, word) in new[] { (false, "Off"), (true, "On") })
            {
                rml.Append(on == account.LoadsLoader
                    ? Button(word, ShowGame, "tab on")
                    : Button(word, () => Prepare($"Turn the Steam launch options setting {word.ToLowerInvariant()}?",
                        _ => buddy.States.PrepareLaunchOptions(s, account, on), $"Steam's launch options for {account.Name} are changed."), "tab"));
            }
            rml.Append("</div></div>");
        }
        return rml.ToString();
    }

    // Looks for a newer MVS Buddy at startup when the person wants that (on unless turned off): one request, off this
    // thread, and the game's screen says what it found. With the check off, the screen has a button for it instead.
    public void StartUpdateCheck()
    {
        if (buddy.Preferences.CheckUpdates)
        {
            CheckForUpdates();
        }
    }

    private void CheckForUpdates()
    {
        if (checkingUpdates)
        {
            return;
        }
        checkingUpdates = true;
        updateNote = null;
        ui.Run(() => buddy.Updates.CheckAsync(ui.Closing), found =>
        {
            checkingUpdates = false;
            update = found;
            updateNote = found == null ? $"MVS Buddy {version} is the newest." : null;
            if (found != null && buddy.Preferences.AutoUpdate)
            {
                // Only ever at the person's standing say-so (off unless they turned it on).
                DownloadUpdate(found);
                return;
            }
            if (onGame)
            {
                ShowGame();
            }
        }, e =>
        {
            checkingUpdates = false;
            updateNote = e is ReleaseCatalogException or OperationCanceledException ? $"Could not look for a newer MVS Buddy: {e.Message}" : null;
            log.LogWarning(e, "The update check failed");
            if (onGame)
            {
                ShowGame();
            }
        });
    }

    // A newer MVS Buddy, said with a button, never applied on its own; or the button to look, when looking at startup
    // is off; or what the last look found.
    private string UpdateSection()
    {
        var rml = new StringBuilder();
        if (update is { } newer)
        {
            rml.Append($"<div class=\"card\"><p class=\"strong\">MVS Buddy {Html.Text(newer.Tag)} is out; this is {Html.Text(version)}.</p>")
                .Append("<p class=\"muted\">Download it and MVS Buddy restarts as the new version, with everything as it is now.</p><div class=\"row\">")
                .Append(Button("Download and restart", () => DownloadUpdate(newer), "primary")).Append("</div></div>");
        }
        else if (!buddy.Preferences.CheckUpdates)
        {
            rml.Append("<div class=\"row\">").Append(checkingUpdates ? "<button class=\"off\">Looking...</button>" : Button("Check for updates", CheckForUpdates, "small"));
            if (updateNote != null)
            {
                rml.Append($" <span class=\"muted\">{Html.Text(updateNote)}</span>");
            }
            rml.Append("</div>");
        }
        // With looking on, a look that failed (offline) says nothing here: the log has it, and the screen already says
        // when the releases could not be read.
        return rml.ToString();
    }

    // Fetches the newer MVS Buddy and starts it in this one's place: the new copy puts itself in MVS Buddy's folder,
    // being newer, and runs from there.
    private void DownloadUpdate(Update newer)
    {
        var work = new Work();
        void Render()
        {
            actions.Clear();
            Show($"<h2>Updating to MVS Buddy {Html.Text(newer.Tag)}</h2>{WorkingRml(work, "Starting...", "MVS Buddy restarts as the new version when this is done.")}");
        }
        var progress = Watch(work, Render);
        ui.Run(() => buddy.Updates.DownloadAsync(newer, progress, ui.Closing), executable =>
        {
            work.On = false;
            log.LogInformation("Starting MVS Buddy {Tag} at {Path} and exiting", newer.Tag, executable);
            Updates.Start(executable, Environment.GetCommandLineArgs().Skip(1));
            window.RequestExit();
        }, e =>
        {
            work.On = false;
            if (e is DownloadException or ReleaseCatalogException or OperationCanceledException or System.ComponentModel.Win32Exception)
            {
                message = ($"The update could not be fetched or started: {e.Message}", true);
                ShowGame();
            }
            else
            {
                Failed(e);
            }
        });
    }

    // Asked once, after the first change, and answered either way for good; the answer can be changed later where the
    // buddy's own settings live. Yes writes the entry now and keeps it pointing at MVS Buddy wherever it moves.
    private string MenuEntryOffer()
    {
        if (!offerMenuEntry || buddy.Preferences.MenuEntry != null || Environment.ProcessPath is not string executable)
        {
            return "";
        }
        bool windows = OperatingSystem.IsWindows();
        var rml = new StringBuilder($"<div class=\"card\"><p class=\"strong\">{(windows ? "Put a shortcut to MVS Buddy on your desktop?" : "Add MVS Buddy to the Games section of your menu?")}</p>");
        rml.Append($"<p class=\"muted\">{(windows ? "The shortcut" : "The entry")} would start {Html.Text(executable)}. Nothing else changes, and you will not be asked again.</p><div class=\"row\">");
        rml.Append(Button("Add it", () =>
        {
            offerMenuEntry = false;
            try
            {
                if (windows)
                {
                    Shortcut.Install(executable);
                }
                else
                {
                    DesktopEntry.Install(executable, Icons.Embedded());
                }
                buddy.Preferences.SetMenuEntry(true);
                message = (windows ? "The shortcut is on your desktop." : "MVS Buddy is in the Games section of your menu.", false);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                message = ($"{(windows ? "The shortcut" : "The menu entry")} could not be written ({e.Message}).", true);
            }
            ShowGame();
        }, "primary"));
        rml.Append(Button("No thanks", () =>
        {
            offerMenuEntry = false;
            buddy.Preferences.SetMenuEntry(false);
            ShowGame();
        }));
        return rml.Append("</div></div>").ToString();
    }

    // Installing -------------------------------------------------------------------------------------------------------

    // The channel to install from, its newest release on the main button, and the others on request.
    private string InstallSection(InstallState s)
    {
        var rml = new StringBuilder("<h3>Install another version:</h3><div class=\"row\">");
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

    // Downloads and checks everything first; nothing in the game changes until the person confirms. With MVS Infinite here,
    // the person first says which other paks are its, since they all move out of the way with it.
    private void Prepare(Release release) => WithPakAnswers(state!, answered => PrepareInstall(answered, release));

    private void PrepareInstall(InstallState s, Release release)
    {
        message = null;
        var stop = CancellationTokenSource.CreateLinkedTokenSource(ui.Closing);
        var work = new Work();
        void Render()
        {
            actions.Clear();
            Show($"<h2>Getting {Html.Text(release.Tag)} ready</h2>{WorkingRml(work, "Starting...", "Nothing in the game changes until you say so.")}"
                + $"<div class=\"actions\">{Button("Cancel", stop.Cancel)}</div>");
        }
        var progress = Watch(work, Render);
        string tag = release.Tag;
        ui.Run(() => buddy.States.PrepareInstallAsync(s, release, null, progress, stop.Token),
            prepared =>
            {
                work.On = false;
                Confirm($"Install {tag}?", prepared, $"OpenVersus {tag} is installed.", "Install");
            },
            e =>
            {
                work.On = false;
                NotReady(e, "The install was cancelled; nothing was changed.");
            });
    }

    // A change that could not be got ready: said on the game's screen, unless it is something unexpected.
    private void NotReady(Exception e, string cancelled = "Cancelled; nothing was changed.")
    {
        if (e is OperationCanceledException)
        {
            message = (cancelled, false);
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
    }

    // Uninstalling, as a flow of choices, nothing assumed: OpenVersus goes with every channel kept for this copy, the copy
    // is left as the person picks (the first look, MVS Infinite, what a clean start set aside, stock, or as it is now), and
    // then MVS Buddy's own files go or stay at their say-so, the preferences asked about on their own.
    private void ChooseUninstall(InstallState s, bool openVersusHere)
    {
        actions.Clear();
        bool uninstall = openVersusHere || kept.Any(k => k.Kind == SetAsideKind.OpenVersus);
        var rml = new StringBuilder(openVersusHere ? "<h2>Uninstall OpenVersus</h2>" : "<h2>Roll back</h2>");
        rml.Append(uninstall
            ? "<p>OpenVersus is deleted from this copy, with every channel kept for it; there is no undo for that. Then this copy is left as you pick here, and then you say what happens to MVS Buddy's own files.</p>"
            : "<p>Pick what this copy is left as. Then you say what happens to MVS Buddy's own files.</p>");

        // One step after another, each with its own look and go-ahead, the copy read again between them.
        void Run(Action<Action> endState, bool cleanStart = false)
        {
            void AfterUninstall() => endState(() => Refresh(() => ChooseDataRemoval(cleanStart)));
            if (uninstall)
            {
                Prepare("Uninstall OpenVersus?", _ => buddy.States.PrepareUninstall(state!), "OpenVersus was uninstalled.", then: () => Refresh(AfterUninstall));
            }
            else
            {
                AfterUninstall();
            }
        }
        void Card(string title, string text, string button, Action go) =>
            rml.Append($"<div class=\"card\"><p class=\"strong\">{Html.Text(title)}</p><p class=\"muted\">{Html.Text(text)}</p>{Button(button, go)}</div>");

        if (firstLook is { } at)
        {
            Card("How it was the first time", $"This copy as it was on {at.LocalDateTime:yyyy-MM-dd}, before MVS Buddy first changed it: what it had then comes back, "
                + "the mod's files and the loader that were not here then go, and other files added since stay.", "Leave it as it was the first time",
                () => Run(next => Prepare("Roll back to how it was the first time?", progress => buddy.States.PrepareRollBack(state!, progress, ui.Closing),
                    "This copy is back to how it was the first time.", then: next)));
        }
        if (kept.Any(k => k.Kind == SetAsideKind.MvsInfinite) && !MvsInfiniteLive(s))
        {
            Card("MVS Infinite", "MVS Infinite, set aside, goes back where it was, with its paks.", "Leave it with MVS Infinite",
                () => Run(next => Prepare("Put MVS Infinite back?", progress => buddy.States.PreparePutBack(state!, SetAsideKind.MvsInfinite, progress, ui.Closing),
                    "MVS Infinite is back.", then: next)));
        }
        if (kept.Any(k => k.Kind == SetAsideKind.Rest))
        {
            Card("What a clean start set aside", "The other mods, tools and backups a clean start set aside go back where they were.", "Leave it with those back",
                () => Run(next => Prepare("Put back what a clean start set aside?", progress => buddy.States.PreparePutBack(state!, SetAsideKind.Rest, progress, ui.Closing),
                    "What a clean start set aside is back.", then: next)));
        }
        if (cleanStartUnavailable == null)
        {
            Card("Stock, as the store installed it", "A clean start: everything the game did not ship is set aside in MVS Buddy's folder. "
                + "If you then delete MVS Buddy's files, what was set aside goes with them.", "Leave it stock",
                () => Run(next =>
                {
                    Show(Working("Looking through the game's folder..."));
                    ui.Run(() => Task.FromResult(buddy.Stock.Read(state!)), report => ShowCleanStart(report, [ExtraKind.MvsInfinite, ExtraKind.Other], next), Failed);
                }, cleanStart: true));
        }
        Card("As it is now", uninstall ? "Nothing more changes in the game's folder once OpenVersus is gone." : "Nothing changes in the game's folder.",
            uninstall ? "Leave it as it is, without OpenVersus" : "Leave it as it is", () => Run(next => next()));
        rml.Append($"<div class=\"actions\">{Button("Cancel", ShowGame)}</div>");
        Show(rml.ToString());
    }

    // The last step: MVS Buddy's own files, which only this deletes, and only at the person's say-so.
    private void ChooseDataRemoval(bool cleanStartJustRan)
    {
        actions.Clear();
        var rml = new StringBuilder("<h2>MVS Buddy's own files</h2>")
            .Append($"<p>Everything MVS Buddy keeps, for every copy of the game, is in {Html.Text(buddy.DataDirectory)}: kept states, what clean starts set aside, "
                + "paks, the release cache, downloads and logs" + (OperatingSystem.IsLinux() ? ", and its entry in the applications menu."
                    : OperatingSystem.IsWindows() ? ", and its shortcut on the desktop." : ".") + "</p>");
        if (cleanStartJustRan)
        {
            rml.Append("<p class=\"bad\">What the clean start just set aside is in there too: deleting MVS Buddy's files deletes it for good.</p>");
        }
        rml.Append("<p>Deleting them means nothing kept and no undo, for any copy. Keeping them costs disk space and nothing else.</p>")
            .Append($"<div class=\"actions\">{Button("Delete MVS Buddy's files", ChoosePreferences, "primary")}{Button("Keep them", () =>
            {
                message = ("Done. MVS Buddy's files are kept.", false);
                Reload();
            })}</div>");
        Show(rml.ToString());
    }

    private void ChoosePreferences()
    {
        actions.Clear();
        Show($"<h2>The preferences too?</h2><p>{Html.Text(Preferences.FileName)} holds your answers to MVS Buddy's questions (the menu entry). "
            + "Deleted, they are asked again if MVS Buddy ever runs here again.</p>"
            + $"<div class=\"actions\">{Button("Delete the preferences too", () => RemoveData(keepPreferences: false), "primary")}{Button("Keep the preferences", () => RemoveData(keepPreferences: true))}</div>");
    }

    private void RemoveData(bool keepPreferences)
    {
        Show(Working("Deleting MVS Buddy's files..."));
        ui.Run(() => Task.FromResult(buddy.RemoveData(keepPreferences)), removal =>
        {
            actions.Clear();
            var rml = new StringBuilder("<h2>Done</h2><p>MVS Buddy's files are deleted" + (keepPreferences ? ", except the preferences." : ".") + "</p>");
            if (removal.Left.Count > 0)
            {
                rml.Append($"<p class=\"muted\">Still here while MVS Buddy runs, and deleted ten seconds after it closes: {Html.Text(string.Join("; ", removal.Left))}</p>");
            }
            // What could not go while MVS Buddy runs (its own program file, the log) goes ten seconds after it closes (Windows).
            rml.Append($"<div class=\"actions\">{Button("Close MVS Buddy", () =>
            {
                if (removal.Left.Count > 0)
                {
                    SelfInstall.DeleteAfterExit(buddy.DataDirectory);
                }
                window.RequestExit();
            }, "primary")}{Button("Back to the game", Reload)}</div>");
            Show(rml.ToString());
        }, Failed);
    }

    // MVS Infinite is live: a plugin of its name, which the reader finds only when it is there. Every switch button hangs
    // on this or on MVS Infinite being set aside, so a copy without it shows nothing of it.
    private static bool MvsInfiniteLive(InstallState s) => s.OtherPlugins.Any(p => p.IsMvsInfinite) || s.OtherPaks.Any(p => p.IsMvsInfinite);

    /// <summary>A pak and its companions (.ucas, .utoc, .sig): one name in one folder, asked about once.</summary>
    private sealed record PakGroup(string Name, string Folder, IReadOnlyList<string> Files, long Size);

    private static List<PakGroup> Groups(IEnumerable<OtherPak> paks) => paks
        .GroupBy(p => Path.Combine(Path.GetDirectoryName(p.Path)!, Path.GetFileNameWithoutExtension(p.Path)))
        .Select(g => new PakGroup(Path.GetFileNameWithoutExtension(g.Key), Path.GetDirectoryName(g.Key)!, g.Select(p => p.Path).ToList(), g.Sum(p => p.Size)))
        .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();

    // MVS Infinite's paks cannot stay in the game's Content/Paks with OpenVersus, so before OpenVersus goes live beside it
    // the person says whether any other non-stock paks there are its too. The answers are kept for this copy: each pak is
    // asked about once. Without MVS Infinite here, or nothing to ask, it goes straight on.
    private void WithPakAnswers(InstallState s, Action<InstallState> then)
    {
        var unanswered = Groups(s.OtherPaks.Where(p => !p.IsMvsInfinite && !p.Answered));
        if (!MvsInfiniteLive(s) || unanswered.Count == 0)
        {
            then(s);
            return;
        }
        ShowPakAnswers(s, unanswered, [], then);
    }

    private void ShowPakAnswers(InstallState s, List<PakGroup> unanswered, HashSet<string> theirs, Action<InstallState> then)
    {
        actions.Clear();
        var rml = new StringBuilder("<h2>MVS Infinite's paks</h2>");
        var known = Groups(s.OtherPaks.Where(p => p.IsMvsInfinite));
        if (known.Count > 0)
        {
            rml.Append($"<p>These are MVS Infinite's, and move out of the game's folder with it: {Html.Text(string.Join(", ", known.Select(g => g.Name)))}.</p>");
        }
        rml.Append("<p>These paks in the game's Content/Paks are not the game's own. MVS Infinite's paks cannot stay there with OpenVersus, "
            + "so pick any that came with it; the others stay where they are. MVS Buddy remembers your answers for this copy.</p>");
        foreach (var group in unanswered)
        {
            string extensions = string.Join(", ", group.Files.Select(f => Path.GetExtension(f).TrimStart('.')).Order(StringComparer.OrdinalIgnoreCase));
            string where = Path.GetRelativePath(GameFolder(s.Game), group.Folder);
            rml.Append($"<div class=\"card\"><p class=\"strong\">{Html.Text(group.Name)}</p><p class=\"muted\">{Html.Text(where)}: {Html.Text(extensions)}, {Size(group.Size)}</p>");
            bool picked = theirs.Contains(group.Name);
            foreach (var (move, word) in new[] { (false, "Not MVS Infinite's"), (true, "MVS Infinite's: move it") })
            {
                rml.Append(Button(word, () =>
                {
                    if (move)
                    {
                        theirs.Add(group.Name);
                    }
                    else
                    {
                        theirs.Remove(group.Name);
                    }
                    ShowPakAnswers(s, unanswered, theirs, then);
                }, move == picked ? "tab on" : "tab"));
            }
            rml.Append("</div>");
        }
        rml.Append("<div class=\"actions\">").Append(Button("Continue", () =>
        {
            var copy = s.Game;
            var mine = unanswered.Where(g => theirs.Contains(g.Name)).SelectMany(g => g.Files).ToList();
            var others = unanswered.Where(g => !theirs.Contains(g.Name)).SelectMany(g => g.Files).ToList();
            Show(Working("Reading what is installed..."));
            ui.Run(() =>
            {
                buddy.States.AnswerMvsInfinitePaks(copy, mine, others);
                return Task.FromResult(buddy.Installs.Read(copy));
            }, answered =>
            {
                state = answered;
                then(answered);
            }, Failed);
        }, "primary")).Append(Button("Cancel", ShowGame)).Append("</div>");
        Show(rml.ToString());
    }

    // OpenVersus of a channel that is kept for this copy and not live now: turning it on brings it back as it was.
    private IEnumerable<Channel> KeptChannels() => kept.Where(k => k.Kind == SetAsideKind.OpenVersus).Select(k => k.Channel!.Value);

    private string Status(InstallState s) => s.Status switch
    {
        InstallStatus.Installed => $"OpenVersus {Version(s)}{ChannelName(s)} is installed.",
        InstallStatus.LoaderMissing => $"OpenVersus {Version(s)}{ChannelName(s)} is here, but the loader it needs is missing.",
        _ when KeptChannels().Any() => s.OtherPlugins.Any(p => p.IsMvsInfinite) ? "MVS Infinite is on; OpenVersus is turned off." : "OpenVersus is turned off.",
        InstallStatus.LoaderOnly => "OpenVersus is not installed; only its loader is here.",
        _ => "OpenVersus is not installed.",
    };

    private static string ChannelName(InstallState s) => s.Channel is { } c ? $" ({c.Channel})" : "";

    // How MVS Buddy knows the channel, and the person's way to say otherwise.
    private string ChannelLine(InstallState s)
    {
        if (s.Channel is not { } c)
        {
            return "";
        }
        string how = c.Source switch
        {
            ChannelSource.Install => $"{c.Channel}: MVS Buddy installed it.",
            ChannelSource.Person => $"{c.Channel}: as you said.",
            _ => $"{c.Channel}, going by the server in its settings; MVS Buddy did not install this copy.",
        };
        var rml = new StringBuilder($"<p class=\"muted\">{Html.Text(how)} ");
        if (s.Mod.Layout == ModLayout.Current)
        {
            var other = c.Channel == Channel.Prod ? Channel.Testing : Channel.Prod;
            rml.Append(Button($"It's {other}", () => SayChannel(s, other), "small"));
        }
        return rml.Append("</p>").ToString();
    }

    // The person saying which channel this copy is: done at once, unless it also changes what is kept, which is asked first.
    private void SayChannel(InstallState s, Channel channel)
    {
        message = null;
        string done = $"MVS Buddy counts this copy as {channel} now.";
        Show(Working("Getting it ready..."));
        ui.Run(() => Task.FromResult(buddy.States.PrepareSetChannel(s, channel)), change =>
        {
            if (change.Summary.Count > 1)
            {
                Confirm($"Count this copy as {channel}?", change, done);
            }
            else
            {
                Apply(change, done);
            }
        }, e => NotReady(e));
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
    private void ShowCleanStart(StockReport report, HashSet<ExtraKind> setAside, Action? then = null)
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
                    ShowCleanStart(report, setAside, then);
                }, keep != aside ? "tab on" : "tab"));
            }
            rml.Append("</div>");
        }
        if (report.Extras.Any(e => e.Kind == ExtraKind.Loader))
        {
            rml.Append("<p class=\"muted\">Ultimate ASI Loader goes too when nothing left needs it, unless one was here before MVS Buddy first changed this copy.</p>");
        }
        foreach (string note in report.Notes)
        {
            rml.Append($"<p class=\"muted\">{Html.Text(note)}</p>");
        }
        rml.Append("<div class=\"actions\">");
        if (setAside.Any(k => kinds.Any(x => x.Item1 == k)))
        {
            var picked = report.Extras.Where(e => setAside.Contains(e.Kind)).ToList();
            rml.Append(Button("Continue", () => Prepare("Clean start?", _ => buddy.States.PrepareSetAside(s, picked), "The clean start is done.", then: then), "primary"));
        }
        else if (then != null)
        {
            // A step of a flow with nothing to set aside: the copy is stock already, so the flow goes on.
            rml.Append(Button("Continue", then, "primary"));
        }
        else
        {
            rml.Append("<button class=\"off\">Continue</button>");
        }
        rml.Append(Button("Cancel", ShowGame)).Append("</div>");
        Show(rml.ToString());
    }

    // What is kept for this copy: OpenVersus of a channel not live now, which turns on as it was, and what clean starts set
    // aside, which goes back where it was. Each can be deleted for good.
    private string KeptSection(InstallState s)
    {
        var rml = new StringBuilder();
        var channels = kept.Where(k => k.Kind == SetAsideKind.OpenVersus).ToList();
        if (channels.Count > 0)
        {
            rml.Append("<h3>Kept</h3>");
        }
        foreach (var k in channels)
        {
            var channel = k.Channel!.Value;
            string name = $"OpenVersus {channel}";
            rml.Append($"<div class=\"release\"><span class=\"strong\">{Html.Text(name)}</span> <span class=\"muted\">as it was on {k.At.LocalDateTime:yyyy-MM-dd}</span> ");
            // With OpenVersus off, its "turn on" is above.
            if (s.Mod.Layout != ModLayout.None)
            {
                rml.Append(Button("Turn on", () => Prepare($"Turn {name} on?", progress => buddy.States.PrepareTurnOn(s, channel, progress, ui.Closing), $"{name} is on."), "small"));
            }
            rml.Append(Button("Delete for good", () => ConfirmDiscard(name,
                $"Its saved state is deleted from MVS Buddy's folder; its paks stay kept, so installing {channel} again does not download them. It starts with a new saved state.",
                $"{name} was deleted.", () => buddy.States.Discard(s.Game, SetAsideKind.OpenVersus, channel)), "small")).Append("</div>");
        }
        var sets = kept.Where(k => k.Kind != SetAsideKind.OpenVersus).ToList();
        if (sets.Count > 0)
        {
            rml.Append("<h3>Set aside</h3>");
        }
        foreach (var k in sets)
        {
            var kind = k.Kind;
            bool mvsi = kind == SetAsideKind.MvsInfinite;
            string name = mvsi ? "MVS Infinite" : "What a clean start set aside";
            string files = $"{k.Files:N0} {(k.Files == 1 ? "file" : "files")}";
            rml.Append($"<div class=\"release\"><span class=\"strong\">{Html.Text(name)}</span> <span class=\"muted\">"
                + (k.At > DateTimeOffset.MinValue ? $"{k.At.LocalDateTime:yyyy-MM-dd}, " : "") + $"{files}, {Size(k.Bytes)}</span> ");
            // With OpenVersus on, MVS Infinite comes back as a switch: OpenVersus is set aside in the same change.
            rml.Append(mvsi && s.Mod.Layout != ModLayout.None && !MvsInfiniteLive(s)
                ? Button("Switch to MVS Infinite", () => Prepare("Switch to MVS Infinite?",
                    progress => buddy.States.PrepareSwitchToMvsInfinite(s, progress, ui.Closing), "Switched to MVS Infinite."), "small")
                : Button("Put back", () => Prepare(mvsi ? "Put MVS Infinite back?" : "Put back what a clean start set aside?",
                    progress => buddy.States.PreparePutBack(s, kind, progress, ui.Closing), $"{name} is back."), "small"));
            rml.Append(Button("Delete for good", () => ConfirmDiscard(mvsi ? "MVS Infinite" : "what a clean start set aside",
                $"Its {files} ({Size(k.Bytes)}) {(k.Files == 1 ? "is" : "are")} deleted from MVS Buddy's folder: Put back cannot bring them back after this.",
                $"{name} was deleted.", () => buddy.States.Discard(s.Game, kind)), "small")).Append("</div>");
        }
        return rml.ToString();
    }

    private void ConfirmDiscard(string what, string sentence, string done, Action discard)
    {
        actions.Clear();
        Show($"<h2>Delete {Html.Text(what)} for good?</h2><p>{Html.Text(sentence)}</p><div class=\"actions\">"
            + Button("Delete for good", () =>
            {
                Show(Working("Deleting..."));
                ui.Run(() =>
                {
                    discard();
                    return Task.FromResult(true);
                }, _ =>
                {
                    message = (done, false);
                    Reload();
                }, Failed);
            }, "primary") + Button("Cancel", ShowGame) + "</div>");
    }

    private static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} bytes",
    };

    // Changes ----------------------------------------------------------------------------------------------------------

    // Gets a change ready off this thread (it reads the game's folder and the copy's store, and may checksum paks), then
    // asks before making it.
    private void Prepare(string title, Func<IProgress<string>, PreparedChange> prepare, string done, (string Label, Action Do)? instead = null, Action? then = null)
    {
        message = null;
        var work = new Work();
        void Render()
        {
            actions.Clear();
            Show($"<h2>{Html.Text(title)}</h2>{WorkingRml(work, "Getting it ready...", "Nothing in the game changes until you say so.")}");
        }
        var progress = Watch(work, Render);
        ui.Run(() => Task.FromResult(prepare(progress)), change =>
        {
            work.On = false;
            Confirm(title, change, done, instead: instead, then: then);
        }, e =>
        {
            work.On = false;
            NotReady(e);
        });
    }

    /// <summary>What running work has reported so far, and for how long it has run; <see cref="On"/> false once its screen is gone.</summary>
    private sealed class Work
    {
        public ProgressLines Lines { get; } = new();

        public Stopwatch Clock { get; } = Stopwatch.StartNew();

        public bool On { get; set; } = true;
    }

    // Shows the work now and whenever it reports or a second passes, while it is on; the reporter to hand to the work.
    private Lines Watch(Work work, Action render)
    {
        render();
        ui.Tick(TimeSpan.FromSeconds(1), () =>
        {
            if (work.On)
            {
                render();
            }
            return work.On;
        });
        return new Lines(ui, lines =>
        {
            if (!work.On)
            {
                return;
            }
            foreach (string line in lines)
            {
                work.Lines.Add(line);
            }
            render();
        });
    }

    // The phases so far, the current one last and brighter; after a few seconds, how long it has been at it, so a stretch
    // nothing reports on (libgit2 looking over thousands of files) still shows the window is alive.
    private static string WorkingRml(Work work, string untilFirst, string note)
    {
        var rml = new StringBuilder("<div class=\"lines\">");
        var lines = work.Lines.Lines;
        if (lines.Count == 0)
        {
            rml.Append($"<p class=\"working\">{Html.Text(untilFirst)}</p>");
        }
        for (int i = 0; i < lines.Count; i++)
        {
            rml.Append($"<p class=\"{(i == lines.Count - 1 ? "working" : "muted")}\">{Html.Text(lines[i])}</p>");
        }
        int seconds = (int)work.Clock.Elapsed.TotalSeconds;
        rml.Append($"</div><p class=\"muted note\">{Html.Text(note)}{(seconds >= 3 ? $" {seconds} s so far." : "")}</p>");
        return rml.ToString();
    }

    // What a change will do, as the change itself says, and the go-ahead; a change that cannot be made says why instead. One
    // that will probably break something (a pak it cannot put back) says so here, is asked about a second time, and may offer
    // another way out (deleting the undo point rather than undoing without the pak).
    private void Confirm(string title, PreparedChange change, string done, string go = "Go ahead", (string Label, Action Do)? instead = null, Action? then = null)
    {
        actions.Clear();
        var rml = new StringBuilder($"<h2>{Html.Text(title)}</h2><ul>");
        foreach (string line in change.Summary)
        {
            rml.Append($"<li>{Html.Text(line)}</li>");
        }
        rml.Append("</ul>");
        if (change.Refused is string why)
        {
            if (!change.Summary.Contains(why))
            {
                rml.Append($"<p class=\"bad\">{Html.Text(why)}</p>");
            }
            rml.Append($"<div class=\"actions\">{Button("Back", ShowGame)}</div>");
        }
        else if (change.Risk is string risk)
        {
            rml.Append($"<p class=\"message bad\">{Html.Text(risk)}</p>");
            rml.Append($"<div class=\"actions\">{Button(go, () => ConfirmRisk(title, change, done, go, instead, then), "primary")}"
                + (instead is { } other ? Button(other.Label, other.Do) : "") + $"{Button("Cancel", ShowGame)}</div>");
        }
        else
        {
            rml.Append($"<div class=\"actions\">{Button(go, () => Apply(change, done, then), "primary")}{Button("Cancel", ShowGame)}</div>");
        }
        Show(rml.ToString());
    }

    // The second time of asking, for a change that will probably break something: what, spelled out, then it is the
    // person's to make.
    private void ConfirmRisk(string title, PreparedChange change, string done, string go, (string Label, Action Do)? instead, Action? then)
    {
        actions.Clear();
        Show($"<h2>Really? {Html.Text(title)}</h2><p class=\"message bad\">{Html.Text(change.Risk!)}</p>"
            + "<p>This is asked twice because it will probably leave the game not working until that is done. If that is what you want, go ahead.</p>"
            + $"<div class=\"actions\">{Button($"Yes, {LowerFirst(go)} anyway", () => Apply(change, done, then), "primary")}{Button("Back", () => Confirm(title, change, done, go, instead, then))}</div>");
    }

    private static string LowerFirst(string text) => text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text.Substring(1);

    // The undo point deleted for good, as the way out of an undo that cannot put a pak back.
    private void DropUndo()
    {
        var copy = game!;
        Show(Working("Deleting the undo point..."));
        ui.Run(() =>
        {
            buddy.States.DropUndo(copy);
            return Task.FromResult(true);
        }, _ =>
        {
            message = ("The undo point was deleted; the last change stays as it is.", false);
            Reload();
        }, Failed);
    }

    // then: what comes after a change made as one step of a flow (an uninstall), instead of the copy's screen.
    private void Apply(PreparedChange change, string done, Action? then = null)
    {
        var work = new Work();
        void Render()
        {
            actions.Clear();
            Show(WorkingRml(work, "Working...", "Leave the window open until this is done."));
        }
        var progress = Watch(work, Render);
        ui.Run(() =>
        {
            change.Apply(progress);
            return Task.FromResult(true);
        }, _ =>
        {
            work.On = false;
            message = (done, false);
            if (then != null)
            {
                then();
                return;
            }
            offerMenuEntry = (OperatingSystem.IsLinux() || OperatingSystem.IsWindows()) && buddy.Preferences.MenuEntry == null;
            Reload();
        }, e =>
        {
            work.On = false;
            if (e is ChangeException { DeniedFolders.Count: > 0 } denied && buddy.Permissions.CanAsk)
            {
                ShowDenied(denied, change, done, then);
            }
            else if (e is ChangeException refused)
            {
                message = (refused.Message, true);
                Reload();
            }
            else
            {
                Failed(e);
            }
        });
    }

    // Windows refused the change in some folders: ask for exactly those, then make the same change again.
    private void ShowDenied(ChangeException denied, PreparedChange change, string done, Action? then)
    {
        actions.Clear();
        Show($"<p class=\"message bad\">{Html.Text(denied.Message)}</p>"
            + "<p>Windows can allow this account to change files there. It asks an administrator once, then MVS Buddy does this again.</p>"
            + $"<div class=\"actions\">{Button("Allow and try again", () => Allow(denied.DeniedFolders, change, done, then), "primary")}{Button("Cancel", Reload)}</div>");
    }

    private void Allow(IReadOnlyList<string> folders, PreparedChange change, string done, Action? then)
    {
        Show(Working("Asking Windows for permission... Answer the prompt Windows shows."));
        ui.Run(() => buddy.Permissions.AskAsync(folders), result =>
        {
            if (result.Outcome == PermissionOutcome.Granted)
            {
                Apply(change, done, then);
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

    /// <summary>
    /// Progress lines from running work, shown on the UI thread: the lines since the last frame together in one call, so
    /// a step that reports hundreds of times a second (libgit2 per file) costs one render a frame, and no line is dropped.
    /// </summary>
    private sealed class Lines(UiThread ui, Action<IReadOnlyList<string>> show) : IProgress<string>
    {
        private readonly List<string> pending = [];
        private bool posted;

        public void Report(string value)
        {
            lock (pending)
            {
                pending.Add(value);
                if (posted)
                {
                    return;
                }
                posted = true;
            }
            ui.Post(Flush);
        }

        private void Flush()
        {
            List<string> batch;
            lock (pending)
            {
                batch = [.. pending];
                pending.Clear();
                posted = false;
            }
            show(batch);
        }
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
