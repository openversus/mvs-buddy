using System.Net;
using System.Security.Cryptography;
using System.Text;
using MvsBuddy.Catalog;
using MvsBuddy.Changes;
using MvsBuddy.Games;

namespace MvsBuddy.Tests;

public sealed class PakStoreTests : IDisposable
{
    private readonly TempTree tree = new();
    private readonly FakeGitHub server = new();

    public void Dispose() => tree.Dispose();

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private Buddy Buddy() => new BuddyBuilder().Http(new HttpClient(server))
        .Host(new HostSystem { Home = tree.Path("home"), DataDirectory = tree.Path("data") }).Build();

    private ReleaseAsset Pak(string name, string text, bool checksum = true)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        server.Answer("/paks/" + name, () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        return new ReleaseAsset(name, bytes.Length, new Uri("https://example.test/paks/" + name), checksum ? Sha(bytes) : null);
    }

    private int Requests(string name) => server.Requests.Count(r => r.RequestUri!.AbsolutePath == "/paks/" + name);

    [Fact]
    public async Task A_pak_at_hand_is_used_where_it_is_and_a_changed_or_missing_one_is_downloaded()
    {
        var buddy = Buddy();
        string savedPaks = tree.Path("pfx", "Saved", "Paks");
        string same = tree.File("pak same", "pfx", "Saved", "Paks", "OVS_Same_P.pak");
        tree.File("pak 12", "pfx", "Saved", "Paks", "OVS_P.pak");
        var assets = new[] { Pak("OVS_Same_P.pak", "pak same"), Pak("OVS_P.pak", "pak 13"), Pak("OVS_New_P.pak", "pak new") };

        var staged = await buddy.Paks.StageAsync(assets, [buddy.Paks.Folder("testing"), savedPaks]);

        Assert.Equal([("OVS_Same_P.pak", false), ("OVS_P.pak", true), ("OVS_New_P.pak", true)], staged.Select(p => (p.Name, p.Downloaded)));
        Assert.Equal(same, staged[0].Path);
        Assert.Equal("pak 13", File.ReadAllText(staged[1].Path));
        Assert.Equal(0, Requests("OVS_Same_P.pak"));
        Assert.Equal("pak 12", File.ReadAllText(tree.Path("pfx", "Saved", "Paks", "OVS_P.pak")));
    }

    [Fact]
    public async Task A_pak_without_a_checksum_is_downloaded_even_with_one_of_its_name_at_hand()
    {
        var buddy = Buddy();
        tree.File("pak", "paks", "OVS_P.pak");

        var staged = await buddy.Paks.StageAsync([Pak("OVS_P.pak", "pak", checksum: false)], [tree.Path("paks")]);

        Assert.True(Assert.Single(staged).Downloaded);
        Assert.Equal(Sha(Encoding.ASCII.GetBytes("pak")), staged[0].Sha256);
    }

    [Fact]
    public void A_sets_manifest_reads_back_and_a_set_without_one_holds_nothing()
    {
        var buddy = Buddy();
        Assert.Empty(buddy.Paks.Read("prod"));
        var entries = new[] { new PakEntry("OVS_P.pak", 6, "abc", "https://example.test/paks/OVS_P.pak") };
        Directory.CreateDirectory(buddy.Paks.Folder("testing"));
        File.WriteAllBytes(Path.Combine(buddy.Paks.Folder("testing"), PakStore.ManifestFile), PakStore.Manifest(entries));

        Assert.Equal(entries, buddy.Paks.Read("testing"));
    }
}
