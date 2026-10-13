# Server backup and restore (spec draft)

Status: decided and built 2026-10-13 (CLAUDE.md "Backup and restore" describes the result). Settings page, Administrator
only (never grantable, like the other server settings).

## Decisions

| # | Topic | Decision |
|---|---|---|
| Q1 | Secrets | The administrator enters a **backup password**; the whole file is encrypted with it (AES-256-GCM, key from the password with PBKDF2-SHA256, 600,000 iterations). Built: `master.key` travels only inside the encrypted body and becomes the target's key on restore (the target's TLS key is encrypted again with it), so every secret, also of third-party plugins, stays readable and a backup works on a new machine. Restore asks for the password; a wrong one restores nothing. |
| Q2 | Versions | The backup holds the database as it was, plus its OADM version and last migration. Restore runs the same EF Core migrations an installed update runs, so an old backup (1.2 on 15.2) restores as long as every migration is kept: **migrations are never deleted** (new rule for CLAUDE.md). Plugin data carries its own format version; a plugin that cannot read an old format skips it. A backup from a newer OADM than the server is refused ("Update OADM to 15.3 or later first."). |
| Q3 | Report | After a restore one line per area: "Devices: 4,812 restored · Users: 12 restored · DHCP server: skipped, the backup's format is too old, set it up again." Also in the server log and the audit log. |
| Q4 | Schedule | **Back up now** (the file downloads to the client) and an **automatic backup** on the server: daily at a chosen time into a server folder (default `<datafolder>/backups`), the last 7 kept (decided 14, changed to 7 on 2026-10-13); its password is stored encrypted with the master key. One automatic backup right before every database upgrade (replaces "database backup before migrations" in "Not built yet"). |
| Q5 | Restore | **Replace all**: the server becomes exactly the backup (devices with credentials, users, roles, tags, event rules, settings, credential list, task history, plugin data). Right before, OADM makes a safety backup of the current state (same password as the restore), so a wrong restore can be undone. Running tasks are cancelled first (confirmed), every user is logged out and logs in with the backup's users. |

## Not in the backup

- The server TLS certificate: the target server keeps its own, so clients that already trust it keep working (a new
  machine has a new certificate, clients confirm it once). `master.key` is in the backup, but only inside the encrypted
  body (Q1).
- Login sessions: removed from the copy; everyone logs in again after a restore.
- Uploads (temporary files), logs, the runtime extraction folder.

## Decided while building

- PBKDF2-SHA256 (600,000 iterations), passwords 8..256 characters.
- Plugins: the host copies `plugin-data/<id>` and the plugin settings (in the database); each plugin reads its own older
  formats on start. Plugins of the backup that are not installed are reported, their settings kept.
- The restore swaps the files at the next start; the server restarts itself (service: exit code 75, the service manager
  starts it; console: a new host in the process).
- Task history is part of the backup.
- The backup before a database upgrade (Q4) is a plain copy of the database (`backups/before-upgrade`, newest five): the
  key stays in place and no password is needed at start.

## Open points

- A fixture backup per released schema version, restored by a test on the current one (today: a database migrated to an
  older migration is backed up and restored).
