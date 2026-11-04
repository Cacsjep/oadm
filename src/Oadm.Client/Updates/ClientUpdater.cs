using Oadm.Contracts.Updates;

namespace Oadm.Client.Updates;

/// <summary>
/// The command line of the client's helper (rule 3: the same program in update mode, a copy in a temporary folder) and of
/// the client the helper starts afterwards.
/// </summary>
public sealed record ClientHelperArguments
{
    public const string Flag = "--update-helper";
    public const string Install = "install";
    public const string Wait = "wait";

    /// <summary><see cref="Install"/>: run the installer; <see cref="Wait"/>: wait for the server's update (the server's computer).</summary>
    public required string Mode { get; init; }

    /// <summary>The client to start afterwards (the installed one).</summary>
    public required string Start { get; init; }

    public required string From { get; init; }

    public required string To { get; init; }

    /// <summary>The installer (<see cref="Install"/>).</summary>
    public string? Installer { get; init; }

    /// <summary>The server's address (<see cref="Wait"/>).</summary>
    public string? Server { get; init; }

    public IReadOnlyList<string> ToList()
    {
        List<string> args = [Flag, Mode, "--update-start", Start, "--update-from", From, "--update-to", To];
        if (Installer is not null)
        {
            args.AddRange(["--update-installer", Installer]);
        }

        if (Server is not null)
        {
            args.AddRange(["--update-server", Server]);
        }

        return args;
    }

    public static ClientHelperArguments? Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? Value(string name) => HelperCommandLine.Value(args, name);

        var mode = Value(Flag);
        if (mode is not (Install or Wait) || Value("--update-start") is not { } start || Value("--update-from") is not { } from || Value("--update-to") is not { } to)
        {
            return null;
        }

        var parsed = new ClientHelperArguments { Mode = mode, Start = start, From = from, To = to, Installer = Value("--update-installer"), Server = Value("--update-server") };
        return (mode == Install && parsed.Installer is null) || (mode == Wait && parsed.Server is null) ? null : parsed;
    }
}

/// <summary>What the client the helper started says first: its own update or the server's (the result dialog).</summary>
public sealed record UpdatedArguments(string Kind, string From, string To, string? Reason)
{
    public const string Flag = "--updated";
    public const string Client = "client";
    public const string Server = "server";

    public IReadOnlyList<string> ToList()
    {
        List<string> args = [Flag, Kind, "--update-from", From, "--update-to", To];
        if (!string.IsNullOrEmpty(Reason))
        {
            args.AddRange(["--update-reason", Reason]);
        }

        return args;
    }

    public static UpdatedArguments? Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? Value(string name) => HelperCommandLine.Value(args, name);

        return Value(Flag) is (Client or Server) and var kind && Value("--update-from") is { } from && Value("--update-to") is { } to
            ? new UpdatedArguments(kind, from, to, Value("--update-reason"))
            : null;
    }
}

/// <summary>Starts the client's helper and ends this client (tests: a fake, never a real installer).</summary>
public interface IClientHelperLauncher
{
    /// <summary>Copies this program to a temporary folder, starts the copy with <paramref name="arguments"/> and closes the client.</summary>
    Task StartAndQuitAsync(IReadOnlyList<string> arguments);
}

/// <summary>
/// The real launcher: the copy in <c>update/helper-*</c> of the client's own data folder (never a shared temporary folder
/// another user could prepare), then the app shuts down.
/// </summary>
public sealed class ClientHelperLauncher(IProcessLauncher processes, string updateFolder, Action quit) : IClientHelperLauncher
{
    public async Task StartAndQuitAsync(IReadOnlyList<string> arguments)
    {
        // A copy of the whole program: never on the UI thread, then back to it to close.
        var copy = await Task.Run(() => HelperCopy.Make(updateFolder)).ConfigureAwait(true);
        processes.Start(copy, arguments);
        quit();
    }
}

