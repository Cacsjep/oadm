using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

using Microsoft.CodeAnalysis;

namespace Oadm.ApiDocs;

/// <summary>The parts of one doc comment as Markdown: summary, remarks, parameters, returns, exceptions, example.</summary>
internal sealed record DocParts(
    string Summary,
    string Remarks,
    IReadOnlyList<(string Name, string Text)> Parameters,
    IReadOnlyList<(string Name, string Text)> TypeParameters,
    string Returns,
    string Value,
    IReadOnlyList<(string Type, string Text)> Exceptions,
    string Example)
{
    public static readonly DocParts Empty = new("", "", [], [], "", "", [], "");

    public bool IsEmpty => Summary.Length == 0 && Remarks.Length == 0 && Parameters.Count == 0 && Returns.Length == 0;
}

/// <summary>
/// Reads doc comments (also <c>&lt;inheritdoc/&gt;</c> from the implemented interface member, the overridden member or a
/// cref) and turns them into Markdown with links to the reference pages and to Microsoft's .NET docs.
/// </summary>
internal sealed partial class DocComment(Layout layout)
{
    private readonly Dictionary<ISymbol, DocParts> _cache = new(SymbolEqualityComparer.Default);

    public DocParts Of(ISymbol symbol)
    {
        symbol = symbol.OriginalDefinition;
        if (_cache.TryGetValue(symbol, out var cached))
        {
            return cached;
        }

        _cache[symbol] = DocParts.Empty; // a cycle of inheritdoc ends here
        var parts = Read(symbol);
        _cache[symbol] = parts;
        return parts;
    }

    /// <summary>The first sentence of the summary as plain text (page descriptions, overview lists).</summary>
    public string FirstSentence(ISymbol symbol)
    {
        var text = Plain(Of(symbol).Summary);
        var match = FirstSentenceRegex().Match(text);
        return match.Success ? match.Value.Trim() : text;
    }

    /// <summary>Markdown without links, code marks and emphasis, on one line.</summary>
    public static string Plain(string markdown)
    {
        var text = MarkdownLinkRegex().Replace(markdown, "$1");
        text = text.Replace("`", "", StringComparison.Ordinal).Replace("**", "", StringComparison.Ordinal);
        text = text.Replace("&lt;", "<", StringComparison.Ordinal).Replace("&gt;", ">", StringComparison.Ordinal).Replace("&amp;", "&", StringComparison.Ordinal);
        return WhitespaceRegex().Replace(text, " ").Trim();
    }

