using System.Text.Json;
using System.Xml.Linq;

using Oadm.Plugins.ActionRules.Rules;

namespace Oadm.Plugins.ActionRules.Tests;

/// <summary>The bundled action catalog against the recorded template lists of AXIS OS 10, 11 and 12.</summary>
public sealed class CatalogTests
{
    private static readonly string[] Versions = ["os10", "os11", "os12"];

    private static XDocument Fixture(string name) => XDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".xml")));

    private static JsonElement Catalog()
    {
        using var stream = typeof(ActionCatalog).Assembly.GetManifestResourceStream("Oadm.Plugins.ActionRules.Catalog.action-catalog.json");
        Assert.NotNull(stream);
        return JsonDocument.Parse(stream).RootElement.Clone();
    }

    [Fact]
    public void Every_template_and_setting_of_the_devices_is_described_and_nothing_else()
    {
        var catalog = Catalog();
        foreach (var (section, method) in new[] { ("actions", "GetActionTemplates"), ("recipients", "GetRecipientTemplates") })
        {
            var device = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var os in Versions)
            {
                var doc = Fixture($"{os}-{method}");
                var templates = section == "actions"
                    ? SoapActionEngine.ParseActionTemplates(doc).Select(t => (t.Token, t.Parameters))
                    : SoapActionEngine.ParseRecipientTemplates(doc).Select(t => (t.Token, t.Parameters));
                foreach (var (token, parameters) in templates)
                {
                    if (!device.TryGetValue(token, out var names))
                    {
                        device[token] = names = new HashSet<string>(StringComparer.Ordinal);
                    }

                    names.UnionWith(parameters);
                }
            }

            var listed = catalog.GetProperty(section).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetProperty("fields").EnumerateObject().Select(f => f.Name).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
            Assert.Empty(device.Keys.Except(listed.Keys));
            foreach (var (token, names) in device)
            {
                Assert.True(names.SetEquals(listed[token]), $"{token}: device {string.Join(",", names.Order())} catalog {string.Join(",", listed[token].Order())}");
            }
        }
    }

    [Fact]
    public void Defaults_fit_their_fields_and_secrets_are_password_fields()
    {
        foreach (var section in new[] { "actions", "recipients" })
        {
            foreach (var template in Catalog().GetProperty(section).EnumerateObject())
            {
                foreach (var field in template.Value.GetProperty("fields").EnumerateObject())
                {
                    var f = field.Value.Deserialize<CatalogField>(ActionRulesJson.Options)!;
                    var where = $"{template.Name} {field.Name}";
                    Assert.Contains(f.Kind, new[] { FieldKinds.Text, FieldKinds.Multiline, FieldKinds.Number, FieldKinds.Bool, FieldKinds.Choice, FieldKinds.Password, FieldKinds.Hidden });
                    Assert.True(f.Kind == FieldKinds.Hidden || !string.IsNullOrWhiteSpace(f.Label), where + ": no label");
                    if (f.Kind == FieldKinds.Choice && f.Default is { Length: > 0 } choice)
                    {
                        Assert.True(f.Choices!.Any(c => c.Value == choice), where + ": default is no choice");
                    }

                    if (f.Kind == FieldKinds.Bool)
                    {
                        Assert.Contains(f.Default, new[] { f.TrueValue, f.FalseValue });
                    }

                    if (RuleText.IsSecret(field.Name) && f.Kind != FieldKinds.Hidden)
                    {
                        Assert.Equal(FieldKinds.Password, ActionCatalog.Field(template.Name, template.Name, field.Name).Kind);
                    }
                }
            }
        }
    }

    [Fact]
    public void Forms_show_the_catalog_labels_and_unknown_settings_by_their_name()
    {
        Assert.Equal("Repeat every (s)", ActionCatalog.Field("com.axis.action.unlimited.notification.tcp", "com.axis.recipient.tcp", "period").Label);
        Assert.Equal("Host", ActionCatalog.Field("com.axis.action.unlimited.notification.tcp", "com.axis.recipient.tcp", "host").Label);
        Assert.Equal(FieldKinds.Hidden, ActionCatalog.Field("com.axis.action.unlimited.notification.tcp", "com.axis.recipient.tcp", "qos").Kind);
        Assert.Equal(("Upload path", FieldKinds.Text), (ActionCatalog.Field("com.example.unknown", null, "upload_path").Label, ActionCatalog.Field("com.example.unknown", null, "upload_path").Kind));
        Assert.Equal(FieldKinds.Password, ActionCatalog.Field("com.example.unknown", null, "proxy_password").Kind);
        Assert.Equal(["message", "period", "zzz"], ActionCatalog.Order("com.axis.action.unlimited.notification.tcp", null, ["zzz", "period", "message"]));
    }
}
