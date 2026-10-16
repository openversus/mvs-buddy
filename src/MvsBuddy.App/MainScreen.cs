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
        rml.Append($"<div class=\"actions\">{Button("License Notices", ShowNotices)}{Button("Back", Back, "primary")}</div>");
        Show(rml.ToString());
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

    private void Reload()
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
        rml.Append(MenuEntryOffer());

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
                        progress => buddy.States.PrepareSwitchToOpenVersus(answered, channel, progress), $"Switched to OpenVersus {channel}.")), "primary")
                    : Button($"Turn OpenVersus {channel} on", () => Prepare($"Turn OpenVersus {channel} on?",
                        progress => buddy.States.PrepareTurnOn(s, channel, progress), $"OpenVersus {channel} is on."), "primary"));
            }
        }
        if (s.WineOverridesMissing.Count > 0)
        {
            rml.Append(Button("Enable WINEDLLOVERRIDES for loader", () => Prepare("Enable a Wine/Proton registry DLL override for the OpenVersus mod loader?", _ => buddy.States.PrepareWineSetting(s),
                "Wine DLL override (registry) for the mod loader is now enabled."), "primary"));
        }
        if (canUndo)
        {
            rml.Append(Button("Undo the last change", () => Prepare("Undo the last change?", progress => buddy.States.PrepareUndo(s, progress), "The last change was undone.")));
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

    // Asked once, after the first change, and answered either way for good; the answer can be changed later where the
    // buddy's own settings live. Yes writes the entry now and keeps it pointing at MVS Buddy wherever it moves.
    private string MenuEntryOffer()
    {
        if (!offerMenuEntry || buddy.Preferences.MenuEntry != null || Environment.ProcessPath is not string executable)
        {
            return "";
        }
        var rml = new StringBuilder("<div class=\"card\"><p class=\"strong\">Add MVS Buddy to the Games section of your menu?</p>");
        rml.Append($"<p class=\"muted\">The entry would start {Html.Text(executable)}. Nothing else changes, and you will not be asked again.</p><div class=\"row\">");
        rml.Append(Button("Add it", () =>
        {
            offerMenuEntry = false;
            try
            {
                DesktopEntry.Install(executable, Icons.Embedded());
                buddy.Preferences.SetMenuEntry(true);
                message = ("MVS Buddy is in the Games section of your menu.", false);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                message = ($"The menu entry could not be written ({e.Message}).", true);
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
        string tag = release.Tag;
        ui.Run(() => buddy.States.PrepareInstallAsync(s, release, null, progress, stop.Token),
            prepared => Confirm($"Install {tag}?", prepared, $"OpenVersus {tag} is installed.", "Install"),
            e => NotReady(e, "The install was cancelled; nothing was changed."));
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

    // Uninstalling OpenVersus, or, when MVS Buddy kept a first look at this copy, rolling the copy back to it instead.
    private void ChooseUninstall(InstallState s, bool openVersusHere)
    {
        void Uninstall() => Prepare("Uninstall OpenVersus?", _ => buddy.States.PrepareUninstall(s), "OpenVersus was uninstalled.");
        if (firstLook is not { } at)
        {
            Uninstall();
            return;
        }
        actions.Clear();
        var rml = new StringBuilder(openVersusHere ? "<h2>Uninstall</h2>" : "<h2>Roll back</h2>");
        if (openVersusHere)
        {
            rml.Append("<div class=\"card\"><p class=\"strong\">Uninstall OpenVersus</p><p class=\"muted\">Its files and paks are deleted, with every "
                + "channel kept for this copy. There is no undo.</p>")
                .Append(Button("Uninstall OpenVersus", Uninstall, "primary")).Append("</div>");
        }
        rml.Append("<div class=\"card\"><p class=\"strong\">Roll back to how it was the first time</p>")
            .Append($"<p class=\"muted\">This copy as it was on {at.LocalDateTime:yyyy-MM-dd}, before MVS Buddy first changed it: what it had then comes back, "
                + "the mod's files and the loader that were not here then go, and other files added since stay. Undo can undo it.</p>")
            .Append(Button("Roll back", () => Prepare("Roll back to how it was the first time?", progress => buddy.States.PrepareRollBack(s, progress),
                "This copy is back to how it was the first time."), openVersusHere ? "" : "primary")).Append("</div>");
        rml.Append($"<div class=\"actions\">{Button("Cancel", ShowGame)}</div>");
        Show(rml.ToString());
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
            rml.Append(Button("Continue", () => Prepare("Clean start?", _ => buddy.States.PrepareSetAside(s, picked), "The clean start is done."), "primary"));
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
                rml.Append(Button("Turn on", () => Prepare($"Turn {name} on?", progress => buddy.States.PrepareTurnOn(s, channel, progress), $"{name} is on."), "small"));
            }
            rml.Append(Button("Delete for good", () => ConfirmDiscard(name,
                $"Its saved state and its paks are deleted from MVS Buddy's folder. Installing {channel} again downloads its paks, and it starts with a new saved state.",
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
                    progress => buddy.States.PrepareSwitchToMvsInfinite(s, progress), "Switched to MVS Infinite."), "small")
                : Button("Put back", () => Prepare(mvsi ? "Put MVS Infinite back?" : "Put back what a clean start set aside?",
                    progress => buddy.States.PreparePutBack(s, kind, progress), $"{name} is back."), "small"));
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
    private void Prepare(string title, Func<IProgress<string>, PreparedChange> prepare, string done)
    {
        message = null;
        string line = "Getting it ready...";
        bool waiting = true;
        void Render()
        {
            actions.Clear();
            Show($"<h2>{Html.Text(title)}</h2><p class=\"working\">{Html.Text(line)}</p><p class=\"muted\">Nothing in the game changes until you say so.</p>");
        }
        Render();
        var progress = new Lines(ui, text =>
        {
            line = text;
            if (waiting)
            {
                Render();
            }
        });
        ui.Run(() => Task.FromResult(prepare(progress)), change =>
        {
            waiting = false;
            Confirm(title, change, done);
        }, e =>
        {
            waiting = false;
            NotReady(e);
        });
    }

    // What a change will do, as the change itself says, and the go-ahead; a change that cannot be made says why instead.
    private void Confirm(string title, PreparedChange change, string done, string go = "Go ahead")
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
        else
        {
            rml.Append($"<div class=\"actions\">{Button(go, () => Apply(change, done), "primary")}{Button("Cancel", ShowGame)}</div>");
        }
        Show(rml.ToString());
    }

    private void Apply(PreparedChange change, string done)
    {
        string line = "Working...";
        bool working = true;
        void Render()
        {
            actions.Clear();
            Show($"<p class=\"working\">{Html.Text(line)}</p><p class=\"muted\">Leave the window open until this is done.</p>");
        }
        Render();
        var progress = new Lines(ui, text =>
        {
            line = text;
            if (working)
            {
                Render();
            }
        });
        ui.Run(() =>
        {
            change.Apply(progress);
            return Task.FromResult(true);
        }, _ =>
        {
            working = false;
            message = (done, false);
            offerMenuEntry = OperatingSystem.IsLinux() && buddy.Preferences.MenuEntry == null;
            Reload();
        }, e =>
        {
            working = false;
            if (e is ChangeException { DeniedFolders.Count: > 0 } denied && buddy.Permissions.CanAsk)
            {
                ShowDenied(denied, change, done);
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
    private void ShowDenied(ChangeException denied, PreparedChange change, string done)
    {
        actions.Clear();
        Show($"<p class=\"message bad\">{Html.Text(denied.Message)}</p>"
            + "<p>Windows can allow this account to change files there. It asks an administrator once, then MVS Buddy does this again.</p>"
            + $"<div class=\"actions\">{Button("Allow and try again", () => Allow(denied.DeniedFolders, change, done), "primary")}{Button("Cancel", Reload)}</div>");
    }

    private void Allow(IReadOnlyList<string> folders, PreparedChange change, string done)
    {
        Show(Working("Asking Windows for permission... Answer the prompt Windows shows."));
        ui.Run(() => buddy.Permissions.AskAsync(folders), result =>
        {
            if (result.Outcome == PermissionOutcome.Granted)
            {
                Apply(change, done);
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
