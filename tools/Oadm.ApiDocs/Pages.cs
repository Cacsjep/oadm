using System.Globalization;
using System.Text;
using System.Text.Json;

using Microsoft.CodeAnalysis;

namespace Oadm.ApiDocs;

/// <summary>The generated reference: Markdown pages by path below the docs root, and the sidebar as JSON.</summary>
internal sealed record ApiDocs(IReadOnlyDictionary<string, string> Pages, string SidebarJson);

/// <summary>Writes the reference pages: an overview, one page per namespace and one per type.</summary>
internal sealed class Pages
{
    /// <summary>Member signatures: "Task&lt;HttpResponseMessage&gt; SendAsync(HttpRequestMessage request, CancellationToken ct)".</summary>
    private static readonly SymbolDisplayFormat SignatureFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeTypeConstraints | SymbolDisplayGenericsOptions.IncludeVariance,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeType | SymbolDisplayMemberOptions.IncludeModifiers
            | SymbolDisplayMemberOptions.IncludeRef | SymbolDisplayMemberOptions.IncludeConstantValue,
        delegateStyle: SymbolDisplayDelegateStyle.NameAndSignature,
        extensionMethodStyle: SymbolDisplayExtensionMethodStyle.StaticMethod,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName | SymbolDisplayParameterOptions.IncludeDefaultValue
            | SymbolDisplayParameterOptions.IncludeParamsRefOut | SymbolDisplayParameterOptions.IncludeExtensionThis,
        propertyStyle: SymbolDisplayPropertyStyle.ShowReadWriteDescriptor,
        kindOptions: SymbolDisplayKindOptions.IncludeMemberKeyword,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
            | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers | SymbolDisplayMiscellaneousOptions.AllowDefaultLiteral);

    /// <summary>Type names in signatures and tables.</summary>
    private static readonly SymbolDisplayFormat TypeFormat = SignatureFormat.WithMemberOptions(SymbolDisplayMemberOptions.None);

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly ApiModel _model;
    private readonly Layout _layout;
    private readonly DocComment _docs;
    private readonly Dictionary<INamedTypeSymbol, List<ISymbol>> _usedBy;

    public Pages(ApiModel model, string basePath)
    {
        _model = model;
        _layout = new Layout(model, basePath);
        _docs = new DocComment(_layout);
        _usedBy = UsedBy(model);
    }

    public static ApiDocs Generate(ApiModel model, string basePath) => new Pages(model, basePath).Write();

    private ApiDocs Write()
    {
        var pages = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            [Layout.Root + "/index.md"] = Overview(),
        };
        foreach (var group in Namespaces())
        {
            pages[$"{Layout.Root}/{Layout.NamespacePath(group.Key)}/index.md"] = NamespacePage(group.Key, group.Value);
            foreach (var type in group.Value)
            {
                pages[$"{Layout.Root}/{Layout.TypePath(type)}.md"] = TypePage(type);
            }
        }

        return new ApiDocs(pages, Sidebar());
    }

    private List<KeyValuePair<INamespaceSymbol, List<INamedTypeSymbol>>> Namespaces() =>
        _model.Types
            .GroupBy(t => t.ContainingNamespace, SymbolEqualityComparer.Default)
            .Select(g => new KeyValuePair<INamespaceSymbol, List<INamedTypeSymbol>>((INamespaceSymbol)g.Key!, [.. g]))
            .OrderBy(g => g.Key.ToDisplayString(), StringComparer.Ordinal)
            .ToList();

    private string Overview()
    {
        var sb = new StringBuilder();
        FrontMatter(sb, "SDK reference", "Every public type of the OADM plugin SDK with its members and their comments.");
        sb.Append("Every public type of the plugin SDK: `Oadm.Sdk` (server parts, no UI) and `Oadm.Sdk.Client` (dialogs, pages, ")
          .Append("toolbar entries and the shared controls). Generated from the source of `main`; the guide pages explain how ")
          .Append("the parts fit together, starting with the [developer overview](").Append(_layout.SitePage("dev/overview/")).Append(").\n\n");
        sb.Append("**Default implementation** marks an interface member with a body in the SDK. In an interface your plugin ")
          .Append("implements (`ITaskPlugin`), you may leave it out. In an interface OADM implements (`ICorePluginContext`), an ")
          .Append("older OADM answers with that default: null, an empty list or `NotSupportedException`, as the member says.\n\n");
        foreach (var group in Namespaces())
        {
            sb.Append("## ").Append(group.Key.ToDisplayString()).Append("\n\n");
            if (Guides.Namespaces.TryGetValue(group.Key.ToDisplayString(), out var about))
            {
                sb.Append(about).Append("\n\n");
            }

            TypeTable(sb, group.Value);
        }

        return sb.ToString();
    }

    private string NamespacePage(INamespaceSymbol ns, List<INamedTypeSymbol> types)
    {
        var name = ns.ToDisplayString();
        var sb = new StringBuilder();
        Guides.Namespaces.TryGetValue(name, out var about);
        FrontMatter(sb, name, about ?? $"The public types of {name}.");
        if (about is not null)
        {
            sb.Append(about).Append("\n\n");
        }

        foreach (var kind in new[] { "Interfaces", "Classes", "Records", "Structs", "Enums", "Delegates" })
        {
            var list = types.Where(t => KindGroup(t) == kind).ToList();
            if (list.Count > 0)
            {
                sb.Append("## ").Append(kind).Append("\n\n");
                TypeTable(sb, list);
            }
        }

        return sb.ToString();
    }

    private void TypeTable(StringBuilder sb, IEnumerable<INamedTypeSymbol> types)
    {
        sb.Append("| Type | Description |\n|---|---|\n");
        foreach (var type in types)
        {
            sb.Append("| [").Append(DocComment.Code(ApiModel.TypeName(type))).Append("](").Append(_layout.Url(type)).Append(") | ")
              .Append(Cell(_docs.FirstSentence(type))).Append(" |\n");
        }

        sb.Append('\n');
    }

    private string TypePage(INamedTypeSymbol type)
    {
        var docs = _docs.Of(type);
        var sb = new StringBuilder();
        FrontMatter(sb, ApiModel.TypeName(type), _docs.FirstSentence(type) is { Length: > 0 } first ? first : $"{Kind(type)} in {type.ContainingNamespace.ToDisplayString()}.");

        sb.Append(Kind(type)).Append(" in [").Append(DocComment.Code(type.ContainingNamespace.ToDisplayString())).Append("](")
          .Append(_layout.Url(type.ContainingNamespace)).Append(") · ").Append(type.ContainingAssembly.Name).Append(".dll");
        if (Obsolete(type) is { } obsolete)
        {
            sb.Append(" · **Obsolete**").Append(obsolete.Length > 0 ? ": " + DocComment.Escape(obsolete) : "");
        }

        sb.Append("\n\n");
        if (docs.Summary.Length > 0)
        {
            sb.Append(docs.Summary).Append("\n\n");
        }

        sb.Append("```csharp\n").Append(Declaration(type)).Append("\n```\n\n");
        Relations(sb, type);
        if (Guides.ForType(type.Name) is { } guide)
        {
            sb.Append("Guide: [").Append(guide.Title).Append("](").Append(_layout.SitePage(guide.Path)).Append(")\n\n");
        }

        AppendList(sb, "Type parameters", docs.TypeParameters.Select(p => (DocComment.Code(p.Name), p.Text)));
        AppendList(sb, "Parameters", docs.Parameters.Select(p => (DocComment.Code(p.Name), p.Text)));
        if (docs.Remarks.Length > 0)
        {
            sb.Append(docs.Remarks).Append("\n\n");
        }

        if (docs.Example.Length > 0)
        {
            sb.Append("**Example**\n\n").Append(docs.Example).Append("\n\n");
        }

        if (type.TypeKind == TypeKind.Enum)
        {
            EnumValues(sb, type);
        }

        XamlTable(sb, type);
        foreach (var group in Members.Of(type))
        {
            sb.Append("## ").Append(group.Title).Append("\n\n");
            foreach (var member in group.Members)
            {
                Member(sb, member);
            }
        }

        UsedByList(sb, type);
        return sb.ToString();
    }

    private void Member(StringBuilder sb, ISymbol member)
    {
        var docs = _docs.Of(member);
        sb.Append("### ").Append(Members.Heading(member)).Append("\n\n");
        sb.Append("```csharp\n").Append(Signature(member)).Append("\n```\n\n");
        var notes = new List<string>();
        if (IsDefaultInterfaceMember(member))
        {
            notes.Add("**Default implementation**");
        }

        if (member.DeclaredAccessibility is Accessibility.Protected or Accessibility.ProtectedOrInternal)
        {
            notes.Add("**Protected**: for classes that derive from it");
        }

        if (Obsolete(member) is { } obsolete)
        {
            notes.Add("**Obsolete**" + (obsolete.Length > 0 ? ": " + DocComment.Escape(obsolete) : ""));
        }

        if (notes.Count > 0)
        {
            sb.Append(string.Join(" · ", notes)).Append("\n\n");
        }

        if (docs.Summary.Length > 0)
        {
            sb.Append(docs.Summary).Append("\n\n");
        }

        var parameters = member is IMethodSymbol method
            ? method.Parameters.Select(p => (DocComment.Code(p.Name), docs.Parameters.FirstOrDefault(d => d.Name == p.Name).Text ?? "")).ToList()
            : [];
        if (parameters.Any(p => p.Item2.Length > 0))
        {
            AppendList(sb, "Parameters", parameters);
        }

        AppendList(sb, "Type parameters", docs.TypeParameters.Select(p => (DocComment.Code(p.Name), p.Text)));
        if (docs.Returns.Length > 0)
        {
            sb.Append("**Returns** ").Append(docs.Returns).Append("\n\n");
        }

        if (docs.Value.Length > 0)
        {
            sb.Append("**Value** ").Append(docs.Value).Append("\n\n");
        }

        AppendList(sb, "Exceptions", docs.Exceptions);
        if (docs.Remarks.Length > 0)
        {
            sb.Append(docs.Remarks).Append("\n\n");
        }

        if (docs.Example.Length > 0)
        {
            sb.Append("**Example**\n\n").Append(docs.Example).Append("\n\n");
        }
    }

    private static void AppendList(StringBuilder sb, string title, IEnumerable<(string Name, string Text)> items)
    {
        var list = items.ToList();
        if (list.Count == 0)
        {
            return;
        }

        sb.Append("**").Append(title).Append("**\n\n");
        foreach (var (name, text) in list)
        {
            sb.Append("- ").Append(name).Append(text.Length > 0 ? ": " + Indent(text) : "").Append('\n');
        }

        sb.Append('\n');
    }

    private void EnumValues(StringBuilder sb, INamedTypeSymbol type)
    {
        sb.Append("## Values\n\n| Name | Value | Description |\n|---|---|---|\n");
        foreach (var field in type.GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue))
        {
            sb.Append("| `").Append(field.Name).Append("` | ").Append(System.Convert.ToString(field.ConstantValue, CultureInfo.InvariantCulture))
              .Append(" | ").Append(Cell(_docs.Of(field).Summary)).Append(" |\n");
        }

        sb.Append('\n');
    }

    private void XamlTable(StringBuilder sb, INamedTypeSymbol type)
    {
        var properties = Members.XamlProperties(type);
        if (properties.Count == 0)
        {
            return;
        }

        sb.Append("## XAML properties\n\n");
        sb.Append(properties.Any(p => p.IsAttached)
            ? "Attached properties are set on another control: `ui:" + type.Name + ".Name=\"...\"`.\n\n"
            : "Set them in XAML (`<ui:" + type.Name + " Name=\"...\" />`) or bind them.\n\n");
        sb.Append("| Name | Type | Description |\n|---|---|---|\n");
        foreach (var property in properties)
        {
            sb.Append("| <span id=\"xaml-").Append(property.Name.ToLowerInvariant()).Append("\"></span>`").Append(property.Name).Append('`')
              .Append(property.IsAttached ? " (attached)" : "").Append(" | ")
              .Append(property.IsEvent ? "event" : property.Type is null ? "" : TypeLink(property.Type))
              .Append(" | ").Append(Cell(_docs.Of(property.Documented).Summary)).Append(" |\n");
        }

        sb.Append('\n');
    }

    private void Relations(StringBuilder sb, INamedTypeSymbol type)
    {
        var lines = new List<string>();
        if (type.TypeKind == TypeKind.Class && type.BaseType is { SpecialType: not SpecialType.System_Object } baseType && !Members.IsRecord(type))
        {
            lines.Add("Inherits " + TypeLink(baseType));
        }

        var interfaces = type.Interfaces.Where(i => !(Members.IsRecord(type) && i.Name == "IEquatable")).ToList();
        if (interfaces.Count > 0)
        {
            lines.Add((type.TypeKind == TypeKind.Interface ? "Extends " : "Implements ") + string.Join(", ", interfaces.Select(TypeLink)));
        }

        var derived = _model.Types
            .Where(t => SymbolEqualityComparer.Default.Equals(t.BaseType?.OriginalDefinition, type) || t.Interfaces.Any(i => SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, type)))
            .ToList();
        if (derived.Count > 0)
        {
            lines.Add((type.TypeKind == TypeKind.Interface ? "Extended or implemented by " : "Derived ") + string.Join(", ", derived.Select(t => TypeLink(t))));
        }

        if (lines.Count > 0)
        {
            sb.Append(string.Join("<br/>\n", lines)).Append("\n\n");
        }
    }

    private void UsedByList(StringBuilder sb, INamedTypeSymbol type)
    {
        if (!_usedBy.TryGetValue(type, out var users) || users.Count == 0)
        {
            return;
        }

        const int max = 20;
        sb.Append("## Used by\n\n");
        foreach (var user in users.Take(max))
        {
            sb.Append("- [").Append(DocComment.Code(DocComment.DisplayName(user))).Append("](").Append(_layout.Url(user)).Append(")\n");
        }

        if (users.Count > max)
        {
            sb.Append("- and ").Append((users.Count - max).ToString(CultureInfo.InvariantCulture)).Append(" more\n");
        }

        sb.Append('\n');
    }

    /// <summary>For every documented type: the members of other documented types whose signature mentions it.</summary>
    private static Dictionary<INamedTypeSymbol, List<ISymbol>> UsedBy(ApiModel model)
    {
        var result = new Dictionary<INamedTypeSymbol, List<ISymbol>>(SymbolEqualityComparer.Default);
        foreach (var owner in model.Types)
        {
            foreach (var member in Members.Of(owner).SelectMany(g => g.Members))
            {
                var mentioned = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
                foreach (var type in SignatureTypes(member))
                {
                    Collect(type, mentioned);
                }

                foreach (var type in mentioned.Where(t => model.TypeSet.Contains(t) && !SymbolEqualityComparer.Default.Equals(t, owner)))
                {
                    if (!result.TryGetValue(type, out var list))
                    {
                        result[type] = list = [];
                    }

                    list.Add(member);
                }
            }
        }

        foreach (var list in result.Values)
        {
            list.Sort((a, b) => string.CompareOrdinal(DocComment.DisplayName(a), DocComment.DisplayName(b)));
        }

        return result;

        static void Collect(ITypeSymbol type, HashSet<INamedTypeSymbol> set)
        {
            switch (type)
            {
                case INamedTypeSymbol named:
                    set.Add(named.OriginalDefinition);
                    foreach (var argument in named.TypeArguments)
                    {
                        Collect(argument, set);
                    }

                    break;
                case IArrayTypeSymbol array:
                    Collect(array.ElementType, set);
                    break;
            }
        }
    }

    private static IEnumerable<ITypeSymbol> SignatureTypes(ISymbol member) => member switch
    {
        IMethodSymbol m => m.Parameters.Select(p => p.Type).Append(m.ReturnType),
        IPropertySymbol p => p.Parameters.Select(x => x.Type).Append(p.Type),
        IFieldSymbol f => [f.Type],
        IEventSymbol e => [e.Type],
        _ => [],
    };

    private string Sidebar()
    {
        var items = new List<object> { new { label = "Overview", slug = Layout.Root } };
        foreach (var group in Namespaces())
        {
            var children = new List<object> { new { label = "Overview", slug = $"{Layout.Root}/{Layout.NamespacePath(group.Key)}" } };
            children.AddRange(group.Value.Select(t => new { label = ApiModel.TypeName(t), slug = $"{Layout.Root}/{Layout.TypePath(t)}" }));
            items.Add(new { label = SidebarLabel(group.Key), collapsed = true, items = children });
        }

        return JsonSerializer.Serialize(items, Indented) + "\n";
    }

    /// <summary>"Client.Controls" for Oadm.Sdk.Client.Controls: the sidebar is narrow, the page title keeps the full name.</summary>
    private static string SidebarLabel(INamespaceSymbol ns)
    {
        var name = ns.ToDisplayString();
        return name.StartsWith("Oadm.Sdk.", StringComparison.Ordinal) ? name["Oadm.Sdk.".Length..] : name;
    }

    private string TypeLink(ITypeSymbol type)
    {
        var text = DocComment.Code(type.ToDisplayString(TypeFormat));
        return type is INamedTypeSymbol named && _layout.Url(named) is { } url ? $"[{text}]({url})" : text;
    }

    private static string Signature(ISymbol member)
    {
        var text = member.ToDisplayString(SignatureFormat);
        if (member.ContainingType?.TypeKind == TypeKind.Interface)
        {
            // Interface members are public and abstract or have a body; the modifiers say nothing to a reader.
            foreach (var modifier in new[] { "abstract ", "virtual " })
            {
                text = text.Replace(modifier, "", StringComparison.Ordinal);
            }
        }

        return text;
    }

    private static string Declaration(INamedTypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Delegate && type.DelegateInvokeMethod is { } invoke)
        {
            return "public delegate " + invoke.ReturnType.ToDisplayString(TypeFormat) + " " + type.ToDisplayString(TypeFormat)
                + "(" + string.Join(", ", invoke.Parameters.Select(p => p.ToDisplayString(SignatureFormat))) + ")";
        }

        var sb = new StringBuilder("public ");
        if (type.IsStatic)
        {
            sb.Append("static ");
        }
        else if (type.IsAbstract && type.TypeKind == TypeKind.Class)
        {
            sb.Append("abstract ");
        }
        else if (type.IsSealed && type.TypeKind == TypeKind.Class)
        {
            sb.Append("sealed ");
        }

        sb.Append(type.TypeKind switch
        {
            TypeKind.Interface => "interface",
            TypeKind.Enum => "enum",
            TypeKind.Struct => Members.IsRecord(type) ? (type.IsReadOnly ? "readonly record struct" : "record struct") : (type.IsReadOnly ? "readonly struct" : "struct"),
            _ => Members.IsRecord(type) ? "record" : "class",
        });
        sb.Append(' ').Append(type.ToDisplayString(TypeFormat.WithGenericsOptions(SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeVariance)));

        if (Members.PrimaryConstructor(type) is { } primary)
        {
            sb.Append('(').Append(string.Join(", ", primary.Parameters.Select(p => p.ToDisplayString(SignatureFormat)))).Append(')');
        }

        var bases = new List<string>();
        if (type.TypeKind == TypeKind.Class && type.BaseType is { SpecialType: not SpecialType.System_Object } baseType && !Members.IsRecord(type))
        {
            bases.Add(baseType.ToDisplayString(TypeFormat));
        }

        if (type.TypeKind == TypeKind.Enum && type.EnumUnderlyingType is { SpecialType: not SpecialType.System_Int32 } underlying)
        {
            bases.Add(underlying.ToDisplayString(TypeFormat));
        }

        bases.AddRange(type.Interfaces.Where(i => !(Members.IsRecord(type) && i.Name == "IEquatable")).Select(i => i.ToDisplayString(TypeFormat)));
        if (bases.Count > 0)
        {
            sb.Append(" : ").Append(string.Join(", ", bases));
        }

        return sb.ToString();
    }

    private static string Kind(INamedTypeSymbol type) => type.TypeKind switch
    {
        TypeKind.Interface => "Interface",
        TypeKind.Enum => "Enum",
        TypeKind.Delegate => "Delegate",
        TypeKind.Struct => Members.IsRecord(type) ? "Record struct" : "Struct",
        _ => Members.IsRecord(type) ? "Record" : type.IsStatic ? "Static class" : "Class",
    };

    private static string KindGroup(INamedTypeSymbol type) => type.TypeKind switch
    {
        TypeKind.Interface => "Interfaces",
        TypeKind.Enum => "Enums",
        TypeKind.Delegate => "Delegates",
        TypeKind.Struct => "Structs",
        _ => Members.IsRecord(type) ? "Records" : "Classes",
    };

    private static bool IsDefaultInterfaceMember(ISymbol member) =>
        member.ContainingType?.TypeKind == TypeKind.Interface && !member.IsStatic && !member.IsAbstract;

    private static string? Obsolete(ISymbol symbol) =>
        symbol.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "ObsoleteAttribute") is { } attribute
            ? attribute.ConstructorArguments.FirstOrDefault().Value as string ?? ""
            : null;

    private static void FrontMatter(StringBuilder sb, string title, string description)
    {
        sb.Append("---\n")
          .Append("title: ").Append(Yaml(title)).Append('\n')
          .Append("description: ").Append(Yaml(description)).Append('\n')
          .Append("editUrl: false\n")
          .Append("---\n\n")
          .Append("<!-- Generated by tools/Oadm.ApiDocs from the SDK doc comments. Do not edit: change the comment in the source. -->\n\n");
    }

    private static string Yaml(string text) => "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    /// <summary>Markdown for a table cell: one line, pipes escaped.</summary>
    private static string Cell(string markdown)
    {
        var text = markdown.Replace("\r", "", StringComparison.Ordinal).Replace("\n\n", "<br/>", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        return text.Contains("```", StringComparison.Ordinal) ? DocComment.Escape(DocComment.Plain(markdown)) : text;
    }

    /// <summary>Continuation lines of a list item indented, so paragraphs and code stay inside the item.</summary>
    private static string Indent(string markdown) => markdown.Replace("\n", "\n  ", StringComparison.Ordinal);
}
