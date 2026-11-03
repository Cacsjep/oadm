using System.Globalization;
using System.Security;

namespace Oadm.Contracts.Updates;

/// <summary>How the helper runs an installer: silent as SYSTEM / root (server) or with the system's elevation prompt and visible progress (client).</summary>
public enum InstallMode
{
    Silent,
    Visible,
}

/// <summary>One installer run: the program, its arguments (no shell), extra environment and where its log is.</summary>
public sealed record InstallerCommand(string FileName, IReadOnlyList<string> Arguments, string LogPath)
{
    /// <summary>Environment variables for the installer (apt: no questions).</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    /// <summary>The installer writes its log itself (msiexec /l*v); otherwise the helper writes the output there.</summary>
    public bool WritesOwnLog { get; init; }
}

/// <summary>What an installer run did.</summary>
public enum InstallOutcome
{
    Done,
    DoneRestartNeeded,
    Busy,
    Cancelled,
    Failed,
}

/// <summary>The outcome with its text (rule 12) and the installer log.</summary>
public sealed record InstallResult(InstallOutcome Outcome, string? Text, string LogPath)
{
    public bool Succeeded => Outcome is InstallOutcome.Done or InstallOutcome.DoneRestartNeeded;
}

/// <summary>
/// The installer calls of docs/spec/self-update.md per platform and the reading of their exit codes (rule 12). Never a
/// shell: arguments are passed one by one; the macOS administrator prompt is <c>osascript</c> with a quoted command.
/// </summary>
public static class InstallerCommands
{
    public const string AptGet = "/usr/bin/apt-get";
    public const string Pkexec = "/usr/bin/pkexec";
    public const string MacInstaller = "/usr/sbin/installer";
    public const string Osascript = "/usr/bin/osascript";
    public const string MacInstallLog = "/var/log/install.log";

    /// <summary>apt-get waits this long for the package lock (rule 12).</summary>
    public const string AptLockOption = "DPkg::Lock::Timeout=300";

    /// <summary>The choice of the macOS server in the .pkg (packaging/macos/build-pkg.sh).</summary>
    public const string MacServerChoice = "com.oadm.choice.server";

    /// <summary>msiexec.exe in the system folder (never found through PATH).</summary>
    public static string Msiexec => Path.Combine(System.Environment.SystemDirectory is { Length: > 0 } system ? system : @"C:\Windows\System32", "msiexec.exe");

