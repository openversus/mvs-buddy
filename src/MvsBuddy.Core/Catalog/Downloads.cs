using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace MvsBuddy.Catalog;

/// <summary>A download that failed or did not match its checksum. The message says which, for the person.</summary>
public sealed class DownloadException : Exception
{
    /// <summary>A failure with its reason.</summary>
    public DownloadException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Files fetched for an install, each checked against the SHA-256 it is supposed to have before anything uses it. They are
/// kept in MVS Buddy's folder by checksum (<c>downloads/&lt;sha256&gt;/&lt;name&gt;</c>), so a file already fetched and still
/// matching is not fetched again.
/// </summary>
public sealed class Downloads
{
    private readonly HttpClient http;
    private readonly HostSystem host;
    private readonly ILogger<Downloads> log;

    internal Downloads(HttpClient http, HostSystem host, ILogger<Downloads> log)
    {
        this.http = http;
        this.host = host;
        this.log = log;
    }

    /// <summary>
    /// A release asset, checked against GitHub's digest. GitHub gives one for every asset uploaded since 2025; one without is
    /// fetched anyway, unchecked, into its own folder, and fetched again each time since there is nothing to match it to.
    /// </summary>
    /// <exception cref="DownloadException">It could not be fetched, or did not match its digest.</exception>
    public Task<string> FetchAsync(ReleaseAsset asset, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        if (asset == null)
        {
            throw new ArgumentNullException(nameof(asset));
        }
        if (asset.Sha256 != null)
        {
            return FetchAsync(asset.DownloadUrl, asset.Name, asset.Sha256, progress, cancellationToken);
        }
        log.LogWarning("{Name} has no checksum on GitHub; downloading it unchecked", asset.Name);
        using var sha = SHA256.Create();
        string key = Hex(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(asset.DownloadUrl.AbsoluteUri))).Substring(0, 16);
        return DownloadAsync(asset.DownloadUrl, asset.Name, Path.Combine(DataDirectory(), "downloads", "unchecked", key), null, progress, cancellationToken);
    }

    /// <summary>Any file whose SHA-256 is known in advance; returns where it is.</summary>
    /// <exception cref="DownloadException">It could not be fetched or did not match.</exception>
    public Task<string> FetchAsync(Uri url, string name, string sha256, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        string path = Path.Combine(DataDirectory(), "downloads", sha256, name);
        if (File.Exists(path) && Sha256Of(path) == sha256)
        {
            log.LogInformation("{Name} already downloaded and matching", name);
            return Task.FromResult(path);
        }
        return DownloadAsync(url, name, Path.GetDirectoryName(path)!, sha256, progress, cancellationToken);
    }

    /// <summary>
    /// <paramref name="asset"/> with its checksum read from the checksum file published beside it, when GitHub gives none
    /// (<see cref="ReleaseAsset.ChecksumUrl"/>, as sha256sum writes them); as it is when that file is missing, unreadable or
    /// does not name it, and then it is downloaded unchecked, as before.
    /// </summary>
    public async Task<ReleaseAsset> WithChecksumAsync(ReleaseAsset asset, CancellationToken cancellationToken = default)
    {
        if (asset == null)
        {
            throw new ArgumentNullException(nameof(asset));
        }
        if (asset.Sha256 != null || asset.ChecksumUrl == null)
        {
            return asset;
        }
        try
        {
            using var response = await http.GetAsync(asset.ChecksumUrl, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            string? sha = ChecksumIn(await response.Content.ReadAsStringAsync().ConfigureAwait(false), asset.Name);
            if (sha != null)
            {
                log.LogInformation("{Name}: checksum from {Url}", asset.Name, asset.ChecksumUrl);
                return asset with { Sha256 = sha };
            }
            log.LogWarning("{Url} does not name {Name}; it is downloaded unchecked", asset.ChecksumUrl, asset.Name);
        }
        catch (HttpRequestException e)
        {
            log.LogWarning(e, "Could not read {Url}; {Name} is downloaded unchecked", asset.ChecksumUrl, asset.Name);
        }
        return asset;
    }

    /// <summary>The checksum <paramref name="text"/> (sha256sum's "hash  name" or "hash *name" lines) gives for <paramref name="name"/>.</summary>
    internal static string? ChecksumIn(string text, string name)
    {
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            int space = line.IndexOf(' ');
            if (space != 64)
            {
                continue;
            }
            string hash = line.Substring(0, 64);
            string file = line.Substring(space).TrimStart(' ').TrimStart('*');
            if (file == name && hash.All(Uri.IsHexDigit))
            {
                return hash.ToLowerInvariant();
            }
        }
        return null;
    }

    private string DataDirectory() => string.IsNullOrEmpty(host.DataDirectory)
        ? throw new DownloadException("MVS Buddy has no folder of its own to download into.")
        : host.DataDirectory;

    // Streams to a .part beside the target, hashing as it goes; keeps it only when it matches (or nothing was expected).
    private async Task<string> DownloadAsync(Uri url, string name, string folder, string? sha256, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, name);
        string part = path + ".part";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("mvs-buddy", BuddyVersion.Current));
            log.LogInformation("Downloading {Url}", url);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new DownloadException($"{name} could not be downloaded: the server answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            }
            string actual;
            using (var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var target = File.Create(part))
            using (var sha = SHA256.Create())
            {
                byte[] buffer = new byte[1 << 16];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    await target.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                    total += read;
                    progress?.Report(total);
                }
                sha.TransformFinalBlock([], 0, 0);
                actual = Hex(sha.Hash!);
            }
            if (sha256 != null && actual != sha256)
            {
                throw new DownloadException($"{name} did not match its checksum (expected {sha256}, got {actual}); it was not kept.");
            }
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            File.Move(part, path);
            return path;
        }
        catch (HttpRequestException e)
        {
            throw new DownloadException($"{name} could not be downloaded. Check the internet connection, then try again.", e);
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DownloadException($"The download of {name} stopped answering. Try again in a moment.", e);
        }
        finally
        {
            if (File.Exists(part))
            {
                File.Delete(part);
            }
        }
    }

    internal static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Hex(sha.ComputeHash(stream));
    }

    private static string Hex(byte[] bytes) => string.Concat(bytes.Select(b => b.ToString("x2")));
}
