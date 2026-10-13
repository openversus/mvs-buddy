namespace MvsBuddy.Installs;

/// <summary>
/// The build of Ultimate ASI Loader the buddy installs, fetched from its author's release and checked against these
/// hashes, so anyone can see it is the published file. One DLL serves under every proxy name: the buddy renames it to
/// <c>xinput1_3.dll</c> on Windows and <c>version.dll</c> under Proton.
/// </summary>
/// <param name="Version">The release it comes from, for people to read.</param>
/// <param name="ZipUrl">The release asset.</param>
/// <param name="ZipSha256">The asset's SHA-256, lowercase hex.</param>
/// <param name="DllName">The DLL's name inside the zip.</param>
/// <param name="DllSha256">The DLL's SHA-256, lowercase hex: how an installed loader is recognised as this build.</param>
/// <param name="LicenseUrl">Its MIT license at the same release, which goes beside it as <c>LICENSE_Ultimate_ASI_Loader.txt</c>.</param>
/// <param name="LicenseSha256">The license file's SHA-256, lowercase hex.</param>
public sealed record AsiLoaderPin(string Version, Uri ZipUrl, string ZipSha256, string DllName, string DllSha256, Uri LicenseUrl, string LicenseSha256)
{
    /// <summary>
    /// v9.7.4. Its <c>dinput8.dll</c> is byte for byte the <c>xinput1_3.dll</c> and <c>version.dll</c> on the author's
    /// moving <c>x64-latest</c> release (checked 2026-10-13), which ovs-client's build pins by digest.
    /// </summary>
    public static AsiLoaderPin Default { get; } = new(
        "v9.7.4",
        new Uri("https://github.com/ThirteenAG/Ultimate-ASI-Loader/releases/download/v9.7.4/Ultimate-ASI-Loader_x64.zip"),
        "8272d83b2692662098746f2d0ad0e2d85f3c8358ab1d63f75fbe835c2c8135fd",
        "dinput8.dll",
        "fa266e3513d02c08a1b808f28c10538a489eaffaa4b0707f7cc1066e71b5afd7",
        new Uri("https://raw.githubusercontent.com/ThirteenAG/Ultimate-ASI-Loader/v9.7.4/license"),
        "ceca73c504e39084b0b80d2a437aea9988313d1915418d3c629c3e4a8313f5a1");
}
