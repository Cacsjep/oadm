using Oadm.Contracts.Plugins;

namespace Oadm.Client.Plugins;

/// <summary>
/// <c>Oadm.Client --self-check</c>: without a window, checks what this published client needs to run: the video decoder, the drawing
/// libraries and every assembly of every plugin next to the exe. Prints each problem and returns 1 (release.yml, smoke tests).
/// </summary>
internal static class ClientSelfCheck
{
    public const string Argument = "--self-check";

    /// <summary>The unpacked add-ons to check too.</summary>
    public static IReadOnlyList<string> AddOnFolders(string[] args) =>
        [.. args.Skip(1).Where((_, i) => string.Equals(args[i], "--add-on", StringComparison.OrdinalIgnoreCase))];

    public static int Run(TextWriter output, string? appFolder = null, IReadOnlyList<string>? addOns = null)
    {
        var problems = new List<string>();
        if (LiveView.DecoderSelfCheck.Run(output) != 0)
        {
            problems.Add("The video decoder does not work.");
        }

        Check(problems, output, "Skia", static () =>
        {
            using var bitmap = new SkiaSharp.SKBitmap(8, 8);
        });
        Check(problems, output, "HarfBuzz", static () =>
        {
            using var buffer = new HarfBuzzSharp.Buffer();
        });

        var checkedParts = 0;
        var root = Path.Combine(appFolder ?? AppContext.BaseDirectory, "plugins");
        var bundled = (Directory.Exists(root) ? Directory.GetDirectories(root) : []).Order(StringComparer.Ordinal).Select(f => (Folder: f, AddOn: false));
        foreach (var (folder, addOn) in bundled.Concat((addOns ?? []).Select(f => (Folder: f, AddOn: true))))
        {
            foreach (var dll in Directory.GetFiles(folder, "*.Client.dll").Order(StringComparer.Ordinal))
            {
                checkedParts++;
                var name = Path.GetFileName(folder.TrimEnd('/', '\\'));
                var context = new PluginLoadContext(Path.GetFullPath(dll), addOn);
                try
                {
                    problems.AddRange(PluginAssemblyCheck.Problems(context.LoadWithoutLock(Path.GetFullPath(dll)), context, IsServerPart).Select(p => $"{name}: {p}"));
                }
#pragma warning disable CA1031 // Every failure of the check is a line of its report.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    problems.Add($"{name}: {ex.Message}");
                }
                finally
                {
                    context.Unload();
                }
            }
        }

        if (checkedParts == 0)
        {
            problems.Add("No plugins next to the exe.");
        }

        output.WriteLine($"Plugins checked: {checkedParts}");
        foreach (var problem in problems)
        {
            output.WriteLine("PROBLEM " + problem);
        }

        return problems.Count == 0 ? 0 : 1;
    }

    private static bool IsServerPart(System.Reflection.AssemblyName reference) =>
        reference.Name?.EndsWith(".Server", StringComparison.Ordinal) == true;

    private static void Check(List<string> problems, TextWriter output, string name, Action load)
    {
        try
        {
            load();
            output.WriteLine(name + ": ok");
        }
#pragma warning disable CA1031 // Every failure of the check is a line of its report.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            problems.Add($"{name}: {ex.Message}");
        }
    }
}
