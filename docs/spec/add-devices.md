# Add devices page

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.

Goal: maximum technician satisfaction. Adding devices is one page, no steps; the server
logs in to every device it finds with the credentials the technician already has.

Toolbar (toolbar plugins, see [plugins.md](plugins.md#toolbar-plugins)): the **Add** menu entries Discovery, Network
range, Add manually, Import from file and Import from ADM open the same dialog window (header = the entry name)
(`Discovery/AddDevicesWindow`, shared dialog controls: title bar, one card, footer) in its modes. How OADM finds devices:
[discovery.md](discovery.md).
- **Discovery**: zero-conf discovery starts immediately; devices appear live. The scan ends after
  `Discovery.ZeroConfSeconds` (Settings page "Discovery duration (s)", default 30, 5..300).
- **Network range**: From / To inputs on top; Enter or the Scan button starts (several ranges add to
  the same list).
- One info line above the list (`ui:ProgressRow`, indeterminate for zero-conf, percent for ranges): the scan status and the
  counts in one text (`AddDevicesViewModel.StatusLine`: "Import finished, 5 devices found · 4 ready to add · 1 need a login
  · 4 selected"; while a scan runs "Searching the network... · 8 found · ..."), then a
  **Stop** button (icon stop) while a zero-conf or range scan runs (`StopScan` for every running
  session; the devices found stay) and **Scan again** (icon refresh) after it finished or was stopped
  (zero-conf: a new session; range: the same range again; found devices stay, deduplicated by serial; a
  "Checking" of the new search never hides the result or selection of an earlier one). The text then
  says "Scan finished, N devices found" or "Scan stopped, N devices found" ("1 device found"), plus ", M already added" when
  devices OADM already manages answered: those are not listed at all and not in the summary.
  Add manually has neither button.
- **Add manually**: Address input (IP or host name, optional port and scheme,
  `https://camera.example.com:8443`); Enter or Find probes that address (`ProbeAddress`); every
  address adds a row; "No Axis device answered at X." when nothing answers. The entered address
  (host[:port]) becomes the device address on add, as typed.
- **Import from file**: first the dialog "Import from file"
  (`Discovery/ImportHelpWindow` + `ImportHelpViewModel`, single operation: the columns, the example
  `DeviceImportFile.Example` in a `ui:CodeView` (the user docs show the same, test `DeviceImportFileTests`), the limits;
  Cancel / **Choose file**), then the client asks for a CSV file (open picker through `IDialogService.OpenFileAsync`) and reads it with
  `Discovery/DeviceImportFile` (O(n)): the Export devices CSV or any CSV with an address column (header names
  case-insensitive: Address, IP address, IP, Host name; optional **User name**, **Password**, **Tags** ([tags.md](tags.md))
  and **Site** ([sites.md](sites.md)); other columns ignored), or a plain file with one address per line; comma or semicolon, UTF-8 (BOM optional), UTF-16 with BOM,
  else Latin-1; a formula guard `'` of the export is removed. A password without a user name is for `root`; a user
  name alone is no credential. The whole file is refused in `ui:MessageWindow` ("Import from file") when it is larger
  than 64 MB (a guard only: the lines are the limit), has more than 30,000 addresses (`DeviceLimits.MaxDevices`; the
  same for an ADM export), no address at all, no Address column (with a header) or is not text.
  Problems of single lines stay in their row (chip "Not added" + detail): no address, not an IP address or host
  name (http/https, port, no path), "Listed before in line N." (same address), line longer than 4,096
  characters, unclosed quote, user name > 64 / password > 256. The page (header "Import from file", "Addresses from
  <file>. Logins in the file are tried first.") shows one row per line at once in file order ("Queued"), then probes the
  addresses with `ProbeAddress(address, user, password, import_line)`, at most `MaxImportProbes` = 64 at a time (a probe holds
  its slot until its watch stream ended, i.e. its logins finished): the row becomes "Checking...", then shows
  the device it found (`DiscoveredRowViewModel.Adopt`, the line's row keeps its place) with the login result;
  "Not found: No Axis device answered at X." (also for a host name that does not resolve), "Not added: Same
  device as <address> (<MAC>)." when another line found the device; managed devices leave the list like
  everywhere. Progress row "Checking addresses: 120 of 5,000" (percent), **Stop** (lines not probed yet: "Not
  checked", neutral), then "Import finished|stopped, N devices found" (+ ", M already added"); no Scan again.
  Summary adds "N not added". Credentials of the file stay in client memory until their probe, go to the server
  only, are never logged and never saved to the credential list (the inline login editor keeps its own "Save to
  credential list"). Then the technician reviews and clicks Add as usual. Scale: 30,000 lines are listed,
  filtered and selected in one reset each (test). The first contact of an imported line waits only `Scan.TimeoutMs`
  (1.5 s, like the range scan; `ProbeAddressRequest.import_line = 5`), Add manually keeps 3 s: 30,000 lines with 10 % off
  take about 12 to 15 minutes on a LAN.

**Waiting list** (Import from file and Import from ADM: while lines did not answer, the footer shows "2 devices do not
answer yet. OADM adds them once they come online." and a secondary button **Add 4 now, 2 later** ("Add 2 later" without
checked rows; `AddWithWaitingCommand`) left of **Add 4 devices**, which adds only the checked devices): with it the lines whose device
did not answer or did not identify itself as an Axis device (`DiscoveredRowViewModel.CanWait`: "Not found" or Unreachable;
not file problems, not Login failed, not "Not checked") go to the server in batches of 5,000 (`WaitingDeviceService.Add`)
with their address, login, expected MAC address and tags, before the found devices are committed. The page reads the waiting list before it probes (`ListWaiting`, a role without the
right: none): a line that does not answer and is waiting already shows **Already waiting** ("An earlier import keeps it
waiting. OADM adds it once it answers.", neutral), is not kept again, and the finish line adds ", 12 already waiting".
Server: `Oadm.Core.Devices.WaitingDeviceStore` (table WaitingDevices, migration
`WaitingDevices`: one entry per address (`WaitingDeviceStore.Normalize`: as a device record stores it, no scheme, upper
case), an address kept again replaces login, MAC address and tags, renews its kept time and is tried like new; writes go
one at a time; password AES-GCM with the master key, entry id as associated data, never listed or logged; at most 30,000,
over it the whole call is refused "The waiting list holds at most 30,000 devices. Remove devices from it first.") and
`Oadm.Server.AddDevices.WaitingDeviceRunner` (hosted): a round every `Devices.WaitingRetryMinutes` (15, 1..1440, Settings
page "Try waiting devices every (min)", after the end of the last round, first round a minute after the start, a changed
setting applies within a minute) or at once for `TryNow`; each round reads the entries to try and, only when there are any,
drops every entry whose address or MAC address OADM manages by now (`DeviceRepository.ListAddressesAsync`, no full rows),
then tries the rest 32 at a time the import's way but with the normal first contact (`StartAddressProbeAsync(importLine:
false)`: a device behind a slow link is found), the expected MAC address and the line's login in the session, the Axis check
before any password, then the credential list, at most 10 rejected per device) and stores a device that answers with
`DeviceAdder` like the add page (the login that worked; factory default as Password not set), its tags, a full refresh and
the audit entry "Device added automatically" (user "system", "<source> (waiting list, kept by <user>): ..."), only while
its entry is still the one the round read (`IsCurrentAsync`: removed or kept again meanwhile = not added, nothing written
back; results carry the kept time they were read with); a try never throws and a failed write of results is logged without
ending the round; a device that
answers but rejects every login is marked Login failed and **not tried again by itself** (no repeated wrong passwords) until
Try now; results are written in batches of 256. Nothing drops off by itself: an entry stays until its device is added (also
another way) or a user removes it. gRPC `WaitingDeviceService` (`waiting.proto`, all "Add and remove devices"): `Add`,
`List(after, limit)` (address order with a cursor, `next_after`: entries removed while a client reads never shift a page;
`checking = 13`: asked for by Try now or in the running round and not tried yet), `Watch` (summary
{count, login_failed, trying, next_try, remaining}, at most once a second), `Remove(ids | all)`,
`TryNow(ids | all; all also tries the failed logins)`; audited "Kept devices waiting" and "Removed waiting devices". Client:
`ServerConnection` watches the summary for roles with the right into `DeviceStore.Waiting` (`WaitingDevicesState`); the
Devices page shows the link "2,412 devices waiting to be added" next to "N devices, M selected" while the list is not empty;
it opens `Devices/WaitingDevicesWindow` (`WaitingDevicesViewModel`: "Next try at 14:35", toolbar Try now, Export, Remove (red,
confirmed; needs a selection), count and search; grid Address, MAC address, Tags, Kept, Last try, Result (warning chip for a
failed login); read in pages of 5,000, read again after each round; Try now and Export take the selection, else every row
the search shows; the export is the import's format without passwords; rows being checked show the spinning "Checking..."
chip and the line above the list "Checking: 120 left"; the list is read again after Try now, when a round starts and when
it ends). Fake mode keeps the list in memory
(`FakeOadmApi.Waiting.cs`, nothing ever answers). Tests: `WaitingDeviceStoreTests` (+ Perf 30,000), server
`WaitingDeviceTests`, client `WaitingDevicesTests` (+ Perf 30,000, screenshots), `AddDevicesPageTests`.

**Import from ADM** (Add menu, `HostPages.AddImportAdm`): the same help dialog in its ADM mode (`ImportHelpViewModel.ForAdm`:
addresses, logins, tags, everything else read from the devices, OADM is one server, an amber warning that the export holds
the passwords in plain text), a `.json` picker, then `Discovery/AdmDeviceExport` reads the device export of AXIS Device
Manager (format 1.x and 2.x: `encrypted`, `fileFormatVersionMayor` (missing = 1), `data.serverInformation` (object, or a
list for several servers), `data.deviceData.devices` {id, macAddress, address, httpPort, httpsPort, deviceUserName,
devicePassword, ...}; 1.x: the password as it is, tags `deviceTag` + `deviceTagRelation`; 2.x: the password base64 of UTF-8
(not decodable: the row's problem "The password in the export cannot be read.", no login), tags `deviceTags` [{name,
devices: [ids]}]; both tag layouts read in both versions; property names case-insensitive, unknown ones ignored) into the import lines of
`DeviceImportFile` (`CheckLine`: the same checks as a CSV): the address with its port when not 443 / 80 (`https://a:8443`,
else `http://a:8080`), the login, the tags, and the MAC address as `ImportLine.ExpectedSerial`. Refused whole: not JSON or
no device list ("not a device export of AXIS Device Manager"), `encrypted: true` ("Export the devices again ... without
encryption"), a major format version other than 1 or 2, no devices. An export
naming several ADM servers asks first (`DevicesViewModel.MultiServerText`: devices only the other servers reach show Not
found). The add page says "Import from ADM" and "Devices from <file> (ADM server <name>). Logins in the export are tried
first."; a row whose device answers with
another MAC address is "Not added: Another device answers at this address (X). The file lists Y." and the server sent that
device no password. Model, firmware, host name, product type, apiMode and securityMode of the export are not used (read
from the device). Tests `AdmDeviceExportTests`, `AddDevicesPageTests`, `FastAddTests`.

**DNS names**: Discovery and Network range have the check box **Use DNS names instead of IP addresses** (below the info
row; remembered per client, `ClientSettings.AddByDnsName`). The server looks up the DNS name of every found IP
(`Oadm.Core.Discovery.IDnsNameLookup`, `SystemDnsNameLookup`: reverse lookup, a full name with a domain, not ".local" or
".arpa", that resolves back to the same IP; 3 s, 16 at a time, once per device and address, never for typed addresses;
`DiscoveredDevice.DnsName`, dropped when the address changes). Checked, the Address column shows the DNS name (tooltip
"IP address x"; the search also finds the IP) and `Commit(use_host_name)` adds those devices by it
(`DeviceAdder.ChooseAddress`); a device without a DNS name is added by IP. The address a device is added with stays its
address (no switching later, the grid shows it). Add manually and Import from file keep the typed address. Tests:
`DiscoveryServiceTests` (fake lookup), `AddDevicesServiceTests`, `AddDevicesPageTests`.

List: checkbox (40 px; shown only for rows that can be added), category icon (36 px), Address (200), MAC address (150),
Model (160), Site (130, shown once a device answers through a site), Login (status, star: takes the rest so status and detail sit right after Model; rows grow so the reason
shows on up to two lines, grid class `wrapRows`), Action (auto, at the right edge). An empty list says what to do first ("Enter a range and press Scan.", "Enter an address and press Find."). Scan
and Find are filled secondary buttons. A page error (Add or discovery failed) stands directly below the list. The scan
progress bar disappears once a scan or import ended; its text stays. Problems of file lines ("Not added") use the
warning chip, devices that do not answer the error chip. Above it one row: the info line (above), Stop / Scan again and the
search box; Discovery and Network range add the DNS name check box below it. Login status per device
(`ui:StatusChip`): Checking... (accent), Authenticated (user) (ok), Password not set (warning),
Login failed (error, reason as tooltip), Unreachable (error), Added (ok, row greyed; already managed devices are not
listed, see above).
Only addable devices can be checked: authenticated ones, and factory-default ones
once a first password is entered. A device is checked by itself the first time it can be added; one the user unchecks stays
unchecked. The check box column's header is a three-state
check box (`AllSelected`: all, some = dash, none of the devices that can be added; O(1) from the counters; disabled while
none can be added): a click checks every addable row the search shows, or unchecks them when all were checked. **Add** (footer, "Add 3 devices") commits the checked devices
in one click and the page always closes (no "Keep open" option).
Every password field (login editor, first password + confirm) is `ui:PasswordBox` with the eye button.

- **Login failed**: a click on the row or the "Log in" link opens the inline editor below the list
  (user name, password, "Save to credential list" checked by default on its own row, Retry, Cancel; while an inline
  editor is open its Retry / Apply is the only accent button, the footer's Add turns secondary). Retry calls
  `RetryAuth` right away (with every other session of the page as `related_session_ids`) and updates
  the row; success checks the row and closes the editor, a wrong password shows "The user name or
  password is wrong.". After a successful Retry the server tries the same credential on every other
  device of the page whose login failed; the page watches the sessions whose stream already ended
  again to receive those results.
- **Factory default**: "Set password" opens the inline password editor (new + confirm, user root,
  hint with the systemready passphrase policy: none = 1-64 printable ASCII, length = at least 15,
  complex = at least 12 with upper, lower, digit and special character; checked client side, the
  device checks again), "Use for all factory-default devices". The password stays in client memory
  until Add; the server sets it (pwdgrp.cgi, HTTPS when offered, POST body only) and
  stores it as the device credential. The row shows "Password ready · Change".

Server: `DiscoveryAuthenticator` (Oadm.Server/AddDevices). For every device event of a watched
session it starts the automatic login once per device state: managed serial = ALREADY_ADDED, probe
said factory default = PASSWORD_NOT_SET (plus the passphrase policy read anonymously), unreachable =
UNREACHABLE, otherwise it tries the known credentials one at a time, at most 10 per device (no
lockout risk): the credentials typed on the page (RetryAuth), then the credential list in the order
added. Passwords of managed devices are never tried on new devices. Before the first credential goes to
a scheme the device must pass the **Axis check** on it: anonymous `basicdeviceinfo
getAllUnrestrictedProperties` answering a 12-hex `SerialNumber` equal to the discovered serial and a
`ProdNbr` (once per scheme and login run). Logins go to HTTPS first, then HTTP ("Add manually" with an
entered scheme: only that one); the handler answers Digest when offered, else Basic, also over plain HTTP
for such a verified device. A device that fails the check gets no credential and shows UNREACHABLE "The
device did not identify itself as an Axis device. No password was sent." (logged as a warning). One try
= `basicdeviceinfo getAllProperties` with the credential (plus `param.cgi` network parameters when the
device allows anonymous access); 401 = next, a scheme that does not answer = the next scheme, no scheme
answering = UNREACHABLE, another serial at the address = UNREACHABLE. A device that logged in over HTTP
with Basic only is stored with scheme http and then needs Digest like every managed device (Basic stays
HTTPS only outside the add page's login), unless the server setting `Devices.AllowBasicOverHttp` is on (then also through
a site; with it off a site device that offers only Basic over HTTP ends LOGIN FAILED with `DeviceMessages.BasicOnlyOverHttp`,
no further credential tried). At most 32 devices log
in at the same time (`DiscoveryAuthenticator.Parallelism`; each device still gets one login after another). Results live per (session, serial) as long as the session (below) together with
the credential that worked (server memory); `Commit` stores that credential for the new device
(explicit `credentials` in the request win). Passwords never leave the server once
stored; RetryAuth credentials travel only client -> server and are kept in server memory (or the
credential list when asked). Adding devices is not a task; the first full refresh is queued in the
background.

**Device groups** ([device-scope.md](device-scope.md), Decisions "New devices"): the server builds an `AddReach` from the caller
(`Oadm.Server.AddDevices.AddReach`): a user with all devices adds everywhere; a user with a device group
discovers and adds only at the sites of the group (`DiscoveryRoutes`: zero-conf, the range scan and address probes on the
server's own network only with "This server", sites outside the group are never asked) and only devices that land inside
the group (an excepted serial is not added); a group of tags or single devices only cannot add ("Your device group has no
site, so you cannot add devices.", `DeviceGroupMessages.CannotAdd` in `Oadm.Sdk.Devices`, also for keeping devices waiting;
the client disables the toolbar's Add and the empty page's Add devices with this text as tooltip, from Your role's
`MyRole.devices`: no "This server" and no site, `UserSession.AddDevicesRefusal`). Sessions belong to the
user who started them (`DiscoverySessionOwners`; `CoreDiscoveryBackend.FindDiscovered` finds only the session's own
devices). A session lives while a watch of it is open and then 30 minutes after its last use (Stop of the page keeps it
that long, so Add still works); then the server stops it and forgets its owner and its logins (typed passwords, the
credential list): a client that crashed leaves nothing behind. A user's sessions stop at once on a logout or an expired
login, a new role or other devices, and new rules or the deletion of the user's group; a session started by a call that
began before such a change is stopped and refused ("Your devices changed. Connect again."). The credential list is never tried for a group user (Retry and lines of a file use
only what was typed; without anything typed: "No password was entered yet. Log in once."). A managed device outside the
caller's devices is only "already added" (no model, address or names). Every new device stores who added it
(`Device.AddedByAllDevices`, `AddedByGroupId`: Commit, the waiting list, the automatic add of core plugins as all
devices); the groups it entered are audited "Entered device group" (target the group, detail "3 devices (added): ...",
`DeviceGroupMembership`, one entry per group), a device that moves to another route "Left device group" / "Entered device
group" ("moved"). The waiting list is scoped by site (`WaitingScope`: the group's sites and the user's own entries for
everywhere, never another user's entries for everywhere, also with "This server"; ids outside: NOT_FOUND "Some of these devices are no longer
waiting.", a line of another site: NOT_FOUND; a line without a site of a group with one site and without "This server"
waits for that site). Each entry stores who kept it (`QueuedByUserId`); a round tries it as that user is now (the
reach read once per round per user and again when `AccessEpochs` shows a change of the user's access or group since that
read; a change during a try ends it before the probe, the credential list and the first login, nothing written, the next
round tries again; routes,
no credential list for a group, inside the group, tags any group uses left out), an entry whose user cannot add there any
more is not tried and nothing is sent ("Not added: the user who kept it cannot add devices here.", held until Try now);
an entry without a user id (kept before device groups existed) is tried as a user with all devices. Tests `AddDevicesScopeTests`, `CanaryTests`,
`WaitingDeviceStoreTests` (+ Perf 30,000 entries per scope), `DeviceGroupMembershipTests` (+ Perf 30,000 devices x 200
groups), `DiscoverySitesTests`.

**Device limit**: one server manages at most 30,000 devices (`Oadm.Sdk.Devices.DeviceLimits.MaxDevices`, text
`TooManyDevicesText`). `DeviceRepository.AddAsync` refuses an add over it (`DeviceLimitException`; adds are serialized), so
every way in is covered; `Commit` checks the whole add first and answers RESOURCE_EXHAUSTED, nothing is stored (Prepare
and Commit read the managed serials once for "managed already" and the limit, and look the session's devices up in one
step, `IDiscoveryBackend.FindDiscovered(session, ids)`: linear at 30,000, Perf test `AddCommitScaleTests`); the add page
shows it in `ui:MessageWindow` and stays open; the automatic add returns `DeviceAutoAddResult.LimitReached` and logs a
warning at most once an hour. Plugin runs are sized by the same limit (System report 50, Device triggers 10,000 watched
devices). Tests `DeviceLimitTests`.

Automatic add by core plugins (the DHCP server's "Automatically add Axis devices that get an
address"): the SDK service `IDeviceAutoAdd` (`ICorePluginContext.AutoAdd`, DIM null) is implemented by
`Oadm.Server.AddDevices.DeviceAutoAddService` on the same code as this page, no second pipeline: `AddAsync(address,
expectedSerial, source)` runs the anonymous probe (`VapixProbe`; nothing = Unreachable, another serial = SerialMismatch),
the Axis check of `DiscoveryAuthenticator` without any credential (fails = NotAxis, "No password was sent."), then for a
factory-default device no login at all, else the automatic login in a session of its own (credential list only, at most
10 rejected, HTTPS first; Unreachable = not added) and stores the device through `DeviceAdder` (shared with `Commit`:
address = the IP, pin from the probe, the credential that worked, verified) with status Ok, Credentials required (no
credential fits; the context menu's "Log in" fixes it) or Password not set (the context menu's "Set password"), queues the
first full refresh and writes the audit entry "Device added automatically" (user "system", target "<ip> (<serial>)",
detail "<source>: <result>"). Not added (not Axis, other serial, unreachable): logged only. `FollowAsync(serial, address,
source)` follows managed devices ([discovery.md](discovery.md#following-moved-devices)). Passwords never reach the plugin.

Follow-up logins: per (session, serial) the authenticator keeps the device as last observed, the
number of rejected automatic attempts and which credentials (SHA-256 of user + password) the device
rejected. When a new credential becomes known it is tried on every LOGIN_FAILED device that has
attempts left and has not rejected it: (a) a successful `RetryAuth` adds it ("entered", or the list
entry when saved) to the session and its `related_session_ids` and starts those logins before the reply
returns (the devices show PENDING); (b) a credential added to the credential list (Credentials page or
"Save to credential list") reloads the candidates of every session. The follow-up uses the normal login
loop: one login at a time per device, at most 32 devices at once (`DiscoveryAuthenticator.Parallelism`), at most 10 rejected credentials per
device in total (typed credentials of `RetryAuth` are remembered as rejected but not counted), entered
credentials first, then the list; credentials added while it runs are
tried in the same run. Unreachable attempts count neither as attempt nor as rejected.
