# Main window: manage devices

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.

Reference for content: the Manage devices page of ADM. Same information density, styled as described in Visual Style.

Layout, top to bottom:
1. Title "Devices". Left navigation rail as described in Visual Style.
2. Toolbar: toolbar plugins (**Add** menu | Remove, Refresh, Export | task plugin actions that declare `ShowInToolbar`,
   then System report (plugin) | plugin entries) in `ui:ToolbarOverflowPanel`: entries that do not fit (narrow
   window, live view open) move into a **More** menu (icon `more`, right of the toolbar). Right-aligned: the AXIS OS
   release notes link (icon only, toolbar controls with the class `trailing` go here), **Group by tag** icon button (icon
   `groupBy`, class `active` while on), **Show each compliance check** icon button (icon `squareActivity`, class `active`
   while on, only while the Compliance column is on), Columns icon button (tooltip "Choose columns") and search box (host parts);
   everything fits on one line at a 1800 px window width with the rail expanded and the live view closed. One main **Add** button (`IsPrimary`: class `main`, flat like every toolbar button) with a menu Discovery (zero-conf scan), Network range,
   Manual, Import from file, Import from ADM instead of five buttons; every toolbar entry is a button with text (only Columns, Group by tag
   and the release notes link are icons). The empty Devices page shows a primary **Add devices** button with the same
   menu.
3. Status line: "N devices, M selected". A user with a device group sees only the devices of the group; a device that
   leaves or enters it leaves or enters the grid (`docs/spec/device-scope.md`).
