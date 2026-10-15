using MvsBuddy.Games;

namespace MvsBuddy.Installs;

/// <summary>Which of the mod's layouts is installed.</summary>
public enum ModLayout
{
    /// <summary>No OpenVersus plugin.</summary>
    None,

    /// <summary>
    /// An old plugin dropped beside the game's executable (where the loader also loads plugins from), with its
    /// <c>.ini</c> files beside it: never the way the mod was meant to be installed, but done.
    /// </summary>
    BesideGame,

    /// <summary>Releases up to 2026.04: <c>plugins/OpenVersus.asi</c> with its <c>.ini</c> files loose beside it.</summary>
    Old,

    /// <summary>Since then: everything in <c>plugins/OpenVersus/</c>, the plugin named <c>OpenVersus_&lt;version&gt;.asi</c>.</summary>
    Current,
}

/// <summary>What an install amounts to, at a glance.</summary>
public enum InstallStatus
{
    /// <summary>Neither the mod nor Ultimate ASI Loader.</summary>
    NotInstalled,

    /// <summary>The mod and Ultimate ASI Loader.</summary>
    Installed,

    /// <summary>The mod without Ultimate ASI Loader beside the game: nothing loads it.</summary>
    LoaderMissing,

    /// <summary>Ultimate ASI Loader without the mod.</summary>
    LoaderOnly,
}

/// <summary>A DLL beside the game under a name Ultimate ASI Loader can take.</summary>
/// <param name="Path">The file.</param>
/// <param name="Sha256">Its SHA-256, lowercase hex.</param>
/// <param name="IsUltimateAsiLoader">Whether it is Ultimate ASI Loader (it carries the loader's name); another program's DLL otherwise.</param>
/// <param name="IsPinnedBuild">Whether it is the exact build the buddy installs (<see cref="AsiLoaderPin"/>).</param>
public sealed record LoaderFile(string Path, string Sha256, bool IsUltimateAsiLoader, bool IsPinnedBuild)
{
    /// <summary>The file's name, such as <c>xinput1_3.dll</c>.</summary>
    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>An ASI plugin that is not OpenVersus, somewhere the loader loads plugins from.</summary>
/// <param name="Path">The file.</param>
/// <param name="IsMvsInfinite">Whether its name is MVS Infinite's (<c>MVSIASI.asi</c>, <c>mvsi.asi</c>, <c>mvs_infinite.asi</c> and the like), which cannot run alongside OpenVersus.</param>
public sealed record OtherPlugin(string Path, bool IsMvsInfinite)
{
    /// <summary>The file's name.</summary>
    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>A pak file in the game's <c>Content/Paks</c> that is neither the game's own nor OpenVersus's.</summary>
/// <param name="Path">The file.</param>
/// <param name="Size">Its size in bytes.</param>
/// <param name="IsMvsInfinite">Whether it is MVS Infinite's: its name starts with <c>MVSI</c> or <c>MVS_Infinite</c>, or the person said so.</param>
public sealed record OtherPak(string Path, long Size, bool IsMvsInfinite)
{
    /// <summary>The file's name.</summary>
    public string Name => System.IO.Path.GetFileName(Path);

    /// <summary>Whether the person said whose it is (MVS Infinite's or not); the buddy asks only about the others.</summary>
    public bool Answered { get; init; }
}

/// <summary>A pak file (a <c>.pak</c>, <c>.utoc</c>, <c>.ucas</c> or <c>.sig</c>).</summary>
/// <param name="Path">The file.</param>
/// <param name="Size">Its size in bytes.</param>
public sealed record PakFile(string Path, long Size)
{
    /// <summary>The file's name.</summary>
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>The mod's settings file and the values that decide where it connects.</summary>
/// <param name="Path">The file: <c>OpenVersus.toml</c>, or the old layout's <c>OpenVersus.ini</c>.</param>
/// <param name="GameServerUrl"><c>[Server.Game] ServerUrl</c>: the server the game talks to. Null when unread or absent.</param>
/// <param name="ProdServerUrl"><c>[Server.Prod] ServerUrl</c>. Null when unread or absent.</param>
/// <param name="LogLevel"><c>[Settings] LogLevel</c>. Null when unread or absent.</param>
/// <param name="Errors">Why the file cannot be used, if it cannot; the mod then runs on its defaults.</param>
public sealed record ModSettings(string Path, string? GameServerUrl, string? ProdServerUrl, string? LogLevel, IReadOnlyList<string> Errors);

/// <summary>The mod's own files, as found.</summary>
/// <param name="Layout">Which layout is installed: the newest found, when there are several.</param>
/// <param name="Plugins">The plugin files (<c>.asi</c>) of that layout.</param>
/// <param name="Version">The newest version named by a current-layout plugin; null for the older layouts or none.</param>
/// <param name="Settings">The settings file, or null when there is none (the mod writes one with its defaults).</param>
/// <param name="OldLayoutFiles">Files of an older layout still there beside a newer one; an old plugin among them loads too.</param>
/// <param name="Backups">Plugins renamed aside (<c>.bak</c>), which do not load.</param>
/// <param name="Files">Every file of the installed layout: all of <c>plugins/OpenVersus/</c>, or an old plugin with the files beside it.</param>
public sealed record ModFiles(ModLayout Layout, IReadOnlyList<string> Plugins, Version? Version, ModSettings? Settings,
    IReadOnlyList<string> OldLayoutFiles, IReadOnlyList<string> Backups, IReadOnlyList<string> Files);

/// <summary>How the buddy knows which channel an install is.</summary>
public enum ChannelSource
{
    /// <summary>The buddy installed it, and wrote the channel down (<see cref="InstallRecord"/>).</summary>
    Install,

