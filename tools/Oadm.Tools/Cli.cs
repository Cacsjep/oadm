using System.Diagnostics;
using System.Reflection;

namespace Oadm.Tools;

/// <summary>A usage error: the message and exit code 2.</summary>
internal sealed class UsageException(string message) : Exception(message);

/// <summary>A failure the user can act on: the message and exit code 1, no stack trace.</summary>
internal sealed class ToolException(string message) : Exception(message);

/// <summary>Options of one command: <c>--name value</c>, flags (<c>--fake</c>) and the rest in order.</summary>
internal sealed class Options
{
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);
    private readonly HashSet<string> _used = new(StringComparer.Ordinal);

    public List<string> Positional { get; } = [];

    /// <summary>Reads <paramref name="args"/>; names in <paramref name="flags"/> take no value.</summary>
    public static Options Parse(IEnumerable<string> args, params string[] flags)
    {
        var options = new Options();
        var list = args.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var arg = list[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                options.Positional.Add(arg);
            }
            else if (flags.Contains(arg, StringComparer.Ordinal))
            {
                options._flags.Add(arg);
            }
            else if (arg.IndexOf('=', StringComparison.Ordinal) is var eq and > 2)
            {
                options.Add(arg[..eq], arg[(eq + 1)..]);
            }
            else if (i + 1 < list.Count)
            {
                options.Add(arg, list[++i]);
            }
            else
            {
                throw new UsageException($"{arg} needs a value");
            }
        }

        return options;
    }

    public string? Get(string name)
    {
        _used.Add(name);
        return _values.TryGetValue(name, out var values) ? values[^1] : null;
    }

    public IReadOnlyList<string> All(string name)
    {
        _used.Add(name);
        return _values.TryGetValue(name, out var values) ? values : [];
    }

    public bool Has(string flag)
    {
        _used.Add(flag);
        return _flags.Contains(flag);
    }

    public int Int(string name, int fallback, int min, int max)
    {
        var text = Get(name);
        if (text is null)
        {
            return fallback;
        }

        return int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value) && value >= min && value <= max
            ? value
            : throw new UsageException($"{name} must be a number from {min} to {max} (got '{text}')");
    }

    /// <summary>Refuses options the command did not read, so a typo never goes unnoticed.</summary>
    public void RejectUnknown()
    {
        if (_values.Keys.Concat(_flags).FirstOrDefault(n => !_used.Contains(n)) is { } unknown)
        {
            throw new UsageException($"unknown option {unknown}");
        }
    }

    private void Add(string name, string value)
    {
        if (!_values.TryGetValue(name, out var values))
        {
            _values[name] = values = [];
        }

        values.Add(value);
    }
}

/// <summary>Small process and environment helpers.</summary>
internal static class Cli
{
    /// <summary>The tool's version without the commit ("3.5.0"); the OADM release it belongs to.</summary>
    public static string Version { get; } =
        (typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    /// <summary>The runtime identifier of the portable OADM for this computer (Apple silicon runs the Intel build).</summary>
    public static string Rid =>
        OperatingSystem.IsWindows() ? "win-x64"
        : OperatingSystem.IsMacOS() ? "osx-x64"
        : System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "linux-arm64" : "linux-x64";

    /// <summary>The executable name of an OADM app on this OS ("Oadm.Server.exe" on Windows).</summary>
    public static string Exe(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    /// <summary>Runs a program and returns its exit code and output (stdout and stderr together).</summary>
    public static (int Code, string Output) Capture(string fileName, IEnumerable<string> args, IDictionary<string, string>? environment = null, TimeSpan? timeout = null)
    {
        var info = new ProcessStartInfo(fileName) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        foreach (var (key, value) in environment ?? new Dictionary<string, string>())
        {
            info.Environment[key] = value;
        }

        try
        {
            using var process = Process.Start(info) ?? throw new ToolException($"{fileName} could not be started.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeout ?? TimeSpan.FromMinutes(2)))
            {
                process.Kill(entireProcessTree: true);
                throw new ToolException($"{Path.GetFileName(fileName)} did not finish in time.");
            }

            return (process.ExitCode, output.Result + error.Result);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new ToolException($"{fileName} could not be started: {ex.Message}");
        }
    }

    /// <summary>Runs a program in this console (its output goes straight to the terminal) and returns its exit code.</summary>
    public static int Run(string fileName, IEnumerable<string> args, IDictionary<string, string>? environment = null, string? workingDirectory = null)
    {
        using var process = Start(fileName, args, environment, workingDirectory);
        process.WaitForExit();
        return process.ExitCode;
    }

    /// <summary>Starts a program in this console without waiting.</summary>
    public static Process Start(string fileName, IEnumerable<string> args, IDictionary<string, string>? environment = null, string? workingDirectory = null)
    {
        var info = new ProcessStartInfo(fileName) { UseShellExecute = false, WorkingDirectory = workingDirectory ?? "" };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        foreach (var (key, value) in environment ?? new Dictionary<string, string>())
        {
            info.Environment[key] = value;
        }

        try
        {
            return Process.Start(info) ?? throw new ToolException($"{fileName} could not be started.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new ToolException($"{fileName} could not be started: {ex.Message}");
        }
    }

    /// <summary>
    /// This tool as a command line (<c>oadm</c>, or <c>dotnet Oadm.Tools.dll</c> for a local tool), to start it again,
    /// e.g. with administrator rights.
    /// </summary>
    public static (string FileName, List<string> Args) Self()
    {
        var process = Environment.ProcessPath ?? throw new ToolException("The path of this tool is not known.");
        return string.Equals(Path.GetFileNameWithoutExtension(process), "dotnet", StringComparison.OrdinalIgnoreCase)
            ? (process, [Path.Combine(AppContext.BaseDirectory, "Oadm.Tools.dll")])
            : (process, []);
    }

    /// <summary>Copies a folder (replacing the target folder), returns the number of files.</summary>
    public static int CopyFolder(string source, string target)
    {
        if (Directory.Exists(target))
        {
            try
            {
                Directory.Delete(target, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ToolException($"{target} is in use ({ex.Message}). Close the program that uses it and try again.");
            }
        }

        var count = 0;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var path = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Copy(file, path, overwrite: true);
            count++;
        }

        return count;
    }
}
