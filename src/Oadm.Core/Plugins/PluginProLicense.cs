using Oadm.Sdk.Licensing;

namespace Oadm.Core.Plugins;

/// <summary>
/// The OADM PRO license as one core plugin run sees it: forwards <see cref="IProLicense.Changed"/> while the plugin runs
/// and drops every handler it subscribed when the plugin stops (<see cref="Dispose"/>), so a stopped plugin's handlers
/// never run and never pile up over restarts.
/// </summary>
public sealed class PluginProLicense : IProLicense, IDisposable
{
    private readonly IProLicense _inner;
    private readonly Lock _gate = new();
    private EventHandler? _handlers;
    private bool _disposed;

    public PluginProLicense(IProLicense inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _inner.Changed += OnChanged;
    }

    public ProLicenseInfo Current => _inner.Current;

    public event EventHandler? Changed
    {
        add
        {
            lock (_gate)
            {
                if (!_disposed)
                {
                    _handlers += value;
                }
            }
        }

        remove
        {
            lock (_gate)
            {
                _handlers -= value;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _handlers = null;
        }

        _inner.Changed -= OnChanged;
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        EventHandler? handlers;
        lock (_gate)
        {
            handlers = _handlers;
        }

        Auth.CallerContext.Raise(handlers, this, e); // the plugin's handlers never see the user whose call changed the license
    }
}
