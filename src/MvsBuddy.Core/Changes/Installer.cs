using System.IO.Compression;
using System.Runtime.InteropServices;
using MvsBuddy.Catalog;
using MvsBuddy.Installs;
using Trinet.Core.IO.Ntfs;

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
    internal const string MarkOfTheWebID = "Zone.Identifier";

    // Alternate data streams are NTFS's, and the library that reads them calls kernel32: off Windows the first call throws
    // DllNotFoundException, which no catch below is for, so nothing here touches a stream anywhere else.
    private static bool Ntfs => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    // The release's plugins/OpenVersus/ only, extracted afresh into <paramref name="into"/> (beside the zip by default);
    // nothing else in it is ever used.
    internal static string ExtractPluginFolder(string zip, IProgress<string>? progress, string? into = null)
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
            RemoveMarkOfTheWeb(target, progress);
        }
        return extracted;
    }

    // The pinned DLL out of the loader's zip, checked on its own: the zip's checksum covers the archive, not this entry.
    internal static string ExtractLoader(string zip, AsiLoaderPin pin, string folder, IProgress<string>? progress)
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
        RemoveMarkOfTheWeb(target, progress);
        return target;
    }

    /// <summary>
    /// Takes the mark of the web (the Zone.Identifier stream) off <paramref name="file"/>: Windows puts it on files from
    /// the internet, and a marked binary has kept the mod from loading. Every file MVS Buddy fetches goes through this as it
    /// lands (<see cref="Downloads"/>), and every file it unpacks again after extraction, so however Windows comes to mark
    /// one, it is clean by the time it is placed. True when a mark was there and went; a mark that could not go is said.
    /// Nothing anywhere but NTFS.
    /// </summary>
    internal static bool RemoveMarkOfTheWeb(string file, IProgress<string>? progress)
    {
        if (!Ntfs || !FileSystem.AlternateDataStreamExists(file, MarkOfTheWebID))
        {
            return false;
        }
        progress?.Report($"Taking the mark of the web off downloaded files... ({Path.GetFileName(file)})");
        try
        {
            FileSystem.DeleteAlternateDataStream(file, MarkOfTheWebID);
            return true;
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            progress?.Report($"Taking the mark of the web off downloaded files... ({Path.GetFileName(file)}: could not, {e.Message})");
            return false;
        }
    }

    /// <summary>The mark of the web off each of <paramref name="files"/> that is there (<see cref="RemoveMarkOfTheWeb"/>); how many had one.</summary>
    internal static int RemoveMarksOf(IEnumerable<string> files) => Ntfs ? files.Where(File.Exists).Count(f => RemoveMarkOfTheWeb(f, null)) : 0;

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
