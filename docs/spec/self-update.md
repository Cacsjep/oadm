# Self update

Status: **built** (2026-11-03). A real update of each installer is the manual test of `packaging/README.md`.

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.

An administrator updates the OADM server and their own client from inside OADM, instead of going to the download page.
**Install now** in the new version dialog starts it; the result is shown to the client after the update, also when it
failed. Site nodes are updated by hand on their computers (see "Site nodes"). Decisions of 2026-11-02, asked one by one;
gaps closed on 2026-11-03 (helper outside the service, silent installs keep the setup type, first version, database copy,
installer results, disk space; no offline path).

## Rules that never break

1. **Administrators only.** Updating the server and a client needs the permission `server.update`, which
   only the Administrator role holds. Other users see the new version dialog as today (Open download page).
2. **The installers do the work.** The server and the client run their own MSI, .deb or .pkg silently
   ([packaging.md](packaging.md)): services, firewall rules, folder rights, plugin cleanup and the MSI's rollback stay the
   installers' job. Never a file swap of a running installation.
3. **No extra app.** The helper that runs the installer is the same program in update mode (`Oadm.Server --update`,
   `Oadm.Client --update`), copied to a folder of its own first (in the program's own data folder, never a shared
   temporary folder) so the installer can replace the
   original.
4. **Only stable releases of `Cacsjep/oadm`**, never a pre-release, never a downgrade, never the same version. The
   installer's SHA-256 must match the release's `SHA256SUMS.txt` (both over HTTPS from GitHub), and the file must be the
   one the platform expects by name and size limit (400 MB); anything else is deleted and refused.
5. **The server remembers.** Every update has a record in the data folder (`update/state.json`: from, to, started,
   step, result, reason, installer log path, database copy) written before the installer starts and completed by the server that starts
   afterwards, old or new. The client only shows what the server says.
6. **Never during a task.** **Install now** is disabled while a task runs (tooltip "3 tasks are running"); once the update
   started, the server starts no new task and no event rule action.
7. **No rollback by OADM.** A failed installer undoes itself where the installer can (MSI); OADM never reinstalls an older
   version. The result says what happened and what to do.
8. **Every update is in the audit log**: who, from, to, result.

9. **The helper never runs inside the service.** Stopping the service must not stop the helper: on Linux it starts as
   its own transient unit (`systemd-run --unit=oadm-update --collect`; systemd stops every process of a service's
   control group), on macOS as its own launchd job (`launchctl submit -l com.oadm.update`), on Windows as a detached
   process (the Service Control Manager does not stop a service's child processes).
10. **A silent update keeps the setup type.** "Client only" stays client only, "Server and client" stays both:
    - Windows: `OADM_SETUP` is set today only in the setup screens (`Sequence="ui"`), which `/qn` skips. It is also set in
      the execute sequence after `MigrateFeatureStates`, and the helper passes no feature properties.
    - macOS: `installer -pkg` remembers no choices. The helper passes a choice file (`-applyChoiceChangesXML`) with
      `com.oadm.choice.server` unselected when no server is installed (no `/Library/LaunchDaemons/com.oadm.server.plist`).
    - Linux: server and client are separate packages, each updates only itself.
11. **A copy of the database first.** Before the installer starts, the server copies its database with the SQLite backup
    API to `update/oadm-<from version>.db`: the new server migrates the database on its first start, and an older server
    cannot open a migrated one. Only the copy of the last update is kept. A failed or missing result names the copy;
    putting it back is a manual step in the user docs, never done by OADM.
12. **Installer results are read, not guessed.** Each exit code has its text in the result:
    - `msiexec` (always `/norestart`, OADM never restarts the computer): 0 done; 3010 done, "Restart the
      computer to finish the update"; 1618 "Another installation is running. Try again later."; anything else "The
      installer failed (code N)." with the log.
    - `apt-get` waits up to 5 minutes for the package lock (`-o DPkg::Lock::Timeout=300`), else "Another installation is
      running. Try again later."; any other failure "The installer failed." with its output in the log
      (apt-get runs with `LC_ALL=C`, so the busy lock is recognized on every system).
    - `installer`: a non-zero exit is "The installer failed." with `/var/log/install.log`.
13. **Enough room.** The data folder needs free space of three times the installer's size (download, installer cache)
    plus the size of the database (its copy), else "Not enough free disk space for the update." before anything is
    downloaded.
14. **The first version with self update is installed by hand.** Older servers and clients have no Install now; the
    release notes of that version say so.

## Server (and a client on the same computer)

1. The new version dialog shows **Install now** (filled accent; Open download page becomes secondary) to an
   Administrator whose server is older than the release. The dialog changes in place to the steps: Download the update,
   Check the download, Install the update.
2. The server checks the free space (rule 13), downloads the installer of its platform from the GitHub release into
   `update/`, checks it, copies the database (rule 11), writes the record, tells every connected client "Updating the
   server to V4.9.0" and starts its helper outside the service (rule 9), then stops accepting calls. GitHub not reachable:
   "Could not download the update. Install it from the download page." with Open download page; nothing else happens.
3. The helper (as SYSTEM / root) waits up to 30 s for the clients on this computer to close (step 6), so the installer
   finds no client file in use. It runs the installer silently with a log (`msiexec /i … /qn /norestart /l*v`,
   `apt-get install -y -o DPkg::Lock::Timeout=300 ./oadm-server_….deb`, `installer -pkg … -target /` with the choice
   file of rule 10), reads its exit code (rule 12), then waits up to 5 minutes for the service to
   answer `AuthService.Status` on this computer with the new version.
4. **The new version answers:** done. **The installer failed** (on Windows the MSI rolled back and the old server runs
   again): the helper writes the installer's error into the record. **The new version does not answer**: the helper writes
   that; the service restarts it on failure, and the server that starts reads the record. The new server fetches the optional plugins
   of its version by itself once it has started (`docs/spec/add-ons.md` rule 3); the update never waits for them.
5. Clients: while the server is away the client shows "Updating the server to V4.9.0" instead of "Connection lost". After
   the reconnect: "Server updated to V4.9.0", or "The update failed. The server runs V4.8.0." with the reason and the
   installer log's path on the server. Not back after 10 minutes: "The server did not come back" and what to check (the
   service OADM Server, the installer log). The record's result is also on the Logs page.
