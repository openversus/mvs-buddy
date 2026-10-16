using Microsoft.Extensions.Logging;

namespace MvsBuddy.Changes;

/// <summary>What asking for permission came to.</summary>
public enum PermissionOutcome
{
    /// <summary>This account may now change files in every folder asked about.</summary>
    Granted,

    /// <summary>The person said no when Windows asked; nothing changed.</summary>
    Declined,

    /// <summary>Windows was asked, and some folders still refuse this account (<see cref="PermissionResult.StillDenied"/>).</summary>
    StillDenied,

    /// <summary>
    /// Not asked: only Windows has a way to ask, or a folder is one MVS Buddy never opens up to an account (a drive, Program
    /// Files, Windows).
    /// </summary>
    NotAsked,
}

/// <summary>What asking for permission came to.</summary>
/// <param name="Outcome">Whether the folders can be changed now.</param>
/// <param name="StillDenied">The folders this account still may not change; empty when it may change them all.</param>
/// <param name="Message">What happened, in words for the person.</param>
public sealed record PermissionResult(PermissionOutcome Outcome, IReadOnlyList<string> StillDenied, string Message);

/// <summary>
/// Asking Windows to let this account change files in folders it is refused in, such as a game under Program Files. An
/// administrator says yes once, and the account gets Modify on exactly those folders, inherited by everything in them. Later
/// changes there need no asking until something (a game update, a reinstall) puts the permissions back, which is why the
/// buddy asks the system each time rather than remembering (<see cref="ChangePlan.DeniedFolders"/>,
/// <see cref="Installs.InstallState.DeniedFolders"/>).
/// </summary>
public sealed class Permissions
{
    private readonly HostSystem host;
    private readonly ILogger<Permissions> log;

    internal Permissions(HostSystem host, ILogger<Permissions> log)
    {
        this.host = host;
        this.log = log;
    }

    /// <summary>Whether permission can be asked for on this system: on Windows.</summary>
    public bool CanAsk => host.IsWindows;

