using System.Globalization;

using Oadm.Contracts.Updates;
using Oadm.Contracts.V1;

namespace Oadm.Client.Updates;

/// <summary>What an update did, as the dialog and the Logs page say it (docs/spec/self-update.md).</summary>
/// <param name="Title">"Server updated to V4.9.0", "The update failed. The server runs V4.8.0.".</param>
/// <param name="IsDone">The new version runs.</param>
/// <param name="Lines">The reason, the installer log, the database copy, the site node hint.</param>
public sealed record UpdateOutcome(string Title, bool IsDone, IReadOnlyList<string> Lines)
{
    /// <summary>The server's last update as the server's record says it; null without one or while it runs.</summary>
    public static UpdateOutcome? OfServer(UpdateRecord? record, string serverVersion, int olderSiteNodes)
    {
        if (record is null || record.Result is "running" or "")
        {
            return null;
        }

        var lines = new List<string>();
        if (record.Result == "done")
        {
            if (record.Reason.Length > 0)
            {
                lines.Add(record.Reason); // "Restart the computer to finish the update"
            }

            if (olderSiteNodes > 0)
            {
                lines.Add(UpdateTexts.UpdateSiteNodes);
            }

            return new UpdateOutcome(UpdateTexts.ServerUpdated(record.ToVersion), true, lines);
        }

        if (record.Reason.Length > 0)
        {
            lines.Add(record.Reason);
        }

        if (record.InstallerLog.Length > 0)
        {
            lines.Add(UpdateTexts.ServerLog(record.InstallerLog));
        }

        if (record.DatabaseCopy.Length > 0)
        {
            lines.Add(UpdateTexts.DatabaseCopy(record.DatabaseCopy));
        }

        return new UpdateOutcome(UpdateTexts.ServerFailed(serverVersion), false, lines);
    }

    /// <summary>The client after its own update: the version it runs now says whether the installer did it.</summary>
    public static UpdateOutcome OfClient(string runningVersion, string to, string? reason)
    {
        if (UpdateVersions.Parse(runningVersion) is { } now && UpdateVersions.Parse(to) is { } target && now >= target)
        {
            return new UpdateOutcome(UpdateTexts.ClientUpdated(to), true, []);
        }

        return new UpdateOutcome(UpdateTexts.ClientFailed, false, string.IsNullOrWhiteSpace(reason) ? [UpdateTexts.InstallerFailed] : [reason]);
    }

    /// <summary>Not back after 10 minutes: what to check.</summary>
    public static UpdateOutcome NotBack() => new(UpdateTexts.NotBack, false, [UpdateTexts.NotBackHelp]);

    /// <summary>"Last server update: V4.8.0 to V4.9.0, 2026-11-03 10:05" (Logs page).</summary>
    public static string LastLine(UpdateRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var when = (record.Finished ?? record.Started)?.ToDateTime().ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture) ?? string.Empty;
        return $"Last server update: {UpdateVersions.Display(record.FromVersion)} to {UpdateVersions.Display(record.ToVersion)}, {when}";
    }
}
