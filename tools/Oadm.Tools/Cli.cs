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
    /// This tool as a command line that works with administrator rights too: the dotnet host of the running runtime with
    /// Oadm.Tools.dll. An elevated process (runas, sudo) gets a fresh environment and would not find a .NET installed for
    /// one user (DOTNET_ROOT, ~/.dotnet) through the tool's own launcher.
    /// </summary>
    public static (string FileName, List<string> Args) Self()
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "Oadm.Tools.dll");
        var process = Environment.ProcessPath ?? throw new ToolException("The path of this tool is not known.");
        if (string.Equals(Path.GetFileNameWithoutExtension(process), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return (process, [dll]);
        }

        // <root>/shared/Microsoft.NETCore.App/<version>/ -> <root>/dotnet
        var runtime = new DirectoryInfo(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());
        var host = runtime.Parent?.Parent?.Parent is { } root ? Path.Combine(root.FullName, Exe("dotnet")) : null;
        return host is not null && File.Exists(host) && File.Exists(dll) ? (host, [dll]) : (process, []);
    }

    /// <summary>True for a plugin id that is a plain folder name ("acme.door-bell"): letters, digits, ".", "-", "_".</summary>
    public static bool IsSafeId(string id) =>
        id.Length is > 0 and <= 128 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')
        && id[0] != '.' && !id.Contains("..", StringComparison.Ordinal);

    /// <summary>
    /// Copies a plugin folder to <c>&lt;root&gt;/&lt;id&gt;</c> and returns the number of files. The id must be a plain folder
    /// name (never "..", a path or empty: the target is always one folder below <paramref name="root"/>). The new copy is
    /// written next to the target first; then the old folder is moved aside and the new one moved in, so a failure (files
    /// in use) leaves the old plugin as it was.
    /// </summary>
    public static int CopyPlugin(string source, string root, string id)
    {
        if (!IsSafeId(id))
        {
            throw new ToolException($"The plugin id \"{id}\" is not a plain name (letters, digits, \".\", \"-\"). Nothing was copied.");
        }

        var parent = Path.GetFullPath(root);
        var target = Path.GetFullPath(Path.Combine(parent, id));
        if (!string.Equals(Path.GetDirectoryName(target), parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolException($"{target} is not a folder of {parent}. Nothing was copied.");
        }

        Directory.CreateDirectory(parent);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var staging = $"{target}.new-{suffix}";
        var old = $"{target}.old-{suffix}";
        var count = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var path = Path.Combine(staging, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.Copy(file, path, overwrite: true);
                count++;
            }

            if (Directory.Exists(target))
            {
                Directory.Move(target, old); // fails as a whole while a program holds a file open
            }

            Directory.Move(staging, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(staging);
            if (!Directory.Exists(target) && Directory.Exists(old))
            {
                Directory.Move(old, target);
            }

            throw new ToolException($"{target} could not be replaced ({ex.Message}). Close the program that uses it and try again; the old plugin is unchanged.");
        }

        TryDelete(old);
        return count;
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // rare: the move worked but a file could not be deleted; the next deploy or a person removes it
        }
    }
}
