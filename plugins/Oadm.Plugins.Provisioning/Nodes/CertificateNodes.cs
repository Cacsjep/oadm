using System.Text.Json.Nodes;

using Oadm.Plugins.Provisioning.Model;

namespace Oadm.Plugins.Provisioning.Nodes;

/// <summary>
/// Certificate (spec node 9): the HTTPS certificate of a PKI issuing policy with PKI's task "Enable HTTPS"
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

    public override string TaskPluginId => HttpsTaskId;

    public override object DefaultSettings => new CertificateSettings();

    protected override string? CheckSettings(CertificateSettings settings, ProfileCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Mode switch
        {
            CertificateMode.Policy => settings.PolicyId == Guid.Empty ? "Choose a PKI policy." : null,
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

        var payload = new JsonObject { ["clockConfirmed"] = false };
        if (settings.PolicyId is { } policy)
        {
            payload["policyId"] = policy.ToString();
        }

        return payload.ToJsonString();
    }

    protected override string Done(CertificateSettings settings, NodeRun run) =>
        settings?.Mode == CertificateMode.Est ? "EST enrollment set up." : "Certificate installed, HTTPS on.";
}

/// <summary>
/// 802.1X (spec node 10): PKI's task "Enable IEEE 802.1X" (<c>oadm.pki.dot1x-enable</c>) with the certificate, identity and
/// CA of PKI's 802.1X policy (no settings of its own; the camera's clock is checked first). The switch is the installer's
/// business: a camera on a port that enforces 802.1X answers only once it authenticates.
/// </summary>
public sealed class Dot1xNode : TaskNode<Dot1xSettings>
{
    public const string Dot1xTaskId = "oadm.pki.dot1x-enable";

    public override NodeKind Kind => NodeKind.Dot1x;

    public override string Name => "802.1X";

    public override string TaskPluginId => Dot1xTaskId;

    public override object DefaultSettings => new Dot1xSettings();

    protected override string? CheckSettings(Dot1xSettings settings, ProfileCheckContext context) => null;

    protected override string Payload(Dot1xSettings settings, NodeRun run) => new JsonObject { ["clockConfirmed"] = false }.ToJsonString();

    protected override string Done(Dot1xSettings settings, NodeRun run) => "802.1X on.";
}
