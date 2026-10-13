using System.Text;
using MvsBuddy.Changes;
using MvsBuddy.Games;
using MvsBuddy.Installs;

namespace MvsBuddy.Tests;

// The manifests are the game's real Manifest_NonUFSFiles_Win64.txt and Manifest_DebugFiles_Win64.txt, and the first lines
// of its Manifest_UFSFiles_Win64.txt (the full one lists 233,617 files inside the paks).
public sealed class StockCheckTests : IDisposable
{
    private readonly TempTree tree = new();

    public void Dispose() => tree.Dispose();

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "stock", name));

    private string Root => tree.Path("MultiVersus");

    private string Win64 => Path.Combine(Root, "MultiVersus", "Binaries", "Win64");

    private Buddy Buddy() => new BuddyBuilder().Host(new HostSystem { Home = tree.Path("home"), DataDirectory = tree.Path("data") }).Build();

    private GameInstall Game() => new(Win64, GameStore.Steam, FoundBy.SteamLibrary) { WinePrefix = tree.Path("pfx") };

    // The game as Steam installs it: every file the loose-files manifest lists, the manifests, and the game's own paks.
    private void Stock()
    {
        foreach (string name in new[] { "Manifest_NonUFSFiles_Win64.txt", "Manifest_DebugFiles_Win64.txt", "Manifest_UFSFiles_Win64.txt" })
        {
            tree.File(Fixture(name), "MultiVersus", name);
        }
        foreach (string line in Fixture("Manifest_NonUFSFiles_Win64.txt").Split('\n'))
        {
            string entry = line.Split('\t')[0].Trim();
            if (entry.Length > 0)
            {
                tree.File("stock", ["MultiVersus", .. entry.Split('/')]);
            }
        }
        foreach (string pak in new[] { "pakchunk0-Windows.pak", "pakchunk0-Windows.sig", "pakchunk0-Windows.ucas", "pakchunk0-Windows.utoc", "global.ucas", "global.utoc" })
        {
            tree.File("stock pak", "MultiVersus", "MultiVersus", "Content", "Paks", pak);
        }
        tree.File("crash pak", "MultiVersus", "Engine", "Programs", "CrashReportClient", "Content", "Paks", "CrashReportClient.pak");
    }

    private string[] W(params string[] parts) => ["MultiVersus", "MultiVersus", "Binaries", "Win64", .. parts];

    [Fact]
    public void A_stock_install_has_nothing_extra()
    {
        Stock();
        var buddy = Buddy();

        var report = buddy.Stock.Read(buddy.Installs.Read(Game()));

        Assert.True(report.HasManifests);
        Assert.Empty(report.Extras);
        Assert.Empty(report.MissingStock);
    }

    [Fact]
    public void Everything_added_is_found_and_told_whose_it_seems_to_be()
    {
        Stock();
        tree.File("asi", W("plugins", "OpenVersus", "OpenVersus_2026.10.12.01.asi"));
        tree.Bytes([.. Encoding.ASCII.GetBytes("MZ "), .. Encoding.Unicode.GetBytes("Ultimate ASI Loader")], W("version.dll"));
        tree.File("mvsi", W("plugins", "MVSIASI.asi"));
        tree.File("mvsi pak", "MultiVersus", "MultiVersus", "Content", "Paks", "MVSI_Content_P.pak");
        tree.File("ovs pak", "MultiVersus", "MultiVersus", "Content", "Paks", "~mods", "OVS_P.pak");
        tree.File("a mod's pak", "MultiVersus", "MultiVersus", "Content", "Paks", "~mods", "pakchunk9-Windows.pak");
        tree.File("dxvk", W("dxgi.dll"));
        tree.File("zip", "MultiVersus", "Engine.zip");
        Directory.CreateSymbolicLink(tree.Path(W("loop")), Root);
        var buddy = Buddy();

        var report = buddy.Stock.Read(buddy.Installs.Read(Game()));

        Assert.Equal(
        [
            "Engine.zip:Other",
            "MultiVersus/Binaries/Win64/dxgi.dll:Other",
            "MultiVersus/Binaries/Win64/plugins/MVSIASI.asi:MvsInfinite",
            "MultiVersus/Binaries/Win64/plugins/OpenVersus/OpenVersus_2026.10.12.01.asi:OpenVersus",
            "MultiVersus/Binaries/Win64/version.dll:OpenVersus",
            "MultiVersus/Content/Paks/MVSI_Content_P.pak:MvsInfinite",
            "MultiVersus/Content/Paks/~mods/OVS_P.pak:OpenVersus",
            "MultiVersus/Content/Paks/~mods/pakchunk9-Windows.pak:Other",
        ], report.Extras.Select(e => $"{e.RelativePath.Replace('\\', '/')}:{e.Kind}").Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_missing_shipped_file_is_reported_but_a_missing_debug_file_is_not()
    {
        Stock();
        File.Delete(tree.Path(W("MultiVersus-Win64-Shipping.exe")));
        var buddy = Buddy();

        var report = buddy.Stock.Read(buddy.Installs.Read(Game() with { Verified = true }));

        Assert.Equal([Path.Combine("MultiVersus", "Binaries", "Win64", "MultiVersus-Win64-Shipping.exe")], report.MissingStock);
        Assert.Contains(report.Notes, n => n.Contains("Verify integrity"));
    }

    [Fact]
    public void A_clean_start_that_keeps_openversus_sets_the_rest_aside_and_restores()
    {
        Stock();
        string asi = tree.File("asi", W("plugins", "OpenVersus", "OpenVersus_2026.10.12.01.asi"));
        string dxgi = tree.File("dxvk", W("dxgi.dll"));
        string mod = tree.File("a mod's pak", "MultiVersus", "MultiVersus", "Content", "Paks", "~mods", "Skins_P.pak");
        var buddy = Buddy();
        var state = buddy.Installs.Read(Game());
        var picked = buddy.Stock.Read(state).Extras.Where(e => e.Kind != ExtraKind.OpenVersus);

        buddy.Changes.HoldNotStock(state, picked).Apply();

        Assert.True(File.Exists(asi));
        Assert.False(File.Exists(dxgi));
        Assert.False(Directory.Exists(Path.GetDirectoryName(mod)), "the emptied ~mods folder goes too");
        var after = buddy.Installs.Read(Game());
        Assert.Equal([ExtraKind.OpenVersus], buddy.Stock.Read(after).Extras.Select(e => e.Kind).Distinct());
        var set = Assert.Single(after.Held);
        Assert.StartsWith("Not stock", set.Name);

        buddy.Changes.Restore(after, set.Name).Apply();

        Assert.Equal("dxvk", File.ReadAllText(dxgi));
        Assert.Equal("a mod's pak", File.ReadAllText(mod));
    }

    [Fact]
    public void Without_manifests_nothing_can_be_told_and_that_is_said()
    {
        tree.Game("MultiVersus");
        var buddy = Buddy();

        var report = buddy.Stock.Read(buddy.Installs.Read(Game()));

        Assert.False(report.HasManifests);
        Assert.Empty(report.Extras);
        Assert.Contains(report.Notes, n => n.Contains("Manifest_"));
    }

    [SkippableFact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void An_unreadable_manifest_makes_the_check_unavailable_instead_of_failing()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "makes a file unreadable with Unix file modes");
        Stock();
        string manifest = tree.Path("MultiVersus", "Manifest_NonUFSFiles_Win64.txt");
        File.SetUnixFileMode(manifest, UnixFileMode.None);
        try
        {
            var buddy = Buddy();

            var report = buddy.Stock.Read(buddy.Installs.Read(Game()));

            Assert.False(report.HasManifests);
            Assert.Empty(report.Extras);
            Assert.Contains(report.Notes, n => n.Contains("could not be read"));
        }
        finally
        {
            File.SetUnixFileMode(manifest, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
