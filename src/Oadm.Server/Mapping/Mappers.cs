using Google.Protobuf.WellKnownTypes;

using Oadm.Core.Devices;
using Oadm.Core.Discovery;
using Oadm.Core.Plugins;
using Oadm.Core.Settings;
using Oadm.Core.Tasks;
using Oadm.Core.Vapix;

using Proto = Oadm.Contracts.V1;
using DeviceCategory = Oadm.Sdk.Devices.DeviceCategory;
using SdkDeviceStatus = Oadm.Sdk.Devices.DeviceStatus;

namespace Oadm.Server.Mapping;

/// <summary>All conversions between Core types and the gRPC contracts. Pure functions.</summary>
public static class Mappers
{
    public static Proto.Device ToProto(Device device, bool hasCredentials)
    {
        ArgumentNullException.ThrowIfNull(device);
        var proto = new Proto.Device
        {
            Id = device.Id.ToString(),
            Serial = device.Serial,
            Address = device.Address,
            UseHostName = device.UseHostName,
            HostName = device.HostName ?? string.Empty,
            Model = device.Model ?? string.Empty,
            FirmwareVersion = device.FirmwareVersion ?? string.Empty,
            UpnpFriendlyName = device.UpnpFriendlyName ?? string.Empty,
            ServerName = device.ServerName ?? string.Empty,
            Status = ToProto(device.Status),
            Scheme = ToSchemeString(device.Scheme),
            HasCredentials = hasCredentials,
            WarrantyExpiry = device.WarrantyExpiry?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            ReplacementModel = device.ReplacementModel ?? string.Empty,
            ProductType = device.ProductType ?? string.Empty,
            Category = ToProto(device.Category),
            HasVideo = device.HasVideo,
            CertTrust = ToProto(device.CertTrust),
            CertSubject = device.CertSubject ?? string.Empty,
            CertIssuer = device.CertIssuer ?? string.Empty,
        };

        if (device.CertNotAfterUtc is { } notAfter)
        {
            proto.CertNotAfter = Timestamp.FromDateTime(DateTime.SpecifyKind(notAfter, DateTimeKind.Utc));
        }

        if (device.CertNameMatches is { } nameMatches)
        {
            proto.CertNameMatches = nameMatches;
        }

        if (device.DhcpEnabled is { } dhcp)
        {
            proto.DhcpEnabled = dhcp;
        }

        if (device.HttpsEnabled is { } https)
        {
            proto.HttpsEnabled = https;
        }

        if (device.Dot1xEnabled is { } dot1x)
        {
            proto.Dot1XEnabled = dot1x;
        }

        if (device.LastSeenUtc is { } seen)
        {
            proto.LastSeen = Timestamp.FromDateTime(DateTime.SpecifyKind(seen, DateTimeKind.Utc));
        }

        proto.Tags.AddRange(device.Tags);
        proto.Apis.AddRange(device.Apis.Select(ToProto));
        return proto;
    }

    public static Proto.DeviceApi ToProto(Sdk.Vapix.DeviceApi api)
    {
        ArgumentNullException.ThrowIfNull(api);
        return new Proto.DeviceApi
        {
            Id = api.Id,
            Version = api.Version,
            Name = api.Name ?? string.Empty,
            Status = api.Status ?? string.Empty,
        };
    }

    public static Proto.DeviceChanged ToProto(DeviceChange change, bool hasCredentials)
    {
        ArgumentNullException.ThrowIfNull(change);
        return new Proto.DeviceChanged
        {
            Kind = change.Kind switch
            {
                DeviceChangeKind.Added => Proto.DeviceChanged.Types.Kind.Added,
                DeviceChangeKind.Updated => Proto.DeviceChanged.Types.Kind.Updated,
                DeviceChangeKind.Removed => Proto.DeviceChanged.Types.Kind.Removed,
                _ => Proto.DeviceChanged.Types.Kind.Unspecified,
            },
            Device = change.Device is null || change.Kind == DeviceChangeKind.Removed
                ? new Proto.Device { Id = change.DeviceId.ToString() }
                : ToProto(change.Device, hasCredentials),
        };
    }

    public static Proto.DeviceStatus ToProto(SdkDeviceStatus status) => status switch
    {
        SdkDeviceStatus.Ok => Proto.DeviceStatus.Ok,
        SdkDeviceStatus.Unreachable => Proto.DeviceStatus.Unreachable,
        SdkDeviceStatus.CredentialsRequired => Proto.DeviceStatus.CredentialsRequired,
        SdkDeviceStatus.PasswordNotSet => Proto.DeviceStatus.PasswordNotSet,
        SdkDeviceStatus.CertificateChanged => Proto.DeviceStatus.CertificateChanged,
        _ => Proto.DeviceStatus.Unknown,
    };

