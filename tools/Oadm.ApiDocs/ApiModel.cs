using System.Collections.Immutable;
using System.Globalization;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Oadm.ApiDocs;

/// <summary>
/// The public surface of the documented assemblies, read with Roslyn from the built assemblies and their doc comment
/// files (exact C# signatures with nullability, no assembly is loaded into the process).
/// </summary>
internal sealed class ApiModel
{
    /// <summary>The assemblies the reference documents.</summary>
    public static readonly string[] DocumentedAssemblies = ["Oadm.Sdk", "Oadm.Sdk.Client"];

    private ApiModel(CSharpCompilation compilation, IReadOnlyList<IAssemblySymbol> assemblies, IReadOnlyList<INamedTypeSymbol> types, IReadOnlyDictionary<string, string> sourceDocs)
    {
        SourceDocs = sourceDocs;
        Compilation = compilation;
        Assemblies = assemblies;
        Types = types;
        TypeSet = types.ToImmutableHashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
    }

    public CSharpCompilation Compilation { get; }

    public IReadOnlyList<IAssemblySymbol> Assemblies { get; }

    /// <summary>Every public type, sorted by namespace and name.</summary>
    public IReadOnlyList<INamedTypeSymbol> Types { get; }

    public ImmutableHashSet<INamedTypeSymbol> TypeSet { get; }

    /// <summary>
    /// Doc comments of partial properties from the source, by documentation id. Source generators (CommunityToolkit.Mvvm's
    /// [ObservableProperty]) give the generated part an empty &lt;inheritdoc/&gt;, which replaces the written comment in the
    /// assembly's doc file.
    /// </summary>
    public IReadOnlyDictionary<string, string> SourceDocs { get; }

    /// <summary>
    /// Reads the documented assemblies (and their dependencies) from <paramref name="directory"/>; <paramref name="repository"/>
    /// (the repository root) adds the comments of partial properties from the SDK sources.
    /// </summary>
    public static ApiModel Load(string directory, string? repository = null)
    {
        var references = new List<MetadataReference>();
        var documented = new Dictionary<string, MetadataReference>(StringComparer.OrdinalIgnoreCase);

        // The framework the process runs on (the trusted platform assemblies outside the documented folder), then every
        // other managed assembly next to the documented ones.
        var folder = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var platform = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !string.Equals(Path.GetDirectoryName(Path.GetFullPath(p)), folder, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var platformNames = platform.Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in platform.Where(IsManaged))
        {
            references.Add(MetadataReference.CreateFromFile(path));
        }

        foreach (var path in Directory.EnumerateFiles(directory, "*.dll"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (platformNames.Contains(name) || !IsManaged(path))
            {
                continue;
            }

            var xml = Path.ChangeExtension(path, ".xml");
            var reference = MetadataReference.CreateFromFile(path, documentation: File.Exists(xml) ? new XmlDocs(xml) : null);
            references.Add(reference);
            if (DocumentedAssemblies.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(xml))
                {
                    throw new InvalidOperationException($"{name}.xml is missing next to {path}: build the SDK with its doc comment file.");
                }

                documented[name] = reference;
            }
        }

        var missing = DocumentedAssemblies.Where(a => !documented.ContainsKey(a)).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException($"Not found in {directory}: {string.Join(", ", missing)}.dll");
        }

