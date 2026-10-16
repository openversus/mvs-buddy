using System.Text;
using MvsBuddy.Catalog;
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
        Assert.Null(buddy.Stock.Unavailable(buddy.Installs.Read(Game())));
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
            "MultiVersus/Binaries/Win64/version.dll:Loader",
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
    public void A_clean_start_that_keeps_openversus_sets_the_rest_aside_and_puts_it_back()
    {
        Stock();
        string asi = tree.File("asi", W("plugins", "OpenVersus", "OpenVersus_2026.10.12.01.asi"));
        string dxgi = tree.File("dxvk", W("dxgi.dll"));
        string mod = tree.File("a mod's pak", "MultiVersus", "MultiVersus", "Content", "Paks", "~mods", "Skins_P.pak");
        var buddy = Buddy();
        var state = buddy.Installs.Read(Game());
        var picked = buddy.Stock.Read(state).Extras.Where(e => e.Kind != ExtraKind.OpenVersus);

        buddy.States.PrepareSetAside(state, picked).Apply();

        Assert.True(File.Exists(asi));
        Assert.False(File.Exists(dxgi));
        Assert.False(Directory.Exists(Path.GetDirectoryName(mod)), "the emptied ~mods folder goes too");
        var after = buddy.Installs.Read(Game());
        Assert.Equal([ExtraKind.OpenVersus], buddy.Stock.Read(after).Extras.Select(e => e.Kind).Distinct());
        Assert.Equal([SetAsideKind.Rest], buddy.States.SetAsideOf(after).Select(a => a.Kind));

        buddy.States.PreparePutBack(after, SetAsideKind.Rest).Apply();

        Assert.Equal("dxvk", File.ReadAllText(dxgi));
        Assert.Equal("a mod's pak", File.ReadAllText(mod));
    }

    // OpenVersus, MVS Infinite, a loader, and a stray tool, as a copy can look after a few people have helped.
    private void Crowded()
    {
        Stock();
        tree.File("asi", W("plugins", "OpenVersus", "OpenVersus_2026.10.12.01.asi"));
        tree.Bytes([.. Encoding.ASCII.GetBytes("MZ "), .. Encoding.Unicode.GetBytes("Ultimate ASI Loader")], W("version.dll"));
        tree.File("license", W(Installer.LicenseName));
        tree.File("mvsi", W("plugins", "MVSIASI.asi"));
        tree.File("mvsi pak", "MultiVersus", "MultiVersus", "Content", "Paks", "MVSI_Content_P.pak");
        tree.File("dxvk", W("dxgi.dll"));
    }

    [Fact]
    public void With_the_manifests_initial_holds_everything_the_game_did_not_ship_but_the_paks()
    {
        Crowded();
        var buddy = Buddy();

        buddy.States.PrepareTurnOff(buddy.Installs.Read(Game())).Apply();

        var store = buddy.States.Open(buddy.States.Layout(Game()));
        string initial = store.Tip("initial")!;
        Assert.Equal(
        [
            "MultiVersus/Binaries/Win64/LICENSE_Ultimate_ASI_Loader.txt",
            "MultiVersus/Binaries/Win64/dxgi.dll",
            "MultiVersus/Binaries/Win64/plugins/MVSIASI.asi",
            "MultiVersus/Binaries/Win64/plugins/OpenVersus/OpenVersus_2026.10.12.01.asi",
            "MultiVersus/Binaries/Win64/version.dll",
        ], store.Paths(store.TreeOf(initial)).Order(StringComparer.Ordinal));
        string message = store.Describe(initial).Message;
        Assert.DoesNotContain("covers", message);
        Assert.Equal(["MVSI_Content_P.pak"], GameStates.PaksIn(message).Select(p => p.Name));
        Assert.DoesNotContain("MultiVersus/Binaries/Win64/dxgi.dll", store.Paths(store.TreeOf(store.Tip("current")!)));
    }

    private List<string> Listing() => Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
        .Select(f => $"{Path.GetRelativePath(Root, f)} {File.ReadAllText(f).GetHashCode()}").Order(StringComparer.Ordinal).ToList();

    [Fact]
    public void With_the_manifests_a_roll_back_after_a_clean_start_puts_back_everything_the_game_did_not_ship()
    {
        Crowded();
        var buddy = Buddy();
        var first = Listing();
        buddy.States.PrepareTurnOff(buddy.Installs.Read(Game())).Apply();
        var state = buddy.Installs.Read(Game());
        buddy.States.PrepareSetAside(state, buddy.Stock.Read(state).Extras).Apply();
        Assert.NotEqual(first, Listing());

        buddy.States.PrepareRollBack(buddy.Installs.Read(Game())).Apply();

        Assert.Equal(first, Listing());
        // MVS Infinite and the rest are back, and the OpenVersus here is the live one again: nothing is set aside or kept.
        Assert.Empty(buddy.States.SetAsideOf(buddy.Installs.Read(Game())));
    }

    [Fact]
    public void Setting_openversus_aside_while_mvs_infinite_stays_keeps_the_loader_it_loads_through()
    {
        Crowded();
        var buddy = Buddy();
        var state = buddy.Installs.Read(Game());
        var picked = buddy.Stock.Read(state).Extras.Where(e => e.Kind == ExtraKind.OpenVersus);

        var clean = buddy.States.PrepareSetAside(state, picked);
        clean.Apply();

        Assert.True(File.Exists(tree.Path(W("version.dll"))));
        Assert.True(File.Exists(tree.Path(W(Installer.LicenseName))));
        Assert.True(File.Exists(tree.Path(W("plugins", "MVSIASI.asi"))));
        Assert.False(File.Exists(tree.Path(W("plugins", "OpenVersus", "OpenVersus_2026.10.12.01.asi"))));
        Assert.Contains(clean.Summary, s => s.StartsWith("Ultimate ASI Loader stays: MVSIASI.asi", StringComparison.Ordinal));
        Assert.Equal([(SetAsideKind.OpenVersus, (Channel?)Channel.Prod)], buddy.States.SetAsideOf(buddy.Installs.Read(Game())).Select(a => (a.Kind, a.Channel)));
    }

    [Fact]
    public void A_clean_start_to_stock_sets_each_end_state_aside_on_its_own_and_keeps_the_loader_that_was_there_first()
    {
        Crowded();
        var buddy = Buddy();
        var state = buddy.Installs.Read(Game());

        var clean = buddy.States.PrepareSetAside(state, buddy.Stock.Read(state).Extras);
        Assert.Contains("Ultimate ASI Loader stays: one was here before MVS Buddy first changed this copy.", clean.Summary);
        clean.Apply();

        var after = buddy.Installs.Read(Game());
        Assert.Equal([ExtraKind.Loader], buddy.Stock.Read(after).Extras.Select(e => e.Kind).Distinct());
        Assert.Equal(InstallStatus.LoaderOnly, after.Status);
        Assert.Equal([SetAsideKind.OpenVersus, SetAsideKind.MvsInfinite, SetAsideKind.Rest], buddy.States.SetAsideOf(after).Select(a => a.Kind));

        buddy.States.PrepareTurnOn(after, Channel.Prod).Apply();

        var on = buddy.Installs.Read(Game());
        Assert.Equal(InstallStatus.Installed, on.Status);
        Assert.False(File.Exists(tree.Path(W("plugins", "MVSIASI.asi"))));
        Assert.False(File.Exists(tree.Path(W("dxgi.dll"))));
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
        Assert.Equal(report.Notes.Single(), buddy.Stock.Unavailable(buddy.Installs.Read(Game())));
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