    public static Proto.DeviceCategory ToProto(DeviceCategory category) => category switch
    {
        DeviceCategory.Camera => Proto.DeviceCategory.Camera,
        DeviceCategory.Encoder => Proto.DeviceCategory.Encoder,
        DeviceCategory.Speaker => Proto.DeviceCategory.Speaker,
        DeviceCategory.Audio => Proto.DeviceCategory.Audio,
        DeviceCategory.Intercom => Proto.DeviceCategory.Intercom,
        DeviceCategory.Radar => Proto.DeviceCategory.Radar,
        DeviceCategory.IoModule => Proto.DeviceCategory.IoModule,
        DeviceCategory.DoorController => Proto.DeviceCategory.DoorController,
        DeviceCategory.Other => Proto.DeviceCategory.Other,
        _ => Proto.DeviceCategory.Unknown,
    };

    public static Proto.CertificateTrust ToProto(CertificateTrust trust) => trust switch
    {
        CertificateTrust.Trusted => Proto.CertificateTrust.Trusted,
        CertificateTrust.SelfSigned => Proto.CertificateTrust.SelfSigned,
        CertificateTrust.Untrusted => Proto.CertificateTrust.Untrusted,
        CertificateTrust.Expired => Proto.CertificateTrust.Expired,
        _ => Proto.CertificateTrust.Unknown,
    };

    public static string ToSchemeString(DeviceScheme scheme) =>
        scheme == DeviceScheme.Http ? Uri.UriSchemeHttp : Uri.UriSchemeHttps;

    public static DeviceScheme ToDeviceScheme(string? scheme) =>
        string.Equals(scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ? DeviceScheme.Http : DeviceScheme.Https;

    /// <summary>Address of the device web UI: scheme://address/.</summary>
    public static string WebUiUrl(Device device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return VapixClient.BuildBaseAddress(ToSchemeString(device.Scheme), device.Address).ToString();
    }

    public static Proto.TaskState ToProto(TaskState state) => state switch
    {
        TaskState.Queued => Proto.TaskState.Queued,
        TaskState.Running => Proto.TaskState.Running,
        TaskState.Done => Proto.TaskState.Done,
        TaskState.Failed => Proto.TaskState.Failed,
        TaskState.Cancelled => Proto.TaskState.Cancelled,
        TaskState.DoneWithWarnings => Proto.TaskState.DoneWithWarnings,
        _ => Proto.TaskState.Unspecified,
    };

    public static Proto.TaskLogLevel ToProto(Sdk.Plugins.TaskLogLevel level) => level switch
    {
        Sdk.Plugins.TaskLogLevel.Info => Proto.TaskLogLevel.Info,
        Sdk.Plugins.TaskLogLevel.Warning => Proto.TaskLogLevel.Warning,
        Sdk.Plugins.TaskLogLevel.Error => Proto.TaskLogLevel.Error,
        _ => Proto.TaskLogLevel.Unspecified,
    };

    public static Proto.TaskLogEntry ToProto(TaskLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new Proto.TaskLogEntry
        {
            Time = Timestamp.FromDateTimeOffset(entry.TimeUtc),
            DeviceId = entry.DeviceId?.ToString() ?? string.Empty,
            Level = ToProto(entry.Level),
            Message = entry.Message,
        };
    }

    public static Proto.UploadedFileInfo ToProto(Sdk.Plugins.UploadedFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return new Proto.UploadedFileInfo { Id = file.Id, Name = file.Name, Size = file.Size, Sha256 = file.Sha256 };
    }

    public static Proto.TaskInfo ToProto(TaskRecord task)
    {
        ArgumentNullException.ThrowIfNull(task);
        var proto = new Proto.TaskInfo
        {
            Id = task.Id.ToString(),
            PluginId = task.PluginId,
            Name = task.Name,
            State = ToProto(task.State),
            Owner = task.Owner,
            Created = Timestamp.FromDateTimeOffset(task.CreatedUtc),
            Progress = task.Progress,
        };

        if (task.StartedUtc is { } started)
        {
            proto.Started = Timestamp.FromDateTimeOffset(started);
        }

        if (task.FinishedUtc is { } finished)
        {
            proto.Finished = Timestamp.FromDateTimeOffset(finished);
        }

        proto.Devices.AddRange(task.Devices.Select(d => new Proto.TaskDeviceResult
        {
            DeviceId = d.DeviceId.ToString(),
            State = ToProto(d.State),
            Message = d.Message ?? string.Empty,
            Progress = d.Progress,
        }));
        return proto;
    }

    public static Proto.TaskChanged ToProto(TaskChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return new Proto.TaskChanged
        {
            Kind = change.Kind switch
            {
                TaskChangeKind.Added => Proto.TaskChanged.Types.Kind.Added,
                TaskChangeKind.Updated => Proto.TaskChanged.Types.Kind.Updated,
                TaskChangeKind.Removed => Proto.TaskChanged.Types.Kind.Removed,
                _ => Proto.TaskChanged.Types.Kind.Unspecified,
            },
            Task = ToProto(change.Task),
        };
    }

    public static Proto.TaskPluginInfo ToProto(RegisteredTaskPlugin plugin, IEnumerable<Guid> runnableDeviceIds)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(runnableDeviceIds);
        var proto = new Proto.TaskPluginInfo
        {
            Id = plugin.Id,
            DisplayName = plugin.Plugin.DisplayName,
            IconKey = plugin.Plugin.IconKey ?? string.Empty,
            ShowInToolbar = plugin.Plugin.ShowInToolbar,
            RequiresDialog = plugin.Plugin.RequiresDialog,
            OwnerCorePluginId = plugin.Owner?.Id ?? string.Empty,
        };
        proto.RunnableDeviceIds.AddRange(runnableDeviceIds.Select(id => id.ToString()));
        return proto;
    }

