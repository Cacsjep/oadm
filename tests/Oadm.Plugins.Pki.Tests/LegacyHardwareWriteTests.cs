using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Core.Vapix;
using Oadm.Plugins.Pki.Ca;
using Oadm.Plugins.Pki.Device;
using Oadm.Plugins.Pki.Policies;
using Oadm.Plugins.Pki.Tasks;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;
using Oadm.Tests.Shared;

using Xunit.Abstractions;

using StepRun = Oadm.Sdk.Testing.StepRun;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>
/// Opt-in write tests of the PKI tasks on real cameras with older AXIS OS (docs/spec/pki.md "Older AXIS OS"; CLAUDE.md "device
/// safety" 6: only when the developer asks, <c>OADM_PKI_HARDWARE_WRITE=1</c>, one test per device named by <c>--filter</c>).
/// The devices come from the git-ignored dev-cameras.yaml (<c>OADM_DEV_CAMERAS</c> or the repository root). Each test saves the
/// device's certificates, CA certificates, web server settings, 802.1X settings and 802.1X certificate set first, runs OADM's own
/// task code over a real <see cref="VapixClient"/> (OADM's handler over HTTP: Digest, or Basic where the device's HTTP login is
/// Basic only, as with the setting that allows it) behind <see cref="GuardedVapix"/>, and restores and
/// compares that state at the end (also on failure). Remote loan cameras: the guard refuses turning IEEE 802.1X on (nobody
/// could recover a locked-out camera), any parameter or network change, and deleting anything that was there before.
/// </summary>
[Trait("Category", "HardwareWrite")]
public sealed class LegacyHardwareWriteTests(ITestOutputHelper output)
{
    /// <summary>Where the saved states go (never secrets): <c>OADM_PKI_HARDWARE_STATE_DIR</c>, else the temp folder.</summary>
    private static string StateFolder =>
        Environment.GetEnvironmentVariable("OADM_PKI_HARDWARE_STATE_DIR") is { Length: > 0 } dir ? dir : Path.Combine(Path.GetTempPath(), "oadm-pki-hardware");

    [HardwareWriteFact]
    public Task Loan_M5054_653() => LoanAsync("195.60.68.14:41079", 11079);

    [HardwareWriteFact]
    public Task Loan_M5525_840() => LoanAsync("195.60.68.14:42035", 12035);

    [HardwareWriteFact]
    public Task Loan_Q3617_980() => LoanAsync("195.60.68.14:43010", 13010);

    [HardwareWriteFact]
    public Task Loan_C1410_1111() => LoanAsync("195.60.68.14:41127", 11127);

    [HardwareWriteFact]
    public Task Local_200_1012() => LocalAsync("10.0.0.200");

    [HardwareWriteFact]
    public Task Local_12_119() => LocalAsync("10.0.0.12");

    private void Say(string text) => output.WriteLine(text);

    private Task WaitForDeviceAsync(IVapixClient vapix) => WaitForDeviceAsync(vapix, Say);

    /// <summary>
    /// Waits until HTTP answers three times in a row, 3 s apart (the web server restarts a few seconds after a connection policy change; AXIS
    /// OS 9.80 refuses connections meanwhile), at most 3 minutes.
    /// </summary>
    internal static async Task WaitForDeviceAsync(IVapixClient vapix, Action<string> say)
    {
        var end = DateTime.UtcNow + TimeSpan.FromMinutes(3);
        var good = 0;
        var waited = false;
        while (true)
        {
            try
            {
                _ = await vapix.ListParametersAsync([PkiCompatibility.FirmwareParameter], CancellationToken.None);
                if (++good >= 3)
                {
                    if (waited)
                    {
                        say("  (the device answers again)");
                    }

                    return;
                }
            }
            catch (Exception ex) when (DateTime.UtcNow < end && ex is HttpRequestException or VapixException or TaskCanceledException or IOException)
            {
                good = 0;
                if (!waited)
                {
                    say("  (waiting for the device: " + ex.Message + ")");
                }

                waited = true;
            }

            await Task.Delay(TimeSpan.FromSeconds(3));
        }
    }

    // ---------------------------------------------------------------- loan cameras: no 802.1X on, nothing but certificates and HTTPS

