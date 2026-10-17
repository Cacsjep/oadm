using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

using Oadm.Sdk.Client;
using Oadm.Sdk.Plugins;

namespace Oadm.ApiDocs.Tests;

/// <summary>The SDK reference generated from the real SDK assemblies next to the tests.</summary>
public sealed partial class ApiDocsTests
{
    private static readonly Lazy<ApiDocs> Docs = new(() => Pages.Generate(ApiModel.Load(AppContext.BaseDirectory, Repository()), "/"));

    private static string Page(string path) => Docs.Value.Pages["api/" + path + ".md"];

    [Fact]
    public void Every_public_type_has_a_page_and_nothing_else_does()
    {
        var exported = new[] { typeof(IPlugin).Assembly, typeof(ICorePluginPage).Assembly }
            .SelectMany(a => a.GetExportedTypes())
            .Where(t => t.GetCustomAttribute<System.Runtime.CompilerServices.CompilerGeneratedAttribute>() is null)
            .ToList();
        var typePages = Docs.Value.Pages.Keys.Where(k => !k.EndsWith("/index.md", StringComparison.Ordinal)).ToList();

        Assert.Equal(exported.Count, typePages.Count);
        Assert.Contains("api/sdk/vapix/ivapixclient.md", typePages);
        Assert.Contains("api/sdk/client/collections/rangeobservablecollection-1.md", typePages);
    }

    [Fact]
    public void Signatures_are_csharp_with_nullability()
    {
        Assert.Contains("Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)", Page("sdk/vapix/ivapixclient"), StringComparison.Ordinal);
        Assert.Contains("string? HostName { get; }", Page("sdk/devices/ideviceinfo"), StringComparison.Ordinal);
        Assert.Contains("public sealed record DeviceApi(string Id, string Version, string? Name = null, string? Status = null)", Page("sdk/vapix/deviceapi"), StringComparison.Ordinal);
    }

