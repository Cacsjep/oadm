using System.Text.Json;

using Oadm.Plugins.ActionRules.Device;
using Oadm.Plugins.ActionRules.Rules;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.ActionRules;

/// <summary>
/// A rule as it was before a run (<see cref="Before"/>, null = the run created it) and as the run left it
/// (<see cref="After"/>, null = the run deleted it). Undo puts it back only while it is still as the run left it.
/// </summary>
public sealed record UndoRule(DeviceRule? Before, DeviceRule? After);

/// <summary>A recipient before and after a run; null before = created, null after = deleted.</summary>
public sealed record UndoRecipient(DeviceRecipient? Before, DeviceRecipient? After);

/// <summary>A schedule before and after a run (ids stay); null before = created, null after = deleted.</summary>
public sealed record UndoSchedule(DeviceSchedule? Before, DeviceSchedule? After);

/// <summary>What one device's task saved for undo.</summary>
public sealed record DeviceUndo(Guid DeviceId, IReadOnlyList<UndoRule> Rules)
{
    public IReadOnlyList<UndoRecipient> Recipients { get; init; } = [];

    public IReadOnlyList<UndoSchedule> Schedules { get; init; } = [];

    public bool IsEmpty => Rules.Count == 0 && Recipients.Count == 0 && Schedules.Count == 0;
}

/// <summary>
/// The rules, recipients and schedules as they were before and after each run, so the run can be undone for
/// <see cref="ActionRulesInfo.UndoKeep"/>: one folder per run in the plugin's data folder (<c>undo/&lt;run&gt;/run.json</c> +
/// one file per device). Password values are encrypted with the server's master key (purpose per run and device); without
/// the protector nothing is saved and undo is not offered. Every file access goes through one lock.
/// </summary>
public sealed class UndoStore(string? folder, ISecretProtector? secrets, TimeProvider time)
{
    private const string RunFile = "run.json";
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Whether runs can be undone on this server.</summary>
    public bool IsAvailable => folder is not null && secrets is not null;