/// <summary>
/// Update client on another computer (docs/spec/self-update.md "Client on another computer") and the close for the
/// server's installer on the server's computer (step 6). Only for an installed client (<see cref="Installation"/>).
/// </summary>
public sealed class ClientUpdater(IReleaseFeed feed, UpdateDownloader downloader, IClientHelperLauncher launcher, InstallFacts facts, UpdatePlatform platform, string updateFolder, string currentVersion)
{
    /// <summary>The update folder of this client (the last installer, helper copies); earlier helper copies are removed.</summary>
    public string Folder => updateFolder;

    /// <summary>Removes the helper copies of earlier updates (client start; a running helper keeps its own).</summary>
    public void CleanUp() => HelperCopy.CleanUp(updateFolder);

    /// <summary>Update client is offered here (installed, no server on this computer; on Linux with pkexec).</summary>
    public bool CanUpdateClient => Installation.CanUpdateClient(facts);

    /// <summary>The server's installer replaces this client (Windows MSI, macOS .pkg on the server's computer).</summary>
    public bool UpdatesWithServer => facts.Os != UpdateOs.Linux && Installation.IsClientInstalled(facts);

    /// <summary>The installed client, started by the helper afterwards.</summary>
    public string Executable => Environment.ProcessPath ?? Path.Combine(facts.BaseDirectory, OperatingSystem.IsWindows() ? "Oadm.Client.exe" : "Oadm.Client");

    /// <summary>Downloads and checks the client installer of <paramref name="version"/>, then the helper installs it and this client closes.</summary>
    public async Task UpdateAsync(string version, Action<UpdateStep, int> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (!CanUpdateClient)
        {
            throw new UpdateRefusedException(UpdateTexts.ClientNotInstalled);
        }

        var target = UpdateVersions.ParseStable(version) ?? throw new UpdateRefusedException(UpdateTexts.PreRelease);
        if (UpdateVersions.Parse(currentVersion) is { } current && !UpdateVersions.IsOlder(currentVersion, version))
        {
            throw new UpdateRefusedException(target == current ? UpdateTexts.SameVersion : UpdateTexts.Downgrade);
        }

        var name = platform.InstallerName(UpdateApp.Client, target) ?? throw new UpdateRefusedException(UpdateTexts.NoInstaller);
        progress(UpdateStep.Download, 0);
        var release = await feed.ByVersionAsync(target, ct).ConfigureAwait(true) ?? throw new UpdateRefusedException(UpdateTexts.DownloadFailed);
        var installer = await downloader.DownloadAsync(release, name, updateFolder, progress, ct).ConfigureAwait(true);
        UpdateFolder.DeleteOldInstallers(updateFolder, installer);
        progress(UpdateStep.Install, 0);
        await launcher.StartAndQuitAsync(new ClientHelperArguments
        {
            Mode = ClientHelperArguments.Install,
            Start = Executable,
            From = currentVersion,
            To = UpdateVersions.Plain(target),
            Installer = installer,
        }.ToList()).ConfigureAwait(true);
    }

    /// <summary>The server's installer starts here: the helper waits for the server's result and starts the new client.</summary>
    public Task CloseForServerAsync(string version, string serverAddress) =>
        launcher.StartAndQuitAsync(new ClientHelperArguments
        {
            Mode = ClientHelperArguments.Wait,
            Start = Executable,
            From = currentVersion,
            To = version,
            Server = serverAddress,
        }.ToList());
}

/// <summary>
/// The client's helper (<c>Oadm.Client --update-helper ...</c>, as the user): <see cref="ClientHelperArguments.Install"/>
/// runs the installer with the system's elevation prompt and visible progress, reads its exit code (rule 12) and starts the
/// client that is installed now; <see cref="ClientHelperArguments.Wait"/> waits until the server answers with the new
/// version, or its update ended without it (it answers its old version again and updates no longer), at most 10 minutes,
/// then starts the client. The started client shows the result; whatever stops the helper, the client is started.
/// </summary>
public sealed class ClientUpdateHelper(UpdateOs os, InstallFacts facts, IInstallerRunner installer, IServerVersionProbe probe, IProcessLauncher processes, TimeProvider time)
{
    public static readonly TimeSpan ServerWait = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan Poll = TimeSpan.FromSeconds(3);

