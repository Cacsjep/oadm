using System.Net;

using Oadm.Core.Devices;
using Oadm.Core.Discovery;
using Oadm.Core.Plugins;
using Oadm.Core.Settings;
using Oadm.Core.Tasks;
using Oadm.Server.Mapping;

using Proto = Oadm.Contracts.V1;
using SdkDeviceStatus = Oadm.Sdk.Devices.DeviceStatus;

namespace Oadm.Server.Tests;

public sealed class MappersTests
{
    [Fact]
    public void DeviceMapsAllColumnsAndLeavesUnknownOptionalsUnset()
    {
        var id = Guid.NewGuid();
        var seen = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
        var device = new Device
        {
            Id = id,
            Serial = "B8A44F631339",
            Address = "10.0.0.48",
            UseHostName = false,
            HostName = "axis-b8a44f631339.local",
            Model = "P3265-V",
            FirmwareVersion = "12.11.77",
            DhcpEnabled = true,
            HttpsEnabled = false,
            Dot1xEnabled = null,
            UpnpFriendlyName = "AXIS P3265-V - B8A44F631339",
            ServerName = "oadm-lab",
            Status = SdkDeviceStatus.CertificateChanged,
            Scheme = DeviceScheme.Https,
            LastSeenUtc = seen,
            Tags = ["lobby"],
        };

        var proto = Mappers.ToProto(device, hasCredentials: true);

        Assert.Equal(id.ToString(), proto.Id);
        Assert.Equal("B8A44F631339", proto.Serial);
        Assert.Equal("P3265-V", proto.Model);
        Assert.Equal("12.11.77", proto.FirmwareVersion);
        Assert.True(proto.HasDhcpEnabled && proto.DhcpEnabled);
        Assert.True(proto.HasHttpsEnabled && !proto.HttpsEnabled);
        Assert.False(proto.HasDot1XEnabled);
        Assert.Equal("oadm-lab", proto.ServerName);
        Assert.Equal(Proto.DeviceStatus.CertificateChanged, proto.Status);
        Assert.Equal("https", proto.Scheme);
        Assert.Equal(seen, proto.LastSeen.ToDateTime());
        Assert.True(proto.HasCredentials);
        Assert.Equal(["lobby"], proto.Tags);
        Assert.Equal(string.Empty, proto.WarrantyExpiry);
        Assert.Null(proto.CertNotAfter);
        Assert.Equal(Proto.CertificateTrust.Unknown, proto.CertTrust);
        Assert.False(proto.HasCertNameMatches);
        Assert.Equal(Proto.DeviceCategory.Unknown, proto.Category);
        Assert.False(proto.HasVideo);
    }

    [Fact]
    public void DeviceMapsCertificateAndCategoryFields()
    {
        var notAfter = new DateTime(2027, 6, 9, 8, 0, 0, DateTimeKind.Utc);
        var device = new Device
        {
            Serial = "B8A44F631339",
            Address = "10.0.0.48",
            ProductType = "Dome Camera",
            Category = Sdk.Devices.DeviceCategory.Camera,
            CertNotAfterUtc = notAfter,
            CertTrust = Core.Vapix.CertificateTrust.SelfSigned,
            CertSubject = "CN=axis-b8a44f631339",
            CertIssuer = "CN=axis-b8a44f631339",
            CertNameMatches = false,
        };

        var proto = Mappers.ToProto(device, hasCredentials: true);

        Assert.Equal(notAfter, proto.CertNotAfter.ToDateTime());
        Assert.Equal(Proto.CertificateTrust.SelfSigned, proto.CertTrust);
        Assert.Equal("CN=axis-b8a44f631339", proto.CertSubject);
        Assert.Equal("CN=axis-b8a44f631339", proto.CertIssuer);
        Assert.True(proto.HasCertNameMatches);
        Assert.False(proto.CertNameMatches);
        Assert.Equal("Dome Camera", proto.ProductType);
        Assert.Equal(Proto.DeviceCategory.Camera, proto.Category);
        Assert.True(proto.HasVideo);
    }

