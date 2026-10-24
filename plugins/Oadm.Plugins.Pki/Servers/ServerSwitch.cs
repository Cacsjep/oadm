using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Sdk.Licensing;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Pki.Servers;

/// <summary>
/// The page methods and texts of Turn on / Turn off of the servers in PKI (the 802.1X and EST servers, docs/spec/pki.md
/// "One plugin"). The same on both pages.
/// </summary>
public static class ServerSwitchMethods
{
    /// <summary>() -> <see cref="ServerSwitchState"/> (everyone with the page).</summary>
    public const string Get = "getServer";

    /// <summary>() -> <see cref="ServerSwitchState"/>: stores "on" and starts the server (Admin, "manage", audited).</summary>
    public const string TurnOn = "turnOn";

    /// <summary>() -> <see cref="ServerSwitchState"/>: stores "off" and stops the server (Admin, "manage", audited).</summary>
    public const string TurnOff = "turnOff";

    /// <summary>Event: the <see cref="ServerSwitchState"/> changed (turned on or off, the license changed).</summary>
    public const string Topic = "server";

    /// <summary>The status of a server that is turned off.</summary>
    public const string OffText = "Off";

    /// <summary>"Could not start: ..." (the reason of a start that failed).</summary>
    public static string StartFailed(string reason) => "Could not start: " + reason;

    /// <summary>Whether <paramref name="method"/> is one of the switch's methods.</summary>
    public static bool IsSwitchMethod(string method) => method is Get or TurnOn or TurnOff;

    /// <summary>The methods that change something (Admin, "manage", for users with all devices).</summary>
    public static bool IsChange(string method) => method is TurnOn or TurnOff;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Serialize(ServerSwitchState state) => JsonSerializer.Serialize(state, Json);

    public static ServerSwitchState Deserialize(string? json) =>
        JsonSerializer.Deserialize<ServerSwitchState>(json ?? "null", Json) ?? throw new JsonException("No server state.");
}

/// <summary>
/// A server's switch as its page shows it: the stored choice (<paramref name="On"/>), whether it runs, whether OADM PRO is
/// active, and the status while it does not run ("Off", <see cref="ProLicenseTexts.NeedsPro"/>, "Could not start: ...").
/// </summary>
public sealed record ServerSwitchState(bool On, bool Running, bool ProActive, string StatusText);

