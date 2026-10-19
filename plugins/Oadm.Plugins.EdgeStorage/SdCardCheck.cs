using System.Globalization;
using System.Text.Json;

using Oadm.Plugins.EdgeStorage.Device;
using Oadm.Plugins.EdgeStorage.Disks;
using Oadm.Sdk.Actions;
using Oadm.Sdk.Compliance;

namespace Oadm.Plugins.EdgeStorage;

/// <summary>
/// Compliance check "SD card": the camera has an SD card that works, optionally with enough free space. A camera without a
/// card fails ("No SD card"): the user chose the devices the check is for. Read only: one <c>disks/list.cgi</c> per device and
/// run, shared by every check of this type.
/// </summary>
public sealed class SdCardCheck : IComplianceCheckType
{
    public const string TypeId = "oadm.edge-storage.sd-card";

    /// <summary>The key of the shared disk list of a run.</summary>
    public const string DisksKey = "oadm.edge-storage:disks";

    public string Id => TypeId;

    public string DisplayName => "SD card";

    public string? IconKey => "hardDrive";

    public string Group => "Storage";

    public string? Description => "The device has a working SD card with enough free space.";

    public IReadOnlyList<ActionField> Fields =>
    [
        ActionField.Number("minFreeGb", "At least free (GB)", 1, 100_000, 1) with { Required = false, Default = null, Hint = "Optional" },
        ActionField.Number("minFreePercent", "At least free (%)", 1, 99, 1) with { Required = false, Default = null, Hint = "Optional" },
    ];

    public string Describe(JsonElement settings)
    {
        var invocation = new ComplianceInvocation(Guid.Empty, string.Empty, settings);
        var parts = new List<string> { "SD card works" };
        if (invocation.GetInt("minFreeGb") is { } gb)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"at least {gb} GB free"));
        }

        if (invocation.GetInt("minFreePercent") is { } percent)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"at least {percent} % free"));
        }

        return string.Join(", ", parts);
    }

    public async Task<ComplianceResult> EvaluateAsync(IComplianceContext ctx, ComplianceInvocation invocation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(invocation);
        var disks = await ctx.Reads.GetOrAddAsync(
            DisksKey, token => new StorageClient(ctx.Vapix!, TimeProvider.System).ListDisksAsync(token), ct).ConfigureAwait(false);
        var card = disks.FirstOrDefault(d => d.IsSdCard && d.IsPresent);
        if (card is null)
        {
            return ComplianceResult.Failed(StorageTexts.NoCard);
        }

        if (card.ProblemText is { } problem)
        {
            return ComplianceResult.Failed(problem);
        }

        var totalGb = (card.TotalKb ?? 0) / 1024d / 1024d;
        var freeGb = (card.FreeKb ?? 0) / 1024d / 1024d;
        var freePercent = totalGb > 0 ? freeGb / totalGb * 100 : 0;
        var text = string.Create(CultureInfo.InvariantCulture, $"{freeGb:0.#} GB free of {totalGb:0.#} GB ({freePercent:0} %)");
        if (invocation.GetInt("minFreeGb") is { } minGb && freeGb < minGb)
        {
            return ComplianceResult.Failed(string.Create(CultureInfo.InvariantCulture, $"{text}, needs {minGb} GB"));
        }

        if (invocation.GetInt("minFreePercent") is { } minPercent && freePercent < minPercent)
        {
            return ComplianceResult.Failed(string.Create(CultureInfo.InvariantCulture, $"{text}, needs {minPercent} %"));
        }

        return ComplianceResult.Passed(text);
    }
}
