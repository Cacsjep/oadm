using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Oadm.NewPlugin;

/// <summary>
/// Writes the content of the NuGet package Oadm.Plugin.Templates from the samples (<c>dotnet new oadm-task</c>,
/// <c>oadm-page</c>, <c>oadm-action</c>): each sample's projects under the sample's short name (the template's
/// sourceName), the plugin parts on <c>Oadm.Plugin.Sdk/&lt;version&gt;</c>, the tests on the Oadm.Sdk packages of the same
/// version, plus a solution, the local tool manifest (the <c>oadm</c> tool of the same version), a .gitignore, a README and the
/// AGENTS.md for AI assistants (and a CLAUDE.md that imports it).
/// The samples stay the single source: a template is never edited by hand.
/// </summary>
internal static partial class Templates
{
    /// <summary>Writes one template folder per sample into <paramref name="output"/> and returns them.</summary>
    public static IReadOnlyList<string> Write(string repository, string output, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        var written = new List<string>();
        foreach (var sample in Scaffold.Samples)
        {
            var root = Path.Combine(output, "oadm-" + sample.Kind);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }

            var projects = new List<string>();
            foreach (var source in Directory.GetDirectories(Path.Combine(repository, "samples"), $"Oadm.Samples.{sample.Name}*")
                         .Where(d => Path.GetFileName(d) is var n && (n == $"Oadm.Samples.{sample.Name}" || n.StartsWith($"Oadm.Samples.{sample.Name}.", StringComparison.Ordinal)))
                         .OrderBy(d => d, StringComparer.Ordinal))
            {
                var project = Path.GetFileName(source).Replace("Oadm.Samples.", "", StringComparison.Ordinal);
                foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Where(f => !IsBuildOutput(Path.GetRelativePath(source, f))))
                {
                    var relative = Path.GetRelativePath(source, file).Replace("Oadm.Samples.", "", StringComparison.Ordinal);
                    var path = Path.Combine(root, project, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var text = File.ReadAllText(file).Replace("Oadm.Samples.", "", StringComparison.Ordinal);
                    if (path.EndsWith(".csproj", StringComparison.Ordinal))
                    {
                        text = Project(text, version);
                        projects.Add($"{project}/{Path.GetFileName(path)}");
                    }

                    File.WriteAllText(path, text, new UTF8Encoding(false));
                }
            }

            WriteFile(root, $"{sample.Name}.slnx", Solution(projects));
            WriteFile(root, "Directory.Build.props", DirectoryProps);
            WriteFile(root, ".gitignore", "bin/\nobj/\n.oadm/\n.vs/\n*.user\nTestResults/\nProperties/launchSettings.json\n");
            WriteFile(root, Path.Combine(".config", "dotnet-tools.json"), ToolManifest(version));
            WriteFile(root, "README.md", Readme(sample));
            WriteFile(root, "AGENTS.md", Agents(sample));
            WriteFile(root, "CLAUDE.md", "@AGENTS.md\n"); // Claude Code reads CLAUDE.md; it imports AGENTS.md
            WriteFile(root, Path.Combine(".template.config", "template.json"), TemplateJson(sample, projects));
            written.Add(root);
        }

