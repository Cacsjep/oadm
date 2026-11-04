using System.Globalization;

using Oadm.Contracts.Updates;
using Oadm.Server.Hosting;

namespace Oadm.Server.Updates;

/// <summary>The command line of the server's helper: <c>Oadm.Server --update --data &lt;folder&gt; --to 4.9.0 --status https://127.0.0.1:5080</c>.</summary>
public sealed record HelperArguments(string DataDirectory, string To, Uri StatusUrl)
{
    public const string Flag = "--update";

    public static HelperArguments Of(string dataDirectory, string to, Uri statusUrl) => new(dataDirectory, to, statusUrl);

    public IReadOnlyList<string> ToList() => [Flag, "--data", DataDirectory, "--to", To, "--status", StatusUrl.ToString()];

    /// <summary>The arguments of a helper start; null when they are incomplete.</summary>
    public static HelperArguments? Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? Value(string name) => HelperCommandLine.Value(args, name);

        return Value("--data") is { Length: > 0 } data && Value("--to") is { Length: > 0 } to
            && Uri.TryCreate(Value("--status"), UriKind.Absolute, out var status)
            ? new HelperArguments(data, to, status)
            : null;
    }

    /// <summary>
    /// Where the helper asks for the version: the listen address with 127.0.0.1 for a wildcard host (0.0.0.0, [::], *, +).
    /// </summary>
    public static Uri StatusUrlFor(string listenUrl)
    {
        var text = (listenUrl ?? string.Empty).Replace("://*", "://0.0.0.0", StringComparison.Ordinal).Replace("://+", "://0.0.0.0", StringComparison.Ordinal);
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            return new Uri("https://127.0.0.1:5080");
        }

        var host = uri.Host is "0.0.0.0" or "[::]" or "::" or "localhost" ? "127.0.0.1" : uri.Host;
        return new UriBuilder(uri.Scheme, host, uri.Port).Uri;
    }
}

