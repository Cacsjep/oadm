using System.Globalization;
using System.Net;

using Microsoft.Extensions.Logging;

using Oadm.Core.Discovery;
using Oadm.Core.Discovery.Mdns;

// Manual test tool for OADM discovery. Read-only: never sends credentials, never writes to devices.
//
//   mdns  [seconds=15] [captureDir]    browse _axis-video._tcp.local, optionally save raw packets
//   scan  <from> <to> [parallelism=32] [timeoutMs=1500]
//   probe <address>                    probe a single address
//   all   <from> <to> [seconds=15]     mDNS + range scan through DiscoveryService (deduplicated)
using var loggerFactory = LoggerFactory.Create(b => b
    .AddSimpleConsole(o => o.SingleLine = true)
    .SetMinimumLevel(Environment.GetEnvironmentVariable("OADM_PROBE_DEBUG") is null ? LogLevel.Information : LogLevel.Debug));

if (args.Length == 0)
{
    Console.WriteLine("usage: mdns [seconds] [captureDir] | scan <from> <to> [parallelism] [timeoutMs] | probe <address> | all <from> <to> [seconds]");
    return 1;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

switch (args[0])
{
    case "mdns":
        await RunMdnsAsync(args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 15, args.Length > 2 ? args[2] : null);
        break;
    case "scan":
        await RunScanAsync(
            IPAddress.Parse(args[1]),
            IPAddress.Parse(args[2]),
            args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 32,
            args.Length > 4 ? int.Parse(args[4], CultureInfo.InvariantCulture) : 1500);
        break;
    case "probe":
        await RunProbeAsync(IPAddress.Parse(args[1]));
        break;
    case "all":
        await RunAllAsync(IPAddress.Parse(args[1]), IPAddress.Parse(args[2]), args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 15);
        break;
    default:
        Console.WriteLine($"unknown command {args[0]}");
        return 1;
}

return 0;

async Task RunMdnsAsync(int seconds, string? captureDir)
{
    Console.WriteLine($"Interfaces: {string.Join(", ", MdnsBrowser.GetMulticastInterfaceAddresses())}");
    var packetNo = 0;
    if (captureDir is not null)
    {
        Directory.CreateDirectory(captureDir);
    }

    var options = new MdnsBrowseOptions
    {
        PacketCapture = captureDir is null
            ? null
            : (data, remote) =>
            {
                var n = Interlocked.Increment(ref packetNo);
                var file = Path.Combine(captureDir, $"{n:D3}_{remote.Address}_{remote.Port}.bin");
                File.WriteAllBytes(file, data.ToArray());
            },
    };

    var browser = new MdnsBrowser(loggerFactory.CreateLogger<MdnsBrowser>());
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
    timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
    try
    {
        await foreach (var i in browser.BrowseAsync(options, timeout.Token))
        {
            Console.WriteLine($"[mdns] {string.Join(",", i.Addresses.Select(a => a.ToString()))} serial={i.Serial} host={i.HostName} port={i.Port} name=\"{i.DisplayName}\" via={i.LocalAddress}");
            Console.WriteLine($"       txt: {string.Join("; ", i.Txt.Select(kv => $"{kv.Key}={kv.Value}"))}");
        }
    }
    catch (OperationCanceledException)
    {
    }

    Console.WriteLine($"captured packets: {packetNo}");
}

async Task RunScanAsync(IPAddress from, IPAddress to, int parallelism, int timeoutMs)
{
    using var probe = new VapixDeviceProbe(TimeSpan.FromMilliseconds(timeoutMs), loggerFactory.CreateLogger<VapixDeviceProbe>());
    var scanner = new RangeScanner(probe);
    var range = Ipv4Range.Create(from, to);
    var progress = new Progress<int>(p =>
    {
        if (p % 10 == 0)
        {
            Console.WriteLine($"progress {p}%");
        }
    });
    var started = DateTime.UtcNow;
    var found = 0;
    await foreach (var r in scanner.ScanAsync(range, new RangeScanOptions { Parallelism = parallelism }, progress, cts.Token))
    {
        found++;
        Print("scan", r);
    }

    Console.WriteLine($"done: {found} Axis devices in {(DateTime.UtcNow - started).TotalSeconds:F1} s");
}

async Task RunProbeAsync(IPAddress address)
{
    using var probe = new VapixDeviceProbe(TimeSpan.FromMilliseconds(3000), loggerFactory.CreateLogger<VapixDeviceProbe>());
    var r = await probe.ProbeAsync(address, cts.Token);
    if (r is null)
    {
        Console.WriteLine("not an Axis device");
    }
    else
    {
        Print("probe", r);
    }
}

async Task RunAllAsync(IPAddress from, IPAddress to, int seconds)
{
    using var probe = new VapixDeviceProbe(TimeSpan.FromMilliseconds(1500), loggerFactory.CreateLogger<VapixDeviceProbe>());
    await using var service = new DiscoveryService(
        new MdnsBrowser(loggerFactory.CreateLogger<MdnsBrowser>()),
        new RangeScanner(probe),
        probe,
        loggerFactory.CreateLogger<DiscoveryService>());

    var mdns = service.StartZeroConf();
    var scan = service.StartRangeScan(from, to);
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
    timeout.CancelAfter(TimeSpan.FromSeconds(seconds));

    var devices = new Dictionary<string, DiscoveredDevice>();
    async Task WatchAsync(DiscoverySession session)
    {
        try
        {
            await foreach (var e in service.WatchAsync(session.Id, timeout.Token))
            {
                if (e.Device is { } d)
                {
                    lock (devices)
                    {
                        devices[d.Serial] = d;
                    }

                    Console.WriteLine($"[{session.Kind}] {d.Address,-15} {d.Serial} {d.Model ?? "?"} status={d.Status} scheme={d.Scheme} host={d.HostName} source={d.Sources}");
                }

                if (e.Finished)
                {
                    Console.WriteLine($"[{session.Kind}] finished");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    await Task.WhenAll(WatchAsync(mdns), WatchAsync(scan));
    await service.StopAsync(mdns.Id);
    await service.StopAsync(scan.Id);

    Console.WriteLine();
    Console.WriteLine("Summary (deduplicated per session):");
    foreach (var d in devices.Values.OrderBy(d => d.Address.GetAddressBytes(), Comparer<byte[]>.Create((a, b) => a.AsSpan().SequenceCompareTo(b))))
    {
        Console.WriteLine($"  {d.Address,-15} {d.Serial} {d.Model ?? "?",-14} {d.Status,-20} {d.Sources}");
    }
}

static void Print(string tag, DeviceProbeResult r)
    => Console.WriteLine($"[{tag}] {r.Address,-15} serial={r.Serial} model={r.Model ?? "?"} fw={r.FirmwareVersion ?? "?"} status={r.Status} scheme={r.Scheme} anonymousInfo={r.AnonymousFullAccess}");