    private DocParts Read(ISymbol symbol)
    {
        var xml = symbol.GetDocumentationCommentXml(expandIncludes: true);
        if (string.IsNullOrWhiteSpace(xml))
        {
            return Inherited(symbol, null) ?? DocParts.Empty;
        }

        XElement root;
        try
        {
            root = XElement.Parse(xml, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException)
        {
            return DocParts.Empty;
        }

        var inherit = root.Element("inheritdoc");
        var baseParts = inherit is null ? null : Inherited(symbol, inherit.Attribute("cref")?.Value);
        if (inherit is not null && baseParts is null && !root.Elements().Any(e => e.Name != "inheritdoc")
            && symbol.GetDocumentationCommentId() is { } id && layout.Model.SourceDocs.TryGetValue(id, out var written))
        {
            root = XElement.Parse(written, LoadOptions.PreserveWhitespace); // a generated partial property: the written comment
        }

        string Block(string name) => root.Element(name) is { } e ? Convert(e) : "";
        var summary = Block("summary");
        var parameters = root.Elements("param").Select(e => ((string)e.Attribute("name")! ?? "", Convert(e))).ToList();
        var typeParameters = root.Elements("typeparam").Select(e => ((string)e.Attribute("name")! ?? "", Convert(e))).ToList();
        var exceptions = root.Elements("exception").Select(e => (CrefLink(e.Attribute("cref")?.Value, null), Convert(e))).ToList();
        var parts = new DocParts(summary, Block("remarks"), parameters, typeParameters, Block("returns"), Block("value"), exceptions, Block("example"));

        return baseParts is null ? parts : new DocParts(
            parts.Summary.Length > 0 ? parts.Summary : baseParts.Summary,
            parts.Remarks.Length > 0 ? parts.Remarks : baseParts.Remarks,
            parts.Parameters.Count > 0 ? parts.Parameters : baseParts.Parameters,
            parts.TypeParameters.Count > 0 ? parts.TypeParameters : baseParts.TypeParameters,
            parts.Returns.Length > 0 ? parts.Returns : baseParts.Returns,
            parts.Value.Length > 0 ? parts.Value : baseParts.Value,
            parts.Exceptions.Count > 0 ? parts.Exceptions : baseParts.Exceptions,
            parts.Example.Length > 0 ? parts.Example : baseParts.Example);
    }

    /// <summary>The doc of the member this one inherits from: the cref, the overridden member or the interface member.</summary>
    private DocParts? Inherited(ISymbol symbol, string? cref)
    {
        if (cref is not null)
        {
            return Resolve(cref) is { } target ? Of(target) : null;
        }

        ISymbol? source = symbol switch
        {
            IMethodSymbol { OverriddenMethod: { } m } => m,
            IPropertySymbol { OverriddenProperty: { } p } => p,
            IEventSymbol { OverriddenEvent: { } e } => e,
            _ => null,
        };
        if (source is null && symbol.ContainingType is { } owner)
        {
            source = owner.AllInterfaces
                .SelectMany(i => i.GetMembers(symbol.Name))
                .FirstOrDefault(m => SymbolEqualityComparer.Default.Equals(owner.FindImplementationForInterfaceMember(m), symbol));
        }

        return source is null ? null : Of(source) is { IsEmpty: false } parts ? parts : null;
    }

    private ISymbol? Resolve(string cref) =>
        DocumentationCommentId.GetFirstSymbolForDeclarationId(cref, layout.Model.Compilation);

    private string Convert(XElement element)
    {
        var sb = new StringBuilder();
        foreach (var node in element.Nodes())
        {
            Append(sb, node);
        }

        return Tidy(sb.ToString());
    }

    private void Append(StringBuilder sb, XNode node)
    {
        switch (node)
        {
            case XText text:
                sb.Append(Escape(WhitespaceRegex().Replace(text.Value, " ")));
                break;
            case XElement e:
                AppendElement(sb, e);
                break;
        }
    }

    private void AppendElement(StringBuilder sb, XElement e)
    {
        switch (e.Name.LocalName)
        {
            case "c":
                sb.Append(Code(e.Value));
                break;
            case "code":
                sb.Append("\n\n```csharp\n").Append(Unindent(e.Value)).Append("\n```\n\n");
                break;
            case "para":
                sb.Append("\n\n").Append(Inner(e)).Append("\n\n");
                break;
            case "br":
                sb.Append("<br/>");
                break;
            case "paramref":
            case "typeparamref":
                sb.Append(Code((string?)e.Attribute("name") ?? ""));
                break;
            case "see" or "seealso" when e.Attribute("langword") is { } word:
                sb.Append(Code(word.Value));
                break;
            case "see" or "seealso" when e.Attribute("href") is { } href:
                sb.Append('[').Append(e.Value.Length > 0 ? Escape(e.Value) : href.Value).Append("](").Append(href.Value).Append(')');
                break;
            case "see" or "seealso":
                sb.Append(CrefLink(e.Attribute("cref")?.Value, e.Nodes().Any() ? Inner(e) : null));
                break;
            case "list":
                var numbered = (string?)e.Attribute("type") == "number";
                sb.Append("\n\n");
                var n = 1;
                foreach (var item in e.Elements("item"))
                {
                    var term = item.Element("term") is { } t ? "**" + Inner(t) + "**" : "";
                    var description = item.Element("description") is { } d ? Inner(d) : item.Element("term") is null ? Inner(item) : "";
                    var line = term.Length > 0 && description.Length > 0 ? term + ": " + description : term + description;
                    sb.Append(numbered ? $"{n++}. " : "- ").Append(line.Trim()).Append('\n');
                }

                sb.Append('\n');
                break;
            default:
                sb.Append(Inner(e));
                break;
        }
    }

    private string Inner(XElement e)
    {
        var sb = new StringBuilder();
        foreach (var node in e.Nodes())
        {
            Append(sb, node);
        }

        return sb.ToString().Trim();
    }

    /// <summary>A link to a cref: "[`IVapixClient.SendAsync`](url)", or the name in code when it has no page.</summary>
    private string CrefLink(string? cref, string? text)
    {
        if (cref is null)
        {
            return text ?? "";
        }

        var symbol = Resolve(cref);
        var label = text ?? Code(symbol is null ? CrefName(cref) : DisplayName(symbol));
        var url = symbol is null ? null : layout.Url(symbol);
        return url is null ? label : $"[{label}]({url})";
    }

    /// <summary>"IVapixClient", "IVapixClient.SendAsync", "Task&lt;T&gt;".</summary>
    public static string DisplayName(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol type => ApiModel.TypeName(type),
        IMethodSymbol { MethodKind: MethodKind.Constructor } ctor => ApiModel.TypeName(ctor.ContainingType),
        { ContainingType: { } owner } => ApiModel.TypeName(owner) + "." + symbol.Name,
        _ => symbol.Name,
    };

    private static string CrefName(string cref)
    {
        var name = cref.Length > 2 && cref[1] == ':' ? cref[2..] : cref;
        var paren = name.IndexOf('(', StringComparison.Ordinal);
        if (paren >= 0)
        {
            name = name[..paren];
        }

        var parts = name.Split('.');
        return parts.Length >= 2 && cref.StartsWith("T:", StringComparison.Ordinal) is false ? parts[^2] + "." + parts[^1] : parts[^1];
    }

    public static string Code(string text)
    {
        text = text.Trim();
        return text.Contains('`', StringComparison.Ordinal) ? "`` " + text + " ``" : "`" + text + "`";
    }

    /// <summary>Text that Markdown would read as HTML or table syntax.</summary>
    public static string Escape(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal);

    private static string Unindent(string code)
    {
        var lines = code.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        while (lines.Count > 0 && lines[0].Trim().Length == 0)
        {
            lines.RemoveAt(0);
        }

        while (lines.Count > 0 && lines[^1].Trim().Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var indent = lines.Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
        return string.Join("\n", lines.Select(l => l.Length >= indent ? l[indent..] : l.TrimStart()));
    }

    /// <summary>Spaces at line ends and between paragraphs cleaned up.</summary>
    private static string Tidy(string markdown)
    {
        var lines = new List<string>();
        var inCode = false;
        foreach (var line in markdown.Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inCode = !inCode;
                lines.Add(line.Trim());
            }
            else
            {
                lines.Add(inCode ? line.TrimEnd() : line.Trim());
            }
        }

        var text = string.Join("\n", lines);
        text = BlankLinesRegex().Replace(text, "\n\n");
        return text.Trim();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLinesRegex();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex MarkdownLinkRegex();

    [GeneratedRegex(@"^.*?[.!?](?=\s|$)")]
    private static partial Regex FirstSentenceRegex();
}
