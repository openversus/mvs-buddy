using Microsoft.Extensions.Logging;
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
        actions.Clear();
        Show($"<h2>Are you really sure?</h2><p>{Html.Text(chosen.UnverifiedReason)}</p><p class=\"path\">{Html.Text(chosen.Win64Directory)}</p>"
            + "<p>The buddy can use it anyway; it will install there and say what it could not check.</p>"
            + $"<div class=\"actions\">{Button("Use it anyway", () => Select(chosen), "primary")}{Button("Back", () => ShowGames())}</div>");
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
        ui.Run(() => Task.FromResult(buddy.Installs.Read(copy)), read =>
        {
            state = read;
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
        if (s.Mod.Settings?.GameServerUrl is string server)
        {
            rml.Append($"<p class=\"muted\">Server: {Html.Text(server)}</p>");
        }
        rml.Append(Message());

        rml.Append("<div class=\"actions\">");
        if (s.Status == InstallStatus.Installed || s.Status == InstallStatus.LoaderMissing)
        {
            rml.Append(Button("Turn OpenVersus off", () => Confirm("Turn OpenVersus off?",
                "Its files and paks, and the loader if the buddy installed it, are set aside in the buddy's own folder; the game runs "
                + "without them until you turn it back on.",
                buddy.Changes.HoldOpenVersus(s), "OpenVersus is off.")));
        }
        if (s.Status == InstallStatus.Disabled)
        {
            rml.Append(Button("Turn OpenVersus on", () => Confirm("Turn OpenVersus on?", "Puts back the files that were set aside.",
                buddy.Changes.Restore(s, InstallChanges.OpenVersusSet), "OpenVersus is on."), "primary"));
        }
        if (s.Held.Where(h => h.IsRecorded && h.Placed.Count > 0).OrderByDescending(h => h.HeldAt).FirstOrDefault() is { } undo)
        {
            rml.Append(Button("Undo the last install", () => Confirm("Undo the last install?",
                $"Puts this copy back the way it was before that install ({undo.Name}): the files it added are deleted, and "
                + "so are any changes made to its settings file since.",
                buddy.Changes.Restore(s, undo.Name), "The install was undone.")));
        }
        rml.Append(Button("Check again", () =>
        {
            message = null;
            Reload();
        }));
        rml.Append(Button("Another copy", () =>
        {
            message = null;
            ShowGames();
        }));
        rml.Append("</div>");

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
    }

    private static string Status(InstallState s) => s.Status switch
    {
        InstallStatus.Installed => $"OpenVersus {Version(s)} is installed.",
        InstallStatus.Disabled => "OpenVersus is turned off.",
        InstallStatus.LoaderOnly => "OpenVersus is not installed; only its loader is here.",
        InstallStatus.LoaderMissing => $"OpenVersus {Version(s)} is here, but the loader it needs is missing.",
        _ => "OpenVersus is not installed.",
    };

    private static string Version(InstallState s) => s.Mod.Version is { } v ? Catalog.ModVersion.Text(v) : "";

    // Changes ----------------------------------------------------------------------------------------------------------

    private void Confirm(string title, string sentence, ChangePlan plan, string done, bool showFiles = false)
    {
        actions.Clear();
        var rml = new System.Text.StringBuilder();
        rml.Append($"<h2>{Html.Text(title)}</h2><p>{Html.Text(sentence)}</p>");
        rml.Append($"<div class=\"actions\">{Button("Go ahead", () => Apply(plan, done), "primary")}{Button("Cancel", ShowGame)}</div>");
        int files = plan.Steps.Count(s => s.StartsWith("Move ", StringComparison.Ordinal));
        rml.Append($"<p class=\"muted\">{files} {(files == 1 ? "file is" : "files are")} moved.</p>");
        if (!showFiles)
        {
            rml.Append(Button("Show the files", () => Confirm(title, sentence, plan, done, showFiles: true), "small"));
        }
        else
        {
            rml.Append("<div class=\"steps\">");
            foreach (string step in plan.Steps)
            {
                rml.Append($"<p>{Html.Text(Shorten(step))}</p>");
            }
            rml.Append("</div>");
        }
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
