namespace Oadm.Plugins.Acap.Tests;

/// <summary>The settings parser and checks, the log reader, and the API client's read methods (recorded answers).</summary>
public sealed class AppSettingsTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private const string Ns = "http://www.axis.com/ParameterDefinitionsSchema";

    /// <summary>A listdefinitions answer in the device's format (types as seen in the recorded full parameter list).</summary>
    private static string Definitions(string app, string parameters) => $"""
        <?xml version="1.0" encoding="iso-8859-1" ?>
        <parameterDefinitions xmlns="{Ns}" version="1.0">
          <model>AXIS P3265-V</model>
          <group name="root">
            <group name="{app}">
        {parameters}
            </group>
          </group>
        </parameterDefinitions>
        """;

    private static readonly string Typed = Definitions("demo", """
        <parameter name="Threshold" value="40" securityLevel="7746" niceName="Threshold"><type><int min="0" max="100" maxlen="3" /></type></parameter>
        <parameter name="Port" value="0" securityLevel="7746" niceName="Port"><type><int><rangeEntry value="0" /><rangeEntry value="1024-65534" /></int></type></parameter>
        <parameter name="Mode" value="fast" securityLevel="7746" niceName="Mode"><type><enum><entry value="fast" niceValue="Fast" /><entry value="exact" niceValue="Exact" /></enum></type></parameter>
        <parameter name="Enabled" value="yes" securityLevel="7746" niceName="Enabled"><type><bool true="yes" false="no" /></type></parameter>
        <parameter name="Server" value="10.0.0.5" securityLevel="7746" niceName="Server address"><type><string maxlen="64" /></type></parameter>
        <parameter name="Key" value="******" securityLevel="7746" niceName="Pre-shared key"><type writeonly="true"><password maxlen="16" /></type></parameter>
        <parameter name="Internal" value="1" securityLevel="7746"><type hidden="true"><string /></type></parameter>
        <parameter name="Build" value="12" securityLevel="7747"><type readonly="true" const="true"><int /></type></parameter>
        <group name="Sub"><parameter name="Level" value="2" securityLevel="7746"><type><int min="1" max="5" /></type></parameter></group>
        """);

    [Fact]
    public void Parses_the_recorded_settings_of_AXIS_Image_Health_Analytics()
    {
        var settings = AppSettings.Parse(Fixture("paramdefinitions-imagehealth-p3265v-12.11.xml"), "AXISImageHealthAnalytics");

        Assert.Equal(14, settings.Count);
        var onvif = Assert.Single(settings, s => s.IsChangeable);
        Assert.Equal("ONVIFEvents", onvif.Name);
        Assert.Equal("ONVIF events", onvif.DisplayName);
        Assert.Equal(AppSettingKind.Text, onvif.Kind);
        Assert.Equal("yes", onvif.Value);
        Assert.All(settings.Where(s => s.Name != "ONVIFEvents"), s => Assert.True(s.Hidden));
        Assert.Equal("Auto relearning", settings.Single(s => s.Name == "AutoRelearning").NiceName);
    }

    [Fact]
    public void Parses_every_type_with_range_choices_and_flags()
    {
        var settings = AppSettings.Parse(Typed, "demo").ToDictionary(s => s.Name);

        Assert.Equal(AppSettingKind.Number, settings["Threshold"].Kind);
        Assert.Equal(new AppSettingRange(0, 100), Assert.Single(settings["Threshold"].Ranges));
        Assert.Equal(3, settings["Threshold"].MaxLength);
        Assert.Equal([new AppSettingRange(0, 0), new AppSettingRange(1024, 65534)], settings["Port"].Ranges);
        Assert.Equal(AppSettingKind.List, settings["Mode"].Kind);
        Assert.Equal(["Fast", "Exact"], settings["Mode"].Choices.Select(c => c.Display));
        Assert.Equal(AppSettingKind.YesNo, settings["Enabled"].Kind);
        Assert.Equal(("yes", "no"), (settings["Enabled"].TrueValue, settings["Enabled"].FalseValue));
        Assert.Equal(64, settings["Server"].MaxLength);
        Assert.Equal(AppSettingKind.Password, settings["Key"].Kind);
        Assert.Null(settings["Key"].Value); // never read back, not even the device's mask
        Assert.True(settings["Key"].IsChangeable);
        Assert.True(settings["Internal"].Hidden);
        Assert.False(settings["Internal"].IsChangeable);
        Assert.True(settings["Build"].ReadOnly);
        Assert.Equal("2", settings["Sub.Level"].Value);
    }

    [Theory]
    [InlineData("# Error: Error -1 getting param in group 'root.parking'\n")]
    [InlineData("")]
    public void An_application_without_settings_has_none(string answer) =>
        Assert.Empty(AppSettings.Parse(answer, "parking"));

    [Fact]
    public void Another_group_in_the_answer_is_not_the_application()
    {
        Assert.Empty(AppSettings.Parse(Typed, "other"));
        Assert.Throws<AcapSettingsException>(() => AppSettings.Parse("<parameterDefinitions", "demo"));
    }

    [Theory]
    [InlineData("Threshold", "100", true, "100")]
    [InlineData("Threshold", "007", true, "7")]
    [InlineData("Threshold", "101", false, "must be 0 to 100.")]
    [InlineData("Threshold", "abc", false, "must be a whole number.")]
    [InlineData("Port", "0", true, "0")]
    [InlineData("Port", "80", false, "must be 0 or 1024 to 65534.")]
    [InlineData("Mode", "exact", true, "exact")]
    [InlineData("Mode", "EXACT", true, "exact")]
    [InlineData("Mode", "slow", false, "must be one of its listed values.")]
    [InlineData("Enabled", "No", true, "no")]
    [InlineData("Enabled", "true", false, "must be yes or no.")]
    [InlineData("Server", "a\nb", false, "contains characters the device does not take.")]
    public void Values_must_fit_the_type_and_range(string name, string value, bool fits, string expected)
    {
        var definition = AppSettings.Parse(Typed, "demo").Single(s => s.Name == name);

        var ok = AppSettings.TryNormalize(definition, value, out var normalized, out var problem);

        Assert.Equal(fits, ok);
        Assert.Equal(expected, fits ? normalized : problem);
    }

    [Fact]
    public void Check_names_every_problem_without_the_value()
    {
        var definitions = AppSettings.Parse(Typed, "demo");
        var values = new Dictionary<string, string>
        {
            ["Threshold"] = "secret-101",
            ["Nope"] = "secret-x",
            ["Internal"] = "secret-2",
            ["Build"] = "secret-3",
            ["Server"] = new string('s', 65),
            ["Mode"] = "exact",
        };

        var problems = AppSettings.Check(definitions, values, "Demo", out var normalized);

        Assert.Equal(
            ["Threshold must be a whole number.", "Nope is not a setting of Demo.", "Internal cannot be changed.", "Build cannot be changed.", "Server address must be at most 64 characters."],
            problems);
        Assert.DoesNotContain(problems, p => p.Contains("secret", StringComparison.Ordinal) || p.Contains("sss", StringComparison.Ordinal));
        Assert.Equal("exact", normalized["Mode"]);
    }

    [Fact]
    public void Update_form_escapes_names_and_values()
    {
        var form = AppSettings.BuildUpdateForm("demo", new Dictionary<string, string> { ["Server"] = "a b&c=d", ["Sub.Level"] = "2" });

        Assert.Equal("action=update&root.demo.Server=a%20b%26c%3Dd&root.demo.Sub.Level=2", form);
        Assert.Throws<ArgumentException>(() => AppSettings.BuildUpdateForm("demo", new Dictionary<string, string> { ["a&b"] = "1" }));
        Assert.Throws<ArgumentException>(() => AppSettings.BuildUpdateForm("a&b", new Dictionary<string, string>()));
    }

    [Fact]
    public void A_refused_update_names_the_setting_never_the_value()
    {
        AppSettings.EnsureUpdated("OK\n", "demo");

        var ex = Assert.Throws<AcapSettingsException>(() => AppSettings.EnsureUpdated("# Error: Error setting 'root.demo.Key' to 'hunter2'!", "demo"));
        Assert.Equal("The device refused the new value of Key.", ex.Message);
        Assert.Equal("Key", ex.Setting);

        var other = Assert.Throws<AcapSettingsException>(() => AppSettings.EnsureUpdated("Request failed: hunter2", "demo"));
        Assert.Equal("The device refused the new settings.", other.Message);
    }

    [Theory]
    [InlineData("ONVIFEvents", true)]
    [InlineData("Sub.Level", true)]
    [InlineData("a&b", false)]
    [InlineData("root.x", true)]
    [InlineData(".x", false)]
    [InlineData("", false)]
    public void Setting_names_are_plain(string name, bool valid) => Assert.Equal(valid, AppSettings.IsValidName(name));

    [Fact]
    public void Definitions_uri_reads_the_group_of_the_application()
    {
        Assert.Equal("/axis-cgi/param.cgi?action=listdefinitions&listformat=xmlschema&group=root.AXISImageHealthAnalytics", AppSettings.BuildDefinitionsUri("AXISImageHealthAnalytics"));
        Assert.Throws<ArgumentException>(() => AppSettings.BuildDefinitionsUri("a&b"));
    }

    [Fact]
    public void Parses_the_recorded_log_of_an_application()
    {
        var log = AppLogReader.Parse(Fixture("systemlog-eva-p3265v-12.11.txt"));

        var line = Assert.Single(log.Lines);
        Assert.False(log.Truncated);
        Assert.Equal("CRIT", line.Level);
        Assert.Equal("eva[8617]", line.Process);
        Assert.Equal(new DateTimeOffset(2026, 10, 27, 7, 45, 40, 900, TimeSpan.FromHours(1)), line.Time);
        Assert.StartsWith("Failed to unregister events on shutdown", line.Message, StringComparison.Ordinal);
        Assert.StartsWith("2026-10-27T07:45:40.900+01:00 axis-b8a44f631339", line.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_log_without_lines_is_empty_and_lines_without_a_time_are_kept()
    {
        Assert.True(AppLogReader.Parse("\n----- Contents of SYSTEM_LOG for 'parking' -----\n\n\n").IsEmpty);

        var log = AppLogReader.Parse("----- Contents of SYSTEM_LOG for 'x' -----\nfree text\r\n");
        var line = Assert.Single(log.Lines);
        Assert.Null(line.Time);
        Assert.Equal("free text", line.Message);
        Assert.Equal("No log lines from this application.", AppLogReader.NoLines);
    }

    [Fact]
    public async Task Keeps_the_newest_10000_lines()
    {
        var text = string.Join("\n", Enumerable.Range(1, 12_000).Select(i => FormattableString.Invariant($"2026-10-27T07:45:40.900+01:00 host [ INFO    ] app[1]: line {i}")));

        var log = await AppLogReader.ReadAsync(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text)), CancellationToken.None);

        Assert.True(log.Truncated);
        Assert.Equal(AppLogReader.MaxLines, log.Lines.Count);
        Assert.Equal("line 2001", log.Lines[0].Message);
        Assert.Equal("line 12000", log.Lines[^1].Message); // newest last
    }

    [Fact]
    public void Keeps_at_most_2_MB()
    {
        var line = new string('x', 4000);
        var log = AppLogReader.Parse(string.Join("\n", Enumerable.Repeat(line, 1000)));

        Assert.True(log.Truncated);
        Assert.True(log.Lines.Sum(l => l.Text.Length + 1) <= AppLogReader.MaxBytes);
        Assert.Equal(AppLogReader.MaxBytes / 4001, log.Lines.Count);
    }

    [Fact]
    public async Task Api_client_reads_settings_and_log_with_GET_only()
    {
        var device = new FakeAcapDevice { LogText = Fixture("systemlog-eva-p3265v-12.11.txt") };
        device.AddSetting("demo", "Threshold", "<int min=\"0\" max=\"100\" />", "40");
        var client = new ApplicationApiClient(device);

        var settings = await client.ReadSettingsAsync("demo", CancellationToken.None);
        var none = await client.ReadSettingsAsync("parking", CancellationToken.None);
        var log = await client.ReadLogAsync("eva", CancellationToken.None);

        Assert.Equal("40", Assert.Single(settings).Value);
        Assert.Empty(none);
        Assert.Single(log.Lines);
        Assert.Empty(device.Writes);
        Assert.All(device.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
        Assert.Equal("/axis-cgi/admin/systemlog.cgi?appname=eva", device.Requests[^1].RequestUri!.OriginalString);
    }
}
