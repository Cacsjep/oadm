using Oadm.Core.Persistence;
using Oadm.Core.Vapix;
using Oadm.Sdk.Vapix;

namespace Oadm.Core.Tests.Vapix;

public sealed class DeviceApiTests
{
    private static readonly DeviceApi[] Apis =
    [
        new("user-management", "1.2"),
        new("network-settings", "1.37"),
        new("privacy-mask", "2.4"),
        new("privacy-mask", "3.0"),
        new("broken", "x.y"),
    ];

    [Fact]
    public void ParseApiListReadsTheRecordedP3265List()
    {
        var apis = VapixParsers.ParseApiList(Fixtures.Read("apidiscovery-getApiList.json"));

        Assert.Equal(72, apis.Count);
        var users = Assert.Single(apis, a => a.Id == "user-management");
        Assert.Equal("1.2", users.Version);
        Assert.Equal(new Version(1, 2), users.ParsedVersion);
        Assert.False(string.IsNullOrEmpty(users.Name));
        Assert.Contains(apis, a => a.Id == "network-settings" && a.Version == "1.37");
        Assert.Contains(apis, a => a.Id == "fwmgr" && a.Version == "1.10");

        // The same API id can be listed with several major versions.
        Assert.Equal(["2.4", "3.0"], apis.Where(a => a.Id == "privacy-mask").Select(a => a.Version).Order());
    }

    [Fact]
    public void ParseApiListThrowsOnAnErrorReply()
    {
        Assert.Throws<VapixException>(() => VapixParsers.ParseApiList("""{"apiVersion":"1.0","error":{"code":2002,"message":"nope"}}"""));
    }

    [Fact]
    public void ParseApiListSkipsEntriesWithoutIdOrVersion()
    {
        var apis = VapixParsers.ParseApiList("""{"apiVersion":"1.0","data":{"apiList":[{"id":"a","version":"1.0"},{"id":"b"},{"version":"1.0"}]}}""");

        Assert.Equal("a", Assert.Single(apis).Id);
    }

    [Theory]
    [InlineData("user-management", "1.0", true)]
    [InlineData("user-management", "1.2", true)]  // same version
    [InlineData("user-management", "1.3", false)] // minor too high
    [InlineData("user-management", "2.0", false)] // other major is another API
    [InlineData("network-settings", "1.9", true)] // 1.37 >= 1.9 (numeric, not string compare)
    [InlineData("USER-MANAGEMENT", "1.1", true)]  // ids are case-insensitive
    [InlineData("fwmgr", "1.0", false)]           // missing
    public void SupportsRequiresSameMajorAndAtLeastTheMinor(string id, string min, bool expected)
    {
        Assert.Equal(expected, Apis.Supports(id, min));
    }

    [Fact]
    public void SupportsPicksTheMatchingMajorWhenSeveralAreListed()
    {
        Assert.True(Apis.Supports("privacy-mask", "2.1"));
        Assert.True(Apis.Supports("privacy-mask", "3.0"));
        Assert.False(Apis.Supports("privacy-mask", "2.5"));
        Assert.False(Apis.Supports("privacy-mask", "1.0"));
        Assert.Equal("3.0", Apis.FindApi("privacy-mask")!.Version);
        Assert.Equal("2.4", Apis.FindApi("privacy-mask", 2)!.Version);
    }

    [Fact]
    public void UnparsableVersionsNeverSatisfyARequirement()
    {
        Assert.Equal(new Version(0, 0), Apis.Single(a => a.Id == "broken").ParsedVersion);
        Assert.False(Apis.Supports("broken", "0.1"));
    }

    [Fact]
    public void RequireReturnsTheMatchingApi()
    {
        Assert.Equal("1.37", Apis.Require("network-settings", "1.20").Version);
    }

    [Fact]
    public void RequireThrowsWithTheFoundVersionInTheMessage()
    {
        var ex = Assert.Throws<DeviceNotCompatibleException>(() => Apis.Require("user-management", "2.0"));

        Assert.Equal("user-management", ex.ApiId);
        Assert.Equal("2.0", ex.RequiredVersion);
        Assert.Equal("1.2", ex.FoundVersion);
        Assert.Equal("Device has user-management 1.2, needs 2.0 or later. Nothing was changed.", ex.Message);
    }

    [Fact]
    public void RequireThrowsForAMissingApi()
    {
        var ex = Assert.Throws<DeviceNotCompatibleException>(() => Apis.Require("fwmgr", "1.0"));

        Assert.Null(ex.FoundVersion);
        Assert.Equal("Device does not support fwmgr (needs 1.0 or later). Nothing was changed.", ex.Message);
    }

    [Fact]
    public void JsonColumnRoundTrips()
    {
        IReadOnlyList<DeviceApi> apis = [new("user-management", "1.2", "User management", "official"), new("ntp", "1.5")];

        var json = DeviceApiJson.Serialize(apis);

        Assert.Equal("""[{"id":"user-management","version":"1.2","name":"User management","status":"official"},{"id":"ntp","version":"1.5"}]""", json);
        Assert.Equal(apis, DeviceApiJson.Deserialize(json));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    public void UnreadableJsonColumnIsAnEmptyList(string? json)
    {
        Assert.Empty(DeviceApiJson.Deserialize(json));
    }
}
