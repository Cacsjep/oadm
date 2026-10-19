using System.Collections.Concurrent;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.DeviceConfig.Configure;

/// <summary>
/// Reads what Configure devices and Create configuration file show of one device: its parameters (definitions and values),
/// and for the source device also its action rules, schedules and stream profiles. A part that cannot be read is a
/// problem of that part only. Read-only.
/// </summary>
public static class DeviceSettingsReader
{
    /// <summary>How long definitions of a model and AXIS OS are kept (they never change for the same AXIS OS).</summary>
    public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);

    private static readonly ConcurrentDictionary<string, (DateTime Until, IReadOnlyList<ParamDefinition> Definitions)> Cache = new(StringComparer.Ordinal);

    /// <summary>Answers the read (<paramref name="full"/>) or definitions query.</summary>
    public static async Task<DeviceSettings> ReadAsync(ITaskQueryContext ctx, IDeviceInfo device, bool full, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);
        if (TaskSupportReasons.ForStatus(device) is { } refused)
        {
            return new DeviceSettings { Error = refused };
        }

        try
        {
            var info = await ctx.Vapix.GetBasicDeviceInfoAsync(ct).ConfigureAwait(false);
            var key = ModelNames.Normalize(info.ProdNbr) + "|" + info.Version;
            IReadOnlyList<ParamDefinition> parameters;
            if (!full && Cache.TryGetValue(key, out var cached) && cached.Until > DateTime.UtcNow)
            {
                parameters = cached.Definitions;
            }
            else
            {
                parameters = await ParamDefinitionsReader.ReadAsync(ctx.Vapix, ct).ConfigureAwait(false);
                Cache[key] = (DateTime.UtcNow + CacheFor, [.. parameters.Select(p => p with { Value = null })]);
                if (!full)
                {
                    parameters = Cache[key].Definitions;
                }
            }

            var settings = new DeviceSettings { Model = info.ProdNbr, Firmware = info.Version, Serial = info.SerialNumber, Parameters = parameters };
            if (!full)
            {
                return settings;
            }

            var problems = new List<string>();
            IReadOnlyList<ConfigActionRule> rules = [];
            IReadOnlyList<ConfigSchedule> schedules = [];
            IReadOnlyList<ConfigStreamProfile> profiles = [];
            try
            {
                rules = await ActionEngineApi.ReadRulesAsync(ctx.Vapix, ct).ConfigureAwait(false);
                schedules = rules.Count == 0 ? [] : await ActionEngineApi.ReadSchedulesAsync(ctx.Vapix, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                problems.Add("Action rules could not be read: " + DeviceErrors.Describe(ex, timeout: null));
            }

            if (device.HasVideo)
            {
                try
                {
                    profiles = (await StreamProfileApi.ListAsync(ctx.Vapix, ct).ConfigureAwait(false)).Profiles;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    problems.Add("Stream profiles could not be read: " + DeviceErrors.Describe(ex, timeout: null));
                }
            }

            return settings with { Rules = rules, Schedules = schedules, StreamProfiles = profiles, Problems = problems };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            if (DeviceErrors.IsCertificateChanged(ex))
            {
                return new DeviceSettings { Error = DeviceMessages.CertificateChanged };
            }

            return new DeviceSettings { Error = DeviceErrors.Describe(ex, timeout: null) };
        }
    }

    /// <summary>Forgets cached definitions (tests).</summary>
    internal static void ClearCache() => Cache.Clear();
}
