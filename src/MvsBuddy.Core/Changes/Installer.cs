using System.IO.Compression;
using MvsBuddy.Catalog;
using MvsBuddy.Installs;

namespace MvsBuddy.Changes;

/// <summary>Choices for an install.</summary>
public sealed record InstallOptions
{
    /// <summary>The name the loader takes; null for <c>xinput1_3.dll</c> on Windows and <c>version.dll</c> under Wine.</summary>
    public string? LoaderName { get; init; }
}

/// <summary>
/// What an install takes from a release's zip and from the loader's, and how a download's progress reads. Installing is
/// <see cref="GameStates.PrepareInstallAsync"/>.
/// </summary>
internal static class Installer
{
    internal const string LicenseName = "LICENSE_Ultimate_ASI_Loader.txt";
    private const string Home = "plugins/OpenVersus/";

    // The release's plugins/OpenVersus/ only, extracted afresh into <paramref name="into"/> (beside the zip by default);
    // nothing else in it is ever used.
    internal static string ExtractPluginFolder(string zip, string? into = null)
    {
        string extracted = into ?? Path.Combine(Path.GetDirectoryName(zip)!, "extracted");
        if (Directory.Exists(extracted))
        {
            Directory.Delete(extracted, recursive: true);
        }
        string root = Path.GetFullPath(extracted) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(zip);
        foreach (var entry in archive.Entries)
        {
            string name = entry.FullName.Replace('\\', '/');
            if (!name.StartsWith(Home, StringComparison.OrdinalIgnoreCase) || name.EndsWith("/", StringComparison.Ordinal))
            {
                continue;
            }
            string target = Path.GetFullPath(Path.Combine(extracted, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root, StringComparison.Ordinal))
            {
                throw new ChangeException($"The zip has an entry that points outside it ({entry.FullName}); it is not installed.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target);
        }
        return extracted;
    }

    // The pinned DLL out of the loader's zip, checked on its own: the zip's checksum covers the archive, not this entry.
    internal static string ExtractLoader(string zip, AsiLoaderPin pin, string folder)
    {
        string target = Path.Combine(folder, pin.DllName);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target))
        {
            File.Delete(target);
        }
        using (var archive = ZipFile.OpenRead(zip))
        {
            var entry = archive.Entries.FirstOrDefault(e => string.Equals(e.FullName, pin.DllName, StringComparison.OrdinalIgnoreCase))
                ?? throw new DownloadException($"{Path.GetFileName(zip)} has no {pin.DllName}.");
            entry.ExtractToFile(target);
        }
        if (Downloads.Sha256Of(target) != pin.DllSha256)
        {
            File.Delete(target);
            throw new DownloadException($"The {pin.DllName} in {Path.GetFileName(zip)} is not the build MVS Buddy expects; it is not installed.");
        }
        return target;
    }

    // A download's bytes as "Downloading X (42%)", once per percent, for a progress line a person can watch move.
    internal sealed class Percent(IProgress<string> report, ReleaseAsset asset) : IProgress<long>
    {
        private int last = -1;

        public static IProgress<long>? Of(IProgress<string>? report, ReleaseAsset asset) =>
            report == null || asset.Size <= 0 ? null : new Percent(report, asset);

        public void Report(long value)
        {
            int percent = (int)Math.Min(100, value * 100 / asset.Size);
            if (Interlocked.Exchange(ref last, percent) != percent)
            {
                report.Report($"Downloading {asset.Name} ({percent}%)");
            }
        }
    }
}