    [Fact]
    public void Types_link_to_their_pages_and_to_the_dotnet_docs()
    {
        var page = Page("sdk/vapix/ivapixclient");
        Assert.Contains("[`ITaskExecutionContext.Vapix`](/api/sdk/plugins/itaskexecutioncontext/#vapix)", page, StringComparison.Ordinal);
        Assert.Contains("(https://learn.microsoft.com/dotnet/api/system.notsupportedexception)", page, StringComparison.Ordinal);
        Assert.Contains("Guide: [Devices and VAPIX](/dev/devices/)", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Links_at_the_domain_root_stay_on_the_site()
    {
        // The site lives at the root of its domain: "//api/..." would point to another host.
        Assert.DoesNotContain(Docs.Value.Pages, p => p.Value.Contains("](//", StringComparison.Ordinal));
        string page = Pages.Generate(ApiModel.Load(AppContext.BaseDirectory, Repository()), "/oadm/").Pages["api/sdk/vapix/ivapixclient.md"];
        Assert.Contains("](/oadm/api/sdk/plugins/itaskexecutioncontext/#vapix)", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Default_interface_members_are_marked()
    {
        var page = Page("sdk/plugins/itaskplugin");
        var section = page[page.IndexOf("### NotSupportedReason", StringComparison.Ordinal)..];
        section = section[..section.IndexOf("\n### ", 4, StringComparison.Ordinal)];
        Assert.Contains("**Default implementation**", section, StringComparison.Ordinal);
        var execute = page[page.IndexOf("### ExecuteAsync", StringComparison.Ordinal)..];
        Assert.DoesNotContain("**Default implementation**", execute[..execute.IndexOf("\n### ", 4, StringComparison.Ordinal)], StringComparison.Ordinal);
    }

    [Fact]
    public void Generated_partial_properties_keep_their_written_comment()
    {
        // [ObservableProperty] gives the generated part an empty <inheritdoc/>; the reference shows the written comment.
        Assert.Contains("The chosen tag that includes this device", Page("sdk/client/controls/devicepickitem"), StringComparison.Ordinal);
    }

    [Fact]
    public void Record_structs_leave_out_generated_members()
    {
        var page = Page("sdk/vapix/videoresolution");
        Assert.Contains("public readonly record struct VideoResolution(int Width, int Height)", page, StringComparison.Ordinal);
        Assert.DoesNotContain("### Deconstruct", page, StringComparison.Ordinal);
        Assert.DoesNotContain("op_Equality", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Enums_list_their_values()
    {
        Assert.Contains("| `CertificateChanged` | 5 |", Page("sdk/devices/devicestatus"), StringComparison.Ordinal);
    }

    [Fact]
    public void Controls_list_their_xaml_properties()
    {
        var page = Page("sdk/client/controls/formfield");
        Assert.Contains("## XAML properties", page, StringComparison.Ordinal);
        Assert.Contains("<span id=\"xaml-hint\"></span>`Hint`", page, StringComparison.Ordinal);
        Assert.DoesNotContain("### HintProperty", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Records_leave_out_generated_members()
    {
        var page = Page("sdk/vapix/deviceapi");
        Assert.DoesNotContain("### Equals", page, StringComparison.Ordinal);
        Assert.DoesNotContain("### Deconstruct", page, StringComparison.Ordinal);
        Assert.DoesNotContain("EqualityContract", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_link_into_the_reference_finds_its_page_and_anchor()
    {
        var anchors = Docs.Value.Pages.ToDictionary(p => Url(p.Key), p => Anchors(p.Value));
        var problems = new List<string>();
        foreach (var (path, text) in Docs.Value.Pages)
        {
            foreach (Match link in LinkRegex().Matches(text))
            {
                var target = link.Groups["page"].Value;
                var anchor = link.Groups["anchor"].Value;
                if (!anchors.TryGetValue(target, out var ids))
                {
                    problems.Add($"{path}: {target}");
                }
                else if (anchor.Length > 0 && !ids.Contains(anchor))
                {
                    problems.Add($"{path}: {target}#{anchor}");
                }
            }
        }

        Assert.Empty(problems);
    }

    [Fact]
    public void The_sidebar_lists_every_page()
    {
        using var json = JsonDocument.Parse(Docs.Value.SidebarJson);
        var slugs = new List<string>();
        Collect(json.RootElement);
        Assert.Equal(Docs.Value.Pages.Count, slugs.Count);
        Assert.All(slugs, s => Assert.True(Docs.Value.Pages.ContainsKey(s + ".md") || Docs.Value.Pages.ContainsKey(s + "/index.md"), s));

        void Collect(JsonElement items)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("slug", out var slug))
                {
                    slugs.Add(slug.GetString()!);
                }

                if (item.TryGetProperty("items", out var children))
                {
                    Collect(children);
                }
            }
        }
    }

    [Fact]
    public void Every_type_a_guide_names_exists()
    {
        var names = ApiModel.Load(AppContext.BaseDirectory).Types.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        Assert.All(Guides.Types.Keys, name => Assert.Contains(name, names));
    }

    [Fact]
    public void Every_type_member_and_xaml_property_has_a_description()
    {
        // The reference is only as good as the SDK comments: a public member without one fails here (write a <summary>).
        var empty = new List<string>();
        foreach (var (path, text) in Docs.Value.Pages.Where(p => !p.Key.EndsWith("/index.md", StringComparison.Ordinal)))
        {
            if (!TypeSummaryRegex().IsMatch(text))
            {
                empty.Add(path + ": the type");
            }

            foreach (Match member in EmptyMemberRegex().Matches(text))
            {
                empty.Add(path + ": " + member.Groups[1].Value);
            }

            foreach (Match row in EmptyXamlRowRegex().Matches(text))
            {
                empty.Add(path + ": " + row.Groups[1].Value);
            }
        }

        Assert.Empty(empty);
    }

    [Fact]
    public void Pages_have_a_title_and_a_description()
    {
        Assert.All(Docs.Value.Pages.Values, page =>
        {
            Assert.StartsWith("---\ntitle: \"", page, StringComparison.Ordinal);
            Assert.Contains("\ndescription: \"", page, StringComparison.Ordinal);
        });
    }

    private static string Repository() => Oadm.Tests.Shared.TestRepository.Root;

    /// <summary>"api/sdk/vapix/ivapixclient.md" -> "/api/sdk/vapix/ivapixclient/".</summary>
    private static string Url(string path)
    {
        var page = path[..^".md".Length];
        page = page.EndsWith("/index", StringComparison.Ordinal) ? page[..^"/index".Length] : page;
        return "/" + page + "/";
    }

    /// <summary>The ids the site gives the page's headings (same rules as the generator) and the spans it writes.</summary>
    private static HashSet<string> Anchors(string markdown)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var inCode = false;
        foreach (var line in markdown.Split('\n'))
        {
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                inCode = !inCode;
            }
            else if (!inCode && HeadingRegex().Match(line) is { Success: true } heading)
            {
                ids.Add(Layout.Slug(heading.Groups[1].Value, seen));
            }
        }

        foreach (Match span in SpanRegex().Matches(markdown))
        {
            ids.Add(span.Groups[1].Value);
        }

        return ids;
    }

    /// <summary>A member heading whose signature block is followed directly by the next heading (or only by its badges).</summary>
    [GeneratedRegex(@"^### (\S+)\n\n```csharp\n[^`]*```\n\n(?:\*\*(?:Default implementation|Protected)[^\n]*\n\n)?(?=### |## |\z)", RegexOptions.Multiline)]
    private static partial Regex EmptyMemberRegex();

    [GeneratedRegex(@"^\| <span id=""xaml-[^""]+""></span>`(\w+)`[^|]*\|[^|]*\| *\|$", RegexOptions.Multiline)]
    private static partial Regex EmptyXamlRowRegex();

    /// <summary>The line below "Interface in ... .dll" is the summary, not the declaration.</summary>
    [GeneratedRegex(@"\.dll[^\n]*\n\n(?!```)\S")]
    private static partial Regex TypeSummaryRegex();

    [GeneratedRegex(@"\]\((?<page>/api/[^)#]*)(?:#(?<anchor>[^)]*))?\)")]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"^#{2,4} (.+)$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex("<span id=\"([^\"]+)\"></span>")]
    private static partial Regex SpanRegex();
}
