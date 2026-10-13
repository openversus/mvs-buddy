using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using MvsBuddy.Games;

namespace MvsBuddy.Installs;

/// <summary>
/// Reads what is installed for a copy of the game, from disk, every time: the game's own updater replaces the plugin
/// behind the buddy's back, so nothing is remembered between reads.
/// </summary>
public sealed class InstallReader
{
    internal const string ModFolder = "OpenVersus";
    internal const string SettingsFile = "OpenVersus.toml";
    internal const string OldSettingsFile = "OpenVersus.ini";

    /// <summary>Names Ultimate ASI Loader can take that a 64-bit Unreal game loads; the mod uses the first two.</summary>
    internal static readonly string[] ProxyNames =
    [
        "xinput1_3.dll", "version.dll", "dinput8.dll", "winmm.dll", "xinput1_4.dll", "xinput9_1_0.dll", "dsound.dll", "winhttp.dll", "wininet.dll",
    ];

    // The old layout's files beside its plugin (ovs-client Layout.ModFiles, current and legacy names).
    private static readonly string[] OldLayoutNames =
    [
        "OpenVersus.asi", OldSettingsFile, SettingsFile, "OVSState.ini", "OVSState.toml", "PatternsCache.cache", "PatternsCache.toml",
    ];

    private static readonly string[] PakExtensions = [".pak", ".utoc", ".ucas", ".sig"];

