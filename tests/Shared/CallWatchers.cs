using Oadm.Core.Plugins;
using Oadm.Sdk.Plugins;

namespace Oadm.Tests.Shared;

/// <summary>The page watcher of a user's call on the host's event hub, for plugin tests that run the real hub.</summary>
internal static class CallWatchers
{
    extension(ICallContext call)
    {
        /// <summary>The watcher of the call's session (its user, session and device scope; no device list).</summary>
        public PluginWatcher Watcher =>
            new(call.UserName, call.SessionId, call.DeviceScopeKey, call.HasAllDevices, _ => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>()));
    }
}