/// <summary>
/// Turn on / Turn off of a server inside PKI (docs/spec/pki.md "One plugin"): the server runs (and opens its port) only
/// while its stored choice is on <b>and</b> OADM PRO is active. The choice is the plugin setting <see cref="SettingKey"/>
/// (JSON true / false, none = off: off by default), kept when the license ends, so a new license starts the server again.
/// The core plugin runs whatever the switch says (PKI is always on); its page reads <see cref="State"/>. Start, stop and
/// license changes run one at a time.
/// </summary>
public sealed class ServerSwitch<TService> : IAsyncDisposable
    where TService : class, IAsyncDisposable
{
    /// <summary>The plugin setting of the choice (the host takes the choices of OADM 4.1 over into it: MergedPlugins.ServerOnSetting).</summary>
    public const string SettingKey = "serverOn";

    private readonly string _name;
    private readonly IPluginSettings _settings;
    private readonly IProLicense? _pro;
    private readonly Func<CancellationToken, Task<TService>> _start;
    private readonly IPluginEvents? _events;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _followGate = new();
    private volatile TService? _service;
    private volatile bool _on;
    private volatile string? _problem;
    private bool _started;
    private bool _disposed;
    private Task _following = Task.CompletedTask;

    /// <param name="name">The server's name in the log ("EST server").</param>
    /// <param name="settings">The plugin's settings (the choice).</param>
    /// <param name="pro">The OADM PRO license; null on hosts without (the server never runs).</param>
    /// <param name="start">Builds and starts the server; a start that throws leaves it off with the reason.</param>
    /// <param name="events">The page's live events (<see cref="ServerSwitchMethods.Topic"/>); null in tests.</param>
    /// <param name="logger">The plugin's logger.</param>
    public ServerSwitch(string name, IPluginSettings settings, IProLicense? pro, Func<CancellationToken, Task<TService>> start, IPluginEvents? events, ILogger? logger)
    {
        _name = name ?? throw new ArgumentNullException(nameof(name));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _start = start ?? throw new ArgumentNullException(nameof(start));
        _pro = pro;
        _events = events;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The running server; null while it is off, without OADM PRO or after a failed start.</summary>
    public TService? Service => _service;

    /// <summary>The stored choice.</summary>
    public bool On => _on;

    /// <summary>OADM PRO is active.</summary>
    public bool ProActive => _pro?.IsActive == true;

    /// <summary>The switch as the page shows it.</summary>
    public ServerSwitchState State
    {
        get
        {
            var running = _service is not null;
            var text = running ? string.Empty
                : !ProActive ? ProLicenseTexts.NeedsPro
                : !_on ? ServerSwitchMethods.OffText
                : _problem is { } problem ? ServerSwitchMethods.StartFailed(problem)
                : ServerSwitchMethods.OffText;
            return new ServerSwitchState(_on, running, ProActive, text);
        }
    }

    /// <summary>
    /// Reads the choice, follows the license and starts the server when it should run. Never throws for a server that
    /// cannot start: the reason shows on the page and the core plugin keeps running (Turn off / Turn on stay possible).
    /// </summary>
    public async Task StartAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _on = ReadChoice(await _settings.GetAsync(SettingKey, ct).ConfigureAwait(false));
        _started = true;
        if (_pro is not null)
        {
            _pro.Changed += OnProChanged;
        }

        await ApplyAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Turn on: refused without OADM PRO (<see cref="ProLicenseTexts.NeedsPro"/>); stores the choice, then starts the server.</summary>
    public async Task<ServerSwitchState> TurnOnAsync(CancellationToken ct)
    {
        if (!ProActive)
        {
            throw new InvalidOperationException(ProLicenseTexts.NeedsPro);
        }

        return await ChangeAsync(true, ct).ConfigureAwait(false);
    }

    /// <summary>Turn off: stores the choice, then stops the server (its port closes).</summary>
    public Task<ServerSwitchState> TurnOffAsync(CancellationToken ct) => ChangeAsync(false, ct);

    private async Task<ServerSwitchState> ChangeAsync(bool on, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Once the change began it runs to the end (like turning a plugin on or off), also when the caller goes away.
            await _settings.SetAsync(SettingKey, on ? "true" : "false", CancellationToken.None).ConfigureAwait(false);
            _on = on;
            ServerSwitchLog.Turned(_logger, _name, on ? "on" : "off");
            await ApplyLockedAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        Publish();
        return State;
    }

    private void OnProChanged(object? sender, EventArgs e)
    {
        if (_stopping.IsCancellationRequested)
        {
            return;
        }

        // The license changed (imported, removed, ran out): start or stop with the kept choice, never on the license's thread.
        lock (_followGate)
        {
            _following = _following.ContinueWith(_ => FollowProAsync(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        }
    }

    private async Task FollowProAsync()
    {
        if (_disposed)
        {
            return; // a license change that raced the shutdown
        }

        try
        {
            await ApplyAsync(_stopping.Token).ConfigureAwait(false);
            ServerSwitchLog.FollowedPro(_logger, _name, ProActive ? "active" : "not active", _service is null ? "stopped" : "running");
            Publish();
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_disposed)
        {
        }
#pragma warning disable CA1031 // Logged; the next change or start applies it.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ServerSwitchLog.FollowFailed(_logger, ex, _name);
        }
    }

    private async Task ApplyAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ApplyLockedAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Runs the server exactly while the choice is on and OADM PRO is active.</summary>
    private async Task ApplyLockedAsync(CancellationToken ct)
    {
        if (_disposed || !_started)
        {
            return;
        }

        var run = _on && ProActive;
        if (run && _service is null)
        {
            try
            {
                // Turn on runs in a user's call: the server's timers and loops outlive it, so they start without the call's
                // context (the host's caller and plugin call mark never flow into background work).
                Task<TService> starting;
                using (ExecutionContext.SuppressFlow())
                {
                    starting = Task.Run(() => _start(ct), CancellationToken.None);
                }

                _service = await starting.ConfigureAwait(false);
                _problem = null;
                ServerSwitchLog.Running(_logger, _name);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
#pragma warning disable CA1031 // The page shows why; the core plugin keeps running.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _problem = ex.Message;
                ServerSwitchLog.StartFailed(_logger, ex, _name);
            }
        }
        else if (!run && _service is { } service)
        {
            _service = null;
            await service.DisposeAsync().ConfigureAwait(false);
            ServerSwitchLog.Stopped(_logger, _name, _on ? "OADM PRO is not active" : "turned off");
        }

        if (!run)
        {
            _problem = null;
        }
    }

    private void Publish()
    {
        try
        {
            _events?.Publish(ServerSwitchMethods.Topic, ServerSwitchMethods.Serialize(State));
        }
#pragma warning disable CA1031 // A page that misses it reads the state again when shown.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private static bool ReadChoice(string? json)
    {
        try
        {
            return json is not null && JsonSerializer.Deserialize<bool>(json);
        }
        catch (JsonException)
        {
            return false; // unreadable: off (fail closed)
        }
    }

    /// <summary>Stops the server and stops following the license; the choice stays stored.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (_pro is not null)
        {
            _pro.Changed -= OnProChanged;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        Task following;
        lock (_followGate)
        {
            following = _following;
        }

        await following.ConfigureAwait(false); // never throws: FollowProAsync logs its own errors
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _disposed = true;
            if (_service is { } service)
            {
                _service = null;
                await service.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }

        _stopping.Dispose();
    }
}

/// <summary>The log lines of <see cref="ServerSwitch{TService}"/>.</summary>
internal static partial class ServerSwitchLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "{Name} turned {State}")]
    public static partial void Turned(ILogger logger, string name, string state);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Name} runs")]
    public static partial void Running(ILogger logger, string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Name} stopped: {Reason}")]
    public static partial void Stopped(ILogger logger, string name, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Name}: OADM PRO is {Pro}, the server is {State}")]
    public static partial void FollowedPro(ILogger logger, string name, string pro, string state);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Name} could not start")]
    public static partial void StartFailed(ILogger logger, Exception ex, string name);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Name} could not follow the license change")]
    public static partial void FollowFailed(ILogger logger, Exception ex, string name);
}
