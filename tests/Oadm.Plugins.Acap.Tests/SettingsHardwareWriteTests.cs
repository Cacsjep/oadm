using System.Net;

using Oadm.Core.Vapix;

namespace Oadm.Plugins.Acap.Tests;

/// <summary>
/// Opt-in write test: changes one registered setting of an installed application on a real device with the real API
/// client, reads it back and sets the old value again. Runs only with <c>OADM_ACAP_SETTINGS_URL</c>,
/// <c>OADM_ACAP_SETTINGS_USER</c>, <c>OADM_ACAP_SETTINGS_PASSWORD</c>, <c>OADM_ACAP_SETTINGS_APP</c> and
/// <c>OADM_ACAP_SETTINGS_NAME</c> (a yes/no or text setting; the test flips yes/no, else appends "x") set, and only when the
/// developer asks for it (CLAUDE.md "device safety" 6). Passed on an AXIS P3265-V (AXIS OS 12.11.118), AXIS Image Health
/// Analytics setting ONVIFEvents (yes, no, back to yes), 2026-10-28.
/// </summary>
[Trait("Category", "HardwareWrite")]
public sealed class SettingsHardwareWriteTests
{
    private static readonly string? Url = Environment.GetEnvironmentVariable("OADM_ACAP_SETTINGS_URL");

    public sealed class WriteFactAttribute : FactAttribute
    {
        public WriteFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Url))
            {
                Skip = "OADM_ACAP_SETTINGS_URL is not set.";
            }
        }
    }

    [WriteFact]
    public async Task A_setting_changes_reads_back_and_goes_back()
    {
        var app = Environment.GetEnvironmentVariable("OADM_ACAP_SETTINGS_APP")!;
        var name = Environment.GetEnvironmentVariable("OADM_ACAP_SETTINGS_NAME")!;
        using var handler = new HttpClientHandler
        {
            Credentials = new NetworkCredential(Environment.GetEnvironmentVariable("OADM_ACAP_SETTINGS_USER"), Environment.GetEnvironmentVariable("OADM_ACAP_SETTINGS_PASSWORD")),
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        using var vapix = new VapixClient(new Uri(Url!.TrimEnd('/') + "/"), handler, disposeHandler: false);
        var api = new ApplicationApiClient(vapix);

        var before = (await api.ReadSettingsAsync(app, CancellationToken.None)).Single(d => d.Name == name);
        var original = before.Value!;
        var changed = original switch
        {
            "yes" => "no",
            "no" => "yes",
            _ => original + "x",
        };

        try
        {
            await api.UpdateSettingsAsync(app, new Dictionary<string, string> { [name] = changed }, CancellationToken.None);
            var after = (await api.ReadSettingsAsync(app, CancellationToken.None)).Single(d => d.Name == name);
            Assert.Equal(changed, after.Value);
        }
        finally
        {
            await api.UpdateSettingsAsync(app, new Dictionary<string, string> { [name] = original }, CancellationToken.None);
        }

        var restored = (await api.ReadSettingsAsync(app, CancellationToken.None)).Single(d => d.Name == name);
        Assert.Equal(original, restored.Value);
    }
}