    private async Task LoanAsync(string address, int httpPort)
    {
        var camera = Camera(address);
        var host = address.Split(':')[0];
        var httpsPort = int.Parse(address.Split(':')[1], System.Globalization.CultureInfo.InvariantCulture);
        await RunAsync(camera, host, httpPort, httpsPort, allowDot1xOn: false, async (h, guard) =>
        {
            // ECC is refused before AXIS OS 12 (SOAP: RSA 2048 only; 11.11 REST: its keystore lists RSA only), before any write.
            await ExpectRefusalAsync(h, guard, PkiTaskIds.HttpsEnable, null, "ECC");

            // EAPOL version 3 below AXIS OS 10.1: refused in Check compatibility, before any write.
            await SavePoliciesAsync(h, eapol: 3);
            var support = h.Support!;
            if (support.MaxEapolVersion < 3)
            {
                await ExpectRefusalAsync(h, guard, PkiTaskIds.Dot1xEnable, null, "EAPOL 3");
            }
            else
            {
                PkiCompatibility.RequireEapol(h.Pki.Service.Policies.FirstOf(PolicyKinds.Dot1xCertificate)!, support);
                Say("EAPOL 3: accepted by the compatibility check (validated only, 802.1X stays off).");
            }

            await SavePoliciesAsync(h, eapol: 2);

            // Enable HTTPS with RSA: the real web server must present the new certificate (TLS handshake on the HTTPS port).
            await RunTaskAsync(h, PkiTaskIds.HttpsEnable, CameraClockPayload);
            var web = await WebServerTls.GetAsync(h.Vapix, CancellationToken.None);
            Say($"After Enable HTTPS: {Describe(web)}");
            Assert.StartsWith(CertificateDeployment.HttpsAliasPrefix, web.CertificateAlias, StringComparison.Ordinal);

            // Renew: HTTPS renews (802.1X has no OADM certificate yet and is skipped); the previous one leaves the device.
            await RunTaskAsync(h, PkiTaskIds.Renew, CameraClockPayload);
            var renewed = await WebServerTls.GetAsync(h.Vapix, CancellationToken.None);
            Say($"After Renew: {Describe(renewed)}");
            Assert.NotEqual(web.CertificateAlias, renewed.CertificateAlias);
            Assert.DoesNotContain((await DeviceCertificates.ListAsync(h.Vapix, support.Certificates, CancellationToken.None)).Select(c => c.Alias), a => a == web.CertificateAlias);

            // Disable HTTPS: HTTP only (every role before 7.10), HTTP still answers.
            await RunTaskAsync(h, PkiTaskIds.HttpsDisable, null);
            var off = await WebServerTls.GetAsync(h.Vapix, CancellationToken.None);
            Say($"After Disable HTTPS: {Describe(off)}");
            Assert.True(ConnectionPolicy.IsHttpOnly(off.Policy));
            if (off.HasRoles)
            {
                Assert.True(ConnectionPolicy.IsHttpOnly(off.OperatorPolicy) && ConnectionPolicy.IsHttpOnly(off.ViewerPolicy));
            }

            // Enable IEEE 802.1X up to the switch-on: the certificates are bound to the certificate set (SetCertSet
            // EAPTLS_WIRED) first; the guard refuses turning 802.1X on, so the task stops at "Set 802.1X configuration".
            var before = guard.Writes.Count;
            var exception = await h.RunAsync(PkiTaskIds.Dot1xEnable, CameraClockPayload);
            PrintSteps(h);
            Assert.NotNull(exception);
            Assert.Contains(GuardedVapix.Dot1xOnRefused, exception!.Message, StringComparison.Ordinal);
            Assert.Contains("Set 802.1X configuration: Failed", h.Lines);
            Say("Writes of the 802.1X run: " + string.Join(" | ", guard.Writes.Skip(before)));
            var state = await Dot1xSettings.ReadAsync(h.Vapix, h.Apis, CancellationToken.None);
            Say($"802.1X after the binding: {state.Describe()} · on {state.Dot1x?.Enabled} · client {state.Dot1x?.CertClient ?? "none"} · CAs {string.Join(", ", state.Dot1x?.CertsCa ?? [])}");
            Assert.NotEqual(Dot1xApiKind.NetworkSettings, state.Api);
            Assert.False(state.Dot1x!.Enabled);
            Assert.StartsWith(CertificateDeployment.Dot1xAliasPrefix, state.Dot1x.CertClient, StringComparison.Ordinal);
            var caAlias = CertificateDeployment.CaAlias(CertificateDeployment.CaAliasPrefix, h.Pki.Service.Ca!.Id);
            Assert.Contains(caAlias, state.Dot1x.CertsCa);

            // "Set the RADIUS server CA" (EST, Renew): only the CA certificates of the set change, the client certificate stays.
            await Dot1xSettings.SetCaCertificatesAsync(h.Vapix, state, h.Apis, [caAlias], CancellationToken.None);
            var again = await Dot1xSettings.ReadAsync(h.Vapix, h.Apis, CancellationToken.None);
            Assert.Equal(state.Dot1x.CertClient, again.Dot1x!.CertClient);
            Assert.Equal([caAlias], again.Dot1x.CertsCa);
            Assert.False(again.Dot1x.Enabled);

            // Disable IEEE 802.1X: already off (read only).
            await RunTaskAsync(h, PkiTaskIds.Dot1xDisable, null);
            Assert.Equal("Already off", h.Detail(PkiSteps.SetDot1xOff));
        });
    }

    // ---------------------------------------------------------------- local lab: the full tasks, 802.1X on and off

