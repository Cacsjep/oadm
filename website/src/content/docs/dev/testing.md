---
title: Testing
description: Unit tests with fakes, headless UI tests, scale tests and tests against real cameras.
---

Every non-trivial class has tests. No test needs a network or a device, except the hardware tests, which you start on
purpose. The tests use xUnit.

## Categories

| Category | Run with | What |
|---|---|---|
| (none) | `manage test unit` | Fast tests with fakes. CI runs them on Windows, Linux and macOS for every release |
| `Timing` | `manage test timing` | Depend on real elapsed time; local only |
| `Perf` | `manage test perf` | 30,000 devices, 50,000 tasks, with a time budget |
| `Hardware` | `manage test hardware` | Read-only, against your cameras |
| `HardwareWrite` | never from `manage` | Change a device. Only on purpose, only on a test device |

Mark a test with `[Trait("Category", "Perf")]` and so on. A pull request runs the unit tests on Linux.

## Task plugins with fakes

A task plugin needs two fakes: an `IVapixClient` that answers like the device, and an `ITaskExecutionContext` that
records the steps. The package `Oadm.Sdk.Testing` has both (`FakeCamera`, `FakeTaskContext`), a device (`FakeDevice`)
and `StepRun`, which ends the task exactly like the server: success adds the step Completed, a failure fails the running
step and skips the rest. `FakeCamera` answers parameter reads and writes, basic device information and the API list
itself, and records every request; `Respond` answers anything else.

```csharp
var camera = new FakeCamera();
var ctx = new FakeTaskContext(camera);

await StepRun.RunAsync(ctx.Steps, () => plugin.ExecuteAsync(ctx, new FakeDevice(), """{"name":"Entrance"}""", CancellationToken.None));

Assert.Equal(
    ["Check compatibility: Done", "Read the current name: Done", "Set the name: Done", "Verify the name: Done", "Completed: Done"],
    StepRun.Lines(ctx.Steps));
```

The [task plugin tutorial](../tutorial-task/#7-the-tests) has the complete tests. Test at least:

- the happy path, step by step;
- a device without the API: nothing is written, the error says "Nothing was changed";
- invalid input: it fails before the first request;
- `CanRun`, `NotSupportedReason` and `GetTaskName`.

Feed parsers with answers recorded from real devices (`Fixtures/`), not with hand-written ones. Note the device and AXIS
OS a fixture came from.

## Time

Code that waits takes a `TimeProvider`. Tests pass a `FakeTimeProvider` (`Microsoft.Extensions.TimeProvider.Testing`)
and advance it, so a 3-minute timeout is tested in milliseconds. A test that still needs real time gets the `Timing`
category.

## Core plugins

Start the server part with `FakeCoreContext` (`Oadm.Sdk.Testing`: devices, a `FakeCamera` per device, settings, the
published events, the tasks it starts) and call `InvokeAsync` like the page would. For events, the
plugin test projects use the server's real `PluginEventHub` and an in-memory page context; the I/O Monitor and Image
Health tests show the pattern. For the page, construct the view model with a fake `ICorePluginClientContext` and check
what it shows after each answer and event.

## Headless UI tests

Dialogs and pages are rendered offscreen with Avalonia headless, with the real theme. A test can click buttons through
the view model, check what is shown and save a screenshot:

```csharp
var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry));
await session.Dispatch(() =>
{
    var view = new ImageHealthView();
    var window = Headless.Host(view);   // the real host page around the view
    window.Show();
    view.DataContext = viewModel;
    Headless.Pump();
    Assert.Equal(7, viewModel.Rows.Count);
    Headless.Capture(window, "image-health-dashboard.png");
    window.Close();
}, CancellationToken.None);
```

`HeadlessEntry`, `Headless.Host`, `Pump` and `Capture` are in the `Support.cs` of a page plugin's tests, e.g.
`tests/Oadm.Plugins.ImageHealth.Tests/Support.cs`; copy them.
Screenshots are written only when `OADM_SCREENSHOT_DIR` is set:

```sh
OADM_SCREENSHOT_DIR=artifacts/screens ./manage.sh test unit
```

Look at the screenshots; assert behaviour and content, never pixel positions or sizes. `OADM_TEST_APPEARANCE=Light`
renders the light appearance. UI is only tested this way: never automate the real desktop.

## Scale

A server can manage 30,000 devices. Every feature with a device list has a `Perf` test with at least 30,000 fake devices that
keeps search, select all, the summary and updates fast, writes its measured times to the test output and has a
generous but meaningful budget.

## Real cameras

Copy `dev-cameras.example.yaml` to `dev-cameras.yaml` at the repository root (git-ignored: it holds passwords) and
list your devices:

```yaml
cameras:
  - address: 192.168.0.90
    user: root
    password: change-me
    scheme: https
    note: "AXIS P3265-V lab"
```

Tests marked `[HardwareFact]` and `[Trait("Category", "Hardware")]` read from them and are skipped when the file is
missing (CI). They must not change the devices. Tests that write are `HardwareWrite`, need their own environment
variables (for example `OADM_STORAGE_WRITE_URL`), and put the device back as it was. Run them only on a test device,
and write in the plugin's `README.md` what you verified on which model and AXIS OS.

## Try it in the app

- `./manage.sh run dev` builds everything and starts the server and the client. Plugins come from `artifacts/plugins/`.
- `./manage.sh run client --fake` starts the client with sample devices and no server, for the host pages. Most built-in
  plugin pages have a backend in `FakeOadmApi`; your plugin needs the real server.