    public async Task StartRunAsync(UndoRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (!IsAvailable)
        {
            return;
        }

        await LockedAsync(async () =>
        {
            Directory.CreateDirectory(RunFolder(run.RunId));
            await WriteAsync(Path.Combine(RunFolder(run.RunId), RunFile), run, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Saves (or updates) what one device's task changed. Throws when the run is not known (its folder is gone), so the task
    /// fails before writing anything it could not undo.
    /// </summary>
    public async Task SaveDeviceAsync(Guid runId, DeviceUndo undo, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(undo);
        if (!IsAvailable)
        {
            return;
        }

        var purpose = Purpose(runId, undo.DeviceId);
        var stored = undo with
        {
            Rules = [.. undo.Rules.Select(r => new UndoRule(Map(r.Before, Protect(purpose)), Map(r.After, Protect(purpose))))],
            Recipients = [.. undo.Recipients.Select(r => new UndoRecipient(Map(r.Before, Protect(purpose)), Map(r.After, Protect(purpose))))],
        };
        await LockedAsync(async () =>
        {
            if (!File.Exists(Path.Combine(RunFolder(runId), RunFile)))
            {
                throw new InvalidOperationException(RuleTexts.CannotSaveForUndo);
            }

            await WriteAsync(Path.Combine(RunFolder(runId), undo.DeviceId.ToString("N") + ".json"), stored, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>What a device's task saved for a run (passwords decrypted), or null.</summary>
    public async Task<DeviceUndo?> ReadDeviceAsync(Guid runId, Guid deviceId, CancellationToken ct)
    {
        if (!IsAvailable)
        {
            return null;
        }

        var stored = await LockedAsync(() => ReadAsync<DeviceUndo>(Path.Combine(RunFolder(runId), deviceId.ToString("N") + ".json"), ct), ct).ConfigureAwait(false);
        if (stored is null)
        {
            return null;
        }

        var purpose = Purpose(runId, deviceId);
        return stored with
        {
            Rules = [.. stored.Rules.Select(r => new UndoRule(Map(r.Before, Unprotect(purpose)), Map(r.After, Unprotect(purpose))))],
            Recipients = [.. stored.Recipients.Select(r => new UndoRecipient(Map(r.Before, Unprotect(purpose)), Map(r.After, Unprotect(purpose))))],
        };
    }

    /// <summary>The runs that can be undone, newest first; runs older than the keep time are removed.</summary>
    public async Task<IReadOnlyList<UndoRun>> ListAsync(CancellationToken ct)
    {
        if (!IsAvailable || !Directory.Exists(folder))
        {
            return [];
        }

        // One lock per run, so the tasks' saves do not wait for the whole list.
        var runs = new List<UndoRun>();
        var oldest = time.GetUtcNow() - ActionRulesInfo.UndoKeep;
        foreach (var dir in Directory.EnumerateDirectories(folder!).ToList())
        {
            var run = await LockedAsync(async () =>
            {
                var found = await ReadAsync<UndoRun>(Path.Combine(dir, RunFile), ct).ConfigureAwait(false);
                var created = found?.CreatedUtc ?? new DateTimeOffset(Directory.GetCreationTimeUtc(dir), TimeSpan.Zero);
                if (created < oldest)
                {
                    TryDelete(dir); // only when it is known to be old: an unreadable file of a new run stays
                    return null;
                }

                return found;
            }, ct).ConfigureAwait(false);
            if (run is not null)
            {
                runs.Add(run);
            }
        }

        return [.. runs.OrderByDescending(r => r.CreatedUtc)];
    }

    public async Task<UndoRun?> FindAsync(Guid runId, CancellationToken ct) =>
        IsAvailable ? await LockedAsync(() => ReadAsync<UndoRun>(Path.Combine(RunFolder(runId), RunFile), ct), ct).ConfigureAwait(false) : null;

    /// <summary>The devices a run saved something for.</summary>
    public IReadOnlyList<Guid> DevicesOf(Guid runId)
    {
        var dir = RunFolder(runId);
        return !Directory.Exists(dir)
            ? []
            : [.. Directory.EnumerateFiles(dir, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => n != Path.GetFileNameWithoutExtension(RunFile))
                .Select(n => Guid.TryParseExact(n, "N", out var id) ? id : Guid.Empty)
                .Where(id => id != Guid.Empty)];
    }

    /// <summary>Sets the run's undone mark unless it has that value already: true for the one caller that changed it.</summary>
    public async Task<bool> TryMarkUndoneAsync(Guid runId, bool undone, CancellationToken ct) =>
        await LockedAsync(async () =>
        {
            var file = Path.Combine(RunFolder(runId), RunFile);
            var run = await ReadAsync<UndoRun>(file, ct).ConfigureAwait(false);
            if (run is null || run.Undone == undone)
            {
                return false;
            }

            await WriteAsync(file, run with { Undone = undone }, ct).ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false);

    private string RunFolder(Guid runId) => Path.Combine(folder ?? string.Empty, runId.ToString("N"));

    private static string Purpose(Guid runId, Guid deviceId) => $"oadm:action-rules:undo:{runId:N}:{deviceId:N}";

    /// <summary>Marks a value that holds a password inside an address, stored encrypted as a whole.</summary>
    private const string LoginMark = "oadm-login:";

    /// <summary>Passwords and values with a password inside an address are encrypted; the latter carry <see cref="LoginMark"/>.</summary>
    private Func<NameValue, string> Protect(string purpose) => p =>
        RuleText.IsSecret(p.Name) ? (p.Value.Length > 0 ? secrets!.Protect(p.Value, purpose) : p.Value)
        : RuleText.HasLogin(p.Value) ? LoginMark + secrets!.Protect(p.Value, purpose)
        : p.Value;

    private Func<NameValue, string> Unprotect(string purpose) => p =>
        RuleText.IsSecret(p.Name) ? (p.Value.Length > 0 ? secrets!.Unprotect(p.Value, purpose) : p.Value)
        : p.Value.StartsWith(LoginMark, StringComparison.Ordinal) ? secrets!.Unprotect(p.Value[LoginMark.Length..], purpose)
        : p.Value;

    private static DeviceRule? Map(DeviceRule? rule, Func<NameValue, string> secret) =>
        rule is null ? null : rule with { Action = rule.Action with { Parameters = Map(rule.Action.Parameters, secret) } };

    private static DeviceRecipient? Map(DeviceRecipient? recipient, Func<NameValue, string> secret) =>
        recipient is null ? null : recipient with { Parameters = Map(recipient.Parameters, secret) };

    private static IReadOnlyList<NameValue> Map(IReadOnlyList<NameValue> parameters, Func<NameValue, string> secret) =>
        [.. parameters.Select(p => p with { Value = secret(p) })];

    private static async Task LockedAsync(Func<Task> action, CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<T> LockedAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task WriteAsync<T>(string file, T value, CancellationToken ct)
    {
        var temp = file + ".tmp";
        await File.WriteAllTextAsync(temp, ActionRulesJson.Serialize(value), ct).ConfigureAwait(false);
        File.Move(temp, file, overwrite: true);
    }

    private static async Task<T?> ReadAsync<T>(string file, CancellationToken ct)
        where T : class
    {
        try
        {
            return File.Exists(file) ? ActionRulesJson.Deserialize<T>(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false)) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or ArgumentException)
        {
            return null;
        }
    }

    private static void TryDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // next time
        }
        catch (UnauthorizedAccessException)
        {
            // next time
        }
    }
}