    // The game's own paks: global.* and pakchunkN[optional]-Windows.*.
    private static readonly Regex GameOwnPak = new(
        "^(global\\.(ucas|utoc)|pakchunk[0-9]+(optional)?-Windows\\.(pak|sig|ucas|utoc))$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex MvsInfiniteName = new("^(mvs|multiversus)(i|inf|infinite)(asi)?(v?[0-9]+)*$", RegexOptions.CultureInvariant);

    private static readonly byte[] LoaderMarker = Encoding.Unicode.GetBytes("Ultimate ASI Loader");
    private static readonly Regex PluginName = new("^OpenVersus_([0-9]+\\.[0-9]+\\.[0-9]+\\.[0-9]+)\\.asi$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly HostSystem host;
    private readonly AsiLoaderPin pin;
    private readonly ILogger<InstallReader> log;

    internal InstallReader(HostSystem host, AsiLoaderPin pin, ILogger<InstallReader> log)
    {
        this.host = host;
        this.pin = pin;
        this.log = log;
    }

    /// <summary>What is installed for <paramref name="game"/> now.</summary>
    public InstallState Read(GameInstall game)
    {
        if (game == null)
        {
            throw new ArgumentNullException(nameof(game));
        }
        string win64 = game.Win64Directory;
        var loaders = Loaders(win64);
        var plugins = LoadablePlugins(win64);
        var mod = Mod(win64, plugins);
        var others = plugins.Where(p => !IsOpenVersus(p)).Select(p => new OtherPlugin(p, IsMvsInfinite(Path.GetFileName(p)))).ToList();
        string? savedPaks = SavedPaksDirectory(game);
        var paks = savedPaks == null ? [] : PakFiles(savedPaks, deep: false).Where(IsOurs).ToList();

        // The game's own folders are only searched when they are known to be the game's: for a folder picked without
        // being recognised, two folders up could be anywhere.
        string game_ = Path.GetFullPath(Path.Combine(win64, "..", ".."));
        var paksInGame = game.Verified
            ? PakFiles(Path.Combine(game_, "Content"), deep: true).Concat(PakFiles(Path.Combine(game_, "Binaries"), deep: true)).Where(IsOurs).ToList()
            : [];
        var otherPaks = game.Verified
            ? PakFiles(Path.Combine(game_, "Content", "Paks"), deep: true).Where(p => !IsOurs(p) && !GameOwnPak.IsMatch(p.Name))
                .Select(p => new OtherPak(p.Path, p.Size, IsMvsInfinitePak(p.Name))).ToList()
            : [];

        var notes = new List<string>();
        var ual = loaders.Where(l => l.IsUltimateAsiLoader).ToList();
        if (ual.Count > 1)
        {
            notes.Add($"There are {ual.Count} copies of Ultimate ASI Loader here ({string.Join(", ", ual.Select(l => l.FileName))}); one is enough.");
        }
        if (mod.Plugins.Count > 1)
        {
            notes.Add($"There are {mod.Plugins.Count} OpenVersus plugins ({string.Join(", ", mod.Plugins.Select(p => Relative(win64, p)))}); one is enough.");
        }
        foreach (string old in mod.OldLayoutFiles.Where(f => f.EndsWith(".asi", StringComparison.OrdinalIgnoreCase)))
        {
            notes.Add(Path.GetDirectoryName(old) == Path.Combine(win64, "plugins")
                ? "An old OpenVersus.asi is still loose in plugins: the current version renames it aside when the game starts, then closes the game once."
                : $"An old OpenVersus plugin is still at {Relative(win64, old)}, where the loader loads it too.");
        }
        if (mod.Settings?.Errors.Count > 0)
        {
            notes.Add($"{Path.GetFileName(mod.Settings.Path)} has a mistake ({mod.Settings.Errors[0]}), so the mod runs on its default settings.");
        }
        foreach (var plugin in others)
        {
            notes.Add(plugin.IsMvsInfinite
                ? $"MVS Infinite ({Relative(win64, plugin.Path)}) is installed, and it cannot run alongside OpenVersus."
                : $"{Relative(win64, plugin.Path)} is another ASI plugin; the buddy cannot tell whether it works alongside OpenVersus.");
        }
        if (paksInGame.Count > 0)
        {
            notes.Add($"OpenVersus paks are inside the game's folder ({string.Join(", ", paksInGame.Select(p => Relative(game_, p.Path)))}); they belong in the user profile, where the mod keeps them.");
        }
        if (otherPaks.Count > 0)
        {
            notes.Add($"The game's Content{Path.DirectorySeparatorChar}Paks holds paks that are neither the game's nor OpenVersus's ({string.Join(", ", otherPaks.Select(p => Relative(game_, p.Path)))}); another mod's, such as MVS Infinite's, which would conflict too.");
        }
        if (savedPaks == null)
        {
            notes.Add("The buddy cannot tell which Wine prefix this copy runs in, so it cannot see the mod's paks.");
        }
        // Asked of the system on every read, since a game update or a reinstall can put a folder's permissions back.
        var mayChange = new List<string> { win64 };
        if (game.Verified)
        {
            mayChange.Add(Path.Combine(game_, "Content", "Paks"));
            mayChange.Add(Path.GetFullPath(Path.Combine(game_, "..")));
        }
        if (savedPaks != null)
        {
            mayChange.Add(savedPaks);
        }
        var denied = mayChange.Where(host.ChangeDeniedIn).ToList();
        if (denied.Count > 0)
        {
            notes.Add($"This account is not allowed to change files in {string.Join(", ", denied)}.");
        }
        var held = new Changes.HeldArea(host).Read(game);
        foreach (var set in held)
        {
            notes.Add(!set.IsRecorded
                ? $"Files from a hold that did not finish are in {set.Folder}; the originals were never moved away, so these can be discarded."
                : set.Placed.Count > 0
                    ? $"{set.Name} can be restored to undo that install ({set.Entries.Count(e => e.IsHeld)} files kept from before it)."
                    : $"{set.Name} is held aside ({set.Entries.Count(e => e.IsHeld)} files); restore it to use it again.");
        }

        var state = new InstallState
        {
            Game = game,
            Loaders = loaders,
            Mod = mod,
            SavedPaksDirectory = savedPaks,
            Paks = paks,
            PaksInGame = paksInGame,
            OtherPaks = otherPaks,
            OtherPlugins = others,
            Held = held,
            DeniedFolders = denied,
            Notes = notes,
        };
        log.LogInformation("{Folder}: {Status}, {Layout} layout {Version}, loaders [{Loaders}], other plugins [{Others}], {Paks} paks in {SavedPaks}",
            win64, state.Status, mod.Layout, mod.Version, string.Join(", ", loaders.Select(l => l.FileName)),
            string.Join(", ", others.Select(o => o.FileName)), paks.Count, savedPaks);
        return state;
    }

    /// <summary>
    /// Whether a pak's name says MVS Infinite: letters and digits only, it has <c>mvsi</c> or <c>infinite</c> in it
    /// (MVSI_Content_P, MVS_Infinite_P, Infinite_P). Nobody has seen the mod's pak names; this is the guess its author's
    /// naming suggests, and only decides what is picked by default.
    /// </summary>
    internal static bool IsMvsInfinitePak(string fileName)
    {
        string name = new string(Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        return name.Contains("mvsi") || name.Contains("infinite");
    }

    /// <summary>
    /// Whether a plugin's name is MVS Infinite's: letters and digits only, it reads as mvs or multiversus followed by i,
    /// inf or infinite (then asi, a version, or nothing), or it names both mvs and infinite. MVSIASI, mvsi, mvs_infinite and
    /// MVS-Infinite-v2 are; mvsinput is not.
    /// </summary>
    internal static bool IsMvsInfinite(string fileName)
    {
        string name = new string(Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        return MvsInfiniteName.IsMatch(name) || (name.Contains("infinite") && (name.Contains("mvs") || name.Contains("multiversus")));
    }

    /// <summary>
    /// Where the game mounts extra paks: <c>%LOCALAPPDATA%\MultiVersus\Saved\Paks</c>. Under Wine that is inside the
    /// prefix, in the folder of the prefix's user: Proton's is <c>steamuser</c>; another prefix's is the one already
    /// holding the game's saved files, else the only one there besides <c>Public</c>. Null when there is no prefix.
    /// </summary>
    internal string? SavedPaksDirectory(GameInstall game)
    {
        string[] fromLocal = ["MultiVersus", "Saved", "Paks"];
        if (host.IsWindows)
        {
            string? local = host.Environment("LOCALAPPDATA");
            return local == null ? null : Path.Combine([local, .. fromLocal]);
        }
        if (game.WinePrefix == null)
        {
            return null;
        }
        string users = Path.Combine(game.WinePrefix, "drive_c", "users");
        var names = Directory.Exists(users)
            ? Directory.EnumerateDirectories(users).Select(Path.GetFileName).Where(n => !string.Equals(n, "Public", StringComparison.OrdinalIgnoreCase)).ToList()
            : [];
        string user = names.FirstOrDefault(n => Directory.Exists(Path.Combine(users, n, "AppData", "Local", "MultiVersus")))
            ?? (names.Contains("steamuser") || names.Count == 0 ? "steamuser" : names[0]);
        return Path.Combine([users, user, "AppData", "Local", .. fromLocal]);
    }

    private IReadOnlyList<LoaderFile> Loaders(string win64)
    {
        var loaders = new List<LoaderFile>();
        foreach (string name in ProxyNames)
        {
            string path = Path.Combine(win64, name);
            if (!File.Exists(path))
            {
                continue;
            }
            byte[] bytes = File.ReadAllBytes(path);
            string sha256 = Sha256(bytes);
            loaders.Add(new LoaderFile(path, sha256, Contains(bytes, LoaderMarker), sha256 == pin.DllSha256));
        }
        return loaders;
    }

    // Every .asi the loader would load: beside the executable, and anywhere under scripts/ and plugins/.
    private static List<string> LoadablePlugins(string win64) =>
        Files(win64, "*.asi", deep: false)
            .Concat(Files(Path.Combine(win64, "scripts"), "*.asi", deep: true))
            .Concat(Files(Path.Combine(win64, "plugins"), "*.asi", deep: true))
            .ToList();

    private static bool IsOpenVersus(string plugin) => Path.GetFileName(plugin).StartsWith("OpenVersus", StringComparison.OrdinalIgnoreCase);

    // The newest layout found is the install; files of older layouts beside it are leftovers.
    private static ModFiles Mod(string win64, List<string> loadable)
    {
        string plugins = Path.Combine(win64, "plugins");
        string home = Path.Combine(plugins, ModFolder);
        var ours = loadable.Where(IsOpenVersus).ToList();
        var current = ours.Where(p => Path.GetDirectoryName(p) == home).ToList();
        var old = ours.Where(p => !current.Contains(p) && IsUnder(p, plugins)).ToList();
        var beside = ours.Where(p => !current.Contains(p) && !old.Contains(p)).ToList();
        var backups = Files(home, "*.bak", deep: false).Concat(Files(plugins, "OpenVersus*.bak", deep: false)).ToList();

        // The old layouts keep their settings and caches beside the plugin.
        var oldFiles = old.Concat(OldLayoutCompanions(plugins)).ToList();
        var besideFiles = beside.Concat(beside.Select(Path.GetDirectoryName).Distinct().SelectMany(d => OldLayoutCompanions(d!))).ToList();

        if (current.Count > 0)
        {
            var version = current.Select(p => PluginName.Match(Path.GetFileName(p)))
                .Where(m => m.Success).Select(m => Version.Parse(m.Groups[1].Value)).DefaultIfEmpty().Max();
            string settings = Path.Combine(home, SettingsFile);
            return new ModFiles(ModLayout.Current, current, version, File.Exists(settings) ? Settings(settings) : null,
                oldFiles.Concat(besideFiles).ToList(), backups, Files(home, "*", deep: true));
        }
        if (old.Count > 0)
        {
            return new ModFiles(ModLayout.Old, old, null, OldSettings(Path.GetDirectoryName(old[0])!), besideFiles, backups, oldFiles);
        }
        if (beside.Count > 0)
        {
            return new ModFiles(ModLayout.BesideGame, beside, null, OldSettings(Path.GetDirectoryName(beside[0])!), [], backups, besideFiles);
        }
        return new ModFiles(ModLayout.None, [], null, null, [], backups, []);
    }

    private static IEnumerable<string> OldLayoutCompanions(string folder) =>
        OldLayoutNames.Where(n => !n.EndsWith(".asi", StringComparison.OrdinalIgnoreCase)).Select(n => Path.Combine(folder, n)).Where(File.Exists);

    private static ModSettings? OldSettings(string folder)
    {
        string ini = Path.Combine(folder, OldSettingsFile);
        return File.Exists(ini) ? new ModSettings(ini, null, null, null, []) : null;
    }

    private static bool IsUnder(string path, string folder) =>
        path.StartsWith(folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static string Relative(string from, string path) =>
        IsUnder(path, from) ? path.Substring(from.TrimEnd(Path.DirectorySeparatorChar).Length + 1) : path;

    private static ModSettings Settings(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            return new ModSettings(path, null, null, null, [e.Message]);
        }
        var toml = TomlSettings.Parse(text, Path.GetFileName(path));
        return new ModSettings(path, toml.Get("Server.Game", "ServerUrl"), toml.Get("Server.Prod", "ServerUrl"), toml.Get("Settings", "LogLevel"), toml.Errors);
    }

    private static bool IsOurs(PakFile pak) => pak.Name.StartsWith("OVS_", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<PakFile> PakFiles(string folder, bool deep) =>
        Files(folder, "*", deep)
            .Where(p => PakExtensions.Any(e => p.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
            .Select(p => new PakFile(Path.GetFullPath(p), new FileInfo(p).Length));

    private static List<string> Files(string folder, string pattern, bool deep)
    {
        try
        {
            return Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder, pattern, deep ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                    .OrderBy(p => p, StringComparer.Ordinal).ToList()
                : [];
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string Sha256(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return string.Concat(sha.ComputeHash(bytes).Select(b => b.ToString("x2")));
    }

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && haystack[i + j] == needle[j])
            {
                j++;
            }
            if (j == needle.Length)
            {
                return true;
            }
        }
        return false;
    }
}