    /// <summary>
    /// The installer call for <paramref name="installer"/>. <paramref name="choiceFile"/> (macOS only): the choice changes
    /// (<see cref="MacChoices"/>), written by the helper when no server is installed.
    /// </summary>
    public static InstallerCommand For(UpdateOs os, InstallMode mode, string installer, string logPath, string? choiceFile = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installer);
        ArgumentException.ThrowIfNullOrWhiteSpace(logPath);
        switch (os)
        {
            case UpdateOs.Windows:
                // /norestart always: OADM never restarts the computer (3010 says so instead).
                return new InstallerCommand(Msiexec, ["/i", installer, mode == InstallMode.Silent ? "/qn" : "/passive", "/norestart", "/l*v", logPath], logPath)
                {
                    WritesOwnLog = true,
                };
            case UpdateOs.MacOs:
            {
                List<string> install = [MacInstaller, "-pkg", installer, "-target", "/"];
                if (choiceFile is not null)
                {
                    install.AddRange(["-applyChoiceChangesXML", choiceFile]);
                }

                if (mode == InstallMode.Silent)
                {
                    return new InstallerCommand(install[0], install[1..], MacInstallLog);
                }

                // The administrator prompt of macOS: do shell script ... with administrator privileges.
                var shell = string.Join(' ', install.Select(ShellQuote));
                return new InstallerCommand(Osascript, ["-e", $"do shell script \"{AppleScriptEscape(shell)}\" with administrator privileges"], MacInstallLog);
            }

            default:
            {
                List<string> apt = [AptGet, "install", "-y", "-o", AptLockOption, installer];
                // LC_ALL=C: apt's messages in English, so the busy package lock is recognized on every system (rule 12).
                var environment = new Dictionary<string, string> { ["DEBIAN_FRONTEND"] = "noninteractive", ["LC_ALL"] = "C" };
                return mode == InstallMode.Silent
                    ? new InstallerCommand(apt[0], apt[1..], logPath) { Environment = environment }
                    // pkexec clears the environment: env sets it for apt-get.
                    : new InstallerCommand(Pkexec, ["/usr/bin/env", "DEBIAN_FRONTEND=noninteractive", "LC_ALL=C", .. apt], logPath);
            }
        }
    }

    /// <summary>Reads an installer's exit code (rule 12); <paramref name="output"/> is what it printed (apt, pkexec, osascript).</summary>
    public static InstallResult Read(UpdateOs os, InstallMode mode, int exitCode, string? output, string logPath)
    {
        output ??= string.Empty;
        if (os == UpdateOs.Windows)
        {
            return exitCode switch
            {
                0 => new InstallResult(InstallOutcome.Done, null, logPath),
                3010 => new InstallResult(InstallOutcome.DoneRestartNeeded, UpdateTexts.RestartComputer, logPath),
                1618 => new InstallResult(InstallOutcome.Busy, UpdateTexts.InstallerBusy, logPath),
                1602 => new InstallResult(InstallOutcome.Cancelled, UpdateTexts.Cancelled, logPath),
                _ => new InstallResult(InstallOutcome.Failed, UpdateTexts.InstallerFailedCode(exitCode), logPath),
            };
        }

        if (exitCode == 0)
        {
            return new InstallResult(InstallOutcome.Done, null, logPath);
        }

        if (os == UpdateOs.Linux)
        {
            if (output.Contains("Could not get lock", StringComparison.OrdinalIgnoreCase)
                || output.Contains("Unable to acquire the dpkg frontend lock", StringComparison.OrdinalIgnoreCase))
            {
                return new InstallResult(InstallOutcome.Busy, UpdateTexts.InstallerBusy, logPath);
            }

            // pkexec: 126 the prompt was dismissed, 127 not authorized.
            if (mode == InstallMode.Visible && exitCode is 126 or 127)
            {
                return new InstallResult(InstallOutcome.Cancelled, UpdateTexts.Cancelled, logPath);
            }

            return new InstallResult(InstallOutcome.Failed, UpdateTexts.InstallerFailed, logPath);
        }

        // macOS: osascript ends with -128 when the administrator prompt was cancelled.
        if (mode == InstallMode.Visible && output.Contains("(-128)", StringComparison.Ordinal))
        {
            return new InstallResult(InstallOutcome.Cancelled, UpdateTexts.Cancelled, MacInstallLog);
        }

        return new InstallResult(InstallOutcome.Failed, UpdateTexts.InstallerFailed, MacInstallLog);
    }

    /// <summary>
    /// The choice changes file of <c>installer -applyChoiceChangesXML</c> that leaves the server out (a client-only Mac:
    /// rule 10, no <see cref="Installation.MacServerDaemon"/>).
    /// </summary>
    public static string MacChoices() => string.Create(CultureInfo.InvariantCulture, $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <array>
            <dict>
                <key>attributeSetting</key>
                <integer>0</integer>
                <key>choiceAttribute</key>
                <string>selected</string>
                <key>choiceIdentifier</key>
                <string>{SecurityElement.Escape(MacServerChoice)}</string>
            </dict>
        </array>
        </plist>
        """);

    /// <summary>A POSIX shell word in single quotes.</summary>
    public static string ShellQuote(string word)
    {
        ArgumentNullException.ThrowIfNull(word);
        return "'" + word.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    private static string AppleScriptEscape(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
