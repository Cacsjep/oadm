namespace Oadm.Plugins.VapixCommander.Library.Tests;

/// <summary>
/// Read-only responses recorded on 10.0.0.48 (AXIS P3265-V, AXIS OS 12.11) in Fixtures/&lt;command id&gt;.txt.
/// Every fixture must be a success for its command and every response.extract must resolve on it.
/// </summary>
public sealed class FixtureTests
{
    private static string FixtureDirectory => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    public static TheoryData<string> FixtureIds()
    {
        var data = new TheoryData<string>();
        foreach (var path in Directory.GetFiles(FixtureDirectory, "*.txt").OrderBy(p => p, StringComparer.Ordinal))
        {
            data.Add(Path.GetFileNameWithoutExtension(path));
        }

        return data;
    }

    [Fact]
    public void There_are_fixtures()
    {
        Assert.True(Directory.GetFiles(FixtureDirectory, "*.txt").Length >= 5);
    }

    [Theory]
    [MemberData(nameof(FixtureIds))]
    public void Recorded_response_is_a_success_and_every_extract_resolves(string id)
    {
        var command = CommandLibrary.Command(id);
        Assert.False(CommandLibrary.Writes(command));
        var body = File.ReadAllText(Path.Combine(FixtureDirectory, id + ".txt"));

        Assert.Null(CommandRendering.Interpret(command, 200, body));
        var values = CommandRendering.Defaults(command);
        foreach (var extract in CommandLibrary.Extracts(command))
        {
            Assert.True(CommandRendering.Extract(extract, body, command, values) is not null,
                $"extract '{extract["label"]}' did not resolve");
        }
    }

    [Fact]
    public void A_vapix_json_error_is_reported_with_message_and_code()
    {
        var command = CommandLibrary.Command("image.light.info");
        const string body = """{"apiVersion": "1.6", "method": "getLightInformation", "error": {"code": 1005, "message": "No light hardware found, could not complete request."}}""";
        Assert.Equal("No light hardware found, could not complete request. (code 1005)", CommandRendering.Interpret(command, 200, body));
    }

    [Fact]
    public void A_param_cgi_error_is_reported()
    {
        var command = CommandLibrary.Command("image.brightness.set");
        Assert.NotNull(CommandRendering.Interpret(command, 200, "# Error: Error setting 'root.ImageSource.I0.Sensor.Brightness' to '500'!"));
        Assert.Null(CommandRendering.Interpret(command, 200, "OK"));
    }
}
