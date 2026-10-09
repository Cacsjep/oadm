using System.Diagnostics;
using System.Text;

using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Oadm.Sdk.Client.Controls;

namespace Oadm.Client.Tests;

/// <summary>ui:CodeView and its pure part CodeText: detection, pretty-printing, tokenizing, fallbacks.</summary>
public sealed class CodeViewTests
{
    private static List<(string Text, CodeTokenKind Kind)> Pieces(CodeDocument doc) =>
        [.. doc.Tokens.Select(t => (doc.Text.Substring(t.Start, t.Length), t.Kind))];

    [Theory]
    [InlineData("application/json; charset=utf-8", "x", CodeLanguage.Json)]
    [InlineData("application/problem+json", "x", CodeLanguage.Json)]
    [InlineData("text/xml", "x", CodeLanguage.Xml)]
    [InlineData("application/soap+xml; charset=utf-8", "x", CodeLanguage.Xml)]
    [InlineData("text/html", "<html><body/></html>", CodeLanguage.Plain)]
    [InlineData("text/plain", "root.Brand.Brand=AXIS\r\nroot.Brand.ProdNbr=P3265-V\r\n", CodeLanguage.KeyValue)]
    [InlineData("text/plain", "# Error: -1 getGroup\r\n", CodeLanguage.KeyValue)]
    [InlineData("text/plain", "# just a note", CodeLanguage.Plain)]
    [InlineData("text/plain", "# Error: x\nroot.A=1", CodeLanguage.KeyValue)]
    [InlineData("text/plain", "OK", CodeLanguage.Plain)]
    [InlineData("text/plain", "Error setting root.X to 5 = bad", CodeLanguage.Plain)]
    [InlineData(null, "  {\"a\":1}", CodeLanguage.Json)]
    [InlineData(null, "[1,2]", CodeLanguage.Json)]
    [InlineData(null, "<?xml version=\"1.0\"?><a/>", CodeLanguage.Xml)]
    [InlineData(null, "<!DOCTYPE html><html></html>", CodeLanguage.Plain)]
    [InlineData(null, "", CodeLanguage.Plain)]
    public void Language_comes_from_the_content_type_then_from_the_text(string? contentType, string text, CodeLanguage expected) =>
        Assert.Equal(expected, CodeText.Detect(text, contentType));

    [Fact]
    public void Minified_json_is_indented_with_two_spaces_and_highlighted()
    {
        var doc = CodeText.Prepare("{\"apiVersion\":\"1.3\",\"data\":{\"enabled\":true,\"count\":-12.5e3,\"none\":null,\"list\":[1,false,\"x\"]}}", contentType: "application/json");

        Assert.Equal(CodeLanguage.Json, doc.Language);
        Assert.Equal(
            "{\n  \"apiVersion\": \"1.3\",\n  \"data\": {\n    \"enabled\": true,\n    \"count\": -12.5e3,\n    \"none\": null,\n    \"list\": [\n      1,\n      false,\n      \"x\"\n    ]\n  }\n}",
            doc.Text.ReplaceLineEndings("\n"));
        var pieces = Pieces(doc);
        Assert.Contains(("\"apiVersion\"", CodeTokenKind.Key), pieces);
        Assert.Contains(("\"1.3\"", CodeTokenKind.String), pieces);
        Assert.Contains(("\"data\"", CodeTokenKind.Key), pieces);
        Assert.Contains(("true", CodeTokenKind.Literal), pieces);
        Assert.Contains(("null", CodeTokenKind.Literal), pieces);
        Assert.Contains(("false", CodeTokenKind.Literal), pieces);
        Assert.Contains(("-12.5e3", CodeTokenKind.Number), pieces);
        Assert.Contains(("1", CodeTokenKind.Number), pieces);
        Assert.Contains(("\"x\"", CodeTokenKind.String), pieces);
        Assert.Contains(pieces, p => p.Kind == CodeTokenKind.Punctuation && p.Text.StartsWith('{'));
    }

