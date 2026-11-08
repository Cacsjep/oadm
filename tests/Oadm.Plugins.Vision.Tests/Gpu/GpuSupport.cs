using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using Xunit.Abstractions;

namespace Oadm.Plugins.Vision.Tests.Gpu;

/// <summary>One detection as plain values (also of a plugin load context's own types).</summary>
internal sealed record Found(string ClassName, double Score, double X, double Y, double Width, double Height)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{ClassName} {Score:P1} at x {X:F3} y {Y:F3} w {Width:F3} h {Height:F3}");
}

/// <summary>How the card's detections compare with the CPU's.</summary>
/// <param name="Same">Every detection of one has its match in the other (same class, overlap and score within the tolerance), except those near the minimum score.</param>
/// <param name="MinIoU">The smallest overlap of a matched pair (1 when nothing was found).</param>
/// <param name="MaxScoreDiff">The largest score difference of a matched pair.</param>
/// <param name="Diff">What did not match.</param>
internal sealed record Comparison(bool Same, double MinIoU, double MaxScoreDiff, int Pairs, string Diff);

internal static class GpuSupport
{
    /// <summary>A pair matches at this overlap or more.</summary>
    public const double MinIoU = 0.9;

    /// <summary>A pair matches at this score difference or less.</summary>
    public const double MaxScoreDiff = 0.05;

    /// <summary>A detection this close above the minimum score may be missing on the other side.</summary>
    public const double NearMinimum = 0.05;

    public static Comparison Compare(IReadOnlyList<Found> cpu, IReadOnlyList<Found> card, double minScore)
    {
        var minIoU = 1.0;
        var maxDiff = 0.0;
        var pairs = 0;
        var diff = new StringBuilder();
        var used = new HashSet<int>();
        foreach (var c in cpu.OrderByDescending(d => d.Score))
        {
            var best = -1;
            var bestIoU = 0.0;
            for (var i = 0; i < card.Count; i++)
            {
                if (!used.Contains(i) && card[i].ClassName == c.ClassName && IoU(c, card[i]) is var iou && iou > bestIoU)
                {
                    (best, bestIoU) = (i, iou);
                }
            }

            if (best < 0 || bestIoU < MinIoU || Math.Abs(card[best].Score - c.Score) > MaxScoreDiff)
            {
                if (c.Score >= minScore + NearMinimum)
                {
                    diff.Append(CultureInfo.InvariantCulture, $"CPU {c} has no match on the card{(best < 0 ? string.Empty : $" (closest {card[best]}, overlap {bestIoU:F3})")}; ");
                }

                continue;
            }

            used.Add(best);
            pairs++;
            minIoU = Math.Min(minIoU, bestIoU);
            maxDiff = Math.Max(maxDiff, Math.Abs(card[best].Score - c.Score));
        }

        for (var i = 0; i < card.Count; i++)
        {
            if (!used.Contains(i) && card[i].Score >= minScore + NearMinimum)
            {
                diff.Append(CultureInfo.InvariantCulture, $"card {card[i]} has no match on the CPU; ");
            }
        }

        return new Comparison(diff.Length == 0, minIoU, maxDiff, pairs, diff.ToString());
    }

    public static double IoU(Found a, Found b)
    {
        var w = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X);
        var h = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);
        if (w <= 0 || h <= 0)
        {
            return 0;
        }

        var inter = w * h;
        return inter / ((a.Width * a.Height) + (b.Width * b.Height) - inter);
    }

    public static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? double.NaN : sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2;
    }

    /// <summary>The ONNX Runtime and DirectML libraries this process loaded, with their paths.</summary>
    public static List<string> NativeModules() => [.. Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
        .Where(m => m.ModuleName.Contains("onnxruntime", StringComparison.OrdinalIgnoreCase) || m.ModuleName.Contains("DirectML", StringComparison.OrdinalIgnoreCase))
        .Select(m => m.FileName)
        .Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>The card's used memory in MB as nvidia-smi reports it (the whole card); null without nvidia-smi.</summary>
    public static int? CardMemoryMb()
    {
        try
        {
            using var smi = Process.Start(new ProcessStartInfo("nvidia-smi", "--query-gpu=memory.used --format=csv,noheader,nounits")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (smi is null)
            {
                return null;
            }

            var text = smi.StandardOutput.ReadToEnd();
            smi.WaitForExit(10_000);
            return int.TryParse(text.Split('\n')[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var mb) ? mb : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The dotnet host this test runs on (the muxer above the shared runtime folder).</summary>
    private static string DotnetHost()
    {
        var runtime = RuntimeEnvironment.GetRuntimeDirectory();
        var root = Path.GetFullPath(Path.Combine(runtime, "..", "..", ".."));
        return Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
    }

    /// <summary>
    /// Starts this test assembly as a fresh process (<see cref="GpuChild.Main"/>) with <paramref name="args"/>, its output
    /// written to the test output; ends it after <paramref name="limit"/>. The child's line "RESULT {json}" is its result.
    /// </summary>
    public static async Task<(int ExitCode, JsonDocument? Result)> RunChildAsync(ITestOutputHelper output, TimeSpan limit, params string[] args)
    {
        var assembly = Path.Combine(AppContext.BaseDirectory, typeof(GpuChild).Assembly.GetName().Name + ".dll");
        var start = new ProcessStartInfo(DotnetHost())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(assembly);
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var child = Process.Start(start) ?? throw new InvalidOperationException("The child process did not start.");
        JsonDocument? result = null;
        var lines = new List<string>();
        void Line(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (lines)
            {
                lines.Add(line);
                if (line.StartsWith("RESULT ", StringComparison.Ordinal))
                {
                    result = JsonDocument.Parse(line["RESULT ".Length..]);
                }
            }
        }

        child.OutputDataReceived += (_, e) => Line(e.Data);
        child.ErrorDataReceived += (_, e) => Line(e.Data is null ? null : "stderr: " + e.Data);
        child.BeginOutputReadLine();
        child.BeginErrorReadLine();
        using var timeout = new CancellationTokenSource(limit);
        try
        {
            await child.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync(CancellationToken.None);
            lines.Add($"The child did not end within {limit}: ended.");
        }

        child.WaitForExit(); // the last output lines
        lock (lines)
        {
            foreach (var line in lines)
            {
                output.WriteLine(line);
            }
        }

        return (child.ExitCode, result);
    }
}

/// <summary>Log lines of the add-ons into a test's output (lines after the test's end are dropped).</summary>
internal sealed class TestOutputLog(ITestOutputHelper output, string name, List<string>? lines = null) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var line = formatter(state, exception);
        if (lines is not null)
        {
            lock (lines)
            {
                lines.Add(line);
            }
        }

        try
        {
            output.WriteLine($"[{name}] {logLevel}: {line}");
        }
        catch (InvalidOperationException)
        {
        }
    }
}

/// <summary>Log lines of the add-ons on the child's console.</summary>
internal sealed class ConsoleLog(string name, List<string>? lines = null) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var line = formatter(state, exception);
        if (lines is not null)
        {
            lock (lines)
            {
                lines.Add(line);
            }
        }

        Console.WriteLine($"[{name}] {logLevel}: {line}");
    }
}
