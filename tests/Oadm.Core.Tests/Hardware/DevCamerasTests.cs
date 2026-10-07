namespace Oadm.Core.Tests.Hardware;

public class DevCamerasTests
{
    [Fact]
    public void ParsesExampleFormat()
    {
        const string yaml = """
            # comment
            cameras:
              - address: 192.168.0.90      # IP or host name
                user: root
                password: change-me
                scheme: http
                note: "AXIS P3265-V lab"
              - address: 10.0.0.2
                password: x
              - user: nobody
            """;

        var cameras = DevCameras.Parse(yaml);

        Assert.Equal(2, cameras.Count);
        Assert.Equal("192.168.0.90", cameras[0].Address);
        Assert.Equal("change-me", cameras[0].Password);
        Assert.Equal("http", cameras[0].EffectiveScheme);
        Assert.Equal("root", cameras[1].User);
        Assert.Equal("https", cameras[1].EffectiveScheme);
        Assert.DoesNotContain("change-me", cameras[0].ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyFileYieldsNoCameras()
    {
        Assert.Empty(DevCameras.Parse(string.Empty));
    }

    [Fact]
    public void FindConfigFileSearchesUpward()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(DevCameras.EnvironmentVariable)))
        {
            return; // explicit path wins over the search; nothing to check here
        }

        var root = Directory.CreateTempSubdirectory("oadm-devcams-");
        try
        {
            var nested = Directory.CreateDirectory(Path.Combine(root.FullName, "a", "b", "c"));
            var file = Path.Combine(root.FullName, DevCameras.FileName);
            File.WriteAllText(file, "cameras: []");

            Assert.Equal(file, DevCameras.FindConfigFile(nested.FullName));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
