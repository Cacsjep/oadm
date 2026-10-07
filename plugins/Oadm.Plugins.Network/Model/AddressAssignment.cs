using System.Text.Json;

namespace Oadm.Plugins.Network.Model;

/// <summary>A selected device in grid order, as the address assignment sees it.</summary>
public sealed record AssignmentDevice(Guid Id, string CurrentAddress);

/// <summary>Result of <see cref="AddressAssigner.Assign"/>: one address per device (null when the range ran out).</summary>
public sealed record AddressAssignmentResult(IReadOnlyList<string?> Addresses)
{
    public int Missing => Addresses.Count(a => a is null);

    /// <summary>"Not enough addresses" message when the range has fewer free addresses than devices, else null.</summary>
    public string? Error => Missing == 0
        ? null
        : $"Not enough addresses: the IP range has {Addresses.Count - Missing} free {(Addresses.Count - Missing == 1 ? "address" : "addresses")} for {Addresses.Count} devices. Extend the range.";
}

/// <summary>
/// Suggests addresses from an <see cref="IpRangeExpression"/> for devices in grid order, like ADM's "Assign the
/// following IP address range": the first free addresses of the range in order. Never suggested: the network and
/// broadcast address of the subnet, loopback/multicast/link-local addresses, the default router, addresses another
/// managed device has, addresses found in use, and an address twice. A device keeps its own current address when the
/// range reaches it.
/// </summary>
public static class AddressAssigner
{
    public static AddressAssignmentResult Assign(
        IpRangeExpression range,
        int? prefixLength,
        string? gateway,
        IReadOnlyList<AssignmentDevice> devices,
        IReadOnlySet<string>? unavailable = null)
    {
        ArgumentNullException.ThrowIfNull(range);
        ArgumentNullException.ThrowIfNull(devices);
        var prefix = prefixLength ?? 32;
        uint? router = Ipv4.TryParse(gateway, out var g) ? g : null;
        var taken = new HashSet<uint>();
        var result = new string?[devices.Count];
        using var candidates = range.Enumerate(prefix).GetEnumerator();
        for (var i = 0; i < devices.Count; i++)
        {
            var own = Ipv4.TryParse(devices[i].CurrentAddress, out var o) ? o : (uint?)null;
            while (candidates.MoveNext())
            {
                var address = candidates.Current;
                var text = Ipv4.Format(address);
                if (taken.Contains(address) || address == router || Ipv4.HostAddressProblem(address, prefix) is not null
                    || (unavailable?.Contains(text) == true && address != own))
                {
                    continue;
                }

                taken.Add(address);
                result[i] = text;
                break;
            }
        }

        return new AddressAssignmentResult(result);
    }
}

/// <summary>What the server knows about an address (query "checkAddresses").</summary>
/// <param name="Address">The address.</param>
/// <param name="DeviceId">The managed device that has this address, if any.</param>
/// <param name="Device">Label of that device ("P3265-V ACCC8E000001").</param>
/// <param name="InUse">Something answers at this address: a ping or a TCP connection on port 80 or 443.</param>
/// <param name="AnswersPing">The address answered an ICMP echo (ping).</param>
public sealed record AddressStatus(string Address, Guid? DeviceId = null, string? Device = null, bool InUse = false, bool AnswersPing = false)
{
    /// <summary>The row status of an address in use: "In use (answers ping)" or "In use (answers on port 80/443)".</summary>
    public string InUseText => AnswersPing ? "In use (answers ping)" : "In use (answers on port 80/443)";
}

/// <summary>Request of the read-only query "checkAddresses".</summary>
/// <param name="Addresses">Candidate IPv4 or IPv6 addresses to probe (ping and TCP 80/443 connect, at most <see cref="AddressCheckRequest.MaxProbes"/>).</param>
/// <param name="Probe">False: only report managed devices.</param>
public sealed record AddressCheckRequest(IReadOnlyList<string> Addresses, bool Probe = true)
{
    public const int MaxProbes = 256;

    public string ToJson() => JsonSerializer.Serialize(this, PayloadJson.Options);

    public static AddressCheckRequest Parse(string? json) =>
        (string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<AddressCheckRequest>(json, PayloadJson.Options))
        ?? new AddressCheckRequest([]);
}

/// <summary>Answer of "checkAddresses": every managed device address plus the probe result of each requested one.</summary>
public sealed record AddressCheckResponse(IReadOnlyList<AddressStatus> Managed, IReadOnlyList<AddressStatus> Probed)
{
    public string ToJson() => JsonSerializer.Serialize(this, PayloadJson.Options);

    public static AddressCheckResponse Parse(string? json) =>
        (string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<AddressCheckResponse>(json, PayloadJson.Options))
        ?? new AddressCheckResponse([], []);
}

/// <summary>A row of the assignment table, as conflict detection sees it.</summary>
public sealed record AssignmentRow(AssignmentDevice Device, string? NewAddress);

