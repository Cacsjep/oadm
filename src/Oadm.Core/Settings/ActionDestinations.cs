using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Oadm.Core.Settings;

/// <summary>
/// Where event rules of device groups may send (docs/spec/device-scope.md, Decisions: Send HTTP request / Send TCP
/// message): an administrator's list of "host" or "host:port" entries (setting <see cref="SettingKey"/>, a JSON array). A
/// host without a port allows every port. Hosts compare case-insensitively as written (no name resolution): a rule that
/// sends to a name must list that name. Rules of users with all devices are not limited.
/// </summary>
public sealed class ActionDestinations(ServerSettingsStore store)
{
    /// <summary>The server setting (JSON array of strings).</summary>
    public const string SettingKey = "Events.ActionDestinations";

    /// <summary>Most entries.</summary>
    public const int MaxEntries = 100;

    private volatile IReadOnlyList<Entry>? _entries;

    /// <summary>The entries as stored (normalized: trimmed, "host" or "host:port", IPv6 in brackets with a port).</summary>
    public async Task<IReadOnlyList<string>> ListAsync(CancellationToken ct) =>
        [.. (await LoadAsync(ct).ConfigureAwait(false)).Select(e => e.Text)];

    /// <summary>Validates and stores the list (replaces it). Throws <see cref="ArgumentException"/> with the first problem.</summary>
    public async Task<IReadOnlyList<string>> SetAsync(IEnumerable<string> entries, CancellationToken ct)
    {
        var parsed = Normalize(entries);
        await store.SetJsonAsync(SettingKey, JsonSerializer.Serialize(parsed.Select(p => p.Text)), ct).ConfigureAwait(false);
        _entries = parsed;
        return [.. parsed.Select(p => p.Text)];
    }

    /// <summary>The entries checked and normalized (duplicates once). Throws <see cref="ArgumentException"/> with the first problem.</summary>
    public static IReadOnlyList<Entry> Normalize(IEnumerable<string> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var parsed = new List<Entry>();
        foreach (var raw in entries.Where(e => !string.IsNullOrWhiteSpace(e)))
        {
            var entry = Parse(raw) ?? throw new ArgumentException($"\"{raw.Trim()}\" is not a host name or IP address with an optional port.");
            if (!parsed.Any(p => p.Text.Equals(entry.Text, StringComparison.OrdinalIgnoreCase)))
            {
                parsed.Add(entry);
            }
        }

        if (parsed.Count > MaxEntries)
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"At most {MaxEntries} addresses."));
        }

        return parsed;
    }

    /// <summary>Whether a rule of a device group may send to <paramref name="host"/>:<paramref name="port"/> (0 = any port).</summary>
    public bool Allows(string? host, int port)
    {
        var entries = _entries ?? [];
        var clean = Host(host);
        return clean.Length > 0 && entries.Any(e => e.Host.Equals(clean, StringComparison.OrdinalIgnoreCase) && (e.Port == 0 || e.Port == port));
    }

    /// <summary>Reads the list once (later writes keep it current).</summary>
    public async Task<IReadOnlyList<Entry>> LoadAsync(CancellationToken ct)
    {
        if (_entries is { } entries)
        {
            return entries;
        }

        var json = await store.GetJsonAsync(SettingKey, ct).ConfigureAwait(false);
        List<Entry> loaded = [];
        try
        {
            foreach (var text in json is null ? [] : JsonSerializer.Deserialize<List<string>>(json) ?? [])
            {
                if (Parse(text) is { } entry)
                {
                    loaded.Add(entry);
                }
            }
        }
        catch (JsonException)
        {
            loaded = []; // unreadable: nothing allowed (fail closed)
        }

        _entries = loaded;
        return loaded;
    }

    /// <summary>"vms.example.com", "10.0.0.5:8080", "[fe80::1]:80", "fe80::1"; null for anything else.</summary>
    public static Entry? Parse(string? text)
    {
        var value = (text ?? string.Empty).Trim();
        if (value.Length == 0 || value.Length > 300 || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) || value.Contains('/', StringComparison.Ordinal))
        {
            return null;
        }

        string host;
        var port = 0;
        if (value.StartsWith('['))
        {
            var end = value.IndexOf(']', StringComparison.Ordinal);
            if (end < 0)
            {
                return null;
            }

            host = value[1..end];
            var rest = value[(end + 1)..];
            if (rest.Length > 0 && (!rest.StartsWith(':') || !TryPort(rest[1..], out port)))
            {
                return null;
            }
        }
        else if (value.Count(c => c == ':') == 1)
        {
            var colon = value.IndexOf(':', StringComparison.Ordinal);
            host = value[..colon];
            if (!TryPort(value[(colon + 1)..], out port))
            {
                return null;
            }
        }
        else
        {
            host = value; // a name, an IPv4 address or an IPv6 address without a port
        }

        host = Host(host);
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown)
        {
            return null;
        }

        var isV6 = IPAddress.TryParse(host, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
        var shown = port == 0 ? host : (isV6 ? $"[{host}]" : host) + ":" + port.ToString(CultureInfo.InvariantCulture);
        return new Entry(shown, host, port);
    }

    private static string Host(string? host) => (host ?? string.Empty).Trim().Trim('[', ']').TrimEnd('.');

    private static bool TryPort(string text, out int port) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535;

    /// <summary>One allowed address: as shown, its host and port (0 = every port).</summary>
    public sealed record Entry(string Text, string Host, int Port);
}