    private async Task LocalAsync(string address)
    {
        var camera = Camera(address);
        await RunAsync(camera, address, 80, 443, allowDot1xOn: true, async (h, guard) =>
        {
            await ExpectRefusalAsync(h, guard, PkiTaskIds.HttpsEnable, null, "ECC");
            await SavePoliciesAsync(h, eapol: 2);

            await RunTaskAsync(h, PkiTaskIds.HttpsEnable, CameraClockPayload);
            var web = await WebServerTls.GetAsync(h.Vapix, CancellationToken.None);
            Say($"After Enable HTTPS: {Describe(web)}");

            await RunTaskAsync(h, PkiTaskIds.Dot1xEnable, CameraClockPayload);
            Assert.Equal("Verify 802.1X settings: Done", h.Lines.Single(l => l.StartsWith("Verify 802.1X settings", StringComparison.Ordinal)));
            var state = await Dot1xSettings.ReadAsync(h.Vapix, h.Apis, CancellationToken.None);
            var set = await CertSoapApi.GetCertSetAsync(h.Vapix, CertSoapApi.WiredDot1xSet, CancellationToken.None);
            Say($"802.1X after Enable: {state.Describe()} · on {state.Dot1x?.Enabled} · {state.Dot1x?.EapolVersion} · GetCertSet client {string.Join(", ", set.Certificates)} · CAs {string.Join(", ", set.CaCertificates)}");
            Assert.True(state.Dot1x!.Enabled);
            Assert.Equal("EAPoLv2", state.Dot1x.EapolVersion);
            Assert.StartsWith(CertificateDeployment.Dot1xAliasPrefix, Assert.Single(set.Certificates), StringComparison.Ordinal);

            await RunTaskAsync(h, PkiTaskIds.Renew, CameraClockPayload);
            var renewed = await CertSoapApi.GetCertSetAsync(h.Vapix, CertSoapApi.WiredDot1xSet, CancellationToken.None);
            Say($"After Renew: web {Describe(await WebServerTls.GetAsync(h.Vapix, CancellationToken.None))} · 802.1X client {string.Join(", ", renewed.Certificates)}");
            Assert.NotEqual(set.Certificates, renewed.Certificates);

            await RunTaskAsync(h, PkiTaskIds.Dot1xDisable, null);
            Assert.False((await Dot1xSettings.ReadAsync(h.Vapix, h.Apis, CancellationToken.None)).Dot1x!.Enabled);

            // EAPOL version 3 from AXIS OS 10.1: the device takes it and reports it.
            await SavePoliciesAsync(h, eapol: 3);
            await RunTaskAsync(h, PkiTaskIds.Dot1xEnable, CameraClockPayload);
            var three = await Dot1xSettings.ReadAsync(h.Vapix, h.Apis, CancellationToken.None);
            Say($"802.1X with EAPOL 3: on {three.Dot1x?.Enabled} · {three.Dot1x?.EapolVersion} · status {three.Dot1x?.Status}");
            Assert.Equal("EAPoLv3", three.Dot1x!.EapolVersion);
            await RunTaskAsync(h, PkiTaskIds.Dot1xDisable, null);

            if (h.BasicOnlyOverHttp)
            {
                // OADM never sends Basic unencrypted: Disable HTTPS refuses before any write.
                await ExpectRefusalAsync(h, guard, PkiTaskIds.HttpsDisable, "{}", "Disable HTTPS with a Basic-only HTTP login");
            }
            else
            {
                await RunTaskAsync(h, PkiTaskIds.HttpsDisable, null);
                Assert.True(ConnectionPolicy.IsHttpOnly((await WebServerTls.GetAsync(h.Vapix, CancellationToken.None)).Policy));
            }
        });
    }

    // ---------------------------------------------------------------- the run, the saved state and the restore

