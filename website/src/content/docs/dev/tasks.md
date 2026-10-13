---
title: Task plugins
description: Context menu tasks that run on many devices, with dialogs, steps and device safety.
---

A task plugin is an entry in the device context menu. The user selects devices, chooses the entry, maybe fills a
dialog, and OADM runs **one task per device** on the server. Each task has its own state, steps and log in the tasks
pane, so one failing device never marks the others as failed.

[Your first plugin](../first-plugin/) builds a complete one. This page explains each part.

## How a run works

```text
Client                                         Server
──────                                         ──────
user picks devices + entry
  └─ dialog (optional) ─► payload JSON ──────► TaskService.Run(plugin, devices, payload)
                                                 ├─ RequiredPermissions(payload) checked
                                                 ├─ GetTaskName(payload) once
                                                 ├─ N tasks written, state Queued
                                                 └─ per device, at most 16 at once:
                                                      ExecuteAsync(ctx, device, payload, ct)
tasks pane ◄──────────── live state, steps ────── Done / Done with warnings / Failed / Cancelled
```

- The payload is the dialog's JSON (or null without a dialog). It lives in memory only, so it may carry a password.
  It is never stored or logged.
- `ExecuteAsync` returns for **Done**, calls `ctx.ReportWarning` for **Done with warnings**, throws for **Failed**. The
  exception message is what the user reads.
- `ct` is cancelled when the user cancels the task. Pass it to every call.
- Once a task ends, the server refreshes the device.

## The members