    public static Proto.CorePluginInfo ToProto(RegisteredCorePlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        return new Proto.CorePluginInfo
        {
            Id = plugin.Id,
            DisplayName = plugin.Plugin.DisplayName,
            IconKey = plugin.Plugin.IconKey ?? string.Empty,
        };
    }

    public static Proto.ServerSettings ToProto(ServerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new Proto.ServerSettings
        {
            PollingIntervalSeconds = settings.PollingIntervalSeconds,
            ScanParallelism = settings.ScanParallelism,
            ScanTimeoutMs = settings.ScanTimeoutMs,
            ServerName = settings.ServerName,
            ListenUrl = settings.ListenUrl,
            FullRefreshMinutes = settings.FullRefreshMinutes,
        };
    }

    /// <summary>Proto to Core. Zero or empty fields keep the <paramref name="current"/> value (partial update).</summary>
    public static ServerSettings FromProto(Proto.ServerSettings proto, ServerSettings current)
    {
        ArgumentNullException.ThrowIfNull(proto);
        ArgumentNullException.ThrowIfNull(current);
        return new ServerSettings(
            proto.PollingIntervalSeconds == 0 ? current.PollingIntervalSeconds : proto.PollingIntervalSeconds,
            proto.ScanParallelism == 0 ? current.ScanParallelism : proto.ScanParallelism,
            proto.ScanTimeoutMs == 0 ? current.ScanTimeoutMs : proto.ScanTimeoutMs,
            string.IsNullOrWhiteSpace(proto.ServerName) ? current.ServerName : proto.ServerName.Trim(),
            string.IsNullOrWhiteSpace(proto.ListenUrl) ? current.ListenUrl : proto.ListenUrl.Trim(),
            proto.FullRefreshMinutes == 0 ? current.FullRefreshMinutes : proto.FullRefreshMinutes);
    }

    public static Proto.DeviceStatus ToProto(DiscoveredDeviceStatus status) => status switch
    {
        DiscoveredDeviceStatus.PasswordNotSet => Proto.DeviceStatus.PasswordNotSet,
        DiscoveredDeviceStatus.CredentialsRequired => Proto.DeviceStatus.CredentialsRequired,
        DiscoveredDeviceStatus.AnonymousAccess => Proto.DeviceStatus.Ok,
        DiscoveredDeviceStatus.Unreachable => Proto.DeviceStatus.Unreachable,
        _ => Proto.DeviceStatus.Unknown,
    };

    public static Proto.DiscoverySource ToProto(DiscoverySources sources) =>
        sources.HasFlag(DiscoverySources.Mdns) ? Proto.DiscoverySource.Mdns
        : sources.HasFlag(DiscoverySources.RangeScan) ? Proto.DiscoverySource.RangeScan
        : Proto.DiscoverySource.Unspecified;

    public static Proto.DiscoveredDevice ToProto(DiscoveredDevice device, bool alreadyManaged, int progressPercent)
    {
        ArgumentNullException.ThrowIfNull(device);
        return new Proto.DiscoveredDevice
        {
            DiscoveredId = device.DiscoveredId,
            Serial = device.Serial,
            Address = device.Address.ToString(),
            HostName = device.HostName ?? string.Empty,
            Model = device.Model ?? string.Empty,
            Status = ToProto(device.Status),
            AlreadyManaged = alreadyManaged,
            Source = ToProto(device.Sources),
            Scheme = device.Scheme ?? string.Empty,
            ProgressPercent = progressPercent,
            ProductType = device.ProductType ?? string.Empty,
            Category = ToProto(DeviceCategoryMapper.Map(device.ProductType)),
        };
    }

    /// <summary>A progress-only (or final) message of a range scan: no discovered_id, only progress and scan_finished.</summary>
    public static Proto.DiscoveredDevice ToProgressProto(int progressPercent, bool finished) => new()
    {
        ProgressPercent = progressPercent,
        ScanFinished = finished,
    };
}