    private const string CameraClockPayload = "{\"clockConfirmed\":true}";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private async Task RunAsync(CameraEntry camera, string host, int httpPort, int httpsPort, bool allowDot1xOn, Func<HardwareHarness, GuardedVapix, Task> body)
    {
        // OADM's own handler: Digest only over HTTP (VapixClient.CreateHandler); a device whose HTTP login is Basic only with
        // the setting that allows Basic over HTTP (BasicOverHttpSetting), as OADM reaches it.
        var basicOnly = await BasicOnlyOverHttpAsync(host, httpPort);
        using var real = HttpClient(host, httpPort, new NetworkCredential(camera.User, camera.Password), basicOnly);
        Say($"Connection: {real.BaseAddress}{(basicOnly ? " (HTTP login is Basic only)" : string.Empty)}");
        var guard = new GuardedVapix(real, allowDot1xOn, Say);
        try
        {
            Say("basicdeviceinfo: AXIS OS " + (await guard.GetBasicDeviceInfoAsync(CancellationToken.None)).Version);
        }
        catch (Exception ex)
        {
            Say($"basicdeviceinfo: {ex.GetType().Name}: {ex.Message}");
        }

        var firmware = await PkiCompatibility.ReadFirmwareAsync(guard, CancellationToken.None);
        Say($"{camera.Note} · AXIS OS {firmware ?? "unknown"} · HTTP {httpPort} · HTTPS {httpsPort}");
        Assert.NotNull(firmware);

        var support = await PkiCompatibility.RequireAsync(guard, Guid.NewGuid(), TimeProvider.System, CancellationToken.None);
        var apis = await guard.GetApiListAsync(CancellationToken.None);
        var dot1x = await Dot1xSettings.ReadAsync(guard, apis, CancellationToken.None);
        Say($"Decision: {support.Describe()} · certificates {support.Certificates} · web server all roles {support.WebServerAllRoles} · max EAPOL {support.MaxEapolVersion} · {dot1x.Describe()} · network-settings {apis.FindApi("network-settings", 1)?.Version ?? "not listed"} · {apis.Count} APIs");

        var current = await StateAsync(guard, support, apis);
        Directory.CreateDirectory(StateFolder);
        var name = Regex.Replace(camera.Address, "[^0-9A-Za-z]", "_");
        var originalPath = Path.Combine(StateFolder, name + "-original.json");
        string original;
        if (File.Exists(originalPath))
        {
            // The state before the first run stays the original: a run that could not restore is repaired first.
            original = await File.ReadAllTextAsync(originalPath);
            Say("Using the original state saved by the first run: " + originalPath);
            guard.Protect(OriginalIds(original));
            if (current != original)
            {
                Say("The device differs from it (an earlier run did not finish): restoring first.");
                await RestoreAsync(guard, support, apis, original);
                await WaitForDeviceAsync(guard);
                Assert.Equal(original, await StateAsync(guard, support, apis));
                guard.Writes.Clear();
            }
        }
        else
        {
            original = current;
            await File.WriteAllTextAsync(originalPath, original);
            Say("Saved the original state to " + originalPath);
            guard.Protect(OriginalIds(original));
        }

        Assert.Empty(guard.Writes);

        await using var h = await HardwareHarness.StartAsync(guard, host, httpsPort, firmware!, apis, Say);
        h.BasicOnlyOverHttp = basicOnly;
        h.Support = support;
        h.Apis = apis;
        Exception? failure = null;
        try
        {
            await body(h, guard);
        }
        catch (Exception ex)
        {
            failure = ex;
            Say("FAILED: " + ex.Message);
        }
        finally
        {
            Say("Restoring ...");
            try
            {
                await WaitForDeviceAsync(guard);
                await RestoreAsync(guard, support, apis, original);
                await WaitForDeviceAsync(guard);
            }
            catch (Exception ex)
            {
                Say("RESTORE FAILED: " + ex.Message);
                failure ??= ex;
            }

            var after = await StateAsync(guard, support, apis);
            await File.WriteAllTextAsync(Path.Combine(StateFolder, name + "-restored.json"), after);
            Say(after == original ? "Restore: the state matches the original." : "Restore: DIFFERENT from the original, see " + Path.Combine(StateFolder, name + "-restored.json"));
            Say("All writes: " + string.Join(" | ", guard.Writes));
            if (after != original)
            {
                failure ??= new InvalidOperationException("The restored state differs from the original.");
            }
        }

        if (failure is not null)
        {
            throw new InvalidOperationException(failure.Message, failure);
        }
    }

    private static async Task<bool> BasicOnlyOverHttpAsync(string host, int port)
    {
        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        using var answer = await probe.GetAsync(new Uri($"http://{host}:{port}/axis-cgi/param.cgi?action=list&group=Properties.Firmware.Version"));
        var schemes = answer.Headers.WwwAuthenticate.Select(h => h.Scheme).ToList();
        return answer.StatusCode == HttpStatusCode.Unauthorized && !schemes.Contains("Digest", StringComparer.OrdinalIgnoreCase);
    }

    private static VapixClient HttpClient(string host, int port, NetworkCredential credentials, bool allowBasic)
    {
        var baseAddress = new Uri($"http://{host}:{port}/");
        var pinning = new CertificatePinning(null);
        return new VapixClient(baseAddress, VapixClient.CreateHandler(baseAddress, credentials, pinning, allowBasic), pinning, TimeSpan.FromSeconds(90)) { AllowsBasicOverHttp = allowBasic };
    }

    private static IReadOnlyList<string> OriginalIds(string json)
    {
        var state = JsonSerializer.Deserialize<DeviceState>(json)!;
        return [.. state.Certificates.Select(c => c.Id), .. state.CaCertificates.Select(c => c.Id)];
    }

    private static async Task<string> StateAsync(IVapixClient vapix, PkiDeviceSupport support, IReadOnlyList<DeviceApi> apis)
    {
        var ct = CancellationToken.None;
        var certificates = await DeviceCertificates.ListAsync(vapix, support.Certificates, ct);
        var cas = await DeviceCertificates.ListCaAsync(vapix, support.Certificates, ct);
        var web = await WebServerTls.GetAsync(vapix, ct);
        var set = await CertSoapApi.GetCertSetAsync(vapix, CertSoapApi.WiredDot1xSet, ct);
        var dot1x = (await Dot1xSettings.ReadAsync(vapix, apis, ct)).Dot1x;
        var state = new DeviceState(
            [.. certificates.Select(c => new IdAndPrint(c.Alias, c.Pem.Length == 0 ? "" : CertificateDeployment.Fingerprint(c.Pem))).OrderBy(c => c.Id, StringComparer.Ordinal)],
            [.. cas.Select(c => new IdAndPrint(c.Alias, c.Pem.Length == 0 ? "" : CertificateDeployment.Fingerprint(c.Pem))).OrderBy(c => c.Id, StringComparer.Ordinal)],
            web.Tls, web.Policy, web.OperatorPolicy, web.ViewerPolicy, [.. web.Ciphers], [.. web.Certificates], [.. web.CaCertificates], [.. web.TrustedCertificates],
            [.. set.Certificates], [.. set.CaCertificates], [.. set.TrustedCertificates],
            dot1x?.Enabled, dot1x?.Identity, dot1x?.EapolVersion, dot1x?.Mode);
        return JsonSerializer.Serialize(state, Indented);
    }