4. Device grid (virtualized): sortable, column chooser, column order and width persisted per
   client, horizontal scroll, multi-select, right-click context menu: the core actions (Open web
   interface, Refresh, **Log in**, **Set password**, **Tags**), a separator, then one **submenu per task group** (`TaskPluginInfo.group`, sorted
   by name, with a group icon: Applications app, Maintenance settings, Network network, Security key,
   Users users, Video video, others plugin; a group is a submenu even with one entry)
   holding **every** menu Task plugin (`ShowInMenus`), sorted by name, with their icons. A plugin whose `CanRun` is
   false for any selected device stays visible but **disabled** (theme's disabled menu look) with the reason as tooltip
   (`ToolTip.ShowOnDisabled`): one device the plugin's text, e.g. "Needs AXIS OS 11.11 or
   later (this device has 11.9.65)"; several devices the most common reason without the device detail and how many of
   the selected devices it concerns, plus the number of other reasons ("Needs AXIS OS 11.11 or later: 3 of 5 selected
   devices (+1 other reason)", `TaskPluginCatalog.NotRunnableReason`, O(selection)); a group whose entries are all
   disabled is still shown. **Remove** comes last, after its own separator (away from the everyday entries); the entry
   of an open submenu stays highlighted. Entries never end with "..." (the host appends none and strips "..." / "…" defensively,
   `TaskPluginNames.Normalize`). Menus and submenus are at least `Oadm.MenuMinWidth` (240) wide (theme).
   The toolbar task buttons have no groups; one the selection cannot run is disabled with the same reason as tooltip
   (`IToolbarContext.CannotRunTaskReason`; no tooltip without a selection).
   **Log in** (icon `key`) is shown only while the selection holds a device with status
   Credentials required (not Password not set: "Set password" below) and follows the status live; it
   applies to those devices of the selection. A rejected stored credential only shows as that status (polling), there
   are no automatic retries. A device that offers only Basic login over plain HTTP while the server setting
   `Devices.AllowBasicOverHttp` is off keeps the status Credentials required; the Status column reads "Basic login only" (`DeviceMessages.BasicLoginOnly`) and its
   tooltip says `DeviceMessages.BasicOnlyOverHttp` (proto `Device.status_detail`, kept in server memory by `DeviceStatusNotes`, set and
   cleared by the status check, never stored: a restart forgets it until the next check). Turning the setting on checks
   these devices again at once (`DevicePollingService.QueueRefresh`). Dialog `Devices/DeviceLoginWindow` (`DeviceLoginViewModel`, single operation:
   `Border.dialogBody`, 460 px, no card): "10.0.0.48 (P3265-V) rejects the stored credentials." / "5 devices reject the
   stored credentials.", User name (prefilled with the stored one the devices share, `GetCredentialUserName`, else
   "root"), Password (`ui:PasswordBox`), "Save to credential list" (checked; administrators only, hidden for
   operators), Cancel / **Log in** (disabled with the reason as tooltip). One `DeviceService.LogIn` call for all of them
   with the shared `ui:ProgressRow`; all accepted: the dialog closes (a credential list note then shows in the message
   window); otherwise the accepted devices are done and leave the dialog, the result stands directly below the password
   field: one device its reason ("The user name or password is wrong."), several "2 of 5 devices rejected it:
   10.0.0.48, 10.0.0.49" (other reasons: "2 of 3 devices failed: 10.0.0.48 (wrong user name or password), 10.0.0.49
   (Unreachable - ...)", at most 5 named, "+N more"). Fake mode accepts every password except "wrong" (10.0.0.23 is
   Credentials required). Tests: `tests/Oadm.Server.Tests/DeviceLoginTests` (+ Perf 5,000 devices in one call),
   `tests/Oadm.Client.Tests/DeviceLoginTests`.
   **Set password** (icon `lock`) is shown only while the selection holds a device with status
   Password not set (e.g. added by the DHCP server's automatic add) and follows the status live; it applies to those
   devices. Dialog `Devices/DeviceSetPasswordWindow` (`DeviceSetPasswordViewModel`, single operation, 460 px, like the add
   page's first-password editor): "10.0.0.40 (AXIS F9111) has no password yet (factory default)." / "3 devices have no
   password yet (factory default).", User name "root" (fixed, read-only), New password + Confirm password
   (`ui:PasswordBox`), hint with the passphrase policy from `GetPassphrasePolicies` (`PasswordRules`, the add page's
   rules; several devices: every policy applies, "length" and "complex" together = at least 15 with every class), errors
   below the fields, Cancel / **Set password** (disabled with the reason as tooltip). One `DeviceService.SetFirstPassword`
   call with the `ui:ProgressRow`; all took it: the dialog closes; otherwise those devices leave the dialog and the result
   stands below the password ("1 of 3 devices failed: 10.0.0.41 (The device already has a password. Nothing was
   changed)"). Fake mode sets it in memory (10.0.0.40 is Password not set, policy "complex"). Tests:
   `tests/Oadm.Server.Tests/SetFirstPasswordTests`, `tests/Oadm.Client.Tests/DeviceSetPasswordTests`.
   **Tags** (icon `tag`, one or many devices) opens the Tags dialog, see "Device tags".
   **OADM** (icon `settings`, after the task groups, before Remove): settings of OADM itself for the selected devices,
   rarely needed; today **RTSP port** (video devices, "Optics Control"), see "Live view".
5. Resizable, collapsible bottom pane **Tasks** (no tabs), one row per task (= per device).
   Columns: Name (3*, min 220 px), Device (110 px), Status (3*, min 220 px: icon plus message), Current step, Start time, Owner, Progress (bar).
   An empty history says "No tasks yet. Right-click devices to run one." **Current step** is
   "Step 3/6 · Upload firmware" plus " · 45 %" while the running step reports progress (tooltip: the text
   and the step detail); a failed task shows its failed step, a successful one just "Completed"; empty for plugins without steps. **Device** is the task's
   device by its device grid address (IP or host name, resolved through the client device store
   and updated live); a device removed since shows as "Removed device 1a2b3c4d" (id shortened).
   Tooltip: "10.0.0.200: Failed - Connection refused". Sortable by the text. Status chips:
   Queued/Cancelled neutral, Running violet, Done green, **Done with warnings** amber (warn
   chip), Failed red. **Details** opens the task details window, live while it is open: one header line with the
   device (address, MAC address, model, status chip with the message), a **Steps** card (scrolls to the step the task
   is on) ("3 of 9 steps finished"; per
   step #, state chip (Pending/Skipped neutral, Running violet, Done green, Warning amber, Failed red),
   name, detail, duration, progress bar + percent only for the running step; rows updated in place) and a
   Log card (level chip, time, device address, message; `TaskService.GetLog`, re-read when the current
   step or the task state changes, new entries appended), built from the shared grid and chip styles; **Cancel task**
   in its footer while the task runs. Pane buttons: details, cancel, delete, a separator, **delete all** (red, with
   confirmation; running tasks are cancelled first; hidden for users with a device group); all work per task.

**Logs page** (rail, bottom; `Logging/LogsViewModel` + `LogsView`): the segmented switch **Server** | **Client** | **Audit**
on top of each card. **Server** (first when the role may see it, permission `logs.server` "See the server log", group "Logs", and only for users
with all devices: its text names devices;
Administrator always, Operator and Viewer off by default): the server's log files read on request by gRPC
`ServerLogService.List(offset, limit, min_level, search)` (`logs.proto`; `Oadm.Server.Logging.ServerLogGrpcService` over
`Oadm.Contracts.Logging.LogFileReader`: the files `oadm-server-*.log`, newest first, entries of the Serilog template, continuation
lines (exceptions) belong to the entry before, read shared while the logger writes, only the files a page needs; at most
500 a page), 200 entries a page (`ServerLogViewModel`), level filter and search on the server, Refresh, grid Time, Level
chip, Source (the class without namespace), Message (tooltip: source, message, exception), read again when the page
opens. **Client**: this client's log of the session (`LogStore`, newest 10,000, live), level filter and search, 200 a page.
Both page with the shared `Logging/LogPager` ("Entries 201 to 400", Newest, Newer, Older); a live client entry never moves an
older page. **Audit** (administrators): as before. Every failed event rule action is also a server log Warning ("Event rule
X: action Y on 10.0.0.48 failed: <reason>", with the exception when it was unexpected), a skipped one and a rule held back by
its limits Information (`ActionRunner.Write`, `RuleEngine.LogRule`). Fake mode: 650 sample entries
(`FakeOadmApi.ServerLog.cs`). Tests: `tests/Oadm.Server.Tests/ServerLogTests`, `tests/Oadm.Client.Tests/LogsPageTests`,
`RuleEngineTests.ActionsRunInOrderAndAFailureDoesNotStopTheNext`.

Device grid columns, default order (Address first: installers find a device by address and model; the column chooser
and the saved per-client order cover other preferences):

| Column | Source |
|---|---|
| (icon) | category icon (camera, encoder, speaker, audio, intercom, radar, I/O module, door controller, generic), tooltip "Camera (Dome Camera)" |
| Address | IP or host name, hyperlink opens device web UI in default browser |
| Model | ProdNbr |
| Status | computed, see enum |
| Compliance | off by default (column chooser; greyed there while compliance is off): the worst compliance result ("2 problems, 1 warning" chip, "OK" plain text, failed checks as tooltip; sorted problems first); while it is on the toolbar toggle **Show each compliance check** shows one column per check instead; hidden without "See compliance". See "Compliance" |
| MAC address | SerialNumber from basicdeviceinfo |
| Firmware | Version |
| SoC | basicdeviceinfo `Soc` (status poll and full refresh), shown in Axis spelling: "Axis Artpec-8" = "ARTPEC-8", other chips as reported (`DeviceSocText`); empty until read; in the search and the export |
| Architecture | off by default (column chooser): basicdeviceinfo `Architecture` (= `root.Properties.System.Architecture`, status poll and full refresh) as reported ("aarch64", "armv7hf"); empty until read; in the search and the export |
| Tags | the device's tags as chips (`c:TagChipList`, `Border.tagChip`: the name in the tag color on a subtle pill), "+N" when they do not fit, tooltip with every tag; sorted by "Building A; PTZ" |
| Site | the site the device is reached through (`Device.site_name`), empty on the server's own network; shown only while the server has sites; in the search and the export |
| DHCP | Network.BootProto == dhcp -> Yes/No |
| HTTPS | HTTPS enabled -> Enabled/Disabled |
| Certificate expires | CertNotAfterUtc as days left: "245 days", "1 day", "Today", "Expired 3 days ago", "Never" for NotAfter 9999-12-31 (RFC 5280, e.g. the Axis device ID certificate). More than the device's days of PKI's Expiry warning policy (`Device.cert_warning_days`, 30 without PKI or without a policy for the device): plain text, warn chip within them, error chip expired; right-aligned (numeric column). Over HTTP read through VAPIX; empty when HTTPS is off or unreadable |
| Certificate | CertTrust: Trusted (plain text), Self-signed (warn chip), Untrusted / Expired (error chip). Empty when HTTPS is off or unreadable. Both certificate cells have a tooltip with subject, issuer and valid-until date |
| IEEE 802.1X | dot1x.Enabled -> Enabled/Disabled |

Polling: server refreshes status (basicdeviceinfo, **3 s** timeout: `StatusPollTimeout`) for every device every **60 s** by
default (`Polling.IntervalSeconds`). Full refresh (basicdeviceinfo + network parameters +
certificate info + server name) on add, after a task finishes on that device,
every **10 minutes** per device that is Ok (`Polling.FullRefreshMinutes`, checked every 30 s; devices that are not Ok
are left to the status poll), immediately when a status poll sees a device go from any non-Ok status back to Ok, and
immediately when it sees a new firmware version or model (a new API list). All full refreshes go through one deduplicating
queue, at most `RefreshParallelism` = 64 at once. Scale: a poll round starts every interval and spreads
its device starts evenly over 90 % of it (`PollRoundAsync`, at most `MaxParallelism` = 200 at once, no setting: the
spread keeps about 40 busy for 30,000 devices answering in 70 ms, the cap only matters while devices are slow or dead);
each device row is read right before its poll. A device that stops answering (timeout, refused connection) is asked once
more at the end of the round before it turns Unreachable; in an outage (more than `OutageShare` = 10 % of the round
failed) there is no second try (30,000 dead devices: one round of 7.6 minutes). More than half of a round failing logs one
warning "Most devices do not answer (F of P). Check the server's network.", and one information when they answer again; single
failed polls are Debug only. After a server start the first scheduled full refresh is staggered: the n-th of N devices
is due n/N of an interval later. A poll that changes nothing but LastSeenUtc is written but not published to watchers (no
30,000 identical updates per minute to every client). Tests `DevicePollingRoundTests`, `DevicePollingScheduleTests`.
