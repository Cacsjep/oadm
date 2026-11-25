using System.Globalization;
using System.Text.Json;

using Oadm.Plugins.Provisioning.Model;
using Oadm.Plugins.Shared;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Provisioning.Nodes;

/// <summary>
/// Autofocus (spec node 11): every lens of the camera through the Optics Control API that the live view's focus uses
/// (<see cref="LensFocus"/>; a multisensor camera has one lens per sensor): checked in a fresh API list and the optics'
/// capabilities, then <c>performAutofocus</c> lens by lens, each answering when its search ended; all together at most
/// <see cref="ProvisioningOptions.AutofocusTimeout"/>. A lens that finds no focus does not stop the others. A camera without
/// remote focus: Not needed. Nothing else is written.
/// </summary>
public sealed class AutofocusNode : DeviceNode<AutofocusSettings>
{
    public const string OpticsApi = LensFocus.OpticsApi;

    public override NodeKind Kind => NodeKind.Autofocus;

    public override string Name => "Autofocus";

    public override string StepName => "Run the autofocus";

    public override object DefaultSettings => new AutofocusSettings();

    protected override string? CheckSettings(AutofocusSettings settings, ProfileCheckContext context) => null;

    protected override async Task<NodeResult> RunOnAsync(AutofocusSettings settings, NodeRun run, Guid deviceId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        var timeout = run.Host.Options.AutofocusTimeout;
        using var limit = new CancellationTokenSource(timeout, run.Host.Time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, limit.Token);
        try
        {
            var vapix = await run.Host.Vapix.CreateAsync(deviceId, linked.Token).ConfigureAwait(false);
            var apis = await vapix.GetApiListAsync(linked.Token).ConfigureAwait(false);
            if (!apis.Supports(OpticsApi, LensFocus.MinVersion)) // a different major version is a different API
            {
                return NodeResult.NotNeeded("no remote focus.");
            }

            var lenses = await LensFocus.LensesAsync(vapix, linked.Token).ConfigureAwait(false);
            if (lenses.Count == 0)
            {
                return NodeResult.NotNeeded("no remote focus.");
            }

            var errors = new List<string>();
            foreach (var lens in lenses)
            {
                if (await LensFocus.AutofocusAsync(vapix, lens, linked.Token).ConfigureAwait(false) is { } error)
                {
                    errors.Add(lenses.Count == 1 ? error : LensFocus.LensName(lens) + ": " + error);
                }
            }

            return errors.Count > 0 ? NodeResult.Error(string.Join(" ", errors))
                : lenses.Count == 1 ? NodeResult.Success("Focus found.")
                : NodeResult.Success(string.Create(CultureInfo.InvariantCulture, $"Focus found on {lenses.Count} lenses."));
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return NodeResult.Error($"The autofocus did not end within {timeout.TotalMinutes:0} minutes.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or InvalidOperationException)
        {
            return NodeResult.Error("The camera did not answer the autofocus: " + ex.Message.TrimEnd('.') + ".");
        }
    }
}