    private async Task RestoreAsync(GuardedVapix vapix, PkiDeviceSupport support, IReadOnlyList<DeviceApi> apis, string originalJson)
    {
        var ct = CancellationToken.None;
        var original = JsonSerializer.Deserialize<DeviceState>(originalJson)!;

        // 802.1X off first (local lab only; on loan cameras it was never turned on).
        var dot1x = await Dot1xSettings.ReadAsync(vapix, apis, ct);
        if (dot1x.Dot1x is { } now && (now.Enabled != original.Dot1xEnabled || now.Identity != original.Dot1xIdentity || now.EapolVersion != original.Dot1xEapol))
        {
            await vapix.RestoreDot1xAsync(dot1x, apis, original, ct);
            Say("Restored the 802.1X settings.");
        }

        // Web server: the original settings exactly (policies, TLS flag, certificate set, ciphers as read then).
        var web = await WebServerTls.GetAsync(vapix, ct);
        var wanted = new WebServerTlsConfiguration(original.WebTls, original.WebPolicy, original.WebCiphers, original.WebCertificates, original.WebCaCertificates, original.WebTrusted, original.WebOperator, original.WebViewer);
        if (WebServerTls.BuildSet(web) != WebServerTls.BuildSet(wanted))
        {
            await WebServerTls.SetAsync(vapix, wanted, ct);
            await WaitForDeviceAsync(vapix);
            Say("Restored the web server settings.");
        }

        // The 802.1X certificate set as it was.
        var set = await CertSoapApi.GetCertSetAsync(vapix, CertSoapApi.WiredDot1xSet, ct);
        var wantedSet = new CertSet(original.SetCertificates, original.SetCaCertificates, original.SetTrusted);
        if (CertSoapApi.BuildSetCertSet(CertSoapApi.WiredDot1xSet, set) != CertSoapApi.BuildSetCertSet(CertSoapApi.WiredDot1xSet, wantedSet))
        {
            await CertSoapApi.SetCertSetAsync(vapix, CertSoapApi.WiredDot1xSet, wantedSet, "Restore 802.1X certificate set", ct);
            Say("Restored the 802.1X certificate set.");
        }

        // Everything the run added (the guard never deletes what was there before).
        foreach (var certificate in await DeviceCertificates.ListAsync(vapix, support.Certificates, ct))
        {
            if (!original.Certificates.Any(c => c.Id == certificate.Alias))
            {
                await DeviceCertificates.DeleteAsync(vapix, support.Certificates, certificate.Alias, ct);
                Say("Deleted certificate " + certificate.Alias);
            }
        }

        foreach (var ca in await DeviceCertificates.ListCaAsync(vapix, support.Certificates, ct))
        {
            if (!original.CaCertificates.Any(c => c.Id == ca.Alias))
            {
                await DeviceCertificates.DeleteCaAsync(vapix, support.Certificates, ca.Alias, ct);
                Say("Deleted CA certificate " + ca.Alias);
            }
        }
    }

    private static async Task SavePoliciesAsync(HardwareHarness h, int eapol)
    {
        foreach (var (kind, change) in new (string, Func<PkiPolicy, PkiPolicy>)[]
        {
            (PolicyKinds.HttpsCertificate, p => p with { Key = PolicyKeys.Rsa }),
            (PolicyKinds.Dot1xCertificate, p => p with { Key = PolicyKeys.Rsa, EapolVersion = eapol }),
        })
        {
            var reply = await h.Pki.InvokeAsync<PolicySaveReply>(PolicyMethods.SavePolicy, change(h.Pki.Service.Policies.FirstOf(kind)!));
            Assert.Null(reply.Error);
        }
    }

    private async Task ExpectRefusalAsync(HardwareHarness h, GuardedVapix guard, string task, string? payload, string what)
    {
        var writes = guard.Writes.Count;
        var ex = await h.RunAsync(task, payload ?? CameraClockPayload);
        PrintSteps(h);
        Assert.NotNull(ex);
        Say($"{what}: refused: {ex!.Message}");
        Assert.IsType<DeviceNotCompatibleException>(ex);
        Assert.Equal(writes, guard.Writes.Count);
    }

    private async Task RunTaskAsync(HardwareHarness h, string task, string? payload)
    {
        var ex = await h.RunAsync(task, payload);
        PrintSteps(h);
        if (ex is not null)
        {
            throw new InvalidOperationException($"{task} failed: {ex.Message}", ex);
        }
    }

    private void PrintSteps(HardwareHarness h)
    {
        Say($"--- {h.LastTask}");
        foreach (var step in h.Context.Steps.Snapshot())
        {
            Say($"  {step.Name}: {step.State}{(string.IsNullOrEmpty(step.Detail) ? string.Empty : " · " + step.Detail)}");
        }

        foreach (var (level, message) in h.Context.LogEntries)
        {
            Say($"  log {level}: {message}");
        }
    }

    private static string Describe(WebServerTlsConfiguration web) =>
        $"TLS {web.Tls} · {web.Policy}{(web.HasRoles ? $"/{web.OperatorPolicy}/{web.ViewerPolicy}" : string.Empty)} · certificate {web.CertificateAlias ?? "none"}";