    [Fact]
    public void Json_escapes_stay_inside_their_string_and_unicode_is_readable()
    {
        var doc = CodeText.Prepare("{\"say\":\"he said \\\"hi\\\": \\\\ \\u00e9 <b>\",\"k\":\"v\"}", CodeLanguage.Json);

        var pieces = Pieces(doc);
        Assert.Contains(("\"he said \\\"hi\\\": \\\\ é <b>\"", CodeTokenKind.String), pieces);
        Assert.Contains(("\"k\"", CodeTokenKind.Key), pieces);
        Assert.Contains(("\"v\"", CodeTokenKind.String), pieces);
    }

    [Fact]
    public void Json_array_root_and_raw_mode_keep_the_exact_text()
    {
        const string raw = "[{\"a\":1},{\"b\":[]}]";
        var pretty = CodeText.Prepare(raw, CodeLanguage.Json);
        var exact = CodeText.Prepare(raw, CodeLanguage.Json, format: false);

        Assert.StartsWith("[\n  {\n    \"a\": 1", pretty.Text.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Equal(raw, exact.Text);
        Assert.Contains(("\"a\"", CodeTokenKind.Key), Pieces(exact));
        Assert.Contains(("1", CodeTokenKind.Number), Pieces(exact));
    }

    [Fact]
    public void Already_indented_json_does_not_change()
    {
        var once = CodeText.Prepare("{\"a\":{\"b\":[1,2]}}", CodeLanguage.Json).Text;
        Assert.Equal(once, CodeText.Prepare(once, CodeLanguage.Json).Text);
    }

    [Theory]
    [InlineData("{\"a\":1")]
    [InlineData("{\"a\":1}}")]
    [InlineData("{a:1}")]
    [InlineData("{\"a\":tru}")]
    public void Invalid_json_falls_back_to_the_exact_text_without_colors(string text)
    {
        var doc = CodeText.Prepare(text, contentType: "application/json");

        Assert.Equal(text, doc.Text);
        Assert.Equal(CodeLanguage.Plain, doc.Language);
        Assert.False(doc.IsHighlighted);
    }

    [Fact]
    public void Minified_soap_is_indented_and_tags_attributes_namespaces_are_highlighted()
    {
        const string soap = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><SOAP-ENV:Envelope xmlns:SOAP-ENV=\"http://www.w3.org/2003/05/soap-envelope\" xmlns:tds=\"http://www.onvif.org/ver10/device/wsdl\"><SOAP-ENV:Body><tds:GetSystemDateAndTimeResponse><tds:Mode a='1'>NTP</tds:Mode><!-- set by DHCP --><tds:Raw><![CDATA[x < y & z]]></tds:Raw><tds:Empty/></tds:GetSystemDateAndTimeResponse></SOAP-ENV:Body></SOAP-ENV:Envelope>";
        var doc = CodeText.Prepare(soap, contentType: "application/soap+xml; charset=utf-8");

        Assert.Equal(CodeLanguage.Xml, doc.Language);
        var lines = doc.Text.ReplaceLineEndings("\n").Split('\n');
        Assert.Equal("<?xml version=\"1.0\" encoding=\"UTF-8\"?>", lines[0]);
        Assert.StartsWith("<SOAP-ENV:Envelope xmlns:SOAP-ENV=", lines[1], StringComparison.Ordinal);
        Assert.Equal("  <SOAP-ENV:Body>", lines[2]);
        Assert.Equal("      <tds:Mode a=\"1\">NTP</tds:Mode>", lines[4]);
        Assert.Equal("      <!-- set by DHCP -->", lines[5]);
        Assert.Equal("      <tds:Raw><![CDATA[x < y & z]]></tds:Raw>", lines[6]);

        var pieces = Pieces(doc);
        Assert.Contains(("SOAP-ENV:Envelope", CodeTokenKind.Tag), pieces);
        Assert.Contains(("xmlns:SOAP-ENV", CodeTokenKind.Attribute), pieces);
        Assert.Contains(("\"http://www.w3.org/2003/05/soap-envelope\"", CodeTokenKind.String), pieces);
        Assert.Contains(("tds:Mode", CodeTokenKind.Tag), pieces);
        Assert.Contains(("a", CodeTokenKind.Attribute), pieces);
        Assert.Contains(("<!-- set by DHCP -->", CodeTokenKind.Comment), pieces);
        Assert.Contains(("x < y & z", CodeTokenKind.String), pieces);
        Assert.Contains(("xml", CodeTokenKind.Tag), pieces);
        Assert.Contains(("version", CodeTokenKind.Attribute), pieces);
        Assert.DoesNotContain(pieces, p => p.Text.Contains("NTP", StringComparison.Ordinal)); // character data stays normal text
    }

    [Fact]
    public void Already_indented_xml_does_not_change_and_has_no_blank_lines()
    {
        var once = CodeText.Prepare("<?xml version=\"1.0\"?>\r\n<root>\r\n  <a x=\"1\">1</a>\r\n</root>", CodeLanguage.Xml).Text;

        Assert.Equal("<?xml version=\"1.0\"?>\n<root>\n  <a x=\"1\">1</a>\n</root>", once);
        Assert.Equal(once, CodeText.Prepare(once, CodeLanguage.Xml).Text);
    }

    [Theory]
    [InlineData("<a><b></a>")]
    [InlineData("<a>1</a><b>2</b>")]
    [InlineData("<!DOCTYPE a [<!ENTITY x \"y\">]><a>&x;</a>")]
    public void Invalid_or_dtd_xml_falls_back_to_the_exact_text(string text)
    {
        var doc = CodeText.Prepare(text, CodeLanguage.Xml);

        Assert.Equal(text, doc.Text);
        Assert.False(doc.IsHighlighted);
    }

    [Fact]
    public void Param_cgi_keys_values_and_error_lines_are_highlighted()
    {
        const string text = "root.Brand.Brand=AXIS\r\nroot.ImageSource.I0.DayNight.ShiftLevel=50\r\nroot.Network.DNSUpdate.Enabled=yes\r\nroot.Empty=\r\n# Error: Error -1 getting param in group 'root.Foo'\r\n# comment\r\nPlain line\r\n";
        Assert.Equal(CodeLanguage.KeyValue, CodeText.Detect(text.Replace("Plain line\r\n", string.Empty, StringComparison.Ordinal), "text/plain"));
        var doc = CodeText.Prepare(text, CodeLanguage.KeyValue);

        Assert.Equal(text, doc.Text);
        var pieces = Pieces(doc);
        Assert.Contains(("root.Brand.Brand", CodeTokenKind.Key), pieces);
        Assert.Contains(("AXIS", CodeTokenKind.String), pieces);
        Assert.Contains(("root.ImageSource.I0.DayNight.ShiftLevel", CodeTokenKind.Key), pieces);
        Assert.Contains(("50", CodeTokenKind.Number), pieces);
        Assert.Contains(("yes", CodeTokenKind.Literal), pieces);
        Assert.Contains(("root.Empty", CodeTokenKind.Key), pieces);
        Assert.Contains(("# Error: Error -1 getting param in group 'root.Foo'", CodeTokenKind.Error), pieces);
        Assert.Contains(("# comment", CodeTokenKind.Comment), pieces);
        Assert.DoesNotContain(pieces, p => p.Text.Contains("Plain line", StringComparison.Ordinal));
        Assert.DoesNotContain(pieces, p => p.Text.Contains('\r') || p.Text.Contains('\n'));
    }

    [Fact]
    public void Value_with_equals_signs_keeps_the_first_as_separator()
    {
        var pieces = Pieces(CodeText.Prepare("root.Url=http://x/?a=b", CodeLanguage.KeyValue));

        Assert.Equal([("root.Url", CodeTokenKind.Key), ("=", CodeTokenKind.Punctuation), ("http://x/?a=b", CodeTokenKind.String)], pieces);
    }

    [Fact]
    public void Plain_text_has_no_tokens()
    {
        var doc = CodeText.Prepare("Hello\nworld", CodeLanguage.Plain);
        Assert.Equal("Hello\nworld", doc.Text);
        Assert.False(doc.IsHighlighted);
        Assert.Same(CodeDocument.Empty, CodeText.Prepare(null));
    }

    [Fact]
    [Trait("Category", "Timing")] // time budget
    public void Large_bodies_are_formatted_but_shown_without_colors_above_the_limit()
    {
        var json = new StringBuilder("[");
        for (var i = 0; i < 60_000; i++)
        {
            json.Append(i == 0 ? string.Empty : ",").Append("{\"id\":").Append(i).Append(",\"name\":\"camera\"}");
        }

        var text = json.Append(']').ToString();
        Assert.True(text.Length > 1_000_000);
        var watch = Stopwatch.StartNew();
        var doc = CodeText.Prepare(text, CodeLanguage.Json);
        watch.Stop();

        Assert.Equal(CodeLanguage.Json, doc.Language);
        Assert.StartsWith("[\n  {\n    \"id\": 0,", doc.Text.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.False(doc.IsHighlighted);
        Assert.True(watch.ElapsedMilliseconds < 5000, $"took {watch.ElapsedMilliseconds} ms");

        var small = CodeText.Prepare("{\"a\":1}", CodeLanguage.Json, highlightLimit: 5);
        Assert.False(small.IsHighlighted);
    }

    [Fact]
    public void Text_above_the_format_limit_is_shown_as_is()
    {
        var text = "[" + new string(' ', CodeText.MaxFormatChars) + "1]";
        var doc = CodeText.Prepare(text, CodeLanguage.Json);

        Assert.Same(text, doc.Text);
        Assert.False(doc.IsHighlighted);
    }

    [Fact]
    [Trait("Category", "Timing")]
    public void Highlighting_a_body_of_the_size_limit_is_fast()
    {
        var json = new StringBuilder("{");
        for (var i = 0; json.Length < 250_000; i++)
        {
            json.Append(i == 0 ? string.Empty : ",").Append("\"key").Append(i).Append("\":[true,null,").Append(i).Append(",\"text\"]");
        }

        var text = json.Append('}').ToString();
        var watch = Stopwatch.StartNew();
        var doc = CodeText.Prepare(text, CodeLanguage.Json, format: false);
        watch.Stop();

        Assert.True(doc.IsHighlighted);
        Assert.True(watch.ElapsedMilliseconds < 2000, $"took {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Tokens_never_overlap_and_stay_inside_the_text_even_for_broken_input()
    {
        string[] inputs = ["{\"a\":\"unterminated", "<a b=\"x><c", "<!-- open", "<![CDATA[ open", "k=v\n#\n=x\nroot.A=", "{\"\\", "<", "<a "];
        foreach (var input in inputs)
        {
            foreach (var language in new[] { CodeLanguage.Json, CodeLanguage.Xml, CodeLanguage.KeyValue })
            {
                var tokens = CodeText.Tokenize(input, language);
                var end = 0;
                foreach (var token in tokens)
                {
                    Assert.True(token.Start >= end && token.Length > 0 && token.Start + token.Length <= input.Length, $"{language} {input}");
                    end = token.Start + token.Length;
                }
            }
        }
    }

    [Fact]
    public async Task Code_view_renders_colored_runs_from_the_theme_and_copies_the_text()
    {
        HeadlessUnitTestSession session = HeadlessSession.Shared;
        await session.Dispatch(() =>
        {
            var view = new CodeView { Text = "{\"enabled\":true}", ContentType = "application/json" };
            var window = new Window { Width = 400, Height = 200, Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            SelectableTextBlock block = view.GetVisualDescendants().OfType<SelectableTextBlock>().Single();
            Assert.Equal(TextWrapping.NoWrap, block.TextWrapping);
            Assert.Contains("Cascadia Mono", block.FontFamily.ToString(), StringComparison.Ordinal);
            Run key = block.Inlines!.OfType<Run>().Single(r => r.Text == "\"enabled\"");
            Assert.Equal(((ISolidColorBrush)window.FindResource("Oadm.Code.KeyBrush")!).Color, ((ISolidColorBrush)key.Foreground!).Color);
            Assert.Equal("{\n  \"enabled\": true\n}", string.Concat(block.Inlines!.OfType<Run>().Select(r => r.Text)).ReplaceLineEndings("\n"));

            block.SelectAll();
            Assert.Equal(view.DisplayedText, block.SelectedText);

            // Raw mode: exactly the given text, still highlighted.
            view.IsFormatted = false;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("{\"enabled\":true}", string.Concat(block.Inlines!.OfType<Run>().Select(r => r.Text)));

            // Not parseable: plain text, no runs.
            view.Text = "{broken";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("{broken", block.Text);
            Assert.True(block.Inlines is null || block.Inlines.Count == 0);
            window.Close();
            return Task.CompletedTask;
        }, CancellationToken.None);
    }
}
