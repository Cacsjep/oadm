# Data model

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.

SQLite runs with the WAL journal (`DatabaseInitializer`) and `synchronous=NORMAL` on every connection
(`SqlitePragmaInterceptor`): a commit does not wait for an fsync, the database stays consistent after a
crash; with FULL a poll round of 5,000 devices took 22 s instead of 6 s.

- `Device`: Id (Guid), Serial (= MAC, unique, upper hex, no separators), Address,
  UseHostName (bool: Address is a name, not an IP), HostName (the verified DNS name or the typed name), Model (ProdNbr), FirmwareVersion, DhcpEnabled, HttpsEnabled,
  Dot1xEnabled, UpnpFriendlyName, ServerName, Status (enum below), Scheme (http/https),
  ProductType (raw basicdeviceinfo ProdType, e.g. "Dome Camera"), Soc (raw basicdeviceinfo Soc, e.g. "Axis Artpec-8",
  max 64, migration `DeviceSoc`; proto `soc = 29`), Category (enum below),
  HasVideo (derived from Category, not stored),
  CertFingerprintSha256 (nullable), CertNotAfterUtc (nullable), CertTrust (enum below),
  CertSubject, CertIssuer, CertNameMatches (nullable bool, address in SAN; stored, not shown
  yet), LastSeenUtc, WarrantyExpiry (nullable, later), ReplacementModel (nullable, later), Tags (JSON array of tag
  names, distinct case-insensitive and sorted, with the spelling of the definition; written only by `DeviceTagStore`;
  proto `repeated string tags = 19`; SDK `IDeviceInfo.Tags`),
  Apis (JSON column `[{id, version, name, status}]` from `apidiscovery.cgi getApiList`, written on
  every full refresh incl. the first one after add; proto `repeated DeviceApi apis = 28`),
  CredentialUserName (not stored: user name of the DeviceCredential, filled by repository reads
  for plugins; never the password).
- `DeviceCredential`: DeviceId, UserName, EncryptedPassword (AES-GCM, see Security).
- `TagDefinition` (table TagDefinitions, migration `DeviceTags`): Id (Guid), Name (1..64, trimmed, no ";" or control
  characters), NormalizedName (upper invariant, unique: names are unique case-insensitive), Color (Blue, Green, Teal,
  Amber, Orange, Red, Pink, Violet; stored as string), CreatedUtc. At most 500 definitions; a device has at most 20 tags.
  `Oadm.Core.Devices.DeviceTagStore` owns both (serialized writes, one transaction per call, rewrites read only the Tags
  column and load just the changed rows), `TagNames` the rules.
- Plugin licenses: no table; settings `Server.Id` (the server ID, a random GUID made at the first start, in backups),
  `Plugins.Licenses` (package id -> license text) and `Plugins.TrialStarted` (package id -> first day), see "Plugin licenses".
- `WaitingDevice` (table WaitingDevices, migration `WaitingDevices`): the import's waiting list, see "Waiting list".
- `CredentialListEntry` (table CredentialListEntries, migration `CredentialList`): Id (Guid),
  UserName (max 64), EncryptedPassword (AES-GCM with the entry id as associated data), CreatedUtc.
  The technician's credential list (Credentials page), at most 20 entries, tried in the order added.
- `Task`: Id, BatchId (shared by the tasks of one Run; proto `batch_id`), PluginId, Name, Status
  (Queued, Running, Done, Failed, Cancelled, DoneWithWarnings), Owner (client machine/user name),
  CreatedUtc, StartedUtc, FinishedUtc, Progress (0-100), PayloadJson (column kept but always NULL:
  payloads may carry secrets and are never persisted; the migration clears old values),
  ScheduledUtc (nullable, unused). Indexes: CreatedUtc (paging, newest first), BatchId, Status
  (migration `TaskStatusIndex`: active tasks for startup recovery and every Watch snapshot).
- `TaskDeviceResult`: TaskId, DeviceId, Status, Message (last progress message, warning or
  error), Progress. Exactly one row per task (proto `TaskInfo.device_id` = its DeviceId).
- `TaskStep` (table TaskSteps, migration `TaskSteps`): key (TaskId, Index), Name (max 200), State
  (Pending, Running, Done, Warning, Skipped, Failed; stored as string), Detail (max 1000: result, progress
  detail, skip reason or error), Progress (0-100), StartedUtc, FinishedUtc (nullable). Cascade-deleted with
  the task; rows are rewritten by index with every task snapshot (a dynamic step may shift later ones).
  At most 200 steps per task (`TaskStepList.MaxSteps`). The current step (`TaskRecord.CurrentStepIndex`,
  not stored) is the running one, else the last that started (the failed one), else the last ended.