    // ---------------------------------------------------------------- dev-cameras.yaml (only what these tests need)

    private sealed record CameraEntry(string Address, string User, string Password, string Note);

    private static CameraEntry Camera(string address)
    {
        var path = Environment.GetEnvironmentVariable("OADM_DEV_CAMERAS") is { Length: > 0 } explicitPath ? explicitPath : Path.Combine(TestRepository.Root, "dev-cameras.yaml");
        var text = File.ReadAllText(path);
        foreach (var block in text.Split("- address:").Skip(1))
        {
            string Field(string name, string first)
            {
                var value = name.Length == 0 ? first : Regex.Match(block, $@"^\s*{name}:\s*(.+)$", RegexOptions.Multiline).Groups[1].Value;
                value = value.Trim();
                return value.Length > 1 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0] ? value[1..^1] : value;
            }

            var lines = block.Split('\n');
            if (Field(string.Empty, lines[0]) == address)
            {
                return new CameraEntry(address, Field("user", ""), Field("password", ""), Field("note", ""));
            }
        }

        throw new InvalidOperationException($"{address} is not in dev-cameras.yaml.");
    }
}

/// <summary>What the tests save and compare (ids, fingerprints and settings; no secrets).</summary>
internal sealed record DeviceState(
    IReadOnlyList<IdAndPrint> Certificates,
    IReadOnlyList<IdAndPrint> CaCertificates,
    bool WebTls,
    string? WebPolicy,
    string? WebOperator,
    string? WebViewer,
    IReadOnlyList<string> WebCiphers,
    IReadOnlyList<string> WebCertificates,
    IReadOnlyList<string> WebCaCertificates,
    IReadOnlyList<string> WebTrusted,
    IReadOnlyList<string> SetCertificates,
    IReadOnlyList<string> SetCaCertificates,
    IReadOnlyList<string> SetTrusted,
    bool? Dot1xEnabled,
    string? Dot1xIdentity,
    string? Dot1xEapol,
    string? Dot1xMode);

internal sealed record IdAndPrint(string Id, string Fingerprint);

/// <summary>
/// The real client behind a guard: reads pass; only the certificate, web server and certificate set writes OADM's PKI tasks
/// make pass. Refused (never sent): turning IEEE 802.1X on unless allowed, any other parameter or network change, a restart,
/// and deleting a certificate that was on the device before the test. Every write is listed (operation names only).
/// </summary>
internal sealed partial class GuardedVapix(IVapixClient inner, bool allowDot1xOn, Action<string> say) : IVapixClient
{
    public const string Dot1xOnRefused = "Refused by the test guard: turning IEEE 802.1X on";

    private static readonly string[] SoapWrites =
        ["CreateCertificate2", "GetPkcs10Request2", "LoadCertificates", "LoadCACertificates", "DeleteCertificates", "SetCertSet", "SetWebServerTlsConfiguration"];

    private readonly HashSet<string> _protected = new(StringComparer.Ordinal);

    public List<string> Writes { get; } = [];

    public Uri BaseAddress => inner.BaseAddress;

    public void Protect(IEnumerable<string> ids) => _protected.UnionWith(ids);

    public Task<BasicDeviceInfo> GetBasicDeviceInfoAsync(CancellationToken ct) => inner.GetBasicDeviceInfoAsync(ct);

    public Task<IReadOnlyDictionary<string, string>> ListParametersAsync(IEnumerable<string> groups, CancellationToken ct) => inner.ListParametersAsync(groups, ct);

    public Task RestartAsync(CancellationToken ct) => throw new InvalidOperationException("Refused by the test guard: restart");

    public Task<IReadOnlyList<DeviceApi>> GetApiListAsync(CancellationToken ct) => inner.GetApiListAsync(ct);

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var path = request.RequestUri!.OriginalString.TrimStart('/');
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
        var write = Check(request.Method, path, body);
        var response = await inner.SendAsync(request, ct);
        if (write is not null)
        {
            Writes.Add(write);
            if (!response.IsSuccessStatusCode || body.Length == 0)
            {
                say($"  device answer to {write}: HTTP {(int)response.StatusCode}");
            }
        }

