using System.Runtime.InteropServices;

using Oadm.Plugins.Vision.Tests.RealModels;

namespace Oadm.Plugins.Vision.Tests.Gpu;

/// <summary>
/// The graphics card the Gpu tests need (docs/spec/vision-center.md "Tests"): DirectML on Windows x64 with a display adapter
/// that is not Windows' basic or remote one, CUDA on Linux x64 with an NVIDIA driver. Found without ONNX Runtime, so the
/// test discovery of a unit run never loads a provider.
/// </summary>
internal static class GpuCard
{
    public const string Category = "Gpu";

    private static readonly Lazy<(string? Name, string? Missing)> Found = new(Find);

    /// <summary>The adapter's name as the system reports it ("NVIDIA GeForce RTX 5070 Ti"); null without a card.</summary>
    public static string? Name => Found.Value.Name;

    /// <summary>Why the Gpu tests skip; null when there is a card to run on.</summary>
    public static string? Missing => Found.Value.Missing;

    private static (string? Name, string? Missing) Find()
    {
        if (SessionFactory.GraphicsCardTarget() is null)
        {
            return (null, $"No graphics card provider on {RuntimeInformation.RuntimeIdentifier} (DirectML: Windows x64, CUDA: Linux x64).");
        }

        if (OperatingSystem.IsWindows())
        {
            var name = WindowsAdapters().FirstOrDefault(n => !n.StartsWith("Microsoft ", StringComparison.OrdinalIgnoreCase));
            return name is null ? (null, "No graphics card for DirectML: only Windows' basic or remote display adapter.") : (name, null);
        }

        const string Gpus = "/proc/driver/nvidia/gpus";
        if (!Directory.Exists(Gpus) || !Directory.EnumerateDirectories(Gpus).Any())
        {
            return (null, $"No NVIDIA card for CUDA ({Gpus} is empty or missing).");
        }

        var model = Directory.EnumerateFiles(Gpus, "information", SearchOption.AllDirectories)
            .SelectMany(File.ReadLines).FirstOrDefault(l => l.StartsWith("Model:", StringComparison.Ordinal));
        return (model?["Model:".Length..].Trim(), null);
    }

    /// <summary>The names of the display adapters (EnumDisplayDevices; mirror drivers left out).</summary>
    private static List<string> WindowsAdapters()
    {
        var names = new List<string>();
        var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
        for (uint i = 0; EnumDisplayDevicesW(null, i, ref device, 0); i++)
        {
            const int MirroringDriver = 0x8;
            if ((device.StateFlags & MirroringDriver) == 0 && !string.IsNullOrWhiteSpace(device.DeviceString) && !names.Contains(device.DeviceString))
            {
                names.Add(device.DeviceString.Trim());
            }

            device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
        }

        return names;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevicesW(string? device, uint index, ref DisplayDevice displayDevice, uint flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;

        public int StateFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }
}

/// <summary>A fact on the graphics card with the real model files <paramref name="needed"/>; skipped with the reason without a card or the files.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class GpuFactAttribute : FactAttribute
{
    public GpuFactAttribute(params string[] needed)
    {
        if ((GpuCard.Missing ?? RealModelFiles.Missing(needed)) is { } reason)
        {
            Skip = reason;
        }
    }
}

/// <summary>A <see cref="GpuFactAttribute"/> that also needs both add-ons' plugin folders (<see cref="GpuPlugins"/>).</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class GpuPluginsFactAttribute : FactAttribute
{
    public GpuPluginsFactAttribute(params string[] needed)
    {
        if ((GpuCard.Missing ?? RealModelFiles.Missing(needed) ?? GpuPlugins.Missing) is { } reason)
        {
            Skip = reason;
        }
    }
}

/// <summary>The plugin folders of both vision add-ons next to the tests (the csproj's VisionGpuPlugins target).</summary>
internal static class GpuPlugins
{
    public const string Ocr = "oadm.ocr";

    public const string Vision = "oadm.vision";

    /// <summary>The folder with oadm.ocr and oadm.vision: OADM_GPU_PLUGINS (e.g. published add-on folders), else gpu-plugins next to the tests.</summary>
    public static string Root => Environment.GetEnvironmentVariable("OADM_GPU_PLUGINS") is { Length: > 0 } root
        ? Path.GetFullPath(root)
        : Path.Combine(AppContext.BaseDirectory, "gpu-plugins");

    public static string? Missing =>
        File.Exists(Path.Combine(Root, Ocr, "Oadm.Plugins.Ocr.Server.dll")) && File.Exists(Path.Combine(Root, Vision, "Oadm.Plugins.Vision.Server.dll"))
            ? null
            : $"The plugin folders of both add-ons are not in {Root}. Run 'manage test gpu' (or build with -p:OadmGpuPlugins=true).";
}