- `TaskLogEntry` (table TaskLogEntries): Id (autoincrement), TaskId (cascade delete), DeviceId
  (nullable = task level), TimeUtc, Level (Info, Warning, Error), Message (max 2000 chars).
  At most 1000 entries per task; the 1000th says that later entries were dropped.
- `ComplianceCheckEntity` / `ComplianceResultEntity` (tables ComplianceChecks, ComplianceResults, migration
  `ComplianceChecks`): the compliance checks and the latest result per check and device, see "Compliance".
- `Setting`: Key, ValueJson.
- `PluginRecord` (table PluginRecords, migration `PluginRecords`): key (PluginId max 128, Collection max 64, Key max 255),
  ValueJson (valid JSON). A plugin's records (`IPluginRecords`, `DbPluginRecords`): one row per record, a plugin sees
  only its own; batch writes are one transaction (existing rows read 500 keys at a time). 60,000 rows written in about
  4 s, one changed in about 10 ms (`PluginRecordsTests`, Perf). First user: the EST server's certificates.

Device status enum: `Ok`, `Unreachable`, `CredentialsRequired` (401/403), `PasswordNotSet`
(factory default), `CertificateChanged`, `Unknown`, and `SiteOffline` (computed, never stored:
the site node of `Device.SiteId` is not online in `ISiteStates`; `DeviceRepository` reads and `DeviceChangeFeed` show it,
writes keep the stored status; `Oadm.Core.Sites.SiteDeviceStatus`, `NoSiteStates` without sites; `SiteStatusPublisher`
publishes a site's devices in one batch when it goes offline or online (a device with a newer change published meanwhile keeps it: `IDeviceChangeFeed.PublishUnlessNewer`) and queues a full refresh after more than 5
minutes offline). Every status switch refuses it before any request with "Site offline: <site>"
(`DeviceMessages.Refusal(IDeviceInfo)`, `TaskSupportReasons.ForStatus(IDeviceInfo)`); a `SiteException` never changes a
status, fails a poll, counts towards the outage share or fails a compliance check (Not checked). Event rules:
`SiteEventSource` (group Sites): "Site offline: <site>" per site ({site}, {address}) and "Any site offline" ({count},
{site}); device status events keep their state while the site is offline. `{site}` in `ActionPlaceholders.Fixed`; an empty
fixed placeholder takes the event's value of the same name. Client: "Site offline", amber icon; fake mode sites Building B
(online) and Warehouse (offline). Tests: `tests/Oadm.Core.Tests/Sites/*` (+ Perf `SiteOfflineScaleTests`),
`tests/Oadm.Server.Tests/SiteOfflineServerTests`, `tests/Oadm.Client.Tests/SiteOfflineTests`.

Device category enum (`Oadm.Sdk.Devices.DeviceCategory`, also used by Core): `Camera`,
`Encoder`, `Speaker`, `Audio`, `Intercom`, `Radar`, `IoModule`, `DoorController`, `Other`,
`Unknown`. Mapped from ProdType in exactly one place, `Oadm.Core.Devices.DeviceCategoryMapper`:
case-insensitive keyword rules, first match wins (intercom/door station, door controller,
encoder/video server, camera, radar, speaker/horn, audio/microphone/amplifier, I/O / relay
module). Empty ProdType is `Unknown`; an unmatched one is `Other` and logged once per string.
Verified: AXIS P3265-V on AXIS OS 12.11 reports ProdType `Dome Camera` (also anonymously via
`getAllUnrestrictedProperties`). `HasVideo` is true for Camera, Encoder and Intercom.
Filled by the anonymous probe on the add page (discovered list shows the icon), on add and on
every basicdeviceinfo poll.

Certificate trust enum: `Trusted` (chain builds to a root in the server OS trust store, or to a
trust anchor of a core plugin such as the PKI CA: `ITrustAnchors`, `Oadm.Core.Vapix.TrustAnchorRegistry`),
`SelfSigned` (subject equals issuer and the only chain error is the untrusted root),
`Untrusted` (any other chain failure, e.g. private CA not in the store), `Expired` (past
NotAfter), `Unknown` (not checked yet or HTTP only). Only the chain is evaluated; the host name
is ignored because devices are usually reached by IP address.