        return response;
    }

    /// <summary>For the restore of the local lab: 802.1X back to the saved on/off, identity and EAPOL version.</summary>
    public async Task RestoreDot1xAsync(Dot1xState state, IReadOnlyList<DeviceApi> apis, DeviceState original, CancellationToken ct)
    {
        if (!allowDot1xOn)
        {
            throw new InvalidOperationException("Refused by the test guard: 802.1X changes on this device");
        }

        if (state.Api == Dot1xApiKind.ParametersAndCertSet)
        {
            var values = new List<(string, string)> { (Dot1xSettings.EnabledParameter, original.Dot1xEnabled == true ? "yes" : "no") };
            if (original.Dot1xIdentity is { } identity)
            {
                values.Add((Dot1xSettings.IdentityParameter, identity));
            }

            if (original.Dot1xEapol is { } eapol)
            {
                values.Add((Dot1xSettings.EapolParameter, eapol.Replace("EAPoLv", string.Empty, StringComparison.Ordinal)));
            }

            using var form = new HttpRequestMessage(HttpMethod.Post, new Uri(Dot1xSettings.ParamPath, UriKind.Relative))
            {
                Content = new StringContent(Dot1xSettings.UpdateBody(values), System.Text.Encoding.UTF8, "application/x-www-form-urlencoded"),
            };
            using var answer = await SendAsync(form, ct);
            return;
        }

        var parameters = new System.Text.Json.Nodes.JsonObject
        {
            ["deviceName"] = state.Dot1x!.DeviceName,
            ["enabled"] = original.Dot1xEnabled == true,
            ["mode"] = original.Dot1xMode ?? NetworkInfoApi.EapTlsMode,
        };
        if (original.Dot1xIdentity is { } id)
        {
            parameters["identity"] = id;
        }

        if (original.Dot1xEapol is { } version)
        {
            parameters["eapolVersion"] = version;
        }

        var request = new System.Text.Json.Nodes.JsonObject
        {
            ["apiVersion"] = apis.FindApi(NetworkInfoApi.ApiId, 1)?.Version ?? "1.0",
            ["context"] = "oadm",
            ["method"] = "setWired8021XConfiguration",
            ["params"] = parameters,
        };
        var reply = await DeviceHttp.SendAsync(this, HttpMethod.Post, NetworkInfoApi.Path, DeviceHttp.JsonType, request.ToJsonString(), ct);
        say("  802.1X restore answer: " + DeviceHttp.FirstLine(reply.Body));
    }

    /// <summary>Null for a read; the write's name when it may pass; throws when the guard refuses it.</summary>
    private string? Check(HttpMethod method, string path, string body)
    {
        var bare = path.Split('?')[0];
        if (method == HttpMethod.Get)
        {
            return path.Contains("action=update", StringComparison.OrdinalIgnoreCase) ? throw Refuse("parameter change " + path) : null;
        }

        if (bare.StartsWith("config/rest/cert/v1", StringComparison.Ordinal))
        {
            var last = Uri.UnescapeDataString(bare.Split('/')[^1]);
            if (method == HttpMethod.Delete && _protected.Contains(last))
            {
                throw Refuse("deleting " + last + ", which was there before the test");
            }

            return $"{method} {bare}";
        }

        if (bare == DeviceSoap.ServicesPath)
        {
            var operation = SoapOperation().Match(body).Groups[1].Value;
            if (operation.StartsWith("Get", StringComparison.Ordinal) && operation != "GetPkcs10Request2")
            {
                return null;
            }

            if (!SoapWrites.Contains(operation))
            {
                throw Refuse("SOAP " + operation);
            }

            if (operation == "DeleteCertificates")
            {
                foreach (Match id in CertificateId().Matches(body))
                {
                    var value = WebUtility.HtmlDecode(id.Groups[1].Value);
                    if (_protected.Contains(value))
                    {
                        throw Refuse("deleting " + value + ", which was there before the test");
                    }
                }
            }

            if (operation == "SetCertSet" && !body.Contains("<acertificates:CertSetName>" + CertSoapApi.WiredDot1xSet + "</acertificates:CertSetName>", StringComparison.Ordinal))
            {
                throw Refuse("SetCertSet of another set");
            }

            return operation;
        }

        if (bare == NetworkInfoApi.Path)
        {
            var name = JsonMethod().Match(body).Groups[1].Value;
            if (name.StartsWith("get", StringComparison.Ordinal))
            {
                return null;
            }

            if (name == "setWired8021XConfiguration" && allowDot1xOn)
            {
                return name;
            }

            throw name == "setWired8021XConfiguration" && body.Contains("\"enabled\":true", StringComparison.Ordinal)
                ? new InvalidOperationException(Dot1xOnRefused)
                : Refuse("network settings " + name);
        }

        if (bare == Dot1xSettings.ParamPath)
        {
            if (!body.Contains("action=update", StringComparison.Ordinal))
            {
                return null;
            }

            var names = body.Split('&').Skip(1).Select(p => Uri.UnescapeDataString(p.Split('=')[0])).ToList();
            if (allowDot1xOn && names.All(n => n.StartsWith(Dot1xSettings.ParameterGroup + ".", StringComparison.Ordinal)))
            {
                return "param.cgi " + string.Join(",", names);
            }

            throw names.Contains(Dot1xSettings.EnabledParameter) && body.Contains("Enabled=yes", StringComparison.Ordinal)
                ? new InvalidOperationException(Dot1xOnRefused)
                : Refuse("parameter change " + string.Join(",", names));
        }

        // JSON APIs: reads only (time-service getDateTimeInfo, basicdeviceinfo, apidiscovery).
        var json = JsonMethod().Match(body).Groups[1].Value;
        return json.StartsWith("get", StringComparison.Ordinal) ? null : throw Refuse($"{method} {bare} {json}");
    }

    private static InvalidOperationException Refuse(string what) => new("Refused by the test guard: " + what);

    [GeneratedRegex(@"<SOAP-ENV:Body>\s*<[a-z]+:([A-Za-z0-9]+)")]
    private static partial Regex SoapOperation();

    [GeneratedRegex(@"<(?:tds:)?CertificateID>([^<]*)</")]
    private static partial Regex CertificateId();

    [GeneratedRegex("\"method\"\\s*:\\s*\"([^\"]+)\"")]
    private static partial Regex JsonMethod();
}

