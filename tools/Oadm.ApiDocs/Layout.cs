using System.Globalization;
using System.Text;

using Microsoft.CodeAnalysis;

namespace Oadm.ApiDocs;

/// <summary>
/// Where every type and member of the reference lives: the page path of each type and the heading anchor of each
/// member, computed before any page is written so pages can link to each other.
/// </summary>
internal sealed class Layout
{
    /// <summary>The reference's folder below the docs root (and its URL path).</summary>
    public const string Root = "api";

    private readonly Dictionary<ISymbol, string> _anchors = new(SymbolEqualityComparer.Default);
    private readonly string _base;

    public Layout(ApiModel model, string basePath)
    {
        Model = model;
        // "/" (the site at the domain root) stays "/", never "//" (a link to another host).
        string trimmed = basePath.Trim('/');
        _base = trimmed.Length == 0 ? "/" : "/" + trimmed + "/";
        foreach (var type in model.Types)
        {
            // The same headings in the same order as the page (Pages.TypePage), so repeated names get the site's ids.
            var slugs = new Dictionary<string, int>(StringComparer.Ordinal);
            if (type.TypeKind == TypeKind.Enum)
            {
                Slug("Values", slugs);
            }

            if (Members.XamlProperties(type).Count > 0)
            {
                Slug("XAML properties", slugs);
            }

            foreach (var group in Members.Of(type))
            {
                Slug(group.Title, slugs);
                foreach (var member in group.Members)
                {
                    _anchors[member] = Slug(Members.Heading(member), slugs);
                }
            }

            foreach (var property in Members.XamlProperties(type))
            {
                foreach (var symbol in property.Symbols)
                {
                    _anchors[symbol] = "xaml-" + property.Name.ToLowerInvariant();
                }
            }
        }
    }

    public ApiModel Model { get; }

    /// <summary>"sdk/vapix" for Oadm.Sdk.Vapix.</summary>
    public static string NamespacePath(INamespaceSymbol ns)
    {
        var name = ns.ToDisplayString();
        if (name.StartsWith("Oadm.", StringComparison.Ordinal))
        {
            name = name["Oadm.".Length..];
        }

        return name.ToLowerInvariant().Replace('.', '/');
    }

    /// <summary>"sdk/vapix/ivapixclient" (generic arity as "-1", nested types joined with "-").</summary>
    public static string TypePath(INamedTypeSymbol type)
    {
        var parts = new List<string>();
        for (var t = type; t is not null; t = t.ContainingType)
        {
            parts.Insert(0, t.Arity > 0 ? $"{t.Name}-{t.Arity.ToString(CultureInfo.InvariantCulture)}" : t.Name);
        }

        return NamespacePath(type.ContainingNamespace) + "/" + string.Join("-", parts).ToLowerInvariant();
    }

    /// <summary>The site URL of a documented type, member or namespace page; null for anything else.</summary>
    public string? Url(ISymbol symbol)
    {
        switch (symbol)
        {
            case INamedTypeSymbol type when Model.TypeSet.Contains(type.OriginalDefinition):
                return _base + Root + "/" + TypePath(type.OriginalDefinition) + "/";
            case INamespaceSymbol ns when Model.Types.Any(t => SymbolEqualityComparer.Default.Equals(t.ContainingNamespace, ns)):
                return _base + Root + "/" + NamespacePath(ns) + "/";
            case { ContainingType: { } owner } when Model.TypeSet.Contains(owner.OriginalDefinition):
                var page = _base + Root + "/" + TypePath(owner.OriginalDefinition) + "/";
                return _anchors.TryGetValue(symbol.OriginalDefinition, out var anchor) ? page + "#" + anchor : page;
            default:
                return ExternalUrl(symbol);
        }
    }

    /// <summary>The page of another docs page of the site, e.g. "dev/tasks/".</summary>
    public string SitePage(string path) => _base + path.TrimStart('/');

    /// <summary>Microsoft's API docs for .NET types (System.*, Microsoft.Extensions.*); null for other libraries.</summary>
    private static string? ExternalUrl(ISymbol symbol)
    {
        var type = symbol as INamedTypeSymbol ?? symbol.ContainingType;
        if (type is null)
        {
            return null;
        }

        type = type.OriginalDefinition;
        var ns = type.ContainingNamespace?.ToDisplayString() ?? "";
        if (!(ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal) || ns.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal)))
        {
            return null;
        }

        var name = new StringBuilder(ns);
        foreach (var t in Chain(type))
        {
            name.Append('.').Append(t.Name);
            if (t.Arity > 0)
            {
                name.Append('-').Append(t.Arity.ToString(CultureInfo.InvariantCulture));
            }
        }

        return "https://learn.microsoft.com/dotnet/api/" + name.ToString().ToLowerInvariant();
    }

    private static List<INamedTypeSymbol> Chain(INamedTypeSymbol type)
    {
        var list = new List<INamedTypeSymbol>();
        for (var t = type; t is not null; t = t.ContainingType)
        {
            list.Insert(0, t);
        }

        return list;
    }

    /// <summary>The heading id the docs site gives a heading (github-slugger), with "-1", "-2" for repeated headings.</summary>
    public static string Slug(string heading, Dictionary<string, int> seen)
    {
        var slug = new StringBuilder();
        foreach (var c in heading.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_')
            {
                slug.Append(c);
            }
            else if (c == ' ')
            {
                slug.Append('-');
            }
        }

        var text = slug.ToString();
        if (seen.TryGetValue(text, out var count))
        {
            seen[text] = count + 1;
            return text + "-" + (count + 1).ToString(CultureInfo.InvariantCulture);
        }

        seen[text] = 0;
        return text;
    }
}
