using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.DeviceConfig;

/// <summary>
/// A configuration file of AXIS Device Manager (<c>AcmDeviceParameterExport</c>, format 3): the model, AXIS OS and MAC address
/// it was made from ("Generic" in templates), and optional lists of parameters, schedules, action rules and stream profiles.
/// The format is known only from files (Axis' published hardening template and files ADM wrote from the user's devices);
/// unknown elements are ignored.
/// </summary>
public sealed record AdmConfig
{
    public const int FormatVersion = 3;

    public const string Generic = "Generic";

    public string Model { get; init; } = Generic;

    public string FirmwareVersion { get; init; } = Generic;

    public string MacAddress { get; init; } = Generic;

    public DateTimeOffset? Generated { get; init; }

    public IReadOnlyList<NameValue> Parameters { get; init; } = [];

    public IReadOnlyList<ConfigSchedule> Schedules { get; init; } = [];

    public IReadOnlyList<ConfigActionRule> Rules { get; init; } = [];

    public IReadOnlyList<ConfigStreamProfile> StreamProfiles { get; init; } = [];

    public bool IsGeneric => string.Equals(Model, Generic, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Reads and writes <see cref="AdmConfig"/> files.</summary>
public static class AdmConfigFile
{
    public const string Extension = "cfg";

    public const string NotAnAdmFile = "This is not an AXIS Device Manager configuration file.";

    /// <summary>Largest file OADM reads (the hardening template is 2 KB, a whole device about 100 KB).</summary>
    public const long MaxFileBytes = 1024L * 1024;

    private const string Root = "AcmDeviceParameterExport";

    /// <summary>"Configuration - AXIS P3265-V 12.11.77.cfg" like ADM names its files.</summary>
    public static string SuggestedFileName(string? model, string? firmware) =>
        string.Join('_', $"Configuration - {ModelNames.Display(model)} {firmware}".Trim().Split(Path.GetInvalidFileNameChars())) + "." + Extension;

    /// <summary>Parses the file; throws <see cref="FormatException"/> with <see cref="NotAnAdmFile"/> or a more exact text.</summary>
    public static AdmConfig Read(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        XElement root;
        try
        {
            root = DeviceXml.Parse(xml.TrimStart('﻿')).Root ?? throw new FormatException(NotAnAdmFile);
        }
        catch (XmlException ex)
        {
            throw new FormatException(NotAnAdmFile, ex);
        }

        if (root.Name.LocalName != Root)
        {
            throw new FormatException(NotAnAdmFile);
        }

        if (int.TryParse(Text(root, "FileFormatVersion"), NumberStyles.None, CultureInfo.InvariantCulture, out var version) && version > AdmConfig.FormatVersion)
        {
            throw new FormatException(string.Create(CultureInfo.InvariantCulture, $"The file has a newer format ({version}) than OADM reads ({AdmConfig.FormatVersion})."));
        }

        return new AdmConfig
        {
            Model = Text(root, "Model") ?? AdmConfig.Generic,
            FirmwareVersion = Text(root, "FirmwareVersion") ?? AdmConfig.Generic,
            MacAddress = Text(root, "MacAddress") ?? AdmConfig.Generic,
            Generated = DateTimeOffset.TryParse(Text(root, "GeneratedTimestamp"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null,
            Parameters = [.. Children(root, "ParameterList", "Parameter")
                .Select(p => (Name: Text(p, "Name"), Value: Text(p, "Value") ?? string.Empty))
                .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                .Select(p => new NameValue(p.Name!.Trim(), p.Value))],
            Schedules = [.. Children(root, "ScheduleList", "Schedule")
                .Select(s => new ConfigSchedule(Text(s, "Id") ?? string.Empty, Text(s, "Name") ?? string.Empty, Lines(Text(s, "ScheduleDefinition"))))
                .Where(s => s.Id.Length > 0)],
            Rules = [.. Children(root, "ActionRuleList", "ActionRule").Select(ReadRule).OfType<ConfigActionRule>()],
            StreamProfiles = [.. Children(root, "StreamProfileList", "StreamProfile")
                .Select(p => new ConfigStreamProfile(Text(p, "Name") ?? string.Empty, Text(p, "Description"), Text(p, "Parameters") ?? string.Empty)
                {
                    IsDefault = string.Equals(Text(p, "IsDefault"), "true", StringComparison.OrdinalIgnoreCase),
                })
                .Where(p => p.Name.Length > 0)],
        };
    }

    /// <summary>The file as ADM writes it: UTF-8 with BOM, CRLF, two spaces indent, empty lists left out.</summary>
    public static byte[] Write(AdmConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var root = new XElement(Root,
            new XElement("FileFormatVersion", AdmConfig.FormatVersion),
            new XElement("Model", config.Model),
            new XElement("FirmwareVersion", config.FirmwareVersion),
            new XElement("GeneratedTimestamp", (config.Generated ?? DateTimeOffset.UtcNow).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture)),
            new XElement("MacAddress", config.MacAddress));
        if (config.Parameters.Count > 0)
        {
            root.Add(new XElement("ParameterList", config.Parameters.Select(p => new XElement("Parameter", new XElement("Name", p.Name), new XElement("Value", p.Value)))));
        }

        if (config.Schedules.Count > 0)
        {
            root.Add(new XElement("ScheduleList", config.Schedules.Select(s =>
                new XElement("Schedule", new XElement("Id", s.Id), new XElement("Name", s.Name), new XElement("ScheduleDefinition", s.Definition)))));
        }

        if (config.Rules.Count > 0)
        {
            root.Add(new XElement("ActionRuleList", config.Rules.Select(WriteRule)));
        }

        if (config.StreamProfiles.Count > 0)
        {
            root.Add(new XElement("StreamProfileList", config.StreamProfiles.Select(p => new XElement("StreamProfile",
                new XElement("Name", p.Name),
                new XElement("Description", p.Description ?? string.Empty),
                new XElement("Parameters", p.Parameters),
                new XElement("IsDefault", p.IsDefault ? "true" : "false")))));
        }

        using var stream = new MemoryStream();
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            Indent = true,
            IndentChars = "  ",
            NewLineChars = "\r\n",
            NewLineHandling = NewLineHandling.Replace,
        };
        using (var writer = XmlWriter.Create(stream, settings))
        {
            new XDocument(new XDeclaration("1.0", "utf-8", "yes"), root).Save(writer);
        }

        return stream.ToArray();
    }

    private static ConfigActionRule? ReadRule(XElement rule)
    {
        var name = Text(rule, "Name");
        var action = rule.Element("PrimaryAction");
        var template = action is null ? null : Text(action, "ActionTemplate");
        if (string.IsNullOrWhiteSpace(name) || action is null || string.IsNullOrWhiteSpace(template))
        {
            return null;
        }

        var start = rule.Element("StartEvent");
        return new ConfigActionRule(
            name.Trim(),
            !string.Equals(Text(rule, "IsEnabled"), "false", StringComparison.OrdinalIgnoreCase),
            start is null || string.IsNullOrWhiteSpace(Text(start, "TopicFilter")) ? null : new ConfigEventFilter(Text(start, "TopicFilter")!, Text(start, "MessageFilter")),
            [.. (rule.Element("ConditionList")?.Elements() ?? [])
                .Where(c => !string.IsNullOrWhiteSpace(Text(c, "TopicFilter")))
                .Select(c => new ConfigEventFilter(Text(c, "TopicFilter")!, Text(c, "MessageFilter")))],
            new ConfigAction(
                Text(action, "Name") ?? template,
                template.Trim(),
                [.. Children(action, "ParameterList", "Parameter")
                    .Select(p => (Name: Text(p, "Name"), Value: Text(p, "Value") ?? string.Empty))
                    .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                    .Select(p => new NameValue(p.Name!, p.Value))])
            {
                Id = Text(action, "Id"),
            })
        {
            ActivationTimeout = string.IsNullOrWhiteSpace(Text(rule, "ActivationTimeout")) ? null : Text(rule, "ActivationTimeout")!.Trim(),
        };
    }

    private static XElement WriteRule(ConfigActionRule rule, int index) =>
        new("ActionRule",
            new XElement("Name", rule.Name),
            new XElement("IsEnabled", rule.Enabled ? "true" : "false"),
            // ADM writes an empty timeout as <ActivationTimeout />.
            new XElement("ActivationTimeout", (object?)rule.ActivationTimeout),
            rule.StartEvent is null
                ? new XElement("StartEvent")
                : new XElement("StartEvent", new XElement("TopicFilter", rule.StartEvent.Topic), new XElement("MessageFilter", rule.StartEvent.Message ?? string.Empty)),
            new XElement("ConditionList", rule.Conditions.Select(c =>
                new XElement("Condition", new XElement("TopicFilter", c.Topic), new XElement("MessageFilter", c.Message ?? string.Empty)))),
            new XElement("PrimaryAction",
                new XElement("Id", rule.Action.Id ?? (index + 1).ToString(CultureInfo.InvariantCulture)),
                new XElement("Name", rule.Action.Name),
                new XElement("ActionTemplate", rule.Action.Template),
                new XElement("ParameterList", rule.Action.Parameters.Select(p => new XElement("Parameter", new XElement("Name", p.Name), new XElement("Value", p.Value))))),
            new XElement("ScheduleList"));

    private static IEnumerable<XElement> Children(XElement parent, string list, string item) =>
        parent.Element(list)?.Elements(item) ?? [];

    private static string? Text(XElement parent, string name) => parent.Element(name)?.Value;

    /// <summary>iCalendar lines as the device wants them (LF), whatever the file used.</summary>
    private static string Lines(string? text) => (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
}