/// <summary>A started PKI plugin and a real device: the task context follows the web server with a TLS handshake.</summary>
internal sealed class HardwareHarness : IAsyncDisposable
{
    private readonly IVapixClient _vapix;
    private readonly string _host;
    private readonly int _httpsPort;
    private readonly Action<string> _say;

    private HardwareHarness(PkiHarness pki, IVapixClient vapix, string host, int httpsPort, PkiFakeDevice device, Action<string> say)
    {
        Pki = pki;
        _vapix = vapix;
        _host = host;
        _httpsPort = httpsPort;
        Device = device;
        _say = say;
    }

    public PkiHarness Pki { get; }

    public PkiFakeDevice Device { get; }

    public IVapixClient Vapix => _vapix;

    public PkiDeviceSupport? Support { get; set; }

    /// <summary>The device's HTTP login is Basic only (OADM connects over HTTPS).</summary>
    public bool BasicOnlyOverHttp { get; set; }

    public IReadOnlyList<DeviceApi> Apis { get; set; } = [];

    public HardwareTaskContext Context { get; private set; } = null!;

    public string LastTask { get; private set; } = string.Empty;

    public string[] Lines => StepRun.Lines(Context.Steps);

    public string? Detail(string step) => StepRun.Detail(Context.Steps, step);

    public static async Task<HardwareHarness> StartAsync(IVapixClient vapix, string host, int httpsPort, string firmware, IReadOnlyList<DeviceApi> apis, Action<string> say)
    {
        var pki = await PkiHarness.StartAsync();
        PkiCompatibility.ClearCache();
        return new HardwareHarness(pki, vapix, host, httpsPort, new PkiFakeDevice(Guid.NewGuid(), host, "ACCC8E000000", firmware) { Apis = apis }, say);
    }

    public async Task<Exception?> RunAsync(string id, string? payload)
    {
        await Task.Delay(TimeSpan.FromSeconds(1.1)); // a new alias second per run
        await LegacyHardwareWriteTests.WaitForDeviceAsync(_vapix, _say); // the web server restarts after a policy change
        LastTask = id;
        Context = new HardwareTaskContext(_vapix, _host, _httpsPort, _say);
        var plugin = Pki.Plugin.TaskPlugins.Single(t => t.Id == id);
        try
        {
            await StepRun.RunAsync(Context.Steps, () => plugin.ExecuteAsync(Context, Device, payload, CancellationToken.None));
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    public ValueTask DisposeAsync() => Pki.DisposeAsync();
}

/// <summary>The server's task context against a real device: "https" means a TLS handshake that must present the fingerprint.</summary>
internal sealed class HardwareTaskContext(IVapixClient vapix, string host, int httpsPort, Action<string> say) : ITaskExecutionContext
{
    public TaskStepList Steps { get; } = new(onWarning: _ => { });

    public Guid TaskId { get; } = Guid.NewGuid();

    public IVapixClient Vapix => vapix;

    public ICorePlugin? Owner => null;

    public ILogger Logger { get; } = NullLogger.Instance;

    public IUploadedFiles Files { get; } = new MemoryFiles();

    public List<(TaskLogLevel Level, string Message)> LogEntries { get; } = [];

    public void PlanSteps(params string[] names) => Steps.Plan(names);

    public ITaskStep BeginStep(string name) => Steps.Begin(name);

    public void ReportProgress(int percent, string? message = null)
    {
    }

    public void ReportWarning(string message) => LogEntries.Add((TaskLogLevel.Warning, message));

    public void Log(TaskLogLevel level, string message) => LogEntries.Add((level, message));

    public async Task UpdateDeviceTlsAsync(string scheme, string? expectedFingerprintSha256, CancellationToken ct)
    {
        if (scheme != "https")
        {
            _ = await vapix.ListParametersAsync([PkiCompatibility.FirmwareParameter], ct); // HTTP answers
            return;
        }

        string served;
        try
        {
            served = await HandshakeAsync(ct);
        }
        catch (Exception ex) when (ex is IOException or SocketException or System.Security.Authentication.AuthenticationException or TimeoutException)
        {
            throw new DeviceIdentityException("The device does not answer over HTTPS: " + ex.Message);
        }

        say($"  TLS handshake on {host}:{httpsPort}: {served[..16]}... expected {expectedFingerprintSha256?[..16] ?? "any"}...");
        if (expectedFingerprintSha256 is not null && !string.Equals(served, expectedFingerprintSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new DeviceIdentityException("The device does not present the expected certificate.");
        }
    }

    private async Task<string> HandshakeAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host, httpsPort, timeout.Token);
        X509Certificate2? seen = null;
#pragma warning disable CA5359 // The served certificate is compared with the expected fingerprint below (OADM's pinning).
        await using var ssl = new SslStream(tcp.GetStream(), false, (_, certificate, _, _) =>
        {
            seen = certificate is null ? null : new X509Certificate2(certificate);
            return true;
        });
#pragma warning restore CA5359
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, timeout.Token);
        using var certificate = seen ?? throw new IOException("No certificate");
        return CaCertificates.Fingerprint(certificate);
    }
}
