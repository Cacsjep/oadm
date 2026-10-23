using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;

using Google.Protobuf;

using Oadm.Contracts.V1;

namespace Oadm.Client.Api;

/// <summary>
/// Fake DeviceWebService so <c>--fake</c> and the tests show the Web View: every device answers with a small page that
/// names it and the requested path; a WebSocket echoes its messages. Devices with a refused status cannot be opened,
/// like on the server.
/// </summary>
public sealed partial class FakeOadmApi
{
    public const string WebViewPluginId = "oadm.web-view";

    public Task<OpenDeviceWebReply> OpenDeviceWebAsync(string deviceId, CancellationToken ct)
    {
        Device? device;
        lock (_gate)
        {
            ThrowIfOffline();
            device = _devices.Find(d => d.Id == deviceId && InViewLocked(d));
        }

        if (device is null)
        {
            // Like the server: a removed device and one outside the user's device group answer the same.
            throw new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.NotFound, Oadm.Sdk.Devices.DeviceMessages.NoLongerAvailable(1)));
        }

        var refusal = device.Status switch
        {
            DeviceStatus.CertificateChanged => Oadm.Sdk.Devices.DeviceMessages.CertificateChanged,
            DeviceStatus.CredentialsRequired => Oadm.Sdk.Devices.DeviceMessages.CredentialsRequired,
            DeviceStatus.PasswordNotSet => Oadm.Sdk.Devices.DeviceMessages.PasswordNotSet,
            DeviceStatus.SiteOffline => FakeSiteOfflineText(device),
            _ => null,
        };
        return Task.FromResult(refusal is null ? new OpenDeviceWebReply { Ok = true } : new OpenDeviceWebReply { Ok = false, Message = refusal });
    }

    public IDeviceWebCall ExchangeDeviceWeb(CancellationToken ct) => new FakeDeviceWebCall(this);

    private string FakeDeviceTitle(string deviceId)
    {
        lock (_gate)
        {
            var device = _devices.Find(d => d.Id == deviceId);
            return device is null ? "Removed device" : $"AXIS {device.Model} ({device.Address})";
        }
    }

    /// <summary>One fake Exchange: answers after the request (and its body) is complete; WebSockets echo.</summary>
    private sealed class FakeDeviceWebCall(FakeOadmApi api) : IDeviceWebCall
    {
        private readonly Channel<DeviceWebFrame> _replies = Channel.CreateUnbounded<DeviceWebFrame>();
        private DeviceWebRequestHead? _head;
        private long _bodyBytes;

        public Task SendAsync(DeviceWebFrame frame, CancellationToken ct)
        {
            switch (frame.KindCase)
            {
                case DeviceWebFrame.KindOneofCase.Request:
                    _head = frame.Request;
                    if (_head.Websocket)
                    {
                        _replies.Writer.TryWrite(new DeviceWebFrame { Response = new DeviceWebResponseHead { Status = 101, SubProtocol = _head.SubProtocols.FirstOrDefault() ?? string.Empty } });
                    }
                    else if (!_head.HasBody)
                    {
                        Answer();
                    }

                    break;
                case DeviceWebFrame.KindOneofCase.Body:
                    _bodyBytes += frame.Body.Length;
                    break;
                case DeviceWebFrame.KindOneofCase.End:
                    Answer();
                    break;
                case DeviceWebFrame.KindOneofCase.Message:
                    _replies.Writer.TryWrite(new DeviceWebFrame { Message = frame.Message });
                    break;
                case DeviceWebFrame.KindOneofCase.Close:
                    _replies.Writer.TryWrite(new DeviceWebFrame { Close = frame.Close });
                    _replies.Writer.TryComplete();
                    break;
            }

            return Task.CompletedTask;
        }

        public Task CompleteAsync()
        {
            if (_head is { Websocket: true })
            {
                _replies.Writer.TryComplete();
            }

            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<DeviceWebFrame> ReadAllAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var frame in _replies.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return frame;
            }
        }

        public void Dispose() => _replies.Writer.TryComplete();

        private void Answer()
        {
            var head = _head!;
            var title = api.FakeDeviceTitle(head.DeviceId);
            var body = Encoding.UTF8.GetBytes("<!doctype html><html><head><meta charset=\"utf-8\"><title>" + WebUtility.HtmlEncode(title) + "</title></head>"
                + "<body style=\"font-family:sans-serif;background:#f5f5f5;color:#222;padding:24px\"><h1>" + WebUtility.HtmlEncode(title) + "</h1>"
                + "<p>Web interface (fake mode): " + WebUtility.HtmlEncode(head.Method + " " + head.PathAndQuery) + "</p>"
                + (_bodyBytes > 0 ? "<p>Body: " + _bodyBytes + " bytes</p>" : string.Empty) + "</body></html>");
            var response = new DeviceWebResponseHead { Status = 200, Reason = "OK" };
            response.Headers.Add(new WebHeader { Name = "Content-Type", Value = "text/html; charset=utf-8" });
            response.Headers.Add(new WebHeader { Name = "Content-Length", Value = body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            _replies.Writer.TryWrite(new DeviceWebFrame { Response = response });
            _replies.Writer.TryWrite(new DeviceWebFrame { Body = ByteString.CopyFrom(body) });
            _replies.Writer.TryWrite(new DeviceWebFrame { End = true });
            _replies.Writer.TryComplete();
        }
    }
}
