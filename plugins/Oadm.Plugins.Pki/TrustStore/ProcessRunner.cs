using System.ComponentModel;
using System.Diagnostics;

namespace Oadm.Plugins.Pki.TrustStore;

/// <summary>Result of an external tool.</summary>
/// <param name="ExitCode">Exit code (-1 when it did not run to the end).</param>
/// <param name="Output">Standard output and error, as far as captured (never for elevated Windows runs).</param>
/// <param name="TimedOut">Killed after the timeout.</param>
/// <param name="Cancelled">The user declined the elevation prompt.</param>
public sealed record ProcessResult(int ExitCode, string Output, bool TimedOut = false, bool Cancelled = false)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut && !Cancelled;

    /// <summary>The first non-empty output line ("&lt;tool&gt; failed: &lt;first line&gt;").</summary>
    public string FirstLine => Output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? $"exit code {ExitCode}";
}

/// <summary>Runs the OS certificate tools. Tests replace it; nothing in a test may touch a real trust store.</summary>
public interface IProcessRunner
{
    /// <summary>Runs <paramref name="fileName"/> with the arguments (no shell), capturing the output.</summary>
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct);

    /// <summary>Windows: starts the tool elevated (<c>runas</c>, UAC prompt); no output is captured.</summary>
    Task<ProcessResult> RunElevatedAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct);

    /// <summary>The full path of a tool on the search path (plus /usr/sbin, /sbin), or null.</summary>
    string? FindExecutable(string name);
}

/// <summary>The real runner (<see cref="Process"/>).</summary>
public sealed class ProcessRunner : IProcessRunner
{
    private static readonly string[] ExtraDirectories = ["/usr/local/sbin", "/usr/local/bin", "/usr/sbin", "/usr/bin", "/sbin", "/bin"];

    public static ProcessRunner Instance { get; } = new();

    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);
        var info = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = info };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            return new ProcessResult(-1, ex.Message);
        }

        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timer.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Kill(process);
            return new ProcessResult(-1, $"No answer within {timeout.TotalSeconds:0} s.", TimedOut: true);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }

        var output = (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false));
        return new ProcessResult(process.ExitCode, output);
    }

    public async Task<ProcessResult> RunElevatedAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);
        var info = new ProcessStartInfo(fileName)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        Process? process;
        try
        {
            process = Process.Start(info);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new ProcessResult(-1, "The installation was cancelled.", Cancelled: true); // ERROR_CANCELLED: UAC declined
        }
        catch (Win32Exception ex)
        {
            return new ProcessResult(-1, ex.Message);
        }

        if (process is null)
        {
            return new ProcessResult(-1, "The tool did not start.");
        }

        using (process)
        {
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timer.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timer.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new ProcessResult(-1, $"No answer within {timeout.TotalSeconds:0} s.", TimedOut: true);
            }

            return new ProcessResult(process.ExitCode, string.Empty);
        }
    }

    public string? FindExecutable(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var directories = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Concat(OperatingSystem.IsWindows() ? [] : ExtraDirectories);
        var names = OperatingSystem.IsWindows() ? new[] { name + ".exe", name } : [name];
        foreach (var directory in directories.Distinct(StringComparer.Ordinal))
        {
            foreach (var candidate in names)
            {
                var full = Path.Combine(directory, candidate);
                if (File.Exists(full))
                {
                    return full;
                }
            }
        }

        return null;
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // already gone
        }
        catch (Win32Exception)
        {
            // best effort
        }
    }
}
