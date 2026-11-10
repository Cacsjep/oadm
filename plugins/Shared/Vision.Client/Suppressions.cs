using System.Diagnostics.CodeAnalysis;

// The folder of linked source shared by plugins is plugins/Shared (namespace Oadm.Plugins.Shared, like ContextCall); these
// types are public in each plugin's own assembly (their XAML and page APIs use them) and never meant for other languages.
[assembly: SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Scope = "namespace",
    Target = "~N:Oadm.Plugins.Shared.Vision.Client", Justification = "The namespace of the linked plugins/Shared source.")]
