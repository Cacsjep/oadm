using System.IO.Compression;
using System.Text;

namespace Oadm.Client.Api;

/// <summary>Fake SupportService so <c>--fake</c> saves a support report: a small ZIP with a summary and one log file.</summary>
public sealed partial class FakeOadmApi
{
    public Task<Oadm.Sdk.Plugins.PluginDownload> CreateSupportReportAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
        }

        var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "summary.txt", "OADM support report\nServer name: fake\n");
            Write(zip, "logs/oadm-server-20261022.log", "2026-10-22 09:00:00.000 +02:00 [INF] Oadm.Server: Fake server started\n");
        }

        buffer.Position = 0;
        return Task.FromResult(new Oadm.Sdk.Plugins.PluginDownload(buffer, "oadm-support-fake-20261022-0900.zip", buffer.Length));

        static void Write(ZipArchive zip, string name, string text)
        {
            using var stream = zip.CreateEntry(name).Open();
            stream.Write(Encoding.UTF8.GetBytes(text));
        }
    }
}