    /// <summary>A server that answers its old version without an update this long from the start never began one.</summary>
    public static readonly TimeSpan NoUpdateWait = TimeSpan.FromMinutes(1);

    public async Task RunAsync(ClientHelperArguments arguments, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        UpdatedArguments started;
        try
        {
            if (arguments.Mode == ClientHelperArguments.Install)
            {
                started = await InstallAsync(arguments, ct).ConfigureAwait(false);
            }
            else
            {
                await WaitForServerAsync(arguments, ct).ConfigureAwait(false);
                started = new UpdatedArguments(UpdatedArguments.Server, arguments.From, arguments.To, null);
            }
        }
#pragma warning disable CA1031 // The user is never left without a client: it starts and shows the failed result.
        catch (Exception)
#pragma warning restore CA1031
        {
            started = Stopped(arguments);
        }

        processes.Start(arguments.Start, started.ToList());
    }

    /// <summary>What the client says when the helper stopped: its own update failed, or the server's result (from its record).</summary>
    public static UpdatedArguments Stopped(ClientHelperArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return arguments.Mode == ClientHelperArguments.Install
            ? new UpdatedArguments(UpdatedArguments.Client, arguments.From, arguments.To, UpdateTexts.HelperStopped)
            : new UpdatedArguments(UpdatedArguments.Server, arguments.From, arguments.To, null);
    }

    private async Task<UpdatedArguments> InstallAsync(ClientHelperArguments arguments, CancellationToken ct)
    {
        var installerPath = Path.GetFullPath(arguments.Installer!); // apt-get takes a path, never a package name
        var folder = Path.GetDirectoryName(installerPath)!;
        string? choices = null;
        if (os == UpdateOs.MacOs && !Installation.HasMacServer(facts))
        {
            choices = Path.Combine(folder, "choices.xml");
            await File.WriteAllTextAsync(choices, InstallerCommands.MacChoices(), ct).ConfigureAwait(false);
        }

        var log = Path.Combine(folder, "install.log");
        var command = InstallerCommands.For(os, InstallMode.Visible, installerPath, log, choices);
        InstallResult result;
        try
        {
            var (code, output) = await installer.RunAsync(command, ct).ConfigureAwait(false);
            if (!command.WritesOwnLog && output.Length > 0)
            {
                await File.AppendAllTextAsync(log, output + Environment.NewLine, ct).ConfigureAwait(false);
            }

            result = InstallerCommands.Read(os, InstallMode.Visible, code, output, command.LogPath);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            result = new InstallResult(InstallOutcome.Failed, UpdateTexts.HelperFailed, log);
        }

        var reason = result.Succeeded ? result.Text : result.Outcome == InstallOutcome.Failed ? $"{result.Text} Installer log: {result.LogPath}" : result.Text;
        return new UpdatedArguments(UpdatedArguments.Client, arguments.From, arguments.To, reason);
    }

    private async Task WaitForServerAsync(ClientHelperArguments arguments, CancellationToken ct)
    {
        var server = Contracts.Security.OadmChannel.NormalizeAddress(arguments.Server);
        var start = time.GetUtcNow();
        var deadline = start + ServerWait;
        var began = false; // the server said it updates, or went silent (the installer stopped it)
        while (true)
        {
            var answer = await probe.AnswerAsync(server, ct).ConfigureAwait(false);
            if (answer is not null && UpdateVersions.IsRelease(answer.Version, arguments.To))
            {
                return; // the new version runs
            }

            if (answer is null || answer.UpdatingTo is not null)
            {
                began = true;
            }
            else if (began || time.GetUtcNow() - start >= NoUpdateWait)
            {
                return; // the old version answers and updates no longer: the update ended without the new version
            }

            if (time.GetUtcNow() >= deadline)
            {
                return;
            }

            await Task.Delay(Poll, time, ct).ConfigureAwait(false);
        }
    }
}
