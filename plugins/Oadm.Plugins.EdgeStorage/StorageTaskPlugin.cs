using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text;

using Oadm.Plugins.EdgeStorage.Device;
using Oadm.Plugins.EdgeStorage.Disks;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.EdgeStorage;

/// <summary>
/// The SD card task of the Edge storage page, one per camera: check, repair, format, encryption on or off, retention. Started
/// only from the page (<see cref="ShowInMenus"/> false); the page's methods carry the rights. Device safety: the API list is
/// read fresh and checked before the first write; the card must be in the slot; format, check and repair need the card
/// unmounted (the camera answers 403 otherwise), so the task unmounts it and always mounts it again, also after a failure.
/// From the unmount on the task does not stop for Cancel (a card is never left unmounted); a job the camera started runs
/// to its end on the camera anyway. Recording to the card stops while it is unmounted. The passphrase is only in the payload
/// (memory) and the POST body, never stored or logged.
/// </summary>
public sealed class StorageTaskPlugin : ITaskPlugin
{
    public const string CompatibilityStep = "Check compatibility";
    public const string ReadStep = "Read the SD card";
    public const string UnmountStep = "Unmount the SD card";
    public const string MountStep = "Mount the SD card";
    public const string CheckStep = "Check the SD card";
    public const string RepairStep = "Repair the SD card";
    public const string FormatStep = "Format the SD card";
    public const string EncryptionOnStep = "Turn on encryption";
    public const string EncryptionOffStep = "Turn off encryption";
    public const string RetentionStep = "Set the retention";

    private readonly EdgeStoragePlugin? _owner;
    private readonly TimeProvider _time;
    private readonly TimeSpan? _pollInterval;

    /// <summary>Created by its core plugin only (no public constructor, so the loader never registers it alone).</summary>
    internal StorageTaskPlugin(EdgeStoragePlugin? owner, TimeProvider? time = null, TimeSpan? pollInterval = null)
    {
        _owner = owner;
        _time = time ?? TimeProvider.System;
        _pollInterval = pollInterval;
    }

    public string Id => EdgeStorageInfo.TaskPluginId;

    public string DisplayName => "SD card";

    public string? IconKey => EdgeStorageInfo.IconKey;

    public string Group => TaskGroups.Maintenance;

    public bool ShowInToolbar => false;

    public bool RequiresDialog => false;

    public bool ShowInMenus => false;

    public bool CanRun(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return TaskSupportReasons.ForStatus(device.Status) is null && device.Apis.Supports(StorageRequests.DiskManagementApi, "1.0");
    }

    public string? NotSupportedReason(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return TaskSupportReasons.ForStatus(device.Status) ?? StorageTexts.NoApi;
    }

    public string GetTaskName(string? payloadJson)
    {
        try
        {
            var payload = EdgeStorageJson.Deserialize<TaskPayload>(payloadJson);
            return StorageActions.TaskName(payload.Action, payload.RetentionDays);
        }
        catch (System.Text.Json.JsonException)
        {
            return EdgeStorageInfo.DisplayName;
        }
    }

