using Oadm.Sdk.Client;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Acap.Tests;

/// <summary>The one "path stays on the device" check of the SDK and its users here (application page, open page; Web View: WebViewTests).</summary>
public sealed class DevicePathsTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/local/ax_msf/redirect.html")]
    [InlineData("/local/objectanalytics/index.html#/")]
    [InlineData("/local/x/index.html#/a?b=c")]
    [InlineData("/axis-cgi/param.cgi?action=list&group=root.Brand")]
    [InlineData("/local/app/a%20b.html")]
    [InlineData("/local/..app/x.html")]
    public void Paths_on_the_device_pass(string path)
    {
        Assert.True(DevicePaths.IsOnDevice(path));
        Assert.True(DevicePageRequest.IsDevicePath(path));
        Assert.Equal(path, new InstalledApplication { Name = "x", ConfigurationPage = path }.ConfigurationPath);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("local/x.html")]
    [InlineData("http://evil.example/x")]
    [InlineData("/http://evil.example/x")]
    [InlineData("//evil.example/x")]
    [InlineData("/local//x.html")]
    [InlineData("/local/x.html?next=https://evil.example")]
    [InlineData("/local/../axis-cgi/admin/systemlog.cgi")]
    [InlineData("/local/..")]
    [InlineData("/local/%2e%2e/axis-cgi/x.cgi")]
    [InlineData("/local/%2E%2e/axis-cgi/x.cgi")]
    [InlineData("/local/a%2fb/x.cgi")]
    [InlineData("/local/a%5cb/x.cgi")]
    [InlineData("/local\\x.html")]
    [InlineData("/local/a b.html")]
    [InlineData("/local/a\tb.html")]
    [InlineData("/local/a\nb.html")]
    public void Paths_that_leave_the_device_fail(string? path)
    {
        Assert.False(DevicePaths.IsOnDevice(path));
        Assert.False(DevicePageRequest.IsDevicePath(path));
    }

    [Theory]
    [InlineData("local/ax_msf/redirect.html", "/local/ax_msf/redirect.html")]
    [InlineData(" /local/app/setup.html ", "/local/app/setup.html")]
    [InlineData("local/app/index.html#/home", "/local/app/index.html#/home")]
    [InlineData("http://evil.example/x", null)]
    [InlineData("//evil.example/x", null)]
    [InlineData("local/../../axis-cgi/admin/systemlog.cgi", null)]
    [InlineData("local/%2e%2e/axis-cgi/admin/systemlog.cgi", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void An_application_page_becomes_a_device_path_or_none(string? page, string? expected)
    {
        Assert.Equal(expected, DevicePaths.ToDevicePath(page));
        Assert.Equal(expected, new InstalledApplication { Name = "x", ConfigurationPage = page }.ConfigurationPath);
    }
}