| Member | Purpose |
|---|---|
| `Id` | Stable id, e.g. `acme.upnp-name` |
| `DisplayName` | The menu and toolbar name, at most 32 characters, never ending in "..." |
| `IconKey` | An icon of the [icon list](../ui/#icons), e.g. `restart` |
| `Group` | The context menu submenu: a `TaskGroups` constant (`Applications`, `Maintenance`, `Network`, `Security`, `Users`, `Video`, `General`) or your own |
| `RequiresDialog` | The client opens your `ITaskPluginDialog` first |
| `ShowInToolbar` | Also a button in the Devices toolbar |
| `CanRun(device)` | Whether the entry is enabled for this device. Cached data only, no requests |
| `NotSupportedReason(device)` | Why `CanRun` is false; shown as the tooltip of the greyed entry |
| `GetTaskName(payload)` | The task's name in the tasks pane |
| `ExecuteAsync(ctx, device, payload, ct)` | The work, once per device |
| `MaxParallelDevices` | Lowers how many devices run at once (firmware uses 4); null = the server setting (16) |
| `ShowInMenus` | False: not in menus; a core plugin starts it from its page |
| `Permissions`, `RequiredPermissions(payload)` | Finer rights for choices of different risk, see [Permissions](#permissions) |

### Greyed entries say why

The context menu always lists the task. When it cannot run on the selection it is greyed out with the reason as
tooltip. Say what is missing or what to do, without a final period. Put details about this one device last, in
parentheses starting with "this device": the host drops them when it sums up several devices ("Needs AXIS OS 11.11 or
later: 3 of 5 selected devices").

```csharp
public bool CanRun(IDeviceInfo device) =>
    device.Status is DeviceStatus.Ok or DeviceStatus.Unknown && device.Apis.Supports("user-management", "1.0");

public string? NotSupportedReason(IDeviceInfo device) =>
    TaskSupportReasons.ForStatus(device.Status)                    // "The device does not answer", ...
    ?? TaskSupportReasons.NeedsApi("the user management API", device);
```

`NeedsApi` tells the user to refresh a device whose API list was not read yet. For a real version check,
`TaskSupportReasons.NeedsFirmware("11.11", device)` says "Needs AXIS OS 11.11 or later (this device has 10.12.338)".

### Task names

The tasks pane shows what each task does, not the menu name: "Add user joe", "Set static IP 10.0.0.60", "Upgrade
firmware to 12.11.77". At most 48 characters, no secrets (names are stored and shown to every user). It is called once
per run, so put everything the name needs into the payload.

## Steps

**Every device request and every wait is its own named step.** The user sees exactly what the task is doing and
where it failed.

```csharp
ctx.PlanSteps("Check compatibility", "Upload firmware", "Wait for the device to come back");

await ctx.StepAsync("Check compatibility", async _ =>
    (await ctx.Vapix.GetApiListAsync(ct)).Require("fwmgr", "1.0"));

using (var step = ctx.BeginStep("Upload firmware"))
{
    // ...
    step.ReportProgress(45, "37 of 82 MB");
}                                                 // returns: Done; throws: Failed with the message

ctx.SkipStep("Factory default", "Keep settings"); // a step that does not apply
```

- `PlanSteps` shows the steps as Pending from the start. `BeginStep` may add steps that were not planned.
- A step ends with `Complete(detail)`, `Warn(message)` (the task ends Done with warnings), `Skip(reason)` or
  `Fail(message)`. Disposing it without an end means Done; an exception escaping it means Failed.
- Beginning the next step ends the running one. Planned steps never reached end Skipped.
- When the task succeeds, OADM adds the final step **Completed** itself.
- Names are short imperatives with the article: "Read users", "Set the name", "Wait for the device to come back".

Progress comes from the steps by themselves. `ctx.ReportProgress(percent, message)` overrides it, and `ctx.Log(level,
message)` writes to the task log in the task details. Never log a secret.

## The dialog

The client part implements `ITaskPluginDialog` with the same `PluginId`:

```csharp
public sealed class MyDialog : ITaskPluginDialog
{
    public string PluginId => "acme.my-task";

    public async Task<string?> ShowAsync(ITaskDialogContext ctx, IReadOnlyList<IDeviceInfo> devices, Window owner)
    {
        // show a window, return the payload JSON, or null when the user cancelled
    }
}
```

`ITaskDialogContext` gives the dialog:

| Member | Use |
|---|---|
| `QueryAsync(deviceId, method, payload, ct)` | Read the device's current state through your `ITaskPluginQuery` on the server |
| `UploadAsync(path, progress, ct)` | Upload a local file (firmware, a package); put the returned id into the payload |
| `Has(permission)` | Hide choices the user's role does not allow |
| `AllDevices` | Every device, e.g. to copy settings from a device that is not selected |

Failed calls throw `RpcException`; show `Status.Detail`, or use `ServerErrors.Message(ex)`.

### Read-only queries

```csharp
public sealed class MyTask : ITaskPlugin, ITaskPluginQuery
{
    public async Task<string?> QueryAsync(ITaskQueryContext ctx, IDeviceInfo device, string method, string? payloadJson, CancellationToken ct) =>
        method switch
        {
            "users" => await ReadUsersAsync(ctx.Vapix, ct),
            _ => throw new ArgumentException("Unknown query " + method),
        };
}
```

A query must never change the device. It runs on the server with the stored login and a 30 s limit.

### Uploaded files

The dialog uploads with `ctx.UploadAsync`, the task opens the file by its id:

```csharp
var file = await ctx.Files.FindAsync(payload.FileId, ct)
    ?? throw new InvalidOperationException("The uploaded file is gone. Choose it again. Nothing was changed.");
await using var stream = await ctx.Files.OpenReadAsync(file.Id, ct);
```

The server deletes uploads after a day; plugins never delete them.

## Permissions

By default a task has one permission, `task:<id>`, which administrators give per role. A task whose choices differ in
risk declares finer ones and names what a run needs:

```csharp
public IReadOnlyList<PermissionDefinition> Permissions { get; } =
[
    new("add", "Add users", "Create accounts on the devices."),
    new("remove", "Remove users", "Delete accounts on the devices.") { OperatorDefault = false, FullDeviceControl = true },
];

public IReadOnlyList<string>? RequiredPermissions(string? payloadJson) =>
    Read(payloadJson)?.Mode == "remove" ? ["remove"] : ["add"];
```

The server refuses the run before any task starts when the role lacks a key. The dialog hides what the role lacks with
`ctx.Has(PermissionKeys.Task(PluginId, "remove"))`. Keys are stored in roles: never rename one.

## Device safety

These rules hold for every plugin that changes a device:

1. **Compatibility first.** `CanRun` checks the cached `device.Apis`. `ExecuteAsync` checks a fresh
   `GetApiListAsync()` with `Require(...)` **before the first write**. A device that lacks the API fails with "Nothing
   was changed".
2. **Validate before writing.** Check every input (addresses, masks, passwords, files) before the first request.
   Use the device's own dry run or validation methods when the API has one.
3. **No lock-out.** Never remove or demote `device.CredentialUserName`. Write the setting that could cut the
   connection (address, HTTPS, the account's password) last, and warn in the dialog when a change may make the device
   unreachable.
4. **Secrets.** Payloads stay in memory. Never log, store or name a password.
5. **Queries are read-only.**
6. **Hardware tests that write** are opt-in only and run only when the developer asks for them, against a test device.
   See [Testing](../testing/).

Say "Nothing was changed" in an error when nothing was. The user then knows the device is as it was.