6. **Client on the server's computer.** The Windows MSI and the macOS .pkg hold the client too, so the update replaces it.
   Clients on that computer (the server tells each one in its Watch stream: it connects from a loopback address or one of
   the server's own addresses and names the server's machine name) start their own helper as the user and close; the
   helper waits for the server's result and starts the client, which shows it: the new version answers, or the old one
   answers again with no update running (`AuthService.Status` `updating_to` empty: the installer failed), or a server that
   never began one after 1 minute, at most 10 minutes. Whatever stops a helper (server or client), it ends with a result:
   the server's writes Failed "The update stopped before it finished.", the client's starts the installed client. On Linux the client is a package of
   its own and updates like a client on another computer.

## Client on another computer

Once the server is newer than the client, an Administrator's client offers **Update client** (the new version dialog
for the server's version; other users: Open download page). The client downloads its installer from the GitHub release
(not reachable: Open download page), checks it like the server does (rules 4 and 13), starts its helper and closes. The
helper runs the installer with the system's elevation prompt and visible progress (`msiexec /i … /passive /norestart`,
`pkexec apt-get install -y -o DPkg::Lock::Timeout=300 ./oadm-client_….deb`, an administrator prompt for
`installer -pkg` with the choice file of rule 10), reads its exit code (rule 12), then starts the client that is installed now: the new one says "OADM updated
to V4.9.0"; the old one (installer failed, cancelled prompt) says "The update failed" with the reason. The setup type stays (rule 10). A computer with an installed OADM server (the MSI's server
folder, the macOS LaunchDaemon) gets no Update client: its installer would update that server too without the server's
safeguards, so the dialog keeps Open download page and the server's own Install now updates both.

## Site nodes

Not updated by OADM (decision 2026-11-02). A node runs with few rights on a computer in the customer's network (its own
virtual account or user, [packaging.md](packaging.md)); running an installer would need a helper with SYSTEM / root rights
there. After a server update, the result says "Update the site nodes on their computers" when a site has a node older than
the server, and the Sites page marks each older node with "Update on its computer" next to its version, with a link to the
download page.

## Server without internet

Not part of self update (decision 2026-11-03). A server or client that cannot reach GitHub is updated the normal way,
with the installer from the download page.

## gRPC

`UpdateService` ([grpc.md](grpc.md)), `Get` and `Start` need `server.update`:

- `Get`: the server's version, platform and machine name, whether the caller runs on the server's computer, whether the
  server is installed, the running tasks, the latest release, the running update, the last result and the sites with an
  older node.
- `Start` (version): checks the rules above, then steps 2 to 3. `FAILED_PRECONDITION` while a task runs, for a
  pre-release, a downgrade, the same version, too little disk space or a download that failed its checks.
- `Watch`: the update's steps. Every signed-in user (decision 2026-11-03): "Updating the server to V4.9.0" reaches every
  client, also those of other roles; the result after the reconnect stays the Administrators' (`Get`).

The update's steps reach every client through this stream while the server runs; the final result through `Get` after
the reconnect.

## How it is built

- **Installed or not** (`Oadm.Contracts.Updates.Installation`, the one place): the server is installed when it runs as
  the service from `Program Files\OADM\Server` (Windows), from `/opt/oadm/server` with dpkg's `oadm-server` list (Linux),
  from `/Library/Application Support/OADM/server` with `/Library/LaunchDaemons/com.oadm.server.plist` (macOS); the client
  from `Program Files\OADM\Client`, `/opt/oadm/client` with dpkg's `oadm-client` list (and `pkexec`, else Update client
  is not offered), `/Applications/OADM.app/Contents/MacOS`. Anything else (the ZIP, `dotnet run`) keeps Open download page.
