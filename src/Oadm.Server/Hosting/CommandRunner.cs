using System.Diagnostics;

namespace Oadm.Server.Hosting;

/// <summary>Exit code and output of an external command.</summary>
public sealed record CommandResult(int ExitCode, string Output)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>Runs operating system tools (netsh, find, chown, chmod). Replaced by a fake in tests.</summary>
public interface ICommandRunner
{
    Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken ct);
}

/// <summary>
/// Starts the process with <see cref="ProcessStartInfo.ArgumentList"/> (no shell, arguments quoted by .NET), captures
/// standard output and error together and gives up after the timeout (default 30 s).
/// </summary>
public sealed class ProcessCommandRunner(TimeSpan? timeout = null) : ICommandRunner
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(30);

    public async Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);
        var info = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"{fileName} could not be started.");
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_timeout);
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            var output = (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false));
            return new CommandResult(process.ExitCode, output.Trim());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{fileName} did not finish within {_timeout.TotalSeconds:0} s.");
        }
    }
}
