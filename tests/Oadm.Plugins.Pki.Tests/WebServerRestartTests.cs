using Oadm.Plugins.Pki.Device;
using Oadm.Plugins.Pki.Policies;
using Oadm.Plugins.Pki.Tasks;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>
/// Verify waits for the web server restart after Enable and Disable HTTPS (AXIS OS 9.80 on hardware: HTTP answers right after
/// the change, then the device refuses connections for a few seconds). The fake camera refuses a number of requests after
/// each web server change; the clock moves only while the task waits (<see cref="TaskHarness.RunWaitingAsync"/>).
/// </summary>
public sealed class WebServerRestartTests
{
    private static readonly TimeSpan Settle = CertificateDeployment.DefaultWebServerSettle;

    [Fact]
    public async Task Disable_waits_until_http_keeps_answering_after_the_restart()
    {
        await using var h = await TaskHarness.StartAsync(webServerSettle: Settle);
        h.Camera.AnswersBeforeRestart = 1; // OADM's first HTTP login works, then the web server restarts
        h.Camera.RestartRefusals = 3;

        Assert.Null(await h.RunWaitingAsync(PkiTaskIds.HttpsDisable));

        Assert.Equal(["Check compatibility: Done", "Read web server settings: Done", "Set HTTP only: Done", "Verify: Done", "Completed: Done"], h.Lines);
        Assert.Equal(3, h.Camera.Refused);
        Assert.Contains("Verify: " + CertificateDeployment.WaitingForRestart, h.WaitDetails);
        Assert.Equal("http", h.Context.Scheme);
        Assert.Equal(ConnectionPolicy.Http, h.Camera.Policy);
    }

    [Fact]
    public async Task Disable_without_a_restart_still_checks_for_the_settle_time()
    {
        await using var h = await TaskHarness.StartAsync(webServerSettle: Settle);

        Assert.Null(await h.RunWaitingAsync(PkiTaskIds.HttpsDisable));

        Assert.Equal("Verify: Done", h.Lines[3]);
        Assert.True(h.WaitDetails.Count * CertificateDeployment.SwitchRetryDelay >= Settle, $"{h.WaitDetails.Count} waits");
        Assert.DoesNotContain("Verify: " + CertificateDeployment.WaitingForRestart, h.WaitDetails);
    }

    [Fact]
    public async Task Disable_fails_with_what_changed_when_the_device_does_not_come_back()
    {
        await using var h = await TaskHarness.StartAsync(webServerSettle: Settle);
        h.Camera.RestartRefusals = 1000;

        var ex = await h.RunWaitingAsync(PkiTaskIds.HttpsDisable);

        Assert.Equal(
            "The device stopped answering when its web server restarted and did not come back within 1 minute. The device was set to HTTP only; its certificates stay. OADM connects over HTTP.",
            ex!.Message);
        Assert.Equal("Verify: Failed", h.Lines[3]);
        Assert.Equal(ConnectionPolicy.Http, h.Camera.Policy);
    }

    [Fact]
    public async Task Disable_fails_when_http_never_answers_and_oadm_keeps_its_connection()
    {
        await using var h = await TaskHarness.StartAsync(webServerSettle: Settle);
        h.Camera.AnswersBeforeRestart = 0;
        h.Camera.RestartRefusals = 1000;

        var ex = await h.RunWaitingAsync(PkiTaskIds.HttpsDisable);

        Assert.Equal("The device did not answer over HTTP within 1 minute. The device was set to HTTP only; its certificates stay. OADM still connects as before.", ex!.Message);
        Assert.Equal("https", h.Context.Scheme);
        Assert.Contains("Verify: " + CertificateDeployment.WaitingForRestart, h.WaitDetails);
    }

    [Fact]
    public async Task Enable_waits_until_the_restarted_web_server_presents_the_new_certificate()
    {
        await using var h = await TaskHarness.StartAsync();
        h.Camera.AnswersBeforeRestart = 0;
        h.Camera.RestartRefusals = 2;

        Assert.Null(await h.RunWaitingAsync(PkiTaskIds.HttpsEnable));

        Assert.Equal(2, h.Camera.Refused);
        Assert.Contains("Verify HTTPS: " + CertificateDeployment.WaitingForRestart, h.WaitDetails);
        Assert.Equal("Verify HTTPS: Done", h.Lines.Single(l => l.StartsWith("Verify HTTPS", StringComparison.Ordinal)));
        Assert.Equal(h.Camera.ServedFingerprint, h.Context.PinnedFingerprint);
    }

    [Fact]
    public async Task Enable_fails_with_what_changed_when_the_web_server_does_not_come_back()
    {
        await using var h = await TaskHarness.StartAsync();
        h.Camera.AnswersBeforeRestart = 0;
        h.Camera.RestartRefusals = 1000;

        var ex = await h.RunWaitingAsync(PkiTaskIds.HttpsEnable);

        Assert.Equal(
            $"The device did not come back with the new certificate within 1 minute. The web server was switched to the certificate \"{h.Camera.WebServerAlias}\". OADM still connects as before.",
            ex!.Message);
        Assert.StartsWith(CertificateDeployment.HttpsAliasPrefix, h.Camera.WebServerAlias, StringComparison.Ordinal);
        Assert.Null(h.Context.PinnedFingerprint);
    }
}
