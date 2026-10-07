using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Plugins.Network.Model;

namespace Oadm.Plugins.DhcpServer.Client;

/// <summary>One row of the lease list. Updated in place when the lease changes (no grid reset).</summary>
public sealed partial class LeaseRowViewModel : ObservableObject
{
    public LeaseRowViewModel(LeaseInfo lease, string? device)
    {
        Mac = lease?.Mac ?? throw new ArgumentNullException(nameof(lease));
        Update(lease, device);
    }

    public string Mac { get; }

    [ObservableProperty]
    public partial LeaseInfo Lease { get; private set; }

    [ObservableProperty]
    public partial string Address { get; private set; } = string.Empty;

    /// <summary>Numeric sort key of the address (10.0.0.9 before 10.0.0.10).</summary>
    [ObservableProperty]
    public partial long AddressSortKey { get; private set; }

    /// <summary>"P3265-V (managed)" for a managed device, else the static name or the host name, else "-".</summary>
    [ObservableProperty]
    public partial string HostOrDevice { get; private set; } = "-";

    [ObservableProperty]
    public partial bool IsManaged { get; private set; }

    [ObservableProperty]
    public partial string Type { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDynamic))]
    public partial bool IsStatic { get; private set; }

    public bool IsDynamic => !IsStatic;

    /// <summary>Lower-case text the search matches against.</summary>
    internal string SearchText { get; private set; } = string.Empty;

    /// <summary>Expires column: "in 23 h", "Expired", "Released", "-" for static leases (refreshed with <see cref="RefreshExpires"/>).</summary>
    [ObservableProperty]
    public partial string Expires { get; private set; } = "-";

    /// <summary>Sort key of the Expires column (static last).</summary>
    [ObservableProperty]
    public partial long ExpiresSortKey { get; private set; }

    public void Update(LeaseInfo lease, string? device)
    {
        ArgumentNullException.ThrowIfNull(lease);
        Lease = lease;
        Address = lease.Address;
        AddressSortKey = Ipv4.TryParse(lease.Address, out var a) ? a : 0;
        IsManaged = device is not null;
        HostOrDevice = device is not null ? $"{device} (managed)" : lease.Name ?? lease.HostName ?? "-";
        IsStatic = lease.IsStatic;
        Type = lease.IsStatic ? "Static" : "Dynamic";
        ExpiresSortKey = lease.IsStatic ? long.MaxValue : lease.ExpiresUtc?.Ticks ?? 0;
        SearchText = string.Join('\n', Mac, Address, HostOrDevice, lease.HostName, lease.Name, Type).ToLowerInvariant();
        RefreshExpires(DateTime.UtcNow);
    }

    public void SetDevice(string? device)
    {
        var expected = device is not null ? $"{device} (managed)" : Lease.Name ?? Lease.HostName ?? "-";
        if (expected == HostOrDevice && IsManaged == (device is not null))
        {
            return;
        }

        Update(Lease, device);
    }

    public void RefreshExpires(DateTime now) => Expires = Describe(Lease, now);

    public static string Describe(LeaseInfo lease, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (lease.IsStatic)
        {
            return "-";
        }

        return lease.State switch
        {
            LeaseInfo.Active when lease.ExpiresUtc is { } expires => In(expires - now),
            LeaseInfo.Released => "Released",
            _ => "Expired",
        };
    }

    private static string In(TimeSpan left)
    {
        if (left <= TimeSpan.Zero)
        {
            return "Expired";
        }

        return left.TotalHours >= 1 ? string.Create(CultureInfo.InvariantCulture, $"in {Math.Floor(left.TotalHours):0} h")
            : left.TotalMinutes >= 1 ? string.Create(CultureInfo.InvariantCulture, $"in {Math.Floor(left.TotalMinutes):0} min")
            : "in less than a minute";
    }
}
