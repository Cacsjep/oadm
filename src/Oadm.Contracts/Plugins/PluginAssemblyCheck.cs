using System.Reflection;
using System.Runtime.Loader;

namespace Oadm.Contracts.Plugins;

/// <summary>
/// The check behind <c>--self-check</c> (docs/spec/packaging.md): follows every assembly a plugin part references, through the plugin's
/// own load context, as a real run would. A package that <c>dotnet publish</c> left out is also missing from the plugin's deps.json, so
/// only loading finds it (Send email without MailKit in 4.11.0: the build worked, an installed server failed).
/// </summary>
public static class PluginAssemblyCheck
{
    /// <summary>
    /// What cannot be loaded; empty when the part has everything it needs. <paramref name="skip"/>: references not to follow (a client
    /// part may use the shared models of its server part, whose libraries the client never loads).
    /// </summary>
    public static IReadOnlyList<string> Problems(Assembly part, AssemblyLoadContext context, Func<AssemblyName, bool>? skip = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Problems(part, context.LoadFromAssemblyName, a => AssemblyLoadContext.GetLoadContext(a) == context, skip); // the host's are checked by running it
    }

    /// <summary>The walk: <paramref name="follow"/> says whose references count too.</summary>
    public static IReadOnlyList<string> Problems(Assembly part, Func<AssemblyName, Assembly> load, Func<Assembly, bool> follow, Func<AssemblyName, bool>? skip = null)
    {
        ArgumentNullException.ThrowIfNull(part);
        ArgumentNullException.ThrowIfNull(load);
        ArgumentNullException.ThrowIfNull(follow);
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { part.GetName().Name ?? string.Empty };
        var waiting = new Queue<Assembly>();
        waiting.Enqueue(part);
        while (waiting.TryDequeue(out var assembly))
        {
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                if (reference.Name is null || !seen.Add(reference.Name) || skip?.Invoke(reference) == true)
                {
                    continue;
                }

                try
                {
                    var loaded = load(reference);
                    if (follow(loaded))
                    {
                        waiting.Enqueue(loaded);
                    }
                }
                catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
                {
                    problems.Add($"{reference.Name} is missing, {assembly.GetName().Name} needs it");
                }
            }
        }

        try
        {
            _ = part.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            problems.Add(ex.LoaderExceptions.FirstOrDefault()?.Message ?? ex.Message);
        }

        return problems;
    }
}
