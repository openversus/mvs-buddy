using System.Reflection;

namespace MvsBuddy.App;

/// <summary>MVS buddy's icon, embedded as a PNG at each size an icon theme takes (<c>icons/&lt;size&gt;.png</c>).</summary>
internal static class Icons
{
    /// <summary>The icon's bytes by size in pixels.</summary>
    internal static IReadOnlyDictionary<int, byte[]> Embedded()
    {
        var assembly = typeof(Icons).Assembly;
        var icons = new Dictionary<int, byte[]>();
        foreach (string name in assembly.GetManifestResourceNames())
        {
            if (name.StartsWith("icons/", StringComparison.Ordinal) && name.EndsWith(".png", StringComparison.Ordinal)
                && int.TryParse(name.Substring(6, name.Length - 10), out int size))
            {
                using var stream = assembly.GetManifestResourceStream(name)!;
                using var bytes = new MemoryStream();
                stream.CopyTo(bytes);
                icons[size] = bytes.ToArray();
            }
        }
        return icons;
    }
}