    public async Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);
        var payload = Validate(EdgeStorageJson.Deserialize<TaskPayload>(payloadJson));
        var unmounts = payload.Action is not StorageActions.Retention;
        var steps = new List<string> { CompatibilityStep, ReadStep };
        steps.AddRange(payload.Action switch
        {
            StorageActions.Check => [UnmountStep, CheckStep, MountStep],
            StorageActions.Repair => [UnmountStep, RepairStep, MountStep],
            StorageActions.Format => [UnmountStep, FormatStep, MountStep],
            StorageActions.EncryptionOn => [UnmountStep, EncryptionOnStep, FormatStep, MountStep],
            StorageActions.EncryptionOff => [UnmountStep, EncryptionOffStep, FormatStep, MountStep],
            _ => [RetentionStep],
        });
        ctx.PlanSteps([.. steps]);

        var client = new StorageClient(ctx.Vapix, _time, _pollInterval);
        try
        {
            await ctx.StepAsync(CompatibilityStep, async _ =>
            {
                var apis = await ctx.Vapix.GetApiListAsync(ct).ConfigureAwait(false);
                apis.Require(StorageRequests.DiskManagementApi, "1.0");
                if (payload.Action is StorageActions.Retention or StorageActions.EncryptionOn or StorageActions.EncryptionOff)
                {
                    apis.Require(StorageRequests.DiskPropertiesApi, "1.0");
                }
            }).ConfigureAwait(false);

            var card = await ctx.StepAsync(ReadStep, async step =>
            {
                var sd = await client.GetSdCardAsync(ct).ConfigureAwait(false);
                step.Complete(CardSummary(sd));
                return sd;
            }).ConfigureAwait(false);

            if (!unmounts)
            {
                await SetRetentionAsync(ctx, client, card, payload.RetentionDays!.Value, ct).ConfigureAwait(false);
                return;
            }

            if (payload.Action == StorageActions.EncryptionOff && card.EncryptionEnabled != true && card.Encrypted != true)
            {
                throw new StorageException("The SD card is not encrypted. Nothing was changed.");
            }

            if (payload.Action == StorageActions.EncryptionOn && card.EncryptionEnabled == true)
            {
                throw new StorageException("The SD card is encrypted already. Nothing was changed.");
            }

            ct.ThrowIfCancellationRequested();
            await RunUnmountedAsync(ctx, client, card, payload).ConfigureAwait(false);
        }
        finally
        {
            if (_owner is not null)
            {
                await _owner.RefreshRowAsync(device.Id).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Unmount, the action, mount: no Cancel from here on. The card is mounted again whatever happens, also when the unmount
    /// answer was lost (the camera may have unmounted it anyway); the first failure stays the task's error.
    /// </summary>
    private static async Task RunUnmountedAsync(ITaskExecutionContext ctx, StorageClient client, EdgeDisk card, TaskPayload payload)
    {
        var none = CancellationToken.None;
        ExceptionDispatchInfo? failure = null;
        try
        {
            if (card.IsUnmounted)
            {
                ctx.SkipStep(UnmountStep, "Not in use already.");
            }
            else
            {
                await ctx.StepAsync(UnmountStep, async step => await client.MountAsync(card.Id, mount: false, null, none).ConfigureAwait(false)).ConfigureAwait(false);
            }

            switch (payload.Action)
            {
                case StorageActions.Check:
                    await JobStepAsync(ctx, CheckStep, p => client.CheckAsync(card.Id, p, none)).ConfigureAwait(false);
                    break;
                case StorageActions.Repair:
                    await JobStepAsync(ctx, RepairStep, p => client.RepairAsync(card.Id, p, none)).ConfigureAwait(false);
                    break;
                case StorageActions.Format:
                    await JobStepAsync(ctx, FormatStep, p => client.FormatAsync(card.Id, p, none)).ConfigureAwait(false);
                    break;
                case StorageActions.EncryptionOn:
                    await ctx.StepAsync(EncryptionOnStep, async _ => await client.EnableEncryptionAsync(card.Id, payload.Passphrase!, none).ConfigureAwait(false)).ConfigureAwait(false);
                    await JobStepAsync(ctx, FormatStep, p => client.FormatAsync(card.Id, p, none)).ConfigureAwait(false);
                    break;
                case StorageActions.EncryptionOff:
                    await ctx.StepAsync(EncryptionOffStep, async _ => await client.DisableEncryptionAsync(card.Id, none).ConfigureAwait(false)).ConfigureAwait(false);
                    await JobStepAsync(ctx, FormatStep, p => client.FormatAsync(card.Id, p, none)).ConfigureAwait(false);
                    break;
            }
        }
#pragma warning disable CA1031 // Kept and rethrown after the card is mounted again.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            failure = ExceptionDispatchInfo.Capture(ex);
        }

        try
        {
            await MountAgainAsync(ctx, client, card.Id).ConfigureAwait(false);
        }
        catch (Exception ex) when (failure is not null)
        {
            ctx.Log(TaskLogLevel.Error, MountStep + ": " + ex.Message); // the step shows it; the first failure is the task's error
        }

        failure?.Throw();
    }

    /// <summary>Mounts the card unless the camera uses it (an unmount that failed before it happened).</summary>
    private static async Task MountAgainAsync(ITaskExecutionContext ctx, StorageClient client, string diskId)
    {
        using var step = ctx.BeginStep(MountStep);
        try
        {
            var now = (await client.ListDisksAsync(CancellationToken.None).ConfigureAwait(false)).FirstOrDefault(d => d.Id == diskId);
            if (now is { IsConnected: true })
            {
                step.Complete("In use already.");
                return;
            }

            await client.MountAsync(diskId, mount: true, null, CancellationToken.None).ConfigureAwait(false);
            step.Complete();
        }
        catch (Exception ex)
        {
            step.Fail(ex.Message);
            throw;
        }
    }

    private static Task JobStepAsync(ITaskExecutionContext ctx, string name, Func<IProgress<int>, Task> job) =>
        ctx.StepAsync(name, async step => await job(new StepProgress(step)).ConfigureAwait(false));

    private static async Task SetRetentionAsync(ITaskExecutionContext ctx, StorageClient client, EdgeDisk card, int days, CancellationToken ct)
    {
        if (card.CleanupMaxAgeDays == days)
        {
            ctx.SkipStep(RetentionStep, "Already " + RetentionText(days) + ".");
            return;
        }

        await ctx.StepAsync(RetentionStep, async step =>
        {
            await client.SetRetentionAsync(card.Id, days, ct).ConfigureAwait(false);
            var after = await client.GetSdCardAsync(ct).ConfigureAwait(false);
            if (after.CleanupMaxAgeDays != days)
            {
                throw new StorageException("The camera kept " + RetentionText(after.CleanupMaxAgeDays) + ".");
            }

            step.Complete(RetentionText(days));
        }).ConfigureAwait(false);
    }

    /// <summary>Checks the payload before any device access ("Nothing was changed" on a bad one).</summary>
    internal static TaskPayload Validate(TaskPayload payload)
    {
        if (!StorageActions.IsKnown(payload.Action))
        {
            throw new ArgumentException("Unknown action. Nothing was changed.");
        }

        if (payload.Action == StorageActions.Retention && payload.RetentionDays is not (>= 0 and <= StorageRequests.MaxRetentionDays))
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"Enter 0 to {StorageRequests.MaxRetentionDays:N0} days. Nothing was changed."));
        }

        if (payload.Action == StorageActions.EncryptionOn && PassphraseProblem(payload.Passphrase) is { } problem)
        {
            throw new ArgumentException(problem + " Nothing was changed.");
        }

        return payload;
    }

    /// <summary>What is wrong with a passphrase (1 to 512 bytes, printable), or null.</summary>
    public static string? PassphraseProblem(string? passphrase) =>
        string.IsNullOrEmpty(passphrase) ? "Enter a passphrase."
        : Encoding.UTF8.GetByteCount(passphrase) > StorageRequests.MaxPassphraseBytes ? "The passphrase is too long."
        : passphrase.Any(char.IsControl) ? "The passphrase contains characters the camera does not accept."
        : null;

    /// <summary>"7 days", "1 day", "No limit".</summary>
    public static string RetentionText(int? days) => days switch
    {
        null => "unknown",
        0 => "No limit",
        1 => "1 day",
        _ => string.Create(CultureInfo.InvariantCulture, $"{days:N0} days"),
    };

    private static string CardSummary(EdgeDisk card) =>
        card.IsUnmounted ? "Not in use"
        : card.TotalKb is > 0 && card.FreeKb is { } free
            ? string.Create(CultureInfo.CurrentCulture, $"{StorageFormat.Size(card.TotalKb.Value)}, {StorageFormat.Size(free)} free")
            : "In use";

    /// <summary>The camera's job progress as the step's progress.</summary>
    private sealed class StepProgress(ITaskStep step) : IProgress<int>
    {
        public void Report(int value) => step.ReportProgress(value);
    }
}

/// <summary>Sizes as the page and the tasks show them ("57.9 GB").</summary>
public static class StorageFormat
{
    /// <summary>The camera's kB (1,024 bytes) in decimal units like the card's label.</summary>
    public static string Size(long kb)
    {
        var bytes = kb * 1024.0;
        return bytes switch
        {
            >= 1e12 => string.Create(CultureInfo.CurrentCulture, $"{bytes / 1e12:0.0} TB"),
            >= 1e9 => string.Create(CultureInfo.CurrentCulture, $"{bytes / 1e9:0.0} GB"),
            >= 1e6 => string.Create(CultureInfo.CurrentCulture, $"{bytes / 1e6:0} MB"),
            _ => string.Create(CultureInfo.CurrentCulture, $"{kb:N0} kB"),
        };
    }
}
