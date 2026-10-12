using Avalonia.Controls;

using Microsoft.Extensions.Logging;

using Oadm.Client.Api;
using Oadm.Client.Infrastructure;
using Oadm.Sdk.Client;

namespace Oadm.Client.LiveView;

/// <summary>
/// <see cref="ILiveVideoAccess"/> of the client: plugin pages get the shared live video component (<see cref="LiveVideoViewModel"/>
/// + <see cref="LiveVideoView"/>) with the client-wide settings, the GPU decoding and the role's "Optics Control".
/// </summary>
public sealed class LiveVideoAccess(IOadmApi api, IVideoDecoderFactory decoders, IUiDispatcher ui, ILogger<LiveVideoAccess> logger,
    LiveVideoSettings settings, GpuDecodingService? gpu = null) : ILiveVideoAccess
{
    public ILiveVideo Create(LiveVideoPlace place)
    {
        ArgumentNullException.ThrowIfNull(place);
        var options = new LiveVideoOptions
        {
            ShowSourceSwitch = place.ShowSourceSwitch,
            AllowFullscreen = place.AllowFullscreen,
            AllowQualitySwitch = place.AllowQualitySwitch,
            ShowControlsButton = place.ShowControlsButton,
            ControlsOpen = place.ControlsOpen,
            FullscreenControlsOpen = place.FullscreenControlsOpen,
        };
        return new HostLiveVideo(new LiveVideoViewModel(api, decoders, ui, logger, settings, options, gpu: gpu));
    }

    /// <summary>One live video of a plugin page.</summary>
    internal sealed class HostLiveVideo : ILiveVideo
    {
        private readonly LiveVideoView _view;
        private bool _disposed;

        public HostLiveVideo(LiveVideoViewModel video)
        {
            Video = video;
            _view = new LiveVideoView { DataContext = video };
            Video.CameraChosen += OnCameraChosen;
        }

        internal LiveVideoViewModel Video { get; }

        internal bool IsDisposed => _disposed;

        public Control View => _view;

        public int Camera => Video.Camera;

        public event EventHandler<int>? CameraChosen;

        public void Start(Guid deviceId, int camera = 0)
        {
            if (!_disposed)
            {
                Video.Start(deviceId.ToString(), camera);
            }
        }

        public void Close()
        {
            if (!_disposed)
            {
                Video.Stop();
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Video.CameraChosen -= OnCameraChosen;
            _view.DataContext = null;
            Video.Dispose();
        }

        private void OnCameraChosen(object? sender, int camera) => CameraChosen?.Invoke(this, camera);
    }
}

/// <summary>
/// The live videos of one plugin page: <see cref="Dispose"/> (page removed, plugin window closed, logout) disposes every
/// video the page created and did not dispose itself.
/// </summary>
internal sealed class PageLiveVideoAccess(ILiveVideoAccess host) : ILiveVideoAccess, IDisposable
{
    private readonly List<ILiveVideo> _videos = [];
    private bool _disposed;

    public ILiveVideo Create(LiveVideoPlace place)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _videos.RemoveAll(v => v is LiveVideoAccess.HostLiveVideo { IsDisposed: true }); // the page disposed them itself
        var video = host.Create(place);
        _videos.Add(video);
        return video;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var video in _videos)
        {
            video.Dispose();
        }

        _videos.Clear();
    }
}