    [Theory]
    [InlineData(Core.Vapix.CertificateTrust.Unknown, Proto.CertificateTrust.Unknown)]
    [InlineData(Core.Vapix.CertificateTrust.Trusted, Proto.CertificateTrust.Trusted)]
    [InlineData(Core.Vapix.CertificateTrust.SelfSigned, Proto.CertificateTrust.SelfSigned)]
    [InlineData(Core.Vapix.CertificateTrust.Untrusted, Proto.CertificateTrust.Untrusted)]
    [InlineData(Core.Vapix.CertificateTrust.Expired, Proto.CertificateTrust.Expired)]
    public void CertificateTrustMapsOneToOne(Core.Vapix.CertificateTrust trust, Proto.CertificateTrust expected)
    {
        Assert.Equal(expected, Mappers.ToProto(trust));
    }

    [Fact]
    public void EveryDeviceCategoryHasAProtoValue()
    {
        foreach (var category in Enum.GetValues<Sdk.Devices.DeviceCategory>())
        {
            Assert.Equal(category.ToString(), Mappers.ToProto(category).ToString());
        }
    }

    [Theory]
    [InlineData(SdkDeviceStatus.Unknown, Proto.DeviceStatus.Unknown)]
    [InlineData(SdkDeviceStatus.Ok, Proto.DeviceStatus.Ok)]
    [InlineData(SdkDeviceStatus.Unreachable, Proto.DeviceStatus.Unreachable)]
    [InlineData(SdkDeviceStatus.CredentialsRequired, Proto.DeviceStatus.CredentialsRequired)]
    [InlineData(SdkDeviceStatus.PasswordNotSet, Proto.DeviceStatus.PasswordNotSet)]
    [InlineData(SdkDeviceStatus.CertificateChanged, Proto.DeviceStatus.CertificateChanged)]
    public void DeviceStatusMapsOneToOne(SdkDeviceStatus status, Proto.DeviceStatus expected)
    {
        Assert.Equal(expected, Mappers.ToProto(status));
    }

    [Fact]
    public void RemovedChangeCarriesOnlyTheId()
    {
        var id = Guid.NewGuid();
        var proto = Mappers.ToProto(new DeviceChange(DeviceChangeKind.Removed, id, null), hasCredentials: false);

        Assert.Equal(Proto.DeviceChanged.Types.Kind.Removed, proto.Kind);
        Assert.Equal(id.ToString(), proto.Device.Id);
        Assert.Equal(string.Empty, proto.Device.Serial);
    }

    [Fact]
    public void WebUiUrlUsesSchemeAndAddress()
    {
        Assert.Equal("https://10.0.0.48/", Mappers.WebUiUrl(new Device { Address = "10.0.0.48", Scheme = DeviceScheme.Https }));
        Assert.Equal("http://cam.local/", Mappers.WebUiUrl(new Device { Address = "cam.local", Scheme = DeviceScheme.Http }));
        Assert.Equal("https://[fe80::1]/", Mappers.WebUiUrl(new Device { Address = "fe80::1", Scheme = DeviceScheme.Https }));
    }

    [Fact]
    public void TaskMapsStatesTimesAndDevices()
    {
        var device = Guid.NewGuid();
        var created = new DateTimeOffset(2026, 10, 7, 10, 0, 0, TimeSpan.Zero);
        var record = new TaskRecord(
            Guid.NewGuid(), "oadm.restart", "Restart", TaskState.Running, "WS01/alice", created, created.AddSeconds(1), null, 40, null,
            [new TaskDeviceRecord(device, TaskState.Failed, "Timeout", 50)]);

        var proto = Mappers.ToProto(new TaskChange(TaskChangeKind.Updated, record));

        Assert.Equal(Proto.TaskChanged.Types.Kind.Updated, proto.Kind);
        Assert.Equal(Proto.TaskState.Running, proto.Task.State);
        Assert.Equal(created, proto.Task.Created.ToDateTimeOffset());
        Assert.NotNull(proto.Task.Started);
        Assert.Null(proto.Task.Finished);
        Assert.Equal(40, proto.Task.Progress);
        var result = Assert.Single(proto.Task.Devices);
        Assert.Equal(Proto.TaskState.Failed, result.State);
        Assert.Equal("Timeout", result.Message);
    }