        var compilation = CSharpCompilation.Create(
            "Oadm.ApiDocs.Reader",
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable)
                .WithMetadataImportOptions(MetadataImportOptions.All)); // private members too: record structs are known by PrintMembers

        var assemblies = DocumentedAssemblies
            .Select(a => (IAssemblySymbol)compilation.GetAssemblyOrModuleSymbol(documented[a])!)
            .ToList();
        var types = assemblies
            .SelectMany(a => AllTypes(a.GlobalNamespace))
            .Where(IsVisible)
            .OrderBy(t => t.ContainingNamespace.ToDisplayString(), StringComparer.Ordinal)
            .ThenBy(t => TypeName(t), StringComparer.OrdinalIgnoreCase)
            .ToList();
        var sourceDocs = repository is null
            ? new Dictionary<string, string>()
            : PartialPropertyDocs(DocumentedAssemblies.Select(a => Path.Combine(repository, "src", a)).Where(Directory.Exists));
        return new ApiModel(compilation, assemblies, types, sourceDocs);
    }

    /// <summary>The type's name as written in C#, with type parameters and containing types ("Outer.Inner&lt;T&gt;").</summary>
    public static string TypeName(INamedTypeSymbol type) => type.ToDisplayString(NameFormat);

    private static readonly SymbolDisplayFormat NameFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    /// <summary>Public, or protected in a type a plugin may derive from.</summary>
    public static bool IsVisibleMember(ISymbol member)
    {
        if (member.IsImplicitlyDeclared || member.Name.Contains('<', StringComparison.Ordinal) || member.Name.Contains('!', StringComparison.Ordinal))
        {
            return false;
        }

        return member.DeclaredAccessibility switch
        {
            Accessibility.Public => true,
            Accessibility.Protected or Accessibility.ProtectedOrInternal => member.ContainingType is { IsSealed: false, IsStatic: false },
            _ => false,
        };
    }

    private static bool IsVisible(INamedTypeSymbol type)
    {
        for (var t = type; t is not null; t = t.ContainingType)
        {
            if (t.DeclaredAccessibility != Accessibility.Public || t.Name.Contains('<', StringComparison.Ordinal) || t.Name.Contains('!', StringComparison.Ordinal))
            {
                return false;
            }
        }

        return !type.GetAttributes().Any(a => a.AttributeClass?.Name == "CompilerGeneratedAttribute");
    }

    private static IEnumerable<INamedTypeSymbol> AllTypes(INamespaceSymbol ns) =>
        ns.GetTypeMembers().SelectMany(Nested).Concat(ns.GetNamespaceMembers().SelectMany(AllTypes));

    private static IEnumerable<INamedTypeSymbol> Nested(INamedTypeSymbol type) =>
        new[] { type }.Concat(type.GetTypeMembers().SelectMany(Nested));

    /// <summary>The written doc comments of every partial property in the sources, as doc file members.</summary>
    private static Dictionary<string, string> PartialPropertyDocs(IEnumerable<string> folders)
    {
        var docs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in folders.SelectMany(f => Directory.EnumerateFiles(f, "*.cs", SearchOption.AllDirectories))
                     .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
        {
            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot();
            foreach (var property in root.DescendantNodes().OfType<PropertyDeclarationSyntax>().Where(p => p.Modifiers.Any(SyntaxKind.PartialKeyword)))
            {
                var comment = property.GetLeadingTrivia().FirstOrDefault(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia));
                if (comment == default)
                {
                    continue;
                }

                var text = string.Join("\n", comment.ToFullString().Split('\n')
                    .Select(l => l.Trim())
                    .Where(l => l.StartsWith("///", StringComparison.Ordinal))
                    .Select(l => l[3..].TrimStart()));
                var owners = property.Ancestors().OfType<BaseTypeDeclarationSyntax>().Reverse().Select(t => t.Identifier.Text);
                var ns = property.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().LastOrDefault()?.Name.ToString();
                var id = "P:" + string.Join(".", new[] { ns }.Where(n => n is not null).Concat(owners).Append(property.Identifier.Text));
                docs[id] = $"<member name=\"{id}\">{text}</member>";
            }
        }

        return docs;
    }

    /// <summary>The doc comments of one assembly from its XML file, by documentation id.</summary>
    private sealed class XmlDocs : DocumentationProvider
    {
        private readonly string _path;
        private readonly Dictionary<string, string> _members;

        public XmlDocs(string path)
        {
            _path = path;
            _members = XDocument.Load(path).Descendants("member")
                .Where(m => m.Attribute("name") is not null)
                .GroupBy(m => (string)m.Attribute("name")!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().ToString(SaveOptions.DisableFormatting), StringComparer.Ordinal);
        }

        protected override string? GetDocumentationForSymbol(string documentationMemberID, CultureInfo preferredCulture, CancellationToken cancellationToken = default) =>
            _members.GetValueOrDefault(documentationMemberID);

        public override bool Equals(object? obj) => obj is XmlDocs other && string.Equals(other._path, _path, StringComparison.OrdinalIgnoreCase);

        public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(_path);
    }

    private static bool IsManaged(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new PEReader(stream);
            return reader.HasMetadata;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }
}
