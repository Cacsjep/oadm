using Google.Protobuf.WellKnownTypes;

using Grpc.Core;

using Oadm.Contracts.V1;
using Oadm.Contracts.Vulnerabilities;

namespace Oadm.Client.Api;

/// <summary>
/// Fake mode: the release's list of Axis CVEs (the same file the server embeds). Check now "fetches" by dating the list
/// now; nothing goes to the internet.
/// </summary>
public sealed partial class FakeOadmApi
{
    private VulnerabilityList? _vulnerabilities;

    /// <summary>The list the fake serves (tests set their own).</summary>
    public VulnerabilityList FakeVulnerabilities
    {
        get
        {
            lock (_gate)
            {
                return _vulnerabilities ??= ReadFakeVulnerabilities();
            }
        }

        set
        {
            lock (_gate)
            {
                _vulnerabilities = value;
            }
        }
    }

    public Task<VulnerabilityListReply> GetVulnerabilitiesAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
        }

        return Task.FromResult(VulnerabilityReply());
    }

    public Task<VulnerabilityListReply> UploadVulnerabilitiesAsync(string listJson, CancellationToken ct)
    {
        if (VulnerabilityMode() != "clients")
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "The server takes the vulnerability list from the clients only while Update online is By the clients."));
        }

        var list = VulnerabilityList.Parse(listJson);
        if (list.Updated > FakeVulnerabilities.Updated)
        {
            FakeVulnerabilities = list with { Source = VulnerabilityList.Sources.Client };
        }

        return Task.FromResult(VulnerabilityReply());
    }

    public Task<VulnerabilityListReply> RefreshVulnerabilitiesAsync(CancellationToken ct)
    {
        if (VulnerabilityMode() != "server")
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "The server fetches the vulnerability list only while Update online is By the server."));
        }

        FakeVulnerabilities = FakeVulnerabilities with { Updated = DateTimeOffset.UtcNow, Source = VulnerabilityList.Sources.Server };
        return Task.FromResult(VulnerabilityReply());
    }

    private string VulnerabilityMode()
    {
        lock (_gate)
        {
            return string.IsNullOrEmpty(_settings.VulnerabilityUpdate) ? "off" : _settings.VulnerabilityUpdate;
        }
    }

    private VulnerabilityListReply VulnerabilityReply()
    {
        var list = FakeVulnerabilities;
        return new VulnerabilityListReply
        {
            ListJson = list.ToJson(),
            Update = VulnerabilityMode(),
            Updated = Timestamp.FromDateTimeOffset(list.Updated),
            Source = list.Source,
            Count = list.Items.Count,
        };
    }

    private static VulnerabilityList ReadFakeVulnerabilities()
    {
        using var stream = typeof(FakeOadmApi).Assembly.GetManifestResourceStream("Oadm.Client.Api.FakeVulnerabilities.json");
        if (stream is null)
        {
            return new VulnerabilityList(DateTimeOffset.UnixEpoch, VulnerabilityList.Sources.Release, []);
        }

        using var reader = new StreamReader(stream);
        return VulnerabilityList.Parse(reader.ReadToEnd());
    }
}