    [Fact]
    public void TaskPluginInfoCarriesOwnerAndRunnableDevices()
    {
        var owner = new Owner();
        var registered = new RegisteredTaskPlugin(new Owned(), owner, new PluginOrigin("p", "1.0.0", null));
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };

        var proto = Mappers.ToProto(registered, ids);

        Assert.Equal("owned", proto.Id);
        Assert.Equal("owner", proto.OwnerCorePluginId);
        Assert.Equal("key", proto.IconKey);
        Assert.Equal(ids.Select(i => i.ToString()), proto.RunnableDeviceIds);
    }

    [Fact]
    public void SettingsRoundTripAndPartialUpdate()
    {
        var current = new ServerSettings(60, 32, 1500, "srv", "http://0.0.0.0:5080");

        Assert.Equal(current, Mappers.FromProto(Mappers.ToProto(current), current));
        Assert.Equal(current with { ServerName = "new" }, Mappers.FromProto(new Proto.ServerSettings { ServerName = " new " }, current));
        Assert.Equal(10, Mappers.ToProto(current).FullRefreshMinutes);
        Assert.Equal(current with { FullRefreshMinutes = 25 }, Mappers.FromProto(new Proto.ServerSettings { FullRefreshMinutes = 25 }, current));
    }

    [Fact]
    public void DiscoveredDeviceMapsStatusSourceAndProgress()
    {
        var device = new DiscoveredDevice(
            "B8A44F631339", "B8A44F631339", IPAddress.Parse("10.0.0.48"), "axis-b8a44f631339", "P3265-V", "12.11.77",
            DiscoveredDeviceStatus.AnonymousAccess, "https", DiscoverySources.Mdns | DiscoverySources.RangeScan, DateTimeOffset.UtcNow, "Network Speaker");

        var proto = Mappers.ToProto(device, alreadyManaged: true, progressPercent: 42);

        Assert.Equal("B8A44F631339", proto.DiscoveredId);
        Assert.Equal("10.0.0.48", proto.Address);
        Assert.Equal(Proto.DeviceStatus.Ok, proto.Status);
        Assert.Equal(Proto.DiscoverySource.Mdns, proto.Source);
        Assert.True(proto.AlreadyManaged);
        Assert.Equal("Network Speaker", proto.ProductType);
        Assert.Equal(Proto.DeviceCategory.Speaker, proto.Category);
        Assert.Equal(42, proto.ProgressPercent);
        Assert.False(proto.ScanFinished);

        Assert.Equal(Proto.DiscoverySource.RangeScan, Mappers.ToProto(DiscoverySources.RangeScan));
        Assert.Equal(Proto.DeviceStatus.Unknown, Mappers.ToProto(DiscoveredDeviceStatus.Unknown));
        Assert.Equal(Proto.DeviceStatus.PasswordNotSet, Mappers.ToProto(DiscoveredDeviceStatus.PasswordNotSet));

        var final = Mappers.ToProgressProto(100, finished: true);
        Assert.Equal(string.Empty, final.DiscoveredId);
        Assert.True(final.ScanFinished);
    }

    private sealed class Owned : Sdk.Plugins.ITaskPlugin
    {
        public string Id => "owned";

        public string DisplayName => "Owned";

        public string? IconKey => "key";

        public bool ShowInToolbar => false;

        public bool RequiresDialog => false;

        public bool CanRun(Sdk.Devices.IDeviceInfo device) => true;

        public Task ExecuteAsync(Sdk.Plugins.ITaskExecutionContext ctx, Sdk.Devices.IDeviceInfo device, string? payloadJson, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Owner : Sdk.Plugins.ICorePlugin
    {
        public string Id => "owner";

        public string DisplayName => "Owner";

        public string? IconKey => null;

        public IReadOnlyList<Sdk.Plugins.ITaskPlugin> TaskPlugins => [];

        public Task StartAsync(Sdk.Plugins.ICorePluginContext ctx, CancellationToken ct) => Task.CompletedTask;

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => Task.FromResult<string?>(null);
    }
}
