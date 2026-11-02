# Self update

Status: **spec, not built**.

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.

An administrator updates the OADM server and their own client from inside OADM, instead of going to the download page.
**Install now** in the new version dialog starts it; the result is shown to the client after the update, also when it
failed. Site nodes are updated by hand on their computers (see "Site nodes"). Decisions of 2026-11-02, asked one by one.

## Rules that never break

1. **Administrators only.** Updating the server and a client needs the permission `server.update`, which
   only the Administrator role holds. Other users see the new version dialog as today (Open download page).
2. **The installers do the work.** The server and the client run their own MSI, .deb or .pkg silently
   ([packaging.md](packaging.md)): services, firewall rules, folder rights, plugin cleanup and the MSI's rollback stay the
   installers' job. Never a file swap of a running installation.
3. **No extra app.** The helper that runs the installer is the same program in update mode (`Oadm.Server --update`,
   `Oadm.Client --update`), copied to a temporary folder first so the installer can replace the
   original.
4. **Only stable releases of `Cacsjep/oadm`**, never a pre-release, never a downgrade, never the same version. The
   installer's SHA-256 must match the release's `SHA256SUMS.txt` (both over HTTPS from GitHub), and the file must be the
   one the platform expects by name and size limit (400 MB); anything else is deleted and refused.
5. **The server remembers.** Every update has a record in the data folder (`update/state.json`: from, to, started,
   step, result, reason, installer log path) written before the installer starts and completed by the server that starts
   afterwards, old or new. The client only shows what the server says.
6. **Never during a task.** **Install now** is disabled while a task runs (tooltip "3 tasks are running"); once the update
   started, the server starts no new task and no event rule action.
7. **No rollback by OADM.** A failed installer undoes itself where the installer can (MSI); OADM never reinstalls an older
   version. The result says what happened and what to do.
8. **Every update is in the audit log**: who, from, to, result.

## Server (and a client on the same computer)

1. The new version dialog shows **Install now** (filled accent; Open download page becomes secondary) to an
   Administrator whose server is older than the release. The dialog changes in place to the steps: Download the update,
   Check the download, Install the update.
2. The server downloads the installer of its platform into `update/`, checks it, writes the record, tells every connected client "update to 4.9.0 starting" and
   starts its helper detached, then stops accepting calls.
3. The helper (as SYSTEM / root) runs the installer silently with a log (`msiexec /i … /qn /norestart /l*v`,
   `apt-get install -y ./oadm-server_….deb`, `installer -pkg … -target /`), then waits up to 5 minutes for the service to
   answer `AuthService.Status` on this computer with the new version.
4. **The new version answers:** done. **The installer failed** (on Windows the MSI rolled back and the old server runs
   again): the helper writes the installer's error into the record. **The new version does not answer**: the helper writes
   that; the service restarts it on failure, and the server that starts reads the record.
5. Clients: while the server is away the client shows "Updating the server to 4.9.0" instead of "Connection lost". After
   the reconnect: "Server updated to 4.9.0", or "The update failed. The server runs 4.8.0." with the reason and the
   installer log's path on the server. Not back after 10 minutes: "The server did not come back" and what to check (the
   service OADM Server, the installer log). The record's result is also on the Logs page.
6. **Client on the server's computer.** The Windows MSI and the macOS .pkg hold the client too, so the update replaces it.
   Clients on that computer (the server tells them by its machine ID) start their own helper as the user and close; the
   helper waits for the server's result and starts the new client, which shows it. On Linux the client is a package of
   its own and updates like a client on another computer.

## Client on another computer

Once the server is newer than the client, an Administrator's client offers **Update client** (the new version dialog
for the server's version; other users: Open download page). The client downloads its installer (from the server when it
has it, else from GitHub), checks it, starts its helper and closes. The helper runs the installer with the system's
elevation prompt and visible progress (`msiexec /i … /passive`, `pkexec apt-get install -y ./oadm-client_….deb`, an
administrator prompt for `installer -pkg`), then starts the client that is installed now: the new one says "OADM updated
to 4.9.0"; the old one (installer failed, cancelled prompt) says "The update failed" with the reason. Feature choices stay (MSI
MigrateFeatureStates, .pkg choices).

## Site nodes

Not updated by OADM (decision 2026-11-02). A node runs with few rights on a computer in the customer's network (its own
virtual account or user, [packaging.md](packaging.md)); running an installer would need a helper with SYSTEM / root rights
there. After a server update, the result says "Update the site nodes on their computers" when a site has a node older than
the server, and the Sites page marks each older node with "Update on its computer" next to its version, with a link to the
download page.

## Server without internet

The server tries GitHub first. Without an answer, an Administrator's client downloads the installers the server needs
(its own platform) and uploads them
(`UpdateService.Upload`, client stream in chunks), like "By the clients" of [vulnerabilities.md](vulnerabilities.md). The
server checks them against `SHA256SUMS.txt`, which the client uploads too.

## gRPC

`UpdateService` ([grpc.md](grpc.md)), every method `server.update`:

- `Get`: the server's version and platform, the latest release, the running update and the last result; whether
  installers are on the server.
- `Start` (version): checks the rules above, then steps 2 to 3. `FAILED_PRECONDITION` while a task runs, for a
  pre-release, a downgrade or a missing installer.
- `Upload`: an installer or `SHA256SUMS.txt` from the client.
- `DownloadInstaller` (platform): a client installer the server has (server stream in chunks).

The update's steps reach every client through a server stream while the server runs; the final result through `Get` after
the reconnect.

## Not part of it

- Automatic or scheduled updates: always an Administrator's click.
- Pre-releases (`-rc.N`).
- A release signature: the SHA-256 of the same GitHub release is the check (decision 2026-11-02).
- Updating clients of other users or other computers from the server.
- Updating site nodes (see "Site nodes").
- winget, an apt repository, the macOS App Store.

## Tests

The update record across a restart (done, failed, not back); the helper's installer call per platform with a
fake installer and a `FakeTimeProvider`; refusal of a pre-release, a downgrade, a wrong checksum, a wrong file name or
size, a running task; the upload path; the client's states (updating, updated, failed, not back) headless with
screenshots; the hint for older site nodes. A real update of each installer is a manual test on
Windows, Debian/Ubuntu and macOS, written down in `packaging/README.md`.