        return written;
    }

    /// <summary>A sample project as a template project: the SDK and packages of <paramref name="version"/> instead of src/.</summary>
    public static string Project(string text, string version)
    {
        var references = SdkReference().Replace(text, m => $"""<PackageReference Include="{m.Groups["name"].Value}" Version="{version}" />""");
        var isPluginPart = !references.Contains("Microsoft.NET.Test.Sdk", StringComparison.Ordinal);
        return isPluginPart
            ? references.Replace("""<Project Sdk="Microsoft.NET.Sdk">""", $"""<Project Sdk="Oadm.Plugin.Sdk/{version}">""", StringComparison.Ordinal)
            : references;
    }

    private const string DirectoryProps = """
        <Project>
          <!-- Every project of the plugin. The plugin parts use Oadm.Plugin.Sdk; after a build the plugin folder is
               .oadm/plugins/<plugin id>/, which "dotnet oadm dev start" loads. -->
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
            <OadmDeployRoot>$(MSBuildThisFileDirectory).oadm/plugins/</OadmDeployRoot>
          </PropertyGroup>
        </Project>

        """;

    private static string Solution(IEnumerable<string> projects) =>
        "<Solution>\n" + string.Concat(projects.Select(p => $"  <Project Path=\"{p}\" />\n")) + "</Solution>\n";

    private static string ToolManifest(string version) => new JsonObject
    {
        ["version"] = 1,
        ["isRoot"] = true,
        ["tools"] = new JsonObject
        {
            ["oadm.tools"] = new JsonObject { ["version"] = version, ["commands"] = new JsonArray("oadm"), ["rollForward"] = false },
        },
    }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";

    private static string Readme(Sample sample) => string.Create(CultureInfo.InvariantCulture, $"""
        # {sample.DisplayName}

        An OADM plugin: {sample.Description}.

        ```
        dotnet tool restore          # the oadm tool of this SDK version
        dotnet oadm dev start        # an OADM server for development, with this plugin
        dotnet build                 # the server restarts with the new build
        dotnet oadm dev client       # the OADM client against it
        dotnet test
        dotnet oadm pack             # the .oadmplugin file for customers
        ```

        Guide: https://oadm.app/dev/tutorial-{sample.Kind}/

        """);

    /// <summary>
    /// The rules for an AI coding assistant in the plugin folder (AGENTS.md): the rules of OADM's own CLAUDE.md that apply
    /// to a plugin outside the repository. The plugin's name, id and display name are filled in by the template.
    /// </summary>
    public static string Agents(Sample sample)
    {
        var kind = sample.Kind switch
        {
            "task" => $"a task plugin: an entry of the device context menu with a dialog ({sample.Name}.Client), one task per device ({sample.Name})",
            "page" => $"a core plugin with its own page in the navigation rail: the server part ({sample.Name}) and the page ({sample.Name}.Client)",
            _ => $"an event rule action ({sample.Name}): OADM runs it when a rule fires",
        };
        return string.Create(CultureInfo.InvariantCulture, $$"""
            # {{sample.DisplayName}}: rules for AI assistants

            This folder is an OADM plugin, made from the template `oadm-{{sample.Kind}}`: {{kind}}. Tests in {{sample.Name}}.Tests.
            Plugin id `{{sample.Id}}`: it names the folder, the settings and the permissions. Never change it after a release.

            OADM is the open alternative to AXIS Device Manager. Users are installers who know Axis devices.

            ## Build, test, run

            ```
            dotnet tool restore          # the oadm tool of this SDK version
            dotnet build                 # the plugin folder .oadm/plugins/{{sample.Id}}/
            dotnet test
            dotnet oadm dev start        # an OADM server with this plugin, restarted after every build
            dotnet oadm dev client       # in a second terminal: the client against it
            dotnet oadm pack             # the .oadmplugin file for users
            ```

            Run `dotnet test` after every step. Never automate the real desktop (no clicks, no screen captures).

            ## Guides

            - Develop with the .NET SDK: https://oadm.app/dev/plugin-sdk/
            - Tutorial of this kind of plugin: https://oadm.app/dev/tutorial-{{sample.Kind}}/
            - Plugin basics: https://oadm.app/dev/plugins/, devices and VAPIX: https://oadm.app/dev/devices/
            - UI kit: https://oadm.app/dev/ui/, testing: https://oadm.app/dev/testing/
            - Device groups: https://oadm.app/dev/device-groups/, SDK versions: https://oadm.app/dev/compatibility/
            - SDK reference (every type and member): https://oadm.app/api/

            ## Device safety (HARD RULE)

            A plugin that changes a device must never risk breaking it.

            1. Declare the VAPIX APIs and minimum versions the plugin needs (`DeviceApi`). `CanRun` checks them against the
               cached `device.Apis`. `ExecuteAsync` reads a fresh `ctx.Vapix.GetApiListAsync()` and calls `Require(...)`
               before the first write. A different major version is a different API. An unsupported device fails with
               `DeviceNotCompatibleException` ("Nothing was changed").
            2. Validate all input (addresses, masks, passwords, files) before the first write. Use the device's own dry run or
               validation where the API has one.
            3. No lock-out: never remove or demote the account OADM uses. Settings that can make the device unreachable need a
               clear warning in the dialog.
            4. Secrets: payloads stay in memory. Never persist or log passwords or keys.
            5. `ITaskPluginQuery` is read only. Dialogs read the current state through `ITaskDialogContext.QueryAsync`.
            6. Use `ReportWarning` for Done with warnings and `Log` for the task's log.
            7. Tests that write to a real device are opt-in only (`[Trait("Category", "HardwareWrite")]`). Never run them
               unless the developer asks, and only against a test device.

            ## UI texts

            - Short and plain, for installers who come from ADM. Subtitles: one short sentence or none.
            - No protocol, API or internal names in the UI. Say what the user can do.
            - Tooltips only when they add something, at most about 8 words, never the label again.
            - Button and menu labels never end with "...". Step names: imperative with the article ("Wait for the device to
              come back").
            - No filler ("easily", "simply", "please note"). "Optional" once per field. No "(s)" plurals.

            ## Client part

            - Reuse the shared controls of `Oadm.Sdk.Client.Controls` (XAML prefix `ui`) and the OADM theme. No local font
              sizes, colours or paddings.
            - Validation: forms derive from `ValidatingViewModel` with rules on its `FormValidator`; every field is a
              `ui:FormField`. The error shows directly below its field, only after the field was edited, left or a submit was
              tried. Submit buttons stay disabled while an error exists, with the reason as tooltip.
            - Loading: anything that can take longer than 1 s shows it from the first moment (`ui:LoadingOverlay` for a
              dialog or page that waits for a read, `ui:ProgressRow` or `ui:ProgressRing` for a list that reloads). The window
              never freezes.
            - Buttons (Fluent): toolbars and card headers use the flat `ui:ToolbarButton`. Icon-only buttons always have a
              tooltip. Only the one main action of a dialog or form is filled (`Button.primary`); `Button.secondary` for the
              others. Destructive confirmations are red (`Button.danger`).
            - MVVM with CommunityToolkit (`[ObservableProperty]`, `[RelayCommand]`), no logic in code-behind. View models are
              testable without Avalonia.

            ## Scale

            OADM manages up to 30,000 devices.

            - A page that reads or changes devices starts from the user's choice (`ui:PageDeviceChoice` first in its toolbar;
              one device: `DevicePickerWindow.ShowOneAsync`), never from every device by itself.
            - Lists of devices or results are virtualized (DataGrid, virtualizing ListBox), never one control per item. Bind
              them to `RangeObservableCollection` and change them in one step (`ReplaceAll`, `AddRange`).
            - Index by id (`Dictionary`, `HashSet`); no `List.Contains` or `FirstOrDefault` over all rows inside a loop.
            - Server calls for many devices are batched; per-device work has bounded parallelism.

            ## Code and tests

            - Async all the way, a `CancellationToken` on every I/O method, no `.Result` or `.Wait()`.
            - Reach devices only through `IVapixClient` and the host's services, never your own sockets or HTTP handlers, so
              devices at a site work too.
            - Every non-trivial class has tests with the fakes of `Oadm.Sdk.Testing` (`FakeCamera`, `FakeDevice`,
              `FakeTaskContext`, `FakeActionContext`, `FakeCoreContext`, `StepRun`). No network in unit tests.
            - Tests never flake: wait for other threads with `Eventually.TrueAsync(condition, "what")`, never a fixed
              `Task.Delay`. Time in the code comes from a `TimeProvider`; tests pass a `FakeTimeProvider` (package
              Microsoft.Extensions.TimeProvider.Testing) and advance it. Unique temp folders and ports per test.
            - Tests check behaviour and content, never pixel positions or sizes.

            """);
    }

    private static string TemplateJson(Sample sample, IReadOnlyList<string> projects)
    {
        var json = new JsonObject
        {
            ["$schema"] = "http://json.schemastore.org/template",
            ["author"] = "OADM contributors",
            ["classifications"] = new JsonArray("OADM", "Plugin", "Axis"),
            ["identity"] = "Oadm.Plugin.Templates." + sample.Kind,
            ["name"] = "OADM " + sample.Kind + " plugin",
            ["shortName"] = "oadm-" + sample.Kind,
            ["description"] = "An OADM plugin: " + sample.Description + ", with its tests.",
            ["tags"] = new JsonObject { ["language"] = "C#", ["type"] = "solution" },
            ["sourceName"] = sample.Name,
            ["preferNameDirectory"] = true,
            ["symbols"] = new JsonObject
            {
                ["id"] = new JsonObject
                {
                    ["type"] = "parameter",
                    ["datatype"] = "string",
                    ["description"] = "The plugin id, e.g. acme.door-overview. Default: community.<name in kebab case>.",
                    ["defaultValue"] = "",
                },
                ["displayName"] = new JsonObject
                {
                    ["type"] = "parameter",
                    ["datatype"] = "string",
                    ["description"] = "The name OADM shows. Default: the project name.",
                    ["defaultValue"] = "",
                },
                ["nameKebab"] = new JsonObject { ["type"] = "derived", ["valueSource"] = "name", ["valueTransform"] = "kebab" },
                ["defaultId"] = new JsonObject
                {
                    ["type"] = "generated",
                    ["generator"] = "join",
                    ["parameters"] = new JsonObject
                    {
                        ["symbols"] = new JsonArray(
                            new JsonObject { ["type"] = "const", ["value"] = "community." },
                            new JsonObject { ["type"] = "ref", ["value"] = "nameKebab" }),
                    },
                },
                ["pluginId"] = new JsonObject
                {
                    ["type"] = "generated",
                    ["generator"] = "coalesce",
                    ["parameters"] = new JsonObject { ["sourceVariableName"] = "id", ["fallbackVariableName"] = "defaultId" },
                    ["replaces"] = sample.Id,
                },
                ["pluginDisplayName"] = new JsonObject
                {
                    ["type"] = "generated",
                    ["generator"] = "coalesce",
                    ["parameters"] = new JsonObject { ["sourceVariableName"] = "displayName", ["fallbackVariableName"] = "name" },
                    ["replaces"] = sample.DisplayName,
                },
            },
            ["forms"] = new JsonObject { ["kebab"] = new JsonObject { ["identifier"] = "kebabCase" } },
            ["primaryOutputs"] = new JsonArray([.. projects.Select(p => (JsonNode)new JsonObject { ["path"] = p })]),
        };
        return json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    private static void WriteFile(string root, string relative, string text)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }

    private static bool IsBuildOutput(string relative)
    {
        var first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return first is "bin" or "obj" || first.StartsWith("TestResults", StringComparison.Ordinal);
    }

    /// <summary>A project reference to an SDK project of src/ (Oadm.Sdk, Oadm.Sdk.Client, Oadm.Sdk.Testing).</summary>
    [GeneratedRegex("""<ProjectReference Include="\.\.\\\.\.\\src\\(?<name>Oadm\.Sdk(\.Client|\.Testing)?)\\[^"]+\.csproj" />""")]
    private static partial Regex SdkReference();
}
