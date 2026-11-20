using System.Text.Json.Nodes;

using Oadm.Plugins.Provisioning.Model;
using Oadm.Sdk.Certificates;

namespace Oadm.Plugins.Provisioning.Nodes;

/// <summary>
/// Certificate (spec node 9): the HTTPS certificate of the chosen PKI issuing policy with PKI's task "Enable HTTPS"
/// (<c>oadm.pki.https-enable</c>: the camera's clock is checked first and a clock that is behind refuses, never confirmed by
/// the flow), or EST enrollment with the EST server's task "Set up EST" (<c>oadm.est.setup</c>), so the camera enrolls and
/// renews by itself. EST is OADM PRO: without it the task's own text ("OADM PRO is not active. Nothing was changed.").
/// </summary>
public sealed class CertificateNode : TaskNode<CertificateSettings>
{
    public const string HttpsTaskId = "oadm.pki.https-enable";
    public const string EstTaskId = "oadm.est.setup";

    public override NodeKind Kind => NodeKind.Certificate;

    public override string Name => "Certificate";

    public override string StepName => "Install the certificate";

    public override string TaskPluginId => HttpsTaskId;

    public override object DefaultSettings => new CertificateSettings();

    protected override string? CheckSettings(CertificateSettings settings, ProfileCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Mode switch
        {
            CertificateMode.Policy => PolicyChoice.Check(settings.PolicyId, IssuingPolicyUse.Https, context),
            CertificateMode.Est => null, // the PKI's enrollment policies decide
            _ => "Choose a PKI policy or EST enrollment.",
        };
    }

    protected override string TaskFor(CertificateSettings settings) =>
        settings?.Mode == CertificateMode.Est ? EstTaskId : HttpsTaskId;

    protected override string Payload(CertificateSettings settings, NodeRun run)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Mode == CertificateMode.Est)
        {
            return new JsonObject { ["action"] = "Setup", ["clockConfirmed"] = false }.ToJsonString();
        }

        return new JsonObject { ["clockConfirmed"] = false, ["policyId"] = settings.PolicyId?.ToString() }.ToJsonString();
    }

    protected override string Done(CertificateSettings settings, NodeRun run) =>
        settings?.Mode == CertificateMode.Est ? "EST enrollment set up." : "Certificate installed, HTTPS on.";
}

/// <summary>
/// 802.1X (spec node 10): PKI's task "Enable IEEE 802.1X" (<c>oadm.pki.dot1x-enable</c>) with the certificate, identity and
/// CA of the chosen PKI IEEE 802.1X certificate policy (the camera's clock is checked first). The switch is the installer's
/// business: a camera on a port that enforces 802.1X answers only once it authenticates.
/// </summary>
public sealed class Dot1xNode : TaskNode<Dot1xSettings>
{
    public const string Dot1xTaskId = "oadm.pki.dot1x-enable";

    public override NodeKind Kind => NodeKind.Dot1x;

    public override string Name => "802.1X";

    public override string StepName => "Turn on 802.1X";

    public override string TaskPluginId => Dot1xTaskId;

    public override object DefaultSettings => new Dot1xSettings();

    protected override string? CheckSettings(Dot1xSettings settings, ProfileCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return PolicyChoice.Check(settings.PolicyId, IssuingPolicyUse.Dot1x, context);
    }

    protected override string Payload(Dot1xSettings settings, NodeRun run)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new JsonObject { ["clockConfirmed"] = false, ["policyId"] = settings.PolicyId?.ToString() }.ToJsonString();
    }

    protected override string Done(Dot1xSettings settings, NodeRun run) => "802.1X on.";
}

/// <summary>The PKI policy of a Certificate or 802.1X node: chosen, and still in PKI when the check can tell.</summary>
public static class PolicyChoice
{
    public const string Choose = "Choose a PKI policy.";

    public const string Gone = "The PKI policy no longer exists. Choose another one.";

    /// <summary>"Add an HTTPS certificate policy on the PKI page first."</summary>
    public static string NoneOf(IssuingPolicyUse use) =>
        $"Add {(use == IssuingPolicyUse.Dot1x ? "an IEEE 802.1X" : "an HTTPS")} certificate policy on the PKI page first.";

    public static string? Check(Guid? policyId, IssuingPolicyUse use, ProfileCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var known = context.Policies?.Where(p => p.Use == use).ToList();
        if (policyId is not { } id || id == Guid.Empty)
        {
            return known is { Count: 0 } ? NoneOf(use) : Choose;
        }

        return known is null || known.Any(p => p.Id == id) ? null : Gone;
    }
}
