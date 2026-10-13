using Microsoft.CodeAnalysis;

namespace Oadm.ApiDocs;

/// <summary>A section of a type page ("Methods") and its members in source order of kind, then by name.</summary>
internal sealed record MemberGroup(string Title, IReadOnlyList<ISymbol> Members);

/// <summary>
/// A property of a control as XAML sees it: the <c>StyledProperty</c> / <c>DirectProperty</c> / attached property field
/// and its CLR property or Get / Set methods, shown once in the "XAML properties" table.
/// </summary>
internal sealed record XamlProperty(string Name, ITypeSymbol? Type, bool IsAttached, bool IsEvent, IReadOnlyList<ISymbol> Symbols)
{
    /// <summary>The symbol whose doc comment describes it: the CLR property, else a Get / Set method, else the field.</summary>
    public ISymbol Documented => Symbols.FirstOrDefault(s => s is IPropertySymbol && HasDoc(s))
        ?? Symbols.FirstOrDefault(s => s is IMethodSymbol && HasDoc(s))
        ?? Symbols.FirstOrDefault(HasDoc)
        ?? Symbols[0];

    private static bool HasDoc(ISymbol symbol) => !string.IsNullOrWhiteSpace(symbol.GetDocumentationCommentXml());
}

internal static class Members
{
    private static readonly HashSet<string> RecordNoise = new(StringComparer.Ordinal)
    {
        "Equals", "GetHashCode", "ToString", "PrintMembers", "Deconstruct", "op_Equality", "op_Inequality", "EqualityContract",
    };

    /// <summary>The sections of a type page with the members they list (XAML properties excluded).</summary>
    public static IReadOnlyList<MemberGroup> Of(INamedTypeSymbol type)
    {
        if (type.TypeKind is TypeKind.Enum or TypeKind.Delegate)
        {
            return [];
        }

        var xaml = XamlProperties(type).SelectMany(p => p.Symbols).ToHashSet(SymbolEqualityComparer.Default);
        var members = type.GetMembers()
            .Where(ApiModel.IsVisibleMember)
            .Where(m => !xaml.Contains(m))
            .Where(m => !(IsRecord(type) && IsRecordNoise(type, m)))
            .Where(m => m is not IMethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise or MethodKind.StaticConstructor or MethodKind.Destructor })
            .Where(m => m is not INamedTypeSymbol)
            .Where(m => !m.IsOverride) // framework plumbing (OnPropertyChanged, Render, ToString): documented by the base
            .Where(m => !SymbolEqualityComparer.Default.Equals(m, PrimaryConstructor(type)))
            .Where(m => !(m is IMethodSymbol { MethodKind: MethodKind.Constructor, Parameters.Length: 0 } && string.IsNullOrWhiteSpace(m.GetDocumentationCommentXml())))
            .ToList();

        var groups = new List<MemberGroup>();
        Add("Constructors", members.Where(m => m is IMethodSymbol { MethodKind: MethodKind.Constructor }));
        Add("Properties", members.Where(m => m is IPropertySymbol));
        Add("Methods", members.Where(m => m is IMethodSymbol { MethodKind: not MethodKind.Constructor }));
        Add("Events", members.Where(m => m is IEventSymbol));
        Add("Fields", members.Where(m => m is IFieldSymbol));
        return groups;

        void Add(string title, IEnumerable<ISymbol> items)
        {
            var list = items
                .OrderBy(m => m.Name, StringComparer.Ordinal)
                .ThenBy(m => m is IMethodSymbol method ? method.Parameters.Length : 0)
                .ToList();
            if (list.Count > 0)
            {
                groups.Add(new MemberGroup(title, list));
            }
        }
    }

    /// <summary>
    /// The primary constructor of a positional record (shown in the declaration, its parameters below it), or null.
    /// </summary>
    public static IMethodSymbol? PrimaryConstructor(INamedTypeSymbol type) =>
        IsRecord(type) && type.InstanceConstructors.FirstOrDefault(c => c.DeclaredAccessibility == Accessibility.Public && c.Parameters.Length > 0
                && !(c.Parameters.Length == 1 && SymbolEqualityComparer.Default.Equals(c.Parameters[0].Type, type))) is { Parameters.Length: > 0 } primary
            && primary.Parameters.All(p => type.GetMembers(p.Name).Any(m => m is IPropertySymbol))
            ? primary
            : null;

    /// <summary>
    /// A record or record struct. Roslyn knows a compiled record by its clone method, which record structs do not have; both
    /// have the generated PrintMembers.
    /// </summary>
    public static bool IsRecord(INamedTypeSymbol type) =>
        type.IsRecord || type.GetMembers("PrintMembers").Any(m => m is IMethodSymbol);

    /// <summary>The page heading of a member: its name ("SendAsync"), the type name for constructors.</summary>
    public static string Heading(ISymbol member) =>
        member is IMethodSymbol { MethodKind: MethodKind.Constructor } ? member.ContainingType.Name : member.Name;

    /// <summary>The XAML properties of an Avalonia control (empty for other types).</summary>
    public static IReadOnlyList<XamlProperty> XamlProperties(INamedTypeSymbol type)
    {
        if (!DerivesFrom(type, "Avalonia.AvaloniaObject") && !type.IsStatic)
        {
            return [];
        }

        var result = new List<XamlProperty>();
        foreach (var field in type.GetMembers().OfType<IFieldSymbol>().Where(f => f.IsStatic && ApiModel.IsVisibleMember(f)))
        {
            var isEvent = DerivesFrom(field.Type, "Avalonia.Interactivity.RoutedEvent");
            var isProperty = DerivesFrom(field.Type, "Avalonia.AvaloniaProperty");
            if (!isEvent && !isProperty)
            {
                continue;
            }

            var suffix = isEvent ? "Event" : "Property";
            if (!field.Name.EndsWith(suffix, StringComparison.Ordinal))
            {
                continue;
            }

            var name = field.Name[..^suffix.Length];
            var isAttached = field.Type.Name.StartsWith("AttachedProperty", StringComparison.Ordinal);
            var symbols = new List<ISymbol> { field };
            symbols.AddRange(type.GetMembers(name).Where(m => m is IPropertySymbol or IEventSymbol && ApiModel.IsVisibleMember(m)));
            symbols.AddRange(type.GetMembers("Get" + name).Concat(type.GetMembers("Set" + name)).Where(m => m is IMethodSymbol && ApiModel.IsVisibleMember(m)));
            var valueType = field.Type is INamedTypeSymbol { IsGenericType: true } generic ? generic.TypeArguments[^1] : null;
            result.Add(new XamlProperty(name, isEvent ? null : valueType, isAttached, isEvent, symbols));
        }

        return [.. result.OrderBy(p => p.Name, StringComparer.Ordinal)];
    }

    public static bool DerivesFrom(ITypeSymbol? type, string fullName)
    {
        for (var t = type; t is not null; t = t.BaseType)
        {
            if (t.OriginalDefinition.ToDisplayString() == fullName || t.OriginalDefinition.ToDisplayString().StartsWith(fullName + "<", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A member of a generated record a reader never calls (equality, printing, the copy constructor).</summary>
    private static bool IsRecordNoise(INamedTypeSymbol record, ISymbol member) =>
        RecordNoise.Contains(member.Name)
        || member is IMethodSymbol { MethodKind: MethodKind.Constructor, Parameters.Length: 1 } ctor
            && SymbolEqualityComparer.Default.Equals(ctor.Parameters[0].Type, record);
}
