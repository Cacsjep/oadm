using System.Collections.Concurrent;

using Microsoft.Extensions.Time.Testing;

using Oadm.Contracts.Updates;
using Oadm.Sdk.Testing;
using Oadm.Server.Hosting;
using Oadm.Server.Updates;

namespace Oadm.Server.Tests.Updates;

/// <summary>
/// The server's helper and its start outside the service, with a fake installer, fake processes and a fake clock: never a
/// real installer, systemctl or launchctl.
/// </summary>
public sealed class ServerUpdateHelperTests
{
    private static readonly Uri Status = new("https://127.0.0.1:5080");

    private sealed class FakeInstaller(int exitCode, string output = "") : IInstallerRunner
    {
        public ConcurrentQueue<InstallerCommand> Runs { get; } = new();

        public UpdateRecordFile? Records { get; set; }

        /// <summary>A failure the helper does not expect.</summary>
        public Exception? Throws { get; set; }

        /// <summary>The record's step while the installer ran.</summary>
        public string? StepDuringRun { get; private set; }

        public Task<(int ExitCode, string Output)> RunAsync(InstallerCommand command, CancellationToken ct)
        {
            StepDuringRun = Records?.Read()?.Step;
            if (Throws is { } ex)
            {
                throw ex;
            }

            Runs.Enqueue(command);
            return Task.FromResult((exitCode, output));
        }
    }

