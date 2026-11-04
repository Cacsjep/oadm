using Oadm.Contracts.Updates;
using Oadm.Server.Hosting;

namespace Oadm.Server.Updates;

/// <summary>Starts the server's update helper outside the service (docs/spec/self-update.md rule 9).</summary>
public interface IServerHelperStarter
{
    /// <summary>Starts <paramref name="helper"/> with <paramref name="arguments"/>; throws when it could not be started.</summary>
    Task StartAsync(string helper, IReadOnlyList<string> arguments, CancellationToken ct);
}

/// <summary>
/// Windows: a detached process (the Service Control Manager does not stop a service's child processes). Linux: its own
/// transient unit (<c>systemd-run --unit=oadm-update --collect</c>: systemd stops every process of the service's control
/// group). macOS: its own launchd job (<c>launchctl submit -l com.oadm.update</c>, which takes no environment, so <c>env</c>
/// sets it; the helper removes the job at its end).
/// </summary>
/// <param name="kept">The environment the helper keeps; null: <see cref="ServiceEnvironment"/>.</param>
public sealed class ServerHelperStarter(UpdateOs os, ICommandRunner commands, IProcessLauncher processes, IReadOnlyDictionary<string, string>? kept = null) : IServerHelperStarter
{
    public const string SystemdRun = "/usr/bin/systemd-run";
    public const string Launchctl = "/bin/launchctl";
    public const string Env = "/usr/bin/env";
    public const string UnitName = "oadm-update";
    public const string LaunchdLabel = "com.oadm.update";

    /// <summary>The environment the helper keeps from the service (the single-file host's extraction folder, the data folder).</summary>
    public static IReadOnlyDictionary<string, string> ServiceEnvironment()
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in new[] { "DOTNET_BUNDLE_EXTRACT_BASE_DIR", Core.Persistence.OadmPaths.DataDirEnvironmentVariable })
        {
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
            {
                environment[name] = value;
            }
        }

        return environment;
    }

    public async Task StartAsync(string helper, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(helper);
        ArgumentNullException.ThrowIfNull(arguments);
        var environment = kept ?? ServiceEnvironment();
        switch (os)
        {
            case UpdateOs.Windows:
                processes.Start(helper, arguments, environment);
                return;
            case UpdateOs.Linux:
            {
                List<string> run = [$"--unit={UnitName}", "--collect", "--quiet"];
                run.AddRange(environment.Select(e => $"--setenv={e.Key}={e.Value}"));
                run.Add(helper);
                run.AddRange(arguments);
                var result = await commands.RunAsync(SystemdRun, run, ct).ConfigureAwait(false);
                if (!result.Succeeded)
                {
                    throw new InvalidOperationException($"systemd-run failed ({result.ExitCode}): {result.Output}");
                }

                return;
            }

            default:
            {
                // A job of an earlier update that is still loaded would refuse the label.
                _ = await commands.RunAsync(Launchctl, ["remove", LaunchdLabel], ct).ConfigureAwait(false);
                List<string> submit = ["submit", "-l", LaunchdLabel, "--"];
                if (environment.Count > 0)
                {
                    submit.Add(Env);
                    submit.AddRange(environment.Select(e => $"{e.Key}={e.Value}"));
                }

                submit.Add(helper);
                submit.AddRange(arguments);
                var result = await commands.RunAsync(Launchctl, submit, ct).ConfigureAwait(false);
                if (!result.Succeeded)
                {
                    throw new InvalidOperationException($"launchctl submit failed ({result.ExitCode}): {result.Output}");
                }

                return;
            }
        }
    }
}
