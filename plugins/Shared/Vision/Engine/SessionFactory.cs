using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;

namespace Oadm.Plugins.Shared.Vision;

/// <summary>Which execution provider a session uses.</summary>
internal enum ExecutionTarget
{
    Cpu,
    DirectMl,
    Cuda,
}

/// <summary>
/// Creates ONNX Runtime sessions for the vision add-ons (docs/spec/ocr.md "GPU", docs/spec/vision-center.md "Load"): on the
/// CPU with the threads the caller gives; on a graphics card with DirectML (Windows) or CUDA (Linux x64). Only the standard
/// operators: no custom operator library is ever registered. The card's test is each add-on's own (per model).
/// </summary>
internal static class SessionFactory
{
    /// <summary>A session of <paramref name="modelPath"/> on <paramref name="target"/>.</summary>
    public static InferenceSession Create(string modelPath, ExecutionTarget target, int threads)
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            IntraOpNumThreads = threads,
            InterOpNumThreads = 1,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
        };
        try
        {
            switch (target)
            {
                case ExecutionTarget.DirectMl:
                    // DirectML needs these two off (ONNX Runtime documentation of the provider).
                    options.EnableMemoryPattern = false;
                    options.AppendExecutionProvider_DML(0);
                    break;
                case ExecutionTarget.Cuda:
                    // The card's memory pool grows by what a call needs, not to the next power of two (spec "GPU").
                    using (var cuda = new OrtCUDAProviderOptions())
                    {
                        cuda.UpdateOptions(new Dictionary<string, string>
                        {
                            ["device_id"] = "0",
                            ["arena_extend_strategy"] = "kSameAsRequested",
                        });
                        options.AppendExecutionProvider_CUDA(cuda);
                    }

                    break;
                case ExecutionTarget.Cpu:
                default:
                    break;
            }

            return new InferenceSession(modelPath, options);
        }
        finally
        {
            options.Dispose();
        }
    }

    /// <summary>The graphics card provider of this platform, or null when there is none to try.</summary>
    public static ExecutionTarget? GraphicsCardTarget()
    {
        if (OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            return ExecutionTarget.DirectMl;
        }

        if (OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            return ExecutionTarget.Cuda;
        }

        return null;
    }

    /// <summary>The card's memory in bytes as DirectML reports it ("15997 MB"), or null when it does not tell.</summary>
    public static long? GraphicsCardMemory(ExecutionTarget target)
    {
        if (target != ExecutionTarget.DirectMl)
        {
            return null;
        }

        try
        {
            foreach (var device in OrtEnv.Instance().GetEpDevices())
            {
                if (device.EpName == "DmlExecutionProvider" && device.HardwareDevice.Metadata.Entries.TryGetValue("DxgiVideoMemory", out var memory))
                {
                    return ParseVideoMemory(memory);
                }
            }
        }
#pragma warning disable CA1031 // Only the size: a failure keeps the general cap.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        return null;
    }

    /// <summary>"15997 MB" in bytes; null for anything else.</summary>
    internal static long? ParseVideoMemory(string? text)
    {
        var parts = (text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && parts[1] == "MB" && long.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var mb) && mb > 0
            ? mb * 1024 * 1024
            : null;
    }

    /// <summary>The graphics card's name ("NVIDIA GeForce RTX 5070 Ti") or a general one.</summary>
    public static string GraphicsCardName(ExecutionTarget target)
    {
        try
        {
            var provider = target == ExecutionTarget.DirectMl ? "DmlExecutionProvider" : "CUDAExecutionProvider";
            foreach (var device in OrtEnv.Instance().GetEpDevices())
            {
                if (device.EpName == provider && device.HardwareDevice.Metadata.Entries.TryGetValue("Description", out var name) && !string.IsNullOrWhiteSpace(name))
                {
                    return name.Trim();
                }
            }
        }
#pragma warning disable CA1031 // Only the name: a failure keeps the general one.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        if (OperatingSystem.IsLinux())
        {
            try
            {
                // The NVIDIA driver's own description of each card.
                var folder = "/proc/driver/nvidia/gpus";
                if (Directory.Exists(folder))
                {
                    foreach (var file in Directory.EnumerateFiles(folder, "information", SearchOption.AllDirectories))
                    {
                        var model = File.ReadLines(file).FirstOrDefault(l => l.StartsWith("Model:", StringComparison.Ordinal));
                        if (model is not null)
                        {
                            return model["Model:".Length..].Trim();
                        }
                    }
                }
            }
#pragma warning disable CA1031
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }

        return "Graphics card";
    }
}
