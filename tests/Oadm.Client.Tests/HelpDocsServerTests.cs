using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Oadm.Client.Help;
using Oadm.Sdk.Testing;

namespace Oadm.Client.Tests;

/// <summary>
/// The docs server of the Help window: the files of the docs folder with their media types, folders to their index,
/// the 404 page, only GET and HEAD, refused paths (traversal, encoded, absolute, links out of the folder), loopback only,
/// and the port freed when it stops. Raw requests over TCP, so no client library cleans up the paths first.
/// </summary>
public sealed class HelpDocsServerTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "oadm-help-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private const string Secret = "SECRET OUTSIDE THE DOCS";

    public HelpDocsServerTests()
    {
        _root = Path.Combine(_folder, "docs");
        Write("index.html", "<html>start page</html>");
        Write("404.html", "<html>page not found</html>");
        Write(Path.Combine("getting-started", "index.html"), "<html>getting started</html>");
        Directory.CreateDirectory(Path.Combine(_root, "empty"));
        File.WriteAllText(Path.Combine(_folder, "secret.txt"), Secret);
        Directory.CreateDirectory(Path.Combine(_folder, "outside"));
        File.WriteAllText(Path.Combine(_folder, "outside", "secret.txt"), Secret);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // a link the test could not remove; the temp folder is cleaned by the system
        }
    }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private DocsServer Server(TimeProvider? time = null) => new(_root, NullLogger.Instance, time);

    private sealed record Response(int Status, Dictionary<string, string> Headers, string Body)
    {
        public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
    }

    /// <summary>Sends one request as it is ("Connection: close") and reads the whole answer.</summary>
    private static async Task<Response> SendAsync(DocsServer server, string method, string target, string? host = null, string extra = "")
    {
        using var client = new TcpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await client.ConnectAsync(IPAddress.Loopback, server.Port, timeout.Token);
        var stream = client.GetStream();
        var request = $"{method} {target} HTTP/1.1\r\nHost: {host ?? "127.0.0.1:" + server.Port}\r\n{extra}Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.Latin1.GetBytes(request), timeout.Token);
        using var answer = new MemoryStream();
        await stream.CopyToAsync(answer, timeout.Token);
        return Parse(Encoding.UTF8.GetString(answer.ToArray()));
    }

    private static Response Parse(string text)
    {
        var split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        Assert.True(split > 0, "no response head: " + text);
        var lines = text[..split].Split("\r\n");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            headers[line[..colon]] = line[(colon + 1)..].Trim();
        }

        return new Response(int.Parse(lines[0].Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture), headers, text[(split + 4)..]);
    }

    [Theory]
    [InlineData("page.html", "text/html; charset=utf-8")]
    [InlineData("site.css", "text/css; charset=utf-8")]
    [InlineData("app.js", "text/javascript; charset=utf-8")]
    [InlineData("module.mjs", "text/javascript; charset=utf-8")]
    [InlineData("data.json", "application/json; charset=utf-8")]
    [InlineData("logo.svg", "image/svg+xml")]
    [InlineData("shot.png", "image/png")]
    [InlineData("photo.jpg", "image/jpeg")]
    [InlineData("photo.webp", "image/webp")]
    [InlineData("photo.avif", "image/avif")]
    [InlineData("favicon.ico", "image/x-icon")]
    [InlineData("font.woff", "font/woff")]
    [InlineData("font.woff2", "font/woff2")]
    [InlineData("robots.txt", "text/plain; charset=utf-8")]
    [InlineData("sitemap.xml", "application/xml; charset=utf-8")]
    [InlineData("pagefind.wasm", "application/wasm")]
    [InlineData("pagefind/fragment/en_1a2b.pf_fragment", "application/octet-stream")]
    [InlineData("pagefind/index/en_1a2b.pf_index", "application/octet-stream")]
    [InlineData("pagefind/pagefind.en_1a2b.pf_meta", "application/octet-stream")]
    [InlineData("pagefind/wasm.unknown.pagefind", "application/octet-stream")]
    public async Task Files_are_served_with_their_media_type(string file, string type)
    {
        Write(file, "content of " + file);
        using var server = Server();

        var response = await SendAsync(server, "GET", "/" + file);

        Assert.Equal(200, response.Status);
        Assert.Equal(type, response.Header("Content-Type"));
        Assert.Equal("content of " + file, response.Body);
        Assert.Equal(Encoding.UTF8.GetByteCount(response.Body).ToString(System.Globalization.CultureInfo.InvariantCulture), response.Header("Content-Length"));
        Assert.Equal("no-cache", response.Header("Cache-Control"));
        Assert.Null(response.Header("Set-Cookie"));
    }

    [Fact]
    public async Task A_folder_answers_with_its_index_and_a_folder_without_slash_redirects()
    {
        using var server = Server();

        Assert.Equal("<html>start page</html>", (await SendAsync(server, "GET", "/")).Body);
        Assert.Equal("<html>getting started</html>", (await SendAsync(server, "GET", "/getting-started/")).Body);
        Assert.Equal("<html>getting started</html>", (await SendAsync(server, "GET", "/getting-started/?q=x#top")).Body);

        var redirect = await SendAsync(server, "GET", "/getting-started?q=x");
        Assert.Equal(301, redirect.Status);
        Assert.Equal("/getting-started/?q=x", redirect.Header("Location"));

        var noIndex = await SendAsync(server, "GET", "/empty/");
        Assert.Equal(404, noIndex.Status);
    }

    [Fact]
    public async Task A_missing_file_answers_with_the_404_page_or_a_plain_404()
    {
        using (var server = Server())
        {
            var missing = await SendAsync(server, "GET", "/no-such-page/");
            Assert.Equal(404, missing.Status);
            Assert.Equal("<html>page not found</html>", missing.Body);
            Assert.Equal("text/html; charset=utf-8", missing.Header("Content-Type"));
        }

        File.Delete(Path.Combine(_root, "404.html"));
        using (var server = Server())
        {
            var plain = await SendAsync(server, "GET", "/no-such-page.html");
            Assert.Equal(404, plain.Status);
            Assert.Equal("Not Found", plain.Body);
        }
    }

    [Theory]
    [InlineData("/../secret.txt")]
    [InlineData("/../outside/secret.txt")]
    [InlineData("/getting-started/../../secret.txt")]
    [InlineData("/%2e%2e/secret.txt")]
    [InlineData("/%2E%2E/outside/secret.txt")]
    [InlineData("/.%2e/secret.txt")]
    [InlineData("/%2e%2e%2fsecret.txt")]
    [InlineData("/getting-started%2F..%2F..%2Fsecret.txt")]
    [InlineData("/%252e%252e/secret.txt")]
    [InlineData("/..\\secret.txt")]
    [InlineData("/getting-started\\..\\..\\secret.txt")]
    [InlineData("/%5c..%5csecret.txt")]
    [InlineData("/./index.html")]
    [InlineData("//localhost/c$/secret.txt")]
    [InlineData("/C:/Windows/win.ini")]
    [InlineData("/C:%5cWindows%5cwin.ini")]
    [InlineData("/etc/passwd")]
    [InlineData("/index.html%00.png")]
    [InlineData("/index.html::$DATA")]
    public async Task Paths_out_of_the_folder_are_refused_with_404(string target)
    {
        using var server = Server();

        var response = await SendAsync(server, "GET", target);

        Assert.Equal(404, response.Status);
        Assert.DoesNotContain(Secret, response.Body, StringComparison.Ordinal);
        Assert.Equal("<html>page not found</html>", response.Body);
    }

    [Fact]
    public async Task An_absolute_target_or_another_host_is_a_bad_request()
    {
        using var server = Server();

        Assert.Equal(400, (await SendAsync(server, "GET", "http://127.0.0.1:" + server.Port + "/index.html")).Status);
        Assert.Equal(400, (await SendAsync(server, "GET", "/index.html", host: "evil.example")).Status);
    }

    [Fact]
    public async Task Links_that_lead_out_of_the_folder_are_refused()
    {
        var folderLink = Path.Combine(_root, "linked");
        var fileLink = Path.Combine(_root, "secret-link.txt");
        if (!TryLinkFolder(folderLink, Path.Combine(_folder, "outside")))
        {
            return; // this computer may not create links (Windows without developer mode); the path rules are tested above
        }

        var fileLinked = TryLinkFile(fileLink, Path.Combine(_folder, "secret.txt"));
        // A link that stays inside the folder still works.
        var insideLink = Path.Combine(_root, "start-link");
        var insideLinked = TryLinkFolder(insideLink, Path.Combine(_root, "getting-started"));
        using var server = Server();

        var throughFolder = await SendAsync(server, "GET", "/linked/secret.txt");
        Assert.Equal(404, throughFolder.Status);
        Assert.DoesNotContain(Secret, throughFolder.Body, StringComparison.Ordinal);
        if (fileLinked)
        {
            var throughFile = await SendAsync(server, "GET", "/secret-link.txt");
            Assert.Equal(404, throughFile.Status);
            Assert.DoesNotContain(Secret, throughFile.Body, StringComparison.Ordinal);
        }

        if (insideLinked)
        {
            Assert.Equal("<html>getting started</html>", (await SendAsync(server, "GET", "/start-link/")).Body);
        }
    }

    private static bool TryLinkFolder(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        // A junction needs no privilege on Windows.
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target])
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        })!;
        mklink.WaitForExit(30_000);
        return Directory.Exists(link);
    }

    private static bool TryLinkFile(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("OPTIONS")]
    public async Task Only_GET_and_HEAD_are_allowed(string method)
    {
        using var server = Server();

        var response = await SendAsync(server, method, "/index.html", extra: method == "POST" ? "Content-Length: 0\r\n" : "");

        Assert.Equal(405, response.Status);
        Assert.Equal("GET, HEAD", response.Header("Allow"));
    }

    [Fact]
    public async Task HEAD_has_the_headers_of_GET_and_no_body()
    {
        using var server = Server();

        var head = await SendAsync(server, "HEAD", "/index.html");
        var missing = await SendAsync(server, "HEAD", "/missing.html");

        Assert.Equal(200, head.Status);
        Assert.Equal("text/html; charset=utf-8", head.Header("Content-Type"));
        Assert.Equal("23", head.Header("Content-Length")); // "<html>start page</html>"
        Assert.Equal(string.Empty, head.Body);
        Assert.Equal(404, missing.Status);
        Assert.Equal(string.Empty, missing.Body);
    }

    [Fact]
    public async Task One_connection_carries_several_requests()
    {
        using var server = Server();
        using var client = new TcpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await client.ConnectAsync(IPAddress.Loopback, server.Port, timeout.Token);
        var stream = client.GetStream();
        var host = "Host: 127.0.0.1:" + server.Port + "\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\n" + host + "\r\nGET /getting-started/ HTTP/1.1\r\n" + host + "Connection: close\r\n\r\n"), timeout.Token);
        using var answer = new MemoryStream();
        await stream.CopyToAsync(answer, timeout.Token);
        var text = Encoding.UTF8.GetString(answer.ToArray());

        Assert.Equal(2, text.Split("HTTP/1.1 200 OK").Length - 1);
        Assert.Contains("Connection: keep-alive", text, StringComparison.Ordinal);
        Assert.EndsWith("<html>getting started</html>", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_idle_connection_is_closed()
    {
        var time = new FakeTimeProvider();
        using var server = Server(time);
        using var client = new TcpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await client.ConnectAsync(IPAddress.Loopback, server.Port, timeout.Token);
        var stream = client.GetStream();
        var read = stream.ReadAsync(new byte[16], timeout.Token).AsTask();

        // The server reads the request head under the idle timeout from the moment it accepts the connection.
        await Eventually.TrueAsync(() =>
        {
            time.Advance(TimeSpan.FromSeconds(31));
            return read.IsCompleted;
        }, "the idle connection closes");
        Assert.Equal(0, await read);
    }

    [Fact]
    public void It_listens_on_loopback_only_and_stopping_frees_the_port()
    {
        int port;
        using (var server = Server())
        {
            port = server.Port;
            Assert.Equal("127.0.0.1", server.Origin.Host);
            Assert.Equal(new Uri("http://127.0.0.1:" + port + "/"), server.Origin);
        }

        // Stopped: the port can be taken again.
        var again = new TcpListener(IPAddress.Loopback, port);
        again.Start();
        again.Stop();
    }

    [Fact]
    public void The_docs_folder_is_OADM_DOCS_DIR_then_next_to_the_executable_then_the_app_bundle_resources()
    {
        var contents = Path.Combine(_folder, "OADM.app", "Contents");
        var exe = Path.Combine(contents, "MacOS");
        var nextToExe = Path.Combine(exe, "docs");
        var resources = Path.Combine(contents, "Resources", "docs");
        Directory.CreateDirectory(exe);
        string? Variable(string name) => name == HelpDocs.FolderVariable ? _root : null;
        Assert.Null(HelpDocs.FindRoot(_ => null, exe));

        Directory.CreateDirectory(nextToExe);
        Assert.Null(HelpDocs.FindRoot(_ => null, exe)); // a docs folder without a start page is no docs

        Directory.CreateDirectory(resources);
        File.WriteAllText(Path.Combine(resources, "index.html"), "start");
        Assert.Equal(Path.GetFullPath(resources), HelpDocs.FindRoot(_ => null, exe));

        File.WriteAllText(Path.Combine(nextToExe, "index.html"), "start");
        Assert.Equal(Path.GetFullPath(nextToExe), HelpDocs.FindRoot(_ => null, exe));

        Assert.Equal(Path.GetFullPath(_root), HelpDocs.FindRoot(Variable, exe));
        // A variable without docs falls back to the next place.
        Assert.Equal(Path.GetFullPath(nextToExe), HelpDocs.FindRoot(_ => Path.Combine(_folder, "nowhere"), exe));
        // Resources/docs only inside an app bundle.
        var plain = Path.Combine(_folder, "plain", "bin");
        Directory.CreateDirectory(Path.Combine(_folder, "plain", "Resources", "docs"));
        File.WriteAllText(Path.Combine(_folder, "plain", "Resources", "docs", "index.html"), "start");
        Assert.Null(HelpDocs.FindRoot(_ => null, plain));
    }
}