    /// <summary>
    /// Asks Windows, with one prompt, to let this account change files in <paramref name="folders"/> (as
    /// <see cref="ChangeException.DeniedFolders"/> or <see cref="ChangePlan.DeniedFolders"/> name them). A folder inside
    /// another one asked about is covered by it. A folder that does not exist yet is made in the same step, and the
    /// permission goes on the first folder made, never on the one it is made in. Whether it worked is asked of the system
    /// afterwards, folder by folder.
    /// </summary>
    public async Task<PermissionResult> AskAsync(IEnumerable<string> folders, CancellationToken cancellationToken = default)
    {
        if (folders == null)
        {
            throw new ArgumentNullException(nameof(folders));
        }
        var comparer = host.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var asked = folders.Select(Full).Distinct(comparer).ToList();
        if (!host.IsWindows)
        {
            return NotAsked(asked, $"This account is not allowed to change files in {string.Join(", ", asked)}. MVS Buddy has no way to ask for that here; the folders' owner can allow it.");
        }
        var targets = Targets(asked);
        var closed = targets.Where(t => IsNeverOpened(t.Folder)).Select(t => t.Folder).ToList();
        if (closed.Count > 0)
        {
            return NotAsked(asked, $"MVS Buddy does not open up {string.Join(", ", closed)} to an account: it is a drive or one of Windows' own folders. Choose a folder for the game inside it instead.");
        }
        if (host.UserSid() is not string sid)
        {
            return NotAsked(asked, "MVS Buddy could not tell which Windows account it runs as, so it did not ask.");
        }

        // One elevated cmd runs every step, so Windows asks once. The account is named by its SID, read here before
        // elevating: an administrator who types their own password must not be the one who gets the permission. "&", not
        // "&&": a step that fails does not stop the others, and the check afterwards says what came of each folder.
        string icacls = Path.Combine(Environment.SystemDirectory, "icacls.exe");
        var steps = new List<string>();
        foreach (var (folder, make) in targets)
        {
            if (make)
            {
                steps.Add($"mkdir \"{folder}\"");
            }
            steps.Add($"\"{icacls}\" \"{folder}\" /grant \"*{sid}:(OI)(CI)M\" /q");
        }
        string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        string arguments = $"/d /s /c \"{string.Join(" & ", steps)}\"";
        log.LogInformation("Asking for permission: {Program} {Arguments}", cmd, arguments);

        int? exit;
        try
        {
            exit = await host.RunElevated(cmd, arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception || e is IOException || e is InvalidOperationException)
        {
            log.LogWarning(e, "Could not ask for permission");
            var after = asked.Where(host.ChangeDeniedIn).ToList();
            return new PermissionResult(PermissionOutcome.StillDenied, after, $"Windows could not ask for permission ({e.Message}), so nothing was changed.");
        }
        if (exit == null)
        {
            log.LogInformation("Permission declined");
            return new PermissionResult(PermissionOutcome.Declined, asked, "Nothing was changed: Windows asked, and the answer was no.");
        }

        var still = asked.Where(host.ChangeDeniedIn).ToList();
        log.LogInformation("Asked for permission (exit code {Exit}); still refused: [{Still}]", exit, string.Join(", ", still));
        return still.Count == 0
            ? new PermissionResult(PermissionOutcome.Granted, still, $"This account may now change files in {string.Join(", ", asked)}.")
            : new PermissionResult(PermissionOutcome.StillDenied, still, $"Windows was asked, but this account is still not allowed to change files in {string.Join(", ", still)}.");
    }

    // The folders the permission goes on: each folder asked about that exists, or for one that does not, the first folder
    // that would be made on the way to it; then only those not inside another.
    private List<(string Folder, bool Make)> Targets(IReadOnlyList<string> asked)
    {
        var comparison = host.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var targets = new List<(string Folder, bool Make)>();
        foreach (string folder in asked)
        {
            var target = new DirectoryInfo(folder);
            while (!target.Exists && target.Parent != null && !target.Parent.Exists)
            {
                target = target.Parent;
            }
            bool make = !target.Exists;
            string path = Full(target.FullName);
            if (!targets.Any(t => string.Equals(t.Folder, path, comparison)))
            {
                targets.Add((path, make));
            }
        }
        return targets.Where(t => !targets.Any(other => t.Folder.StartsWith(other.Folder + Path.DirectorySeparatorChar, comparison))).ToList();
    }

    // A drive, Windows and everything in it, and the folders other programs' folders are made in.
    private bool IsNeverOpened(string folder)
    {
        string full = Full(folder);
        if (string.Equals(full, Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd(Path.DirectorySeparatorChar);
        if (windows.Length > 0 && (string.Equals(full, windows, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(windows + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
        var parents = new[]
        {
            Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonApplicationData,
        }.Select(Environment.GetFolderPath).Select(p => p.TrimEnd(Path.DirectorySeparatorChar)).Where(p => p.Length > 0);
        string? users = Path.GetDirectoryName(host.Home.TrimEnd(Path.DirectorySeparatorChar));
        return parents.Concat(users == null ? [] : [users]).Any(p => string.Equals(full, p, StringComparison.OrdinalIgnoreCase));
    }

    // The full path without a trailing separator, except for a drive's root, which needs it ("C:" alone means the current
    // folder on C:).
    private static string Full(string folder)
    {
        string full = Path.GetFullPath(folder);
        return full.Length > (Path.GetPathRoot(full)?.Length ?? 0) ? full.TrimEnd(Path.DirectorySeparatorChar) : full;
    }

    private PermissionResult NotAsked(IReadOnlyList<string> asked, string message)
    {
        log.LogInformation("Did not ask for permission: {Message}", message);
        return new PermissionResult(PermissionOutcome.NotAsked, asked, message);
    }
}
