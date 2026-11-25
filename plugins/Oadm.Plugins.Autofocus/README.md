# Autofocus (`oadm.autofocus`)

Task plugin, server only, always on. Context menu Maintenance, no dialog, no toolbar button. Task name "Autofocus". Spec:
[Bundled plugins](../../docs/spec/bundled-plugins.md).

## Decisions

- Every lens of the camera (decision 2026-11-24): a multisensor camera has one lens per sensor and each is focused, one
  after the other. A group view has no lens of its own. The live view keeps focusing only the lens of the shown
  channel.
- The lens of opticsId `n` is named "Camera n+1", like the camera's own channels (`Image.I<n>.Name` "Camera 1").
- A lens that finds no focus or does not end within 2 minutes is a warning; the other lenses still focus. A request the
  camera refuses (HTTP error) fails the task.
- Only the lens moves: the autofocus area, zoom and every other setting stay as they are.

## Task steps

Optics Control API (`axis-cgi/opticscontrol.cgi`, API id `optics-control` 1.x), the API of the live view's focus. The
code is `plugins/Shared/LensFocus.cs`, linked into this plugin and Provisioning's Autofocus node.

| Step | What happens |
|---|---|
| Check the lenses | a fresh API list (`Require("optics-control", "1.0")`: a different major version fails "Nothing was changed"), then `getCapabilities`: the optics with the capability `focus`. None: the step ends with the warning "No lens of this camera has remote focus.". Detail: "4 lenses" |
| Focus camera 1, Focus camera 2, ... | `performAutofocus` for that lens; the camera answers when its search ended (about 4 s). Error 1300: warning "The camera could not find focus. Set focus by hand." |

`CanRun`: status Ok or Unknown and `optics-control` 1.x in the cached API list; otherwise "Needs remote focus".

## Verified

`getCapabilities` recorded read-only on the AXIS Virtual Loan Tool on 2026-11-24: AXIS P3735-PLE (AXIS OS 13.0.114,
4 optics) and AXIS P4708-PLVE (12.11.147, 2 optics), each optics with zoom and focus (test fixtures). `performAutofocus`
per lens was verified on the AXIS P3265-V (live view) and, on 2026-11-24, on every lens of both loan cameras in turn
(the task's requests sent by hand, not through a server): each answered `"data": {}` (focus found) and afterwards every
lens had its own new focus position (P3735-PLE 0.32 to 0.40, P4708-PLVE 0.48 and 0.51; all 0.4839 before), none still
moving. The task itself has not run against a camera through a server yet.

## Tests

`tests/Oadm.Plugins.Autofocus.Tests`: every lens in turn (P3735-PLE), a lens without focus (P4708-PLVE), no API in the
fresh list (nothing sent), a fixed lens, a refused request, a lens that never answers (`FakeTimeProvider`).

## Device groups

The tasks run only on devices of the user's view and touch only their own device; no dialog, no query
([device groups](../../docs/spec/device-scope.md)); listed in `CanaryTests.PluginsConverted`.
