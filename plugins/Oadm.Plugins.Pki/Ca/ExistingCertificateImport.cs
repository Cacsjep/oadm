using System.Globalization;

using Microsoft.Extensions.Logging;

using Oadm.Plugins.Pki.Device;
using Oadm.Plugins.Pki.Tasks;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Pki.Ca;

/// <summary>
/// The one pass that brings certificates of the OADM CAs from before the list of issued certificates (OADM 4.1 and older)
/// into it: some minutes after the start, the certificate list (REST cert v1, read only) of every device that answers and
/// has AXIS OS 11.11 or later, 4 devices at a time. Done once per server (plugin setting <c>existingImported</c>), and only
/// when a round reached the devices: at least one device was read, or there is nothing to read (no devices, or none that
/// answers with the certificate API). A round that read no device (all offline, statuses not known yet) runs again after
/// <see cref="RetryInterval"/> and at the next start. Devices that did not answer get theirs listed when a PKI task or the
/// View certificates window reads their certificates.
/// </summary>
public sealed partial class ExistingCertificateImport(PkiService service, ISystemAccess system, IPluginSettings settings, TimeProvider time, TimeSpan delay, ILogger logger)
{
    /// <summary>Set when the pass reached the devices (the time, ISO 8601).</summary>
    public const string DoneKey = "existingImported";

    /// <summary>Devices read at the same time.</summary>
    public const int Parallel = 4;

    private readonly ILogger _logger = logger;

    /// <summary>The next round after one that reached no device.</summary>
    public TimeSpan RetryInterval { get; init; } = TimeSpan.FromHours(1);

    /// <summary>Runs the pass unless it ran before; stops with <paramref name="ct"/> (the plugin stopping) and runs again at the next start.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            if (await settings.GetAsync(DoneKey, ct).ConfigureAwait(false) is not null)
            {
                return;
            }

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, time, ct).ConfigureAwait(false);
            }

            while (!await RoundAsync(ct).ConfigureAwait(false))
            {
                await Task.Delay(RetryInterval, time, ct).ConfigureAwait(false);
            }

            await settings.SetAsync(DoneKey, PkiJson.Serialize(time.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture)), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // stopping: the pass runs again at the next start
        }
#pragma warning disable CA1031 // Background work: logged; certificates still come in through the tasks.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogFailed(ex);
        }
    }

    /// <summary>One round over the devices; true when it reached them (or there is nothing to read).</summary>
    private async Task<bool> RoundAsync(CancellationToken ct)
    {
        var devices = await system.Devices.ListAsync(ct).ConfigureAwait(false);
        var answering = devices.Where(d => d.Status == DeviceStatus.Ok).ToList();
        var candidates = answering.Where(PkiCompatibility.CanRunCertificateTask).ToList();
        int read = 0, failed = 0, added = 0;
        await System.Threading.Tasks.Parallel.ForEachAsync(candidates, new ParallelOptions { MaxDegreeOfParallelism = Parallel, CancellationToken = ct }, async (device, token) =>
        {
            try
            {
                var vapix = await system.Vapix.CreateAsync(device.Id, token).ConfigureAwait(false);
                var list = await CertApi.ListCertificatesAsync(vapix, token).ConfigureAwait(false);
                Interlocked.Add(ref added, await service.ImportFoundAsync(device, list.Select(c => (c.Alias, c.Pem)), token).ConfigureAwait(false));
                Interlocked.Increment(ref read);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                Interlocked.Increment(ref failed);
                LogDeviceFailed(device.Address, ex.Message);
            }
        }).ConfigureAwait(false);

        var reached = read > 0 || devices.Count == 0 || (answering.Count > 0 && candidates.Count == 0);
        if (reached)
        {
            LogDone(read, added, failed);
        }
        else
        {
            LogNotReached(devices.Count, failed);
        }

        return reached;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "PKI: the certificates of {Device} could not be read for the list of issued certificates: {Error}")]
    private partial void LogDeviceFailed(string device, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "PKI: read the certificates of {Read} devices for the list of issued certificates: {Added} added, {Failed} devices not read")]
    private partial void LogDone(int read, int added, int failed);

    [LoggerMessage(Level = LogLevel.Information, Message = "PKI: no device of {Devices} could be read for the list of issued certificates ({Failed} did not answer); tried again later")]
    private partial void LogNotReached(int devices, int failed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "PKI: reading the certificates of the devices for the list of issued certificates failed")]
    private partial void LogFailed(Exception ex);
}
