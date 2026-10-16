using System.Reflection;

namespace MvsBuddy;

/// <summary>MVS Buddy's own version, from VERSION at the repository root (Directory.Build.props).</summary>
internal static class BuddyVersion
{
    /// <summary>The version without the commit the SDK appends after a <c>+</c>.</summary>
    internal static readonly string Current = Read();

    private static string Read()
    {
        string? text = typeof(BuddyVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(text))
        {
            return "0";
        }
        int plus = text!.IndexOf('+');
        return plus < 0 ? text : text.Substring(0, plus);
    }
}
