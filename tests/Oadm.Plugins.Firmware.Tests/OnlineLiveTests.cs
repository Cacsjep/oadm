using Oadm.Plugins.Firmware.Online;

namespace Oadm.Plugins.Firmware.Tests;

/// <summary>
/// The real lookup against www.axis.com, read-only (listings only, never a .bin). Opt-in: runs only with
/// OADM_AXIS_LIVE=1 (no network in unit tests); <c>dotnet test --filter Category=Live</c>.
/// </summary>
public sealed class OnlineLiveTests
{
    [Fact]
    [Trait("Category", "Live")]
    public async Task The_real_listings_of_axis_com_read()
    {
        if (Environment.GetEnvironmentVariable("OADM_AXIS_LIVE") != "1")
        {
            return;
        }

        var lookup = new OnlineFirmwareLookup(AxisSite.Instance);
        var result = await lookup.LookupAsync(["P3265-V", "C1410", "P1455-LE", "P12-MkIII"], CancellationToken.None);

        Assert.False(result.Unreachable);
        foreach (var model in result.Models)
        {
            Console.WriteLine($"{model.Model}: {model.Source}/{model.Product} latest {model.Latest} failed {model.Failed}; "
                + string.Join(" | ", model.Tracks.Select(t => $"{t.Name}: {t.Newest} ({t.Versions[0].Size} bytes, {t.Versions.Count} versions)")));
        }

        Assert.True(result.Models[0].IsListed);
        Assert.True(result.Models[1].IsListed);
        Assert.False(result.Models[3].IsListed);
        var file = await lookup.FindVersionAsync("MPQT", "P3265-V", result.Models[0].Tracks[0].Newest, CancellationToken.None);
        Console.WriteLine($"{file?.Url} {file?.Size} {file?.Sha256}");
        Assert.NotNull(file);
    }
}