/// <summary>
/// The server's helper (rule 3: the same program in update mode, a copy in the update folder; rule 9: outside the service),
/// as SYSTEM / root: waits up to 30 s for the clients on this computer to close, runs the installer silently with a log
/// (rule 12 reads its exit code), then waits up to 5 minutes for the server here to answer with the new version and writes
/// the result into the record. On macOS it removes its own launchd job at the end.
/// </summary>
public sealed class ServerUpdateHelper(
    UpdateRecordFile records,
    UpdateOs os,
    InstallFacts facts,
    IInstallerRunner installer,
    IClientProcesses clients,
    IServerVersionProbe probe,
    ICommandRunner commands,
    TimeProvider time)
{
    public static readonly TimeSpan ClientWait = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan NewVersionWait = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan Poll = TimeSpan.FromSeconds(3);

    /// <summary>The longest a helper can take: the clients, the installer's limit, the wait for the new version.</summary>
    public static readonly TimeSpan WorstCase = ClientWait + ProcessInstallerRunner.Limit + NewVersionWait;
    public const string LogName = "helper.log";
    public const string ChoiceFileName = "choices.xml";

    /// <summary>The helper's own log (update/helper.log): what it did, never a secret.</summary>
    public string LogPath => Path.Combine(records.Folder, LogName);

    /// <summary><c>Oadm.Server --update ...</c>: the helper with the real installer, processes and clock.</summary>
    public static Task<int> RunFromCommandLineAsync(IReadOnlyList<string> args) =>
        RunFromCommandLineAsync(args, UpdatePlatform.Current.Os, new ProcessCommandRunner(), Console.Error);

    /// <summary>As above with the platform and the command runner of a test (bad arguments end before the helper exists).</summary>
    internal static async Task<int> RunFromCommandLineAsync(IReadOnlyList<string> args, UpdateOs os, ICommandRunner commands, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(error);
        if (HelperArguments.Parse(args) is not { } arguments)
        {
            await error.WriteLineAsync("Usage: Oadm.Server --update --data <folder> --to <version> --status <url>").ConfigureAwait(false);
            if (os == UpdateOs.MacOs)
            {
                // A submitted job with bad arguments would be started again and again: it goes before the helper ends.
                await RemoveJobAsync(commands).ConfigureAwait(false);
            }

            return 2;
        }

        var helper = new ServerUpdateHelper(
            new UpdateRecordFile(arguments.DataDirectory),
            os,
            InstallFacts.Current(isService: true),
            new ProcessInstallerRunner(),
            new SystemClientProcesses(SystemClientProcesses.InstalledClientFolder(os)),
            new GrpcServerVersionProbe(Auth.ServerTlsCertificate.FingerprintOf(arguments.DataDirectory)),
            commands,
            TimeProvider.System);
        return await helper.RunAsync(arguments, CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task RemoveJobAsync(ICommandRunner commands)
    {
        try
        {
            _ = await commands.RunAsync(ServerHelperStarter.Launchctl, ["remove", ServerHelperStarter.LaunchdLabel], CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            // Nothing more the helper can do.
        }
    }

    public async Task<int> RunAsync(HelperArguments arguments, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        try
        {
            await RunCoreAsync(arguments, ct).ConfigureAwait(false);
            return 0;
        }
#pragma warning disable CA1031 // Whatever stops the helper, the record ends with a result: the server never waits for nothing.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Write("The helper stopped: " + ex.Message);
            try
            {
                await FinishAsync(UpdateRecord.Failed, ex is OperationCanceledException ? UpdateTexts.Cancelled : UpdateTexts.HelperStopped, null, CancellationToken.None).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // The server gives up on its own after its limit.
            catch (Exception recordFailed)
#pragma warning restore CA1031
            {
                Write("The result could not be written: " + recordFailed.Message);
            }

            return 1;
        }
        finally
        {
            if (os == UpdateOs.MacOs)
            {
                // launchctl submit keeps a job alive: the helper removes it (this ends the helper too).
                Write("Removing the job " + ServerHelperStarter.LaunchdLabel);
                await RemoveJobAsync(commands).ConfigureAwait(false);
            }
        }
    }

    private async Task RunCoreAsync(HelperArguments arguments, CancellationToken ct)
    {
        var record = await records.ReadAsync(ct).ConfigureAwait(false);
        if (record is not { IsRunning: true } || record.To != arguments.To || string.IsNullOrEmpty(record.Installer))
        {
            Write($"Nothing to do: no running update to {arguments.To}.");
            return;
        }

        Write($"Update from {record.From} to {record.To}, installer {record.Installer}");
        if (record.Step == UpdateRecord.StepInstalling)
        {
            // A restarted helper (launchd keeps a submitted job alive): the installer ran already, never twice.
            Write("The installer ran already; waiting for the new version.");
            await WaitForNewVersionAsync(arguments, record, record.InstallerLog ?? Path.Combine(records.Folder, "install.log"), null, ct).ConfigureAwait(false);
            return;
        }

        var installerPath = Path.GetFullPath(record.Installer);
        if (!IsInUpdateFolder(installerPath) || !await MatchesAsync(installerPath, record.InstallerSha256, ct).ConfigureAwait(false))
        {
            // Only the checked download of the server runs, nothing else in its place.
            Write("The installer is not the checked download; nothing was installed.");
            await FinishAsync(UpdateRecord.Failed, UpdateTexts.WrongChecksum, record.InstallerLog ?? string.Empty, ct).ConfigureAwait(false);
            return;
        }

        await WaitForClientsAsync(ct).ConfigureAwait(false);

        string? choices = null;
        if (os == UpdateOs.MacOs && !Installation.HasMacServer(facts))
        {
            choices = Path.Combine(records.Folder, ChoiceFileName);
            await File.WriteAllTextAsync(choices, InstallerCommands.MacChoices(), ct).ConfigureAwait(false);
        }

        var log = record.InstallerLog ?? Path.Combine(records.Folder, "install.log");
        var command = InstallerCommands.For(os, InstallMode.Silent, installerPath, log, choices);
        Write($"Running {command.FileName} {string.Join(' ', command.Arguments)}");
        await records.UpdateRunningAsync(r => r with { Step = UpdateRecord.StepInstalling }, ct).ConfigureAwait(false);
        int code;
        string output;
        try
        {
            (code, output) = await installer.RunAsync(command, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            Write("The installer could not be started: " + ex.Message);
            await FinishAsync(UpdateRecord.Failed, UpdateTexts.HelperFailed, command.LogPath, ct).ConfigureAwait(false);
            return;
        }

        if (!command.WritesOwnLog && output.Length > 0)
        {
            try
            {
                await File.AppendAllTextAsync(command.LogPath == InstallerCommands.MacInstallLog ? Path.Combine(records.Folder, "install.log") : command.LogPath, output + Environment.NewLine, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The installer ran: its result counts, the copy of its output is a help only.
                Write("The installer output could not be added to its log: " + ex.Message);
            }
        }

        var result = InstallerCommands.Read(os, InstallMode.Silent, code, output, command.LogPath);
        Write(string.Create(CultureInfo.InvariantCulture, $"Installer exit code {code}: {result.Outcome}"));
        if (!result.Succeeded)
        {
            await FinishAsync(UpdateRecord.Failed, result.Text ?? UpdateTexts.InstallerFailed, result.LogPath, ct).ConfigureAwait(false);
            return;
        }

        var note = result.Outcome == InstallOutcome.DoneRestartNeeded ? UpdateTexts.RestartComputer : null;
        await WaitForNewVersionAsync(arguments, record, result.LogPath, note, ct).ConfigureAwait(false);
    }

    private async Task WaitForNewVersionAsync(HelperArguments arguments, UpdateRecord record, string log, string? note, CancellationToken ct)
    {
        await records.UpdateRunningAsync(r => r with { Step = UpdateRecord.StepWait, Reason = note ?? r.Reason, InstallerLog = log }, ct).ConfigureAwait(false);
        var deadline = time.GetUtcNow() + NewVersionWait;
        while (true)
        {
            if (UpdateVersions.IsRelease(await probe.VersionAsync(arguments.StatusUrl, ct).ConfigureAwait(false), record.To))
            {
                Write("The new version answers.");
                await FinishAsync(UpdateRecord.Done, note, log, ct, keepReason: note is null).ConfigureAwait(false);
                return;
            }

            if (time.GetUtcNow() >= deadline)
            {
                break;
            }

            await Task.Delay(Poll, time, ct).ConfigureAwait(false);
        }

        Write("The new version did not answer.");
        await FinishAsync(UpdateRecord.NotBack, UpdateTexts.NewVersionSilent, log, ct).ConfigureAwait(false);
    }

    /// <summary>The installer lies directly in the update folder (the server downloaded it there).</summary>
    private bool IsInUpdateFolder(string path) =>
        string.Equals(Path.GetDirectoryName(path), Path.GetFullPath(records.Folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> MatchesAsync(string path, string? sha256, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(sha256) || !File.Exists(path))
        {
            return false;
        }

        var stream = File.OpenRead(path);
        await using (stream.ConfigureAwait(false))
        {
            var hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
            return string.Equals(Convert.ToHexString(hash), sha256, StringComparison.OrdinalIgnoreCase);
        }
    }

    private async Task WaitForClientsAsync(CancellationToken ct)
    {
        var deadline = time.GetUtcNow() + ClientWait;
        while (clients.Count() > 0 && time.GetUtcNow() < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), time, ct).ConfigureAwait(false);
        }

        if (clients.Count() > 0)
        {
            Write("Clients still run on this computer; the installer starts anyway.");
        }
    }

    /// <summary>The result into a running record (one the server finished already stays); a null log keeps the record's.</summary>
    private Task<UpdateRecord?> FinishAsync(string result, string? reason, string? log, CancellationToken ct, bool keepReason = false) =>
        records.UpdateRunningAsync(r => r with { Result = result, Reason = keepReason ? r.Reason : reason, InstallerLog = log ?? r.InstallerLog, FinishedUtc = time.GetUtcNow() }, ct);

    private void Write(string line)
    {
        try
        {
            Directory.CreateDirectory(records.Folder);
            File.AppendAllText(LogPath, time.GetUtcNow().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) + " " + line + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The log is a help only.
        }
    }
}
