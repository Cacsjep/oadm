using Grpc.Core;

using Oadm.Contracts.V1;

namespace Oadm.Client.Api;

/// <summary>
/// Fake mode of the live view's zoom and focus and of "OADM > RTSP port", in memory. Like the development camera, the
/// P3265-V models zoom from 1 to 2.7057 and focus; every other device has neither.
/// </summary>
public sealed partial class FakeOadmApi
{
    /// <summary>Text of an autofocus that "fails", for tests: an autofocus area starting at the far right.</summary>
    public const string FakeFocusNotFound = "The camera could not find focus. Try another autofocus area or set focus by hand.";

    private readonly Dictionary<string, OpticsState> _optics = [];
    private readonly Dictionary<string, int> _rtspPorts = [];

    public async Task<OpticsState> GetOpticsAsync(string deviceId, int sensor, CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(50), ct).ConfigureAwait(false);
        lock (_gate)
        {
            ThrowIfOffline();
            return OpticsOf(deviceId).Clone();
        }
    }

    public async Task<OpticsState> SetOpticsAsync(SetOpticsRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        bool autofocus = request.Autofocus || request.ResetFocus || request.FocusWindow is not null;
        await Task.Delay(TimeSpan.FromMilliseconds(autofocus ? 800 : 50), ct).ConfigureAwait(false);
        lock (_gate)
        {
            ThrowIfOffline();
            var state = OpticsOf(request.DeviceId);
            if (!state.Supported)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "This camera has no remote focus."));
            }

            if (request.HasMagnification)
            {
                state.Magnification = Math.Clamp(request.Magnification, 1, state.MaxMagnification);
            }
            else if (request.HasFocusPosition)
            {
                state.FocusPosition = Math.Clamp(request.FocusPosition, 0, 1);
            }
            else if (request.FocusWindow is { } window)
            {
                if (window.X >= 0.9)
                {
                    throw new RpcException(new Status(StatusCode.FailedPrecondition, FakeFocusNotFound));
                }

                state.FocusWindow = window.Clone();
                state.FocusPosition = 0.55;
            }
            else if (request.ResetFocus)
            {
                state.FocusPosition = 0.6;
            }
            else if (request.Autofocus)
            {
                state.FocusPosition = 0.53;
            }

            return state.Clone();
        }
    }

    /// <summary>The PTZ commands the fake received (tests); the Q6135-LE (10.0.0.22) is a PTZ camera with three presets.</summary>
    public List<MovePtzRequest> PtzMoves { get; } = [];

    public async Task<PtzState> GetPtzAsync(string deviceId, int camera, CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(50), ct).ConfigureAwait(false);
        lock (_gate)
        {
            ThrowIfOffline();
            var model = _devices.Find(d => d.Id == deviceId)?.Model
                ?? throw new RpcException(new Status(StatusCode.NotFound, "device not found"));
            if (!model.Contains("Q6135", StringComparison.Ordinal))
            {
                return new PtzState();
            }

            return new PtzState
            {
                Supported = true,
                CanMove = true,
                CanZoom = true,
                CanCenter = true,
                CanGoHome = true,
                Presets = { "Home", "Gate", "Parking" },
                Zoom = 1,
            };
        }
    }

    public Task MovePtzAsync(MovePtzRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            ThrowIfOffline();
            PtzMoves.Add(request.Clone());
        }

        return Task.CompletedTask;
    }

    public Task<RtspPortReply> GetRtspPortAsync(IReadOnlyCollection<string> deviceIds, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            var ports = deviceIds.Distinct().Select(id => _rtspPorts.GetValueOrDefault(id, 554)).Distinct().ToList();
            return Task.FromResult(new RtspPortReply { Port = ports.Count == 1 ? ports[0] : 554, Mixed = ports.Count > 1 });
        }
    }

    public Task<RtspPortReply> SetRtspPortAsync(IReadOnlyCollection<string> deviceIds, int port, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            if (port is < 0 or > 65535)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "The RTSP port must be from 1 to 65535."));
            }

            foreach (var id in deviceIds)
            {
                if (port is 0 or 554)
                {
                    _rtspPorts.Remove(id);
                }
                else
                {
                    _rtspPorts[id] = port;
                }
            }

            return Task.FromResult(new RtspPortReply { Port = port is 0 ? 554 : port });
        }
    }

    private OpticsState OpticsOf(string deviceId)
    {
        if (_optics.TryGetValue(deviceId, out var state))
        {
            return state;
        }

        var model = _devices.Find(d => d.Id == deviceId)?.Model
            ?? throw new RpcException(new Status(StatusCode.NotFound, "device not found"));
        state = model.Contains("P3265", StringComparison.Ordinal)
            ? new OpticsState
            {
                Supported = true,
                HasZoom = true,
                HasFocus = true,
                MaxMagnification = 2.7057,
                Magnification = 2.38,
                FocusPosition = 0.5367,
                FocusWindow = new OpticsWindow { X = 0.18, Y = 0.11, Width = 0.53, Height = 0.82 },
            }
            : new OpticsState();
        _optics[deviceId] = state;
        return state;
    }
}