    /// <summary>The person said so.</summary>
    Person,

    /// <summary>A guess from the server address in its settings: the testing releases' settings use port 8420.</summary>
    Server,
}

/// <summary>Which channel the installed OpenVersus is, and how the buddy knows.</summary>
public sealed record ModChannel(Catalog.Channel Channel, ChannelSource Source);

/// <summary>What is installed for one copy of the game, read from disk.</summary>
public sealed record InstallState
{
    /// <summary>The game copy this describes.</summary>
    public required GameInstall Game { get; init; }

    /// <summary>The DLLs beside the game under Ultimate ASI Loader's names, whether or not they are it.</summary>
    public required IReadOnlyList<LoaderFile> Loaders { get; init; }

    /// <summary>The mod's own files.</summary>
    public required ModFiles Mod { get; init; }

    /// <summary>
    /// Where the game mounts the mod's paks from (<c>%LOCALAPPDATA%\MultiVersus\Saved\Paks</c>, inside the Wine prefix on
    /// Linux), whether or not it exists yet. Null when the buddy cannot tell: on Linux without a known prefix.
    /// </summary>
    public string? SavedPaksDirectory { get; init; }

    /// <summary>The mod's paks (<c>OVS_*</c>) in <see cref="SavedPaksDirectory"/>.</summary>
    public required IReadOnlyList<PakFile> Paks { get; init; }

    /// <summary>
    /// The mod's paks (<c>OVS_*</c>) anywhere in the game's own <c>Content</c> or <c>Binaries</c> (<c>Content/Paks</c>, a
    /// <c>~mods</c> folder), put there by hand. They belong in <see cref="SavedPaksDirectory"/>. Only looked for in a
    /// verified install.
    /// </summary>
    public required IReadOnlyList<PakFile> PaksInGame { get; init; }

    /// <summary>
    /// Paks in the game's <c>Content/Paks</c> (at any depth) that are neither the game's own nor OpenVersus's: another
    /// mod's, such as MVS Infinite's. Only looked for in a verified install.
    /// </summary>
    public required IReadOnlyList<OtherPak> OtherPaks { get; init; }

    /// <summary>ASI plugins that are not OpenVersus, where the loader loads them from.</summary>
    public required IReadOnlyList<OtherPlugin> OtherPlugins { get; init; }

    /// <summary>
    /// The folders the buddy changes for this copy that this account may not change, as the system answers when it was
    /// read: the game's <c>Win64</c>, and for a recognised install its <c>Content/Paks</c> and its own folder, and where the
    /// mod's paks go. Empty when it may change them all. On Windows, permission can be asked for.
    /// </summary>
    public IReadOnlyList<string> DeniedFolders { get; init; } = [];

    /// <summary>
    /// Under Wine: the game's prefix is not made yet, because the game has never been started there, so the paks have
    /// nowhere to go. The person starts the game once first; <see cref="Notes"/> says so.
    /// </summary>
    public bool NeedsFirstLaunch { get; init; }

    /// <summary>What the buddy wrote down when it installed the mod here; null when it did not, or the mod is not here.</summary>
    public InstallRecord? Record { get; init; }

    /// <summary>Which channel the installed mod is; null when the mod is not installed.</summary>
    public ModChannel? Channel { get; init; }

    /// <summary>Things worth telling the person, in plain sentences; empty when nothing stands out.</summary>
    public required IReadOnlyList<string> Notes { get; init; }

    /// <summary>The install at a glance.</summary>
    public InstallStatus Status
    {
        get
        {
            bool loader = Loaders.Any(l => l.IsUltimateAsiLoader);
            if (Mod.Layout == ModLayout.None)
            {
                return loader ? InstallStatus.LoaderOnly : InstallStatus.NotInstalled;
            }
            return loader ? InstallStatus.Installed : InstallStatus.LoaderMissing;
        }
    }
}
