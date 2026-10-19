using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Sdk.Compliance;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Sdk.Testing;

/// <summary>
/// One compliance check run on one device, like the server's: the device, its VAPIX client and reads shared by every check
/// evaluated with the same context (<see cref="ReadCount"/> counts the reads that really ran).
/// </summary>
public sealed class FakeComplianceContext(IDeviceInfo device, IVapixClient? vapix = null) : IComplianceContext, IComplianceReads
{
    private readonly ConcurrentDictionary<string, Lazy<Task<object?>>> _reads = new(StringComparer.Ordinal);
    private int _readCount;

    /// <summary>How many shared reads ran (one per key).</summary>
    public int ReadCount => _readCount;

    /// <inheritdoc/>
    public IDeviceInfo Device { get; } = device ?? throw new ArgumentNullException(nameof(device));

    /// <inheritdoc/>
    public IVapixClient? Vapix { get; } = vapix;

    /// <inheritdoc/>
    public IComplianceReads Reads => this;

    /// <inheritdoc/>
    public DateTimeOffset Now { get; init; } = DateTimeOffset.UtcNow;

    /// <inheritdoc/>
    public ILogger Logger { get; init; } = NullLogger.Instance;

    /// <inheritdoc/>
    public ICorePlugin? Owner { get; init; }

    /// <inheritdoc/>
    public async Task<T> GetOrAddAsync<T>(string key, Func<CancellationToken, Task<T>> read, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(read);
        var value = _reads.GetOrAdd(key, _ => new Lazy<Task<object?>>(async () =>
        {
            Interlocked.Increment(ref _readCount);
            return await read(ct).ConfigureAwait(false);
        }));
        return (T)(await value.Value.ConfigureAwait(false))!;
    }

    /// <summary>The invocation of a check with these settings (a JSON object, e.g. {"application": "objectanalytics"}).</summary>
    public static ComplianceInvocation Invocation(JsonObject settings, string name = "Check") =>
        new(Guid.NewGuid(), name, JsonSerializer.SerializeToElement(settings));
}

/// <summary>The context of <see cref="IComplianceCheckType.ListChoicesAsync"/> for tests.</summary>
public sealed class FakeComplianceQueryContext(IDeviceInfo? device = null, IVapixClient? vapix = null) : IComplianceQueryContext
{
    /// <inheritdoc/>
    public ILogger Logger { get; init; } = NullLogger.Instance;

    /// <inheritdoc/>
    public ICorePlugin? Owner { get; init; }

    /// <inheritdoc/>
    public IDeviceRepository Devices { get; init; } = new FakeCoreContext();

    /// <inheritdoc/>
    public IDeviceInfo? Device { get; } = device;

    /// <inheritdoc/>
    public IVapixClient? Vapix { get; } = vapix;
}
