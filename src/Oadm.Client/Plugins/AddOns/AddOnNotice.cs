using Oadm.Client.Api;
using Oadm.Client.Dialogs;
using Oadm.Client.Infrastructure;
using Oadm.Client.Shell;
using Oadm.Contracts.V1;

namespace Oadm.Client.Plugins.AddOns;

/// <summary>
/// After the main window opened and the new version dialog closed, for an Administrator (docs/spec/add-ons.md "Plugins page"): the dialog about optional plugins of an older
/// OADM version and optional plugins that are not loaded. Once per server and user for one state: it comes back only when
/// the state changes, for example after the next update. Never throws: a failed read never disturbs the connect.
/// </summary>
public sealed class AddOnNotice(IOadmApi api, IClientSettingsStore settings, UserSession session, IDialogService dialogs)
{
    /// <summary>The rows the dialog names: an older version, or not loaded.</summary>
    internal static IReadOnlyList<AddOnInfo> Rows(AddOnList list) =>
        [.. list.AddOns.Where(a => a.UpdateAvailable || a.State == AddOnState.AddOnFailed)];

    /// <summary>What the stored state is compared with: the rows, their versions and the ZIP each one needs.</summary>
    internal static string StateOf(IEnumerable<AddOnInfo> rows) =>
        string.Join('|', rows.Select(r => $"{r.Id}:{r.State}:{r.Version}:{r.DownloadUrl}"));

    /// <summary>Shows the dialog when due. True: the user wants the Plugins page.</summary>
    public async Task<bool> ShowIfNeededAsync(CancellationToken ct)
    {
        if (!session.IsAdmin)
        {
            return false;
        }

        var key = $"{session.ServerAddress}/{session.UserName}"; // before the first wait: the user may sign out meanwhile
        try
        {
            var list = await api.ListAddOnsAsync(ct).ConfigureAwait(true);
            var rows = Rows(list);
            var stored = settings.Current.AddOnNotices;
            if (rows.Count == 0)
            {
                // Nothing to say. While one installs it may still fail: the same state then stays told.
                if (!list.AddOns.Any(a => a.State == AddOnState.AddOnInstalling) && stored.Remove(key))
                {
                    settings.Save();
                }

                return false;
            }

            var state = StateOf(rows);
            if (stored.TryGetValue(key, out var seen) && seen == state)
            {
                return false;
            }

            var open = await dialogs.ConfirmAsync(OptionalPluginTexts.NoticeTitle, OptionalPluginTexts.Notice(rows), OptionalPluginTexts.OpenPlugins, OptionalPluginTexts.Later).ConfigureAwait(true);
            stored[key] = state; // after the dialog: one that could not show comes back
            settings.Save();
            return open;
        }
#pragma warning disable CA1031 // An older server, a failed read or a dialog that cannot show: nothing to say.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }
}
