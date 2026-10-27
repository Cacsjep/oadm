using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Oadm.Client.Updates;

/// <summary>The newest OADM release: its version, the notes of the release page and its address.</summary>
public sealed record ReleaseInfo(Version Version, string VersionText, string Notes);

/// <summary>Reads the newest OADM release; null when it cannot be read in time.</summary>
public interface IReleaseCheck
{
    Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct);
}

/// <summary>
/// The latest release of the OADM repository on GitHub (<c>releases/latest</c>: never a draft or a pre-release). One
/// request at client start with a 2 s limit; no answer, an error or anything unexpected is null (nothing is shown).
/// </summary>
public sealed class GitHubReleaseCheck(HttpClient http, TimeProvider? time = null) : IReleaseCheck
{
    public const string LatestUrl = "https://api.github.com/repos/Cacsjep/oadm/releases/latest";

    /// <summary>The check with its own HTTP client (GitHub, never a device).</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0030:Do not use banned APIs", Justification = "The release check asks GitHub, not a device.")]
    public static GitHubReleaseCheck Create() => new(new HttpClient());

    /// <summary>How long the client waits for GitHub; enough on any computer with internet.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    public async Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct)
    {
        using var limit = new CancellationTokenSource(Timeout, time ?? TimeProvider.System);
        using var both = CancellationTokenSource.CreateLinkedTokenSource(ct, limit.Token);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("OADM-Client", ClientVersion.Text));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await http.SendAsync(request, both.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var body = await response.Content.ReadAsStreamAsync(both.Token).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(body, cancellationToken: both.Token).ConfigureAwait(false);
            return Parse(doc.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>The release of a <c>releases/latest</c> answer; null for a draft, a pre-release or an unknown tag.</summary>
    public static ReleaseInfo? Parse(JsonElement release)
    {
        if (release.ValueKind != JsonValueKind.Object
            || Bool(release, "draft") || Bool(release, "prerelease")
            || !release.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String
            || ClientVersion.Parse(tag.GetString()) is not { } version)
        {
            return null;
        }

        string notes = release.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() ?? "" : "";
        return new ReleaseInfo(version, ClientVersion.Format(version), notes);
    }

    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}

/// <summary>The version of this client and how versions are read and written ("v4.5.0" and "4.5.0" are 4.5.0).</summary>
public static class ClientVersion
{
    /// <summary>"4.5.0"; "1.0.0" in a development build (no version set).</summary>
    public static string Text { get; } = Settings.AboutViewModel.VersionOf(typeof(ClientVersion).Assembly);

    /// <summary>A development build has no release version: it never asks for a newer one.</summary>
    public static bool IsDevelopmentBuild(string text) => Parse(text) is not { } v || v == new Version(1, 0, 0);

    /// <summary>Major.minor.patch of "v4.6.0", "4.6.0-rc.1" or "4.6.0+abc"; null when it is none.</summary>
    public static Version? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string core = text.Trim().TrimStart('v', 'V').Split('+')[0].Split('-')[0];
        string[] parts = core.Split('.');
        if (parts.Length != 3)
        {
            return null;
        }

        var numbers = new int[3];
        for (int i = 0; i < 3; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return null;
            }
        }

        return new Version(numbers[0], numbers[1], numbers[2]);
    }

    /// <summary>"V4.6.0".</summary>
    public static string Format(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return string.Create(CultureInfo.InvariantCulture, $"V{version.Major}.{version.Minor}.{version.Build}");
    }
}