    private sealed class FakeClients(params int[] counts) : IClientProcesses
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public int Count() => counts.Length == 0 ? 0 : counts[Math.Min(Interlocked.Increment(ref _calls) - 1, counts.Length - 1)];
    }

    private sealed class FakeProbe(string? version) : IServerVersionProbe
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public string? Version { get; set; } = version;

        /// <summary>The answer from the second call on (null: always <see cref="Version"/>).</summary>
        public string? AfterFirst { get; set; }

        public Task<string?> VersionAsync(Uri server, CancellationToken ct) =>
            Task.FromResult(Interlocked.Increment(ref _calls) > 1 && AfterFirst is not null ? AfterFirst : Version);
    }

    internal sealed class FakeCommands : ICommandRunner
    {
        public ConcurrentQueue<(string File, IReadOnlyList<string> Args)> Runs { get; } = new();

        public int ExitCode { get; set; }

        public Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken ct)
        {
            Runs.Enqueue((fileName, arguments));
            return Task.FromResult(new CommandResult(ExitCode, ExitCode == 0 ? string.Empty : "failed"));
        }
    }

    internal sealed class FakeLauncher : IProcessLauncher
    {
        public ConcurrentQueue<(string File, IReadOnlyList<string> Args)> Starts { get; } = new();

        public void Start(string fileName, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment = null) =>
            Starts.Enqueue((fileName, arguments));
    }

    private sealed class Setup : IDisposable
    {
        private readonly TempFolder _folder = new();

        public Setup(UpdateOs os, int exitCode, string output = "", string? answers = "4.9.0", bool macServer = true, int[]? clients = null)
        {
            Records = new UpdateRecordFile(_folder.Path);
            Installer = new FakeInstaller(exitCode, output);
            Probe = new FakeProbe(answers);
            Clients = new FakeClients(clients ?? []);
            var facts = new InstallFacts(os, "/", @"C:\Program Files", true, f => macServer && f == Installation.MacServerDaemon);
            Helper = new ServerUpdateHelper(Records, os, facts, Installer, Clients, Probe, Commands, Time);
            var installerPath = Path.Combine(Records.Folder, os == UpdateOs.Windows ? "OADM-4.9.0-win-x64.msi" : os == UpdateOs.MacOs ? "OADM-4.9.0-osx-x64.pkg" : "oadm-server_4.9.0_amd64.deb");
            Directory.CreateDirectory(Records.Folder);
            File.WriteAllText(installerPath, "the checked installer");
            Records.Write(new UpdateRecord
            {
                From = "4.8.0",
                To = "4.9.0",
                StartedUtc = Time.GetUtcNow(),
                Step = UpdateRecord.StepInstall,
                Installer = installerPath,
                InstallerSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(installerPath))),
                InstallerLog = Path.Combine(Records.Folder, "install.log"),
                RequestedBy = "admin@PC-1",
            });
            Installer.Records = Records;
        }

        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero));

        public UpdateRecordFile Records { get; }

        public FakeInstaller Installer { get; }

        public FakeProbe Probe { get; }

        public FakeClients Clients { get; }

        public FakeCommands Commands { get; } = new();

        public ServerUpdateHelper Helper { get; }

        public Task<int> RunAsync() => Helper.RunAsync(new HelperArguments(_folder.Path, "4.9.0", Status), CancellationToken.None);

        public void Dispose() => _folder.Dispose();
    }

    [Theory]
    [InlineData(UpdateOs.Windows)]
    [InlineData(UpdateOs.Linux)]
    [InlineData(UpdateOs.MacOs)]
    public async Task TheInstallerRunsSilentlyAndTheNewVersionIsDone(UpdateOs os)
    {
        using var setup = new Setup(os, 0);

        Assert.Equal(0, await setup.RunAsync());

        var run = Assert.Single(setup.Installer.Runs);
        Assert.Equal(InstallerCommands.For(os, InstallMode.Silent, setup.Records.Read()!.Installer!, run.LogPath).Arguments, run.Arguments);
        var record = setup.Records.Read()!;
        Assert.Equal(UpdateRecord.Done, record.Result);
        Assert.Null(record.Reason);
        Assert.Equal(os == UpdateOs.MacOs, setup.Commands.Runs.Any(r => r.File == ServerHelperStarter.Launchctl && r.Args.SequenceEqual(["remove", "com.oadm.update"])));
    }

    [Theory]
    [InlineData(UpdateOs.Windows)]
    [InlineData(UpdateOs.MacOs)]
    public async Task AnyFailureOfTheHelperEndsTheRecordWithAReason(UpdateOs os)
    {
        using var setup = new Setup(os, 0);
        setup.Installer.Throws = new NotSupportedException("unexpected");

        Assert.Equal(1, await setup.RunAsync());

        var record = setup.Records.Read()!;
        Assert.Equal((UpdateRecord.Failed, UpdateTexts.HelperStopped), (record.Result, record.Reason));
        Assert.NotNull(record.FinishedUtc);
        Assert.Equal(os == UpdateOs.MacOs, setup.Commands.Runs.Any(r => r.Args.SequenceEqual(["remove", "com.oadm.update"])));
    }

    [Fact]
    public async Task AMacHelperWithBadArgumentsRemovesItsJob()
    {
        var commands = new FakeCommands();
        using var error = new StringWriter();

        Assert.Equal(2, await ServerUpdateHelper.RunFromCommandLineAsync(["--update", "--to", "4.9.0"], UpdateOs.MacOs, commands, error));

        var (file, args) = Assert.Single(commands.Runs);
        Assert.Equal(ServerHelperStarter.Launchctl, file);
        Assert.Equal(["remove", "com.oadm.update"], args);
        Assert.Contains("Usage", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWindowsHelperWithBadArgumentsRunsNothing()
    {
        var commands = new FakeCommands();
        using var error = new StringWriter();

        Assert.Equal(2, await ServerUpdateHelper.RunFromCommandLineAsync(["--update"], UpdateOs.Windows, commands, error));
        Assert.Empty(commands.Runs);
    }

    [Fact]
    public async Task ARestartOfTheComputerIsNamed()
    {
        using var setup = new Setup(UpdateOs.Windows, 3010);

        await setup.RunAsync();

        var record = setup.Records.Read()!;
        Assert.Equal(UpdateRecord.Done, record.Result);
        Assert.Equal(UpdateTexts.RestartComputer, record.Reason);
    }

    [Theory]
    [InlineData(UpdateOs.Windows, 1618, "", "Another installation is running. Try again later.")]
    [InlineData(UpdateOs.Windows, 1603, "", "The installer failed (code 1603).")]
    [InlineData(UpdateOs.Linux, 100, "E: Could not get lock /var/lib/dpkg/lock-frontend", "Another installation is running. Try again later.")]
    [InlineData(UpdateOs.Linux, 100, "dpkg: error processing archive", "The installer failed.")]
    [InlineData(UpdateOs.MacOs, 1, "installer: Error", "The installer failed.")]
    public async Task AFailedInstallerWritesItsReasonAndNeverWaits(UpdateOs os, int code, string output, string reason)
    {
        using var setup = new Setup(os, code, output);

        await setup.RunAsync();

        var record = setup.Records.Read()!;
        Assert.Equal(UpdateRecord.Failed, record.Result);
        Assert.Equal(reason, record.Reason);
        Assert.Equal(0, setup.Probe.Calls);
        if (os == UpdateOs.MacOs)
        {
            Assert.Equal(InstallerCommands.MacInstallLog, record.InstallerLog);
        }
    }

    /// <summary>Moves the fake clock on until the helper ended (the outcome never depends on how fast the machine is).</summary>
    private static Task PumpAsync(Task run, FakeTimeProvider time, TimeSpan step) =>
        Eventually.TrueAsync(
            () =>
            {
                if (!run.IsCompleted)
                {
                    time.Advance(step);
                }

                return run.IsCompleted;
            },
            "the helper ends");

    [Fact]
    public async Task ANewVersionThatDoesNotAnswerIsNotBackAfterFiveMinutes()
    {
        using var setup = new Setup(UpdateOs.Linux, 0, answers: null);
        var started = setup.Time.GetUtcNow();

        var run = setup.RunAsync();
        await PumpAsync(run, setup.Time, ServerUpdateHelper.Poll);
        await run;

        var record = setup.Records.Read()!;
        Assert.Equal(UpdateRecord.NotBack, record.Result);
        Assert.Equal(UpdateTexts.NewVersionSilent, record.Reason);
        Assert.True(record.FinishedUtc - started >= ServerUpdateHelper.NewVersionWait);
    }

    [Fact]
    public async Task TheOldVersionAnsweringIsNotDone()
    {
        using var setup = new Setup(UpdateOs.Windows, 0, answers: "4.8.0");
        setup.Probe.AfterFirst = "4.9.0";

        var run = setup.RunAsync();
        await PumpAsync(run, setup.Time, ServerUpdateHelper.Poll);
        await run;

        Assert.Equal(UpdateRecord.Done, setup.Records.Read()!.Result);
        Assert.True(setup.Probe.Calls >= 2);
    }

    [Fact]
    public async Task TheHelperWaitsForTheClientsOnThisComputer()
    {
        using var setup = new Setup(UpdateOs.Windows, 0, clients: [1, 1, 0]);

        var run = setup.RunAsync();
        await PumpAsync(run, setup.Time, TimeSpan.FromSeconds(1));
        await run;

        Assert.Single(setup.Installer.Runs);
        Assert.True(setup.Clients.Calls >= 3);
    }

    [Fact]
    public async Task AClientOnlyMacKeepsTheServerOutWithAChoiceFile()
    {
        using var setup = new Setup(UpdateOs.MacOs, 0, macServer: false);

        await setup.RunAsync();

        var run = Assert.Single(setup.Installer.Runs);
        var at = run.Arguments.ToList().IndexOf("-applyChoiceChangesXML");
        Assert.True(at > 0);
        Assert.Contains("com.oadm.choice.server", await File.ReadAllTextAsync(run.Arguments[at + 1]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMacWithItsServerNeedsNoChoiceFile()
    {
        using var setup = new Setup(UpdateOs.MacOs, 0, macServer: true);

        await setup.RunAsync();

        Assert.DoesNotContain("-applyChoiceChangesXML", Assert.Single(setup.Installer.Runs).Arguments);
    }

    [Fact]
    public async Task AFinishedRecordIsLeftAlone()
    {
        using var setup = new Setup(UpdateOs.Windows, 0);
        setup.Records.Write(setup.Records.Read()! with { Result = UpdateRecord.Failed, Reason = "earlier" });

        await setup.RunAsync();

        Assert.Empty(setup.Installer.Runs);
        Assert.Equal("earlier", setup.Records.Read()!.Reason);
    }

    [Fact]
    public async Task TheStepIsWrittenBeforeTheInstallerRunsAndARestartedHelperNeverRunsItTwice()
    {
        using var setup = new Setup(UpdateOs.MacOs, 0, answers: "4.9.0");
        setup.Records.Write(setup.Records.Read()! with { Step = UpdateRecord.StepInstalling }); // a helper that ran it and died

        await setup.RunAsync();

        Assert.Empty(setup.Installer.Runs);
        Assert.Equal(UpdateRecord.Done, setup.Records.Read()!.Result); // it waited for the new version

        using var fresh = new Setup(UpdateOs.Windows, 0);
        await fresh.RunAsync();
        Assert.Equal(UpdateRecord.StepInstalling, fresh.Installer.StepDuringRun);
    }

    [Fact]
    public async Task OnlyTheCheckedDownloadInTheUpdateFolderRuns()
    {
        using var changed = new Setup(UpdateOs.Windows, 0);
        File.WriteAllText(changed.Records.Read()!.Installer!, "replaced after the check");

        await changed.RunAsync();

        Assert.Empty(changed.Installer.Runs);
        Assert.Equal((UpdateRecord.Failed, UpdateTexts.WrongChecksum), (changed.Records.Read()!.Result, changed.Records.Read()!.Reason));

        using var elsewhere = new Setup(UpdateOs.Windows, 0);
        var outside = Path.Combine(Path.GetTempPath(), "oadm-update-tests", Guid.NewGuid().ToString("N") + ".msi");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.Copy(elsewhere.Records.Read()!.Installer!, outside);
        elsewhere.Records.Write(elsewhere.Records.Read()! with { Installer = outside });

        await elsewhere.RunAsync();

        Assert.Empty(elsewhere.Installer.Runs);
        Assert.Equal(UpdateRecord.Failed, elsewhere.Records.Read()!.Result);
        File.Delete(outside);
    }

    [Fact]
    public void TheServersGiveUpIsLongerThanTheHelpersWorstCase() =>
        Assert.True(ServerUpdateCoordinator.GiveUpAfter > ServerUpdateHelper.ClientWait + ProcessInstallerRunner.Limit + ServerUpdateHelper.NewVersionWait);

    [Theory]
    [InlineData(@"C:\Program Files\OADM\Client\Oadm.Client.exe", @"C:\Program Files\OADM\Client", true)]
    [InlineData(@"C:\ProgramData\OADM\update\helper-1\Oadm.Client.exe", @"C:\Program Files\OADM\Client", false)]
    [InlineData("/home/me/.local/share/Oadm/update/helper-1/Oadm.Client", "/opt/oadm/client", false)]
    [InlineData("/opt/oadm/client/Oadm.Client", "/opt/oadm/client/", true)]
    public void OnlyInstalledClientsAreWaitedFor(string executable, string folder, bool counted) =>
        Assert.Equal(counted, SystemClientProcesses.IsIn(executable, folder));

    [Fact]
    public void TheServersHelperPinsTheServersOwnCertificate()
    {
        using var key = System.Security.Cryptography.ECDsa.Create();
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=oadm", key, System.Security.Cryptography.HashAlgorithmName.SHA256);
        using var own = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var other = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(2));

        var pinned = new GrpcServerVersionProbe(Contracts.Security.ServerCertificatePinning.Fingerprint(own));

        Assert.True(pinned.Accepts(own));
        Assert.False(pinned.Accepts(other));
        Assert.False(pinned.Accepts(null));
        Assert.True(new GrpcServerVersionProbe().Accepts(other)); // the client's helper reads only the version
    }

    [Fact]
    public async Task TheHelperStartsOutsideTheServiceOnEachPlatform()
    {
        var commands = new FakeCommands();
        var launcher = new FakeLauncher();

        await new ServerHelperStarter(UpdateOs.Windows, commands, launcher).StartAsync("C:/data/update/helper-1/Oadm.Server.exe", ["--update"], CancellationToken.None);
        Assert.Equal("C:/data/update/helper-1/Oadm.Server.exe", Assert.Single(launcher.Starts).File);
        Assert.Empty(commands.Runs);

        await new ServerHelperStarter(UpdateOs.Linux, commands, launcher).StartAsync("/var/lib/oadm/update/helper-1/Oadm.Server", ["--update", "--to", "4.9.0"], CancellationToken.None);
        var (file, args) = Assert.Single(commands.Runs);
        Assert.Equal("/usr/bin/systemd-run", file);
        Assert.Equal(["--unit=oadm-update", "--collect", "--quiet"], args.Take(3));
        Assert.Equal(["/var/lib/oadm/update/helper-1/Oadm.Server", "--update", "--to", "4.9.0"], args.TakeLast(4));

        var mac = new FakeCommands();
        await new ServerHelperStarter(UpdateOs.MacOs, mac, launcher).StartAsync("/Library/Application Support/OADM/update/helper-1/Oadm.Server", ["--update"], CancellationToken.None);
        Assert.Equal(["remove", "com.oadm.update"], mac.Runs.First().Args);
        Assert.Equal(["submit", "-l", "com.oadm.update", "--", "/Library/Application Support/OADM/update/helper-1/Oadm.Server", "--update"], mac.Runs.Last().Args);
    }

    [Fact]
    public async Task AHelperThatCannotStartIsReported()
    {
        var commands = new FakeCommands { ExitCode = 1 };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ServerHelperStarter(UpdateOs.Linux, commands, new FakeLauncher()).StartAsync("/x/Oadm.Server", [], CancellationToken.None));
    }

    [Theory]
    [InlineData("https://0.0.0.0:5080", "https://127.0.0.1:5080/")]
    [InlineData("https://[::]:5090", "https://127.0.0.1:5090/")]
    [InlineData("http://localhost:6000", "http://127.0.0.1:6000/")]
    [InlineData("https://10.0.0.5:5080", "https://10.0.0.5:5080/")]
    public void TheHelperAsksTheServerOnThisComputer(string listen, string expected) =>
        Assert.Equal(expected, HelperArguments.StatusUrlFor(listen).ToString());

    [Fact]
    public void TheHelperArgumentsRoundTrip()
    {
        var args = new HelperArguments("/var/lib/oadm", "4.9.0", Status);
        Assert.Equal(args, HelperArguments.Parse(args.ToList()));
        Assert.Null(HelperArguments.Parse(["--update"]));
    }
}
