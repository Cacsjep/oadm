namespace Oadm.Core.Sites;

/// <summary>
/// Raises an event whose handlers are plugin code (<c>ISites.SitesChanged</c>, <c>IRelay.StateChanged</c>) from the host's
/// own threads (timers, a site session's Watch loop, a relay's loop): every handler runs on its own, and one that throws
/// never ends the host's loop or the process. A broken plugin never takes the server down.
/// </summary>
public static class PluginEvents
{
    public static void Raise(EventHandler? handler, object sender)
    {
        if (handler is null)
        {
            return;
        }

        foreach (var single in handler.GetInvocationList().Cast<EventHandler>())
        {
            try
            {
                single(sender, EventArgs.Empty);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The plugin's own failure: its handler is skipped this time, the host carries on.
            }
        }
    }
}
