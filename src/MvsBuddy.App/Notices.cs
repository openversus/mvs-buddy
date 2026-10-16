using System.Reflection;

namespace MvsBuddy.App;

/// <summary>One thing MVS Buddy ships or installs, and the terms it comes under.</summary>
/// <param name="Name">What it is called.</param>
/// <param name="License">The license's name, for the list.</param>
/// <param name="Resource">The embedded file holding the license text.</param>
/// <param name="Role">What it does in MVS Buddy, in a few words.</param>
internal sealed record Notice(string Name, string License, string Resource, string Role)
{
    /// <summary>The license text.</summary>
    internal string Text()
    {
        using var stream = typeof(Notice).Assembly.GetManifestResourceStream(Resource) ?? throw new FileNotFoundException(Resource);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

/// <summary>Everything in MVS Buddy that is someone's work under a license, MVS Buddy itself first.</summary>
internal static class Notices
{
    /// <summary>In the order the notices screen lists them.</summary>
    internal static readonly IReadOnlyList<Notice> All =
    [
        new("MVS Buddy", "MIT", "notices/MVS-Buddy.txt", "This garbage fire program"),
        new("Ultimate ASI Loader", "MIT", "notices/Ultimate-ASI-Loader.txt", "The mod loader which loads OpenVersus, and MVS Buddy installs it beside the game; its license goes beside it too"),
        new("RmlUiFlat", "MIT", "notices/RmlUiFlat.txt", "How MVS Buddy generates its user interface"),
        new("RmlUi, FreeType, SDL3 and the Lato font", "MIT, FreeType, zlib and others; SIL Open Font License", "THIRD-PARTY-NOTICES.txt", "The engine inside RmlUiFlat: the interface library, text and font rendering, the window and input"),
        new("LibGit2Sharp", "MIT", "notices/LibGit2Sharp.txt", "How MVS Buddy keeps track of different installs of OpenVersus with different settings"),
        new("libgit2", "GPL version 2 with the linking exception", "notices/libgit2.txt", "The library underneath LibGit2Sharp"),
        new("Tomlyn", "BSD 2-Clause", "notices/Tomlyn.txt", "How MVS Buddy reads and edits the OpenVersus settings file, as well as its own"),
        new("Serilog", "Apache 2.0", "notices/Serilog.txt", "How MVS Buddy creates log files saying what it did"),
        new(".NET", "MIT", "notices/dotnet.txt", "How MVS Buddy runs on Windows, Steam Deck, and regular Linux installs from a single codebase, and how it can be built from source"),
    ];
}
