using System.Globalization;

using Oadm.Plugins.Vision.Api;
using Oadm.Plugins.Vision.Model;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Vision.Pipelines;

/// <summary>
/// Copies of a saved pipeline on other cameras (docs/spec/vision-center.md "Copy to cameras"): independent pipelines with the
/// same nodes, links, mode and frame rate; areas, lines and arrows keep their place in the picture (they are relative).
/// </summary>
internal static class PipelineCopies
{
    /// <summary>Camera options read at once at most.</summary>
    public const int OptionsAtOnce = 4;

    /// <summary>
    /// The copy of <paramref name="original"/> for <paramref name="camera"/>: no id and no owner yet, its name
    /// "&lt;name&gt; (&lt;camera&gt;)", the original's video source and resolution where <paramref name="options"/> offer them,
    /// else the camera's defaults (null options: they could not be read).
    /// </summary>
    public static Pipeline CopyOf(Pipeline original, IDeviceInfo camera, CameraOptions? options)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(camera);
        return original with
        {
            Id = string.Empty,
            OwnerScopeKey = null,
            Name = NameOf(original.Name, camera),
            DeviceId = camera.Id,
            VideoSource = options is not null && options.VideoSources.Contains(original.VideoSource) ? original.VideoSource : 1,
            Resolution = ResolutionOf(original.Resolution, options),
            Nodes = [.. original.Nodes],
            Links = [.. original.Links],
        };
    }

    /// <summary>"Gate 1 people (axis-b8a44f631339)": the camera's host name, else its address; at most the name limit.</summary>
    public static string NameOf(string original, IDeviceInfo camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        var suffix = " (" + (string.IsNullOrWhiteSpace(camera.HostName) ? camera.Address : camera.HostName) + ")";
        var room = Math.Max(1, VisionLimits.MaxNameLength - suffix.Length);
        var name = (original.Length > room ? original[..room].TrimEnd() : original) + suffix;
        return name.Length > VisionLimits.MaxNameLength ? name[..VisionLimits.MaxNameLength] : name;
    }

    /// <summary><paramref name="name"/>, else "&lt;name&gt; 2", "&lt;name&gt; 3", ... the first not in <paramref name="taken"/> (within the name limit).</summary>
    public static string Unique(string name, ISet<string> taken)
    {
        ArgumentNullException.ThrowIfNull(taken);
        if (!taken.Contains(name))
        {
            return name;
        }

        for (var i = 2; ; i++)
        {
            var number = " " + i.ToString(CultureInfo.InvariantCulture);
            var stem = name.Length + number.Length > VisionLimits.MaxNameLength ? name[..(VisionLimits.MaxNameLength - number.Length)] : name;
            if (!taken.Contains(stem + number))
            {
                return stem + number;
            }
        }
    }

    /// <summary>The original's resolution when the camera offers it, else the default or the camera's largest below it.</summary>
    public static Resolution ResolutionOf(Resolution original, CameraOptions? options)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (options is not { Resolutions.Count: > 0 } offered)
        {
            return Resolution.Default;
        }

        if (offered.Resolutions.Contains(original))
        {
            return original;
        }

        if (offered.Resolutions.Contains(Resolution.Default))
        {
            return Resolution.Default;
        }

        var limit = (long)Resolution.Default.Width * Resolution.Default.Height;
        return offered.Resolutions
            .Where(r => (long)r.Width * r.Height <= limit)
            .OrderByDescending(r => (long)r.Width * r.Height)
            .FirstOrDefault() ?? Resolution.Default;
    }
}
