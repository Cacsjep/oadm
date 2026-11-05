using Oadm.Contracts.Updates;

namespace Oadm.Client.Tests;

/// <summary>The update helper is a copy of the client: a window, so it needs the native libraries beside the exe.</summary>
public sealed class HelperCopyTests
{
    [Fact]
    public void TheCopyTakesTheNativeLibrariesAndNothingElse()
    {
        var root = Directory.CreateTempSubdirectory("oadm-helper-").FullName;
        try
        {
            var app = Directory.CreateDirectory(Path.Combine(root, "app")).FullName;
            foreach (var name in (string[])["Oadm.Client.exe", "libSkiaSharp.dll", "libavcodec.so.63", "libavcodec.63.dylib", "appsettings.json"])
            {
                File.WriteAllText(Path.Combine(app, name), "x");
            }

            var copy = HelperCopy.Make(Path.Combine(root, "update"), Path.Combine(app, "Oadm.Client.exe"));

            var files = Directory.GetFiles(Path.GetDirectoryName(copy)!).Select(Path.GetFileName).Order(StringComparer.Ordinal);
            Assert.Equal(["Oadm.Client.exe", "libSkiaSharp.dll", "libavcodec.63.dylib", "libavcodec.so.63"], files);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("avcodec-63.dll", true)]
    [InlineData("libSkiaSharp.so", true)]
    [InlineData("libavutil.so.61", true)]
    [InlineData("libAvaloniaNative.dylib", true)]
    [InlineData("THIRD-PARTY-NOTICES.txt", false)]
    public void NativeLibrariesAreKnownByTheirName(string name, bool native)
    {
        Assert.Equal(native, HelperCopy.IsNativeLibrary(name));
    }
}