- **Release files** by exact name (`UpdatePlatform`): `OADM-<version>-win-x64.msi`, `oadm-server_<version>_<arch>.deb`,
  `oadm-client_<version>_<arch>.deb`, `OADM-<version>-osx-x64.pkg`, and `SHA256SUMS.txt`; only from
  `https://github.com/Cacsjep/oadm/releases/download/` (`GitHubReleaseFeed`, `UpdateDownloader`).
- **Server** (`Oadm.Server/Updates`): `ServerUpdateCoordinator` (checks, download into `update/`, the database copy
  `DatabaseCopy`, the record `UpdateRecordFile`, the hold of tasks and event rule actions, the follow of the record, the
  audit), `UpdateGate` (`updating_to` of Status from Install now until the result; UNAVAILABLE for every call but Status
  and UpdateService once the helper started; a helper that cannot start always opens the server again),
  `ServerHelperStarter` (rule 9), `ServerUpdateHelper` (`Oadm.Server --update --data <folder> --to <version> --status
  <url>`, its log `update/helper.log`). The helper's copy lies in `update/helper-*` of the data folder (SYSTEM / root
  only), never a folder other users can write. The helper runs only the installer in `update/` whose SHA-256 the record
  holds, writes the step `installing` before the installer runs (a helper launchd starts again never runs it twice),
  kills the installer's process tree after 30 minutes, waits only for clients run from the installed client folder and
  asks the server for its version with the server's own pinned certificate. A record still running longer than the
  helper's worst case plus 5 minutes (`GiveUpAfter`, about 40 minutes) is failed ("The installer did not finish."); a
  not-back or given-up record of a server that runs the new version is done. The record is written through a temporary
  file of its own; a change is read, decided and written under `update/state.json.lock` (the give-up never overwrites the
  helper's result), and a file the other process holds open is read or replaced again a few times (async pauses, never a
  blocked thread). The server follows a running record until it is finished
  and always lets tasks, event rule actions and calls start again; an old server that starts while the record still runs
  (the MSI rolled back) holds task starts and event rule actions until the helper's result. A task that started between
  the check and the hold is counted before the download ("1 task is running."). A download that receives nothing for 60
  seconds or takes longer than 30 minutes stops ("Could not download the update. ..."); after GitHub's redirects a file
  may only come from github.com, objects.githubusercontent.com or release-assets.githubusercontent.com
  (`GitHubReleaseFeed.AllowedHosts`). Progress goes out once per whole percent. An old installer that cannot be deleted
  stays (never a reason to stop); the earlier database copy goes only once the new one is complete. A task a plugin
  starts during the update is refused like Run (FAILED_PRECONDITION "Updating the server to V4.9.0. Try again after the
  update."). A macOS helper with bad arguments removes its launchd job before it ends.
- **Client** (`Oadm.Client/Updates`): `UpdateNotifier` (which dialog), `UpdateViewModel` (offer, steps, away, result),
  `ServerUpdateTracker` (the Watch stream, the banner "Updating the server to V4.9.0", "The server did not come back"
  after 10 minutes), `ClientUpdater` (Update client, the close for the server's installer) and `ClientUpdateHelper`
  (`Oadm.Client --update-helper install|wait ...`, a copy in `update/helper-*` of the client's own data folder, removed
  on the next start, with a small window "Installing OADM V4.9.0" that cannot be closed). Download progress reaches the
  dialog on its own thread; any failure is shown in the dialog, never a crash. A client that comes back to a server still
  waiting for its helper's result asks `Get` again every 10 seconds (at most 30 times). The client the helper starts gets `--updated client|server` and shows the result first. msiexec 1602
  (the elevation prompt or the installer cancelled), pkexec 126/127 and osascript -128 are "The update was cancelled.".
- **Logs page**: the Audit tab (Administrators) shows the last update's result above the entries.
- Tests never run a real installer, `systemd-run` or `launchctl`: the installer, the process starts, the downloads, the
  disk and the clock are fakes (`Oadm.Server.Tests/Updates`, `Oadm.Client.Tests/SelfUpdateTests`).

## Not part of it

- Automatic or scheduled updates: always an Administrator's click.
- Pre-releases (`-rc.N`).
- A release signature: the SHA-256 of the same GitHub release is the check (decision 2026-11-02).
- Updating clients of other users or other computers from the server.
- Updating site nodes (see "Site nodes").
- Servers and clients without access to GitHub (see "Server without internet").
- Putting the database copy back (a manual step in the user docs).
- winget, an apt repository, the macOS App Store.

## Tests

The update record across a restart (done, failed, not back); the helper's start outside the service and its installer
call per platform (choice file on macOS, each exit code) with a fake installer and a `FakeTimeProvider`; the database
copy; refusal of a pre-release, a downgrade, the same version, a wrong checksum, a wrong file name or size, too little
disk space, a running task; `Package.wxs` sets the setup type in the execute sequence; the client's states (updating, updated, failed, not back) headless with
screenshots; the hint for older site nodes. A real update of each installer is a manual test on
Windows, Debian/Ubuntu and macOS, each with "Server and client" and "Client only", written down in `packaging/README.md`.