/// <summary>
/// Problems of the new addresses in the assignment table, one message per row (null = fine). The same rules as the
/// server's <see cref="PayloadValidator"/> plus what only the server knows (managed devices, addresses in use).
/// </summary>
public static class AddressConflicts
{
    public static IReadOnlyList<string?> Find(
        IReadOnlyList<AssignmentRow> rows,
        int? prefixLength,
        string? gateway,
        IReadOnlyDictionary<string, AddressStatus>? known = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        uint? router = Ipv4.TryParse(gateway, out var g) ? g : null;
        var counts = rows
            .Select(r => Ipv4.TryParse(r.NewAddress, out var a) ? a : (uint?)null)
            .Where(a => a is not null)
            .GroupBy(a => a!.Value)
            .ToDictionary(x => x.Key, x => x.Count());

        var result = new string?[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var text = row.NewAddress?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                result[i] = "No address";
                continue;
            }

            if (!Ipv4.TryParse(text, out var address))
            {
                result[i] = "Not a valid IPv4 address";
                continue;
            }

            var prefix = prefixLength ?? 32;
            if (Ipv4.HostAddressProblem(address, prefix) is { } problem)
            {
                result[i] = Capitalize(problem);
            }
            else if (address == router)
            {
                result[i] = "Same as the default router";
            }
            else if (router is { } r && prefixLength is { } p && !Ipv4.SameSubnet(address, r, p))
            {
                result[i] = "Outside the subnet of the default router";
            }
            else if (counts.GetValueOrDefault(address) > 1)
            {
                result[i] = "Assigned to more than one device";
            }
            else if (known?.GetValueOrDefault(text) is { DeviceId: { } other } status && other != row.Device.Id)
            {
                result[i] = $"Used by {status.Device ?? "another managed device"}";
            }
            else if (known?.GetValueOrDefault(text) is { InUse: true } inUse && !string.Equals(text, row.Device.CurrentAddress, StringComparison.Ordinal))
            {
                result[i] = inUse.InUseText;
            }
        }

        return result;
    }

    /// <summary>
    /// Problems of the new static IPv6 addresses, one message per row (null = fine): missing, invalid, unusable,
    /// duplicate, another managed device's address, or in use (answers ping or TCP 80/443).
    /// </summary>
    public static IReadOnlyList<string?> FindIpv6(
        IReadOnlyList<AssignmentRow> rows,
        IReadOnlyDictionary<string, AddressStatus>? known = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        // O(n): duplicates and managed addresses are dictionary lookups, never a scan of every row or known address per row.
        var parsed = rows.Select(r => PayloadValidator.TryParseIpv6(r.NewAddress, out var a) ? a : null).ToList();
        var counts = new Dictionary<System.Net.IPAddress, int>();
        foreach (var a in parsed)
        {
            if (a is not null)
            {
                counts[a] = counts.GetValueOrDefault(a) + 1;
            }
        }

        Dictionary<System.Net.IPAddress, AddressStatus>? knownByAddress = null;
        var result = new string?[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            var text = rows[i].NewAddress?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                result[i] = "No IPv6 address";
                continue;
            }

            if (parsed[i] is not { } address)
            {
                result[i] = "Not a valid IPv6 address";
                continue;
            }

            if (PayloadValidator.Ipv6Problem(text) is { } problem)
            {
                result[i] = Capitalize(problem);
            }
            else if (counts.GetValueOrDefault(address) > 1)
            {
                result[i] = "Assigned to more than one device";
            }
            else if ((knownByAddress ??= IndexIpv6(known)).GetValueOrDefault(address) is { DeviceId: { } other } status && other != rows[i].Device.Id)
            {
                result[i] = $"Used by {status.Device ?? "another managed device"}";
            }
            else if (knownByAddress.GetValueOrDefault(address) is { InUse: true } inUse && !AddressCheck.IsOwnAddress(text, [rows[i].Device.CurrentAddress]))
            {
                result[i] = inUse.InUseText;
            }
        }

        return result;
    }

    /// <summary>Known IPv6 addresses by parsed address (any notation); the first status of an address wins.</summary>
    private static Dictionary<System.Net.IPAddress, AddressStatus> IndexIpv6(IReadOnlyDictionary<string, AddressStatus>? known)
    {
        var index = new Dictionary<System.Net.IPAddress, AddressStatus>();
        foreach (var status in known?.Values ?? [])
        {
            if (PayloadValidator.TryParseIpv6(status.Address, out var a))
            {
                index.TryAdd(a, status);
            }
        }

        return index;
    }

    /// <summary>"is the broadcast address of its subnet" becomes "Broadcast address of its subnet".</summary>
    private static string Capitalize(string problem)
    {
        var text = problem.StartsWith("is the ", StringComparison.Ordinal) ? problem[7..]
            : problem.StartsWith("is a ", StringComparison.Ordinal) ? problem[5..]
            : problem.StartsWith("is ", StringComparison.Ordinal) ? problem[3..]
            : problem;
        return char.ToUpperInvariant(text[0]) + text[1..];
    }
}
