using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

using Microsoft.Extensions.Time.Testing;

using Oadm.Core.Vapix;

namespace Oadm.Core.Tests.Vapix;

/// <summary>
/// The clock offset of the status poll measures only the exchange whose answer carries the Date header
/// (docs/spec/main-window.md "Clock"): a Digest challenge and the connection setup before it are not part of the round
/// trip.
/// </summary>
public sealed class VapixClockRoundTripTests
{
    // A whole second: the header cuts the fraction off.
    private static readonly DateTimeOffset Start = new(2026, 10, 7, 9, 18, 14, 0, TimeSpan.Zero);

    [Fact]
    public async Task TheOffsetUsesOnlyTheFinalAttemptOfTheHandler()
    {
        var time = new FakeTimeProvider(Start);
        using var handler = new FakeHttpMessageHandler((_, _) =>
        {
            // Like the handler's own Digest retry: the first request is written and answered 401, then the authorized one.
            RequestSendClock.MarkWrite();
            time.Advance(TimeSpan.FromSeconds(2)); // connection setup and the 401 challenge
            RequestSendClock.MarkWrite();
            time.Advance(TimeSpan.FromMilliseconds(200)); // the authorized exchange
            var response = Fixtures.Json(Fixtures.Read("basicdeviceinfo-getAllProperties.json"));
            response.Headers.TryAddWithoutValidation("Date", time.GetUtcNow().ToString("r", CultureInfo.InvariantCulture));
            return response;
        });
        using var client = new VapixClient(new Uri("http://10.0.0.48/"), handler, disposeHandler: false);

        var timed = await client.GetTimedBasicDeviceInfoAsync(null, time, CancellationToken.None);

        // Final exchange 16.000 to 16.200, middle 16.100; header "09:18:16" + 0.5 s = 16.500: 400 ms (the whole trip: 1,400).
        Assert.Equal(Start.AddSeconds(2), timed.Sent);
        Assert.Equal(400, timed.OffsetMs);
    }

    [Fact]
    public async Task ADigestChallengeOfARealConnectionIsNotPartOfTheRoundTrip()
    {
        var time = new FakeTimeProvider(Start);
        await using var device = DigestDevice.Start(time, challengeTakes: TimeSpan.FromSeconds(3));
        var baseAddress = new Uri($"http://127.0.0.1:{device.Port}/");
        var pinning = new CertificatePinning();
        using var client = new VapixClient(baseAddress, VapixClient.CreateHandler(baseAddress, new NetworkCredential("root", "pass"), pinning), pinning);

        var timed = await client.GetTimedBasicDeviceInfoAsync(null, time, CancellationToken.None);

        // The 401 took 3 s of server time; the authorized exchange none: middle 17.000, header "09:18:17" + 0.5 s.
        Assert.True(device.Challenged >= 1);
        Assert.True(device.Authorized >= 1);
        Assert.Equal(Start.AddSeconds(3), timed.Sent);
        Assert.Equal(500, timed.OffsetMs);
    }

    /// <summary>
    /// A loopback device: a request without Authorization advances the fake clock and answers a Digest 401, an authorized
    /// one answers basicdeviceinfo with the fake clock in its Date header. HTTP/1.1 with keep-alive.
    /// </summary>
    private sealed class DigestDevice : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly FakeTimeProvider _time;
        private readonly TimeSpan _challengeTakes;
        private readonly List<Task> _connections = [];
        private Task _accept = Task.CompletedTask;
        private int _challenged;
        private int _authorized;

        private DigestDevice(FakeTimeProvider time, TimeSpan challengeTakes)
        {
            _time = time;
            _challengeTakes = challengeTakes;
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int Challenged => Volatile.Read(ref _challenged);

        public int Authorized => Volatile.Read(ref _authorized);

        public static DigestDevice Start(FakeTimeProvider time, TimeSpan challengeTakes)
        {
            var device = new DigestDevice(time, challengeTakes);
            device._listener.Start();
            device._accept = Task.Run(device.AcceptAsync);
            return device;
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            Task[] all;
            lock (_connections)
            {
                all = [_accept, .. _connections];
            }

            try
            {
                await Task.WhenAll(all);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException or IOException or InvalidOperationException)
            {
            }

            _stop.Dispose();
        }

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                var socket = await _listener.AcceptTcpClientAsync(_stop.Token);
                lock (_connections)
                {
                    _connections.Add(Task.Run(() => ServeAsync(socket)));
                }
            }
        }

        private async Task ServeAsync(TcpClient socket)
        {
            using (socket)
            {
                var stream = socket.GetStream();
                var reader = new BufferedReader(stream);
                while (!_stop.IsCancellationRequested)
                {
                    var head = await reader.ReadHeadAsync(_stop.Token);
                    if (head is null)
                    {
                        return;
                    }

                    var length = HeaderValue(head, "Content-Length") is { } value ? int.Parse(value, CultureInfo.InvariantCulture) : 0;
                    await reader.SkipAsync(length, _stop.Token);
                    string response;
                    if (HeaderValue(head, "Authorization") is null)
                    {
                        Interlocked.Increment(ref _challenged);
                        _time.Advance(_challengeTakes);
                        response = "HTTP/1.1 401 Unauthorized\r\n"
                            + "WWW-Authenticate: Digest realm=\"AXIS_ACCC8E000001\", nonce=\"0a1b2c3d4e5f\", algorithm=MD5, qop=\"auth\"\r\n"
                            + "Content-Length: 0\r\n\r\n";
                    }
                    else
                    {
                        Interlocked.Increment(ref _authorized);
                        var body = Fixtures.Read("basicdeviceinfo-getAllProperties.json");
                        response = "HTTP/1.1 200 OK\r\n"
                            + "Date: " + _time.GetUtcNow().ToString("r", CultureInfo.InvariantCulture) + "\r\n"
                            + "Content-Type: application/json\r\n"
                            + "Content-Length: " + Encoding.UTF8.GetByteCount(body).ToString(CultureInfo.InvariantCulture) + "\r\n\r\n"
                            + body;
                    }

                    await stream.WriteAsync(Encoding.UTF8.GetBytes(response), _stop.Token);
                }
            }
        }

        private static string? HeaderValue(string head, string name)
        {
            foreach (var line in head.Split("\r\n"))
            {
                var colon = line.IndexOf(':', StringComparison.Ordinal);
                if (colon > 0 && line[..colon].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return line[(colon + 1)..].Trim();
                }
            }

            return null;
        }
    }

    /// <summary>Reads a request head up to the empty line and skips bodies, over one connection.</summary>
    private sealed class BufferedReader(Stream stream)
    {
        private readonly byte[] _buffer = new byte[16 * 1024];
        private int _start;
        private int _end;

        public async Task<string?> ReadHeadAsync(CancellationToken ct)
        {
            while (true)
            {
                var text = Encoding.ASCII.GetString(_buffer, _start, _end - _start);
                var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (end >= 0)
                {
                    _start += end + 4;
                    return text[..end];
                }

                if (!await FillAsync(ct))
                {
                    return null;
                }
            }
        }

        public async Task SkipAsync(int count, CancellationToken ct)
        {
            while (count > 0)
            {
                if (_end == _start && !await FillAsync(ct))
                {
                    return;
                }

                var take = Math.Min(count, _end - _start);
                _start += take;
                count -= take;
            }
        }

        private async Task<bool> FillAsync(CancellationToken ct)
        {
            if (_start > 0)
            {
                Array.Copy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }

            var read = await stream.ReadAsync(_buffer.AsMemory(_end), ct);
            _end += read;
            return read > 0;
        }
    }
}
