# Architecture

This is the design document for the Save System package, written against the shipped code. It is the
page to read before reviewing the source or adding a feature. The user-facing pages explain *how to
use* the package; this one explains *why it is shaped the way it is* and *what guarantees the shape
buys*.

- Usage: [index](../index.md), [Slots and profiles](../Slots-And-Profiles.md),
  [Sync and recovery](../Sync-And-Recovery.md), [Events and listeners](../Events-And-Listeners.md),
  [Schema changes](../Schema-Changes.md)
- Decisions: [ADR-001 Core save model](Decisions/ADR-001-Core-Save-Model.md),
  [ADR-002 DI integrations as gated assemblies](Decisions/ADR-002-DI-Integrations-As-Gated-Assemblies.md),
  [ADR-003 Sync safety invariants](Decisions/ADR-003-Sync-Safety-Invariants.md),
  [ADR-004 Schema change guard](Decisions/ADR-004-Schema-Change-Guard.md)

---

## 1. Problem and goals

The package was designed after a post-mortem of a production save system in a shipped free-to-play
mobile game. That system had good instincts (it flushed on pause, it guarded empty uploads) but the
wrong shape: a single orchestrator class of well over a thousand lines, hardcoded lists of save
consumers, `PlayerPrefs` as the local store, no account scoping, no conflict metadata, unguarded
async entry points and catch-all error handling.

The failure classes that came out of that post-mortem are the requirements list for this package.
They are described generically below; each one is mapped to the mechanism that removes it in
[section 12](#12-failure-classes-and-the-mechanism-that-removes-each).

**Goals**

- **G1. One class, one registration.** A game declares a save domain by subclassing `SaveSlot<TData>`
  and passing the instance to the service. Every cross-cutting flow (load, flush, restore, probe,
  delete) picks it up automatically, with no second place to edit.
- **G2. No silent data loss.** Corrupt files are quarantined, never overwritten. An empty payload
  never lands over known content. Nothing crosses between accounts. A partial restore is never
  reported as a full one.
- **G3. Multi-device correctness without trusting clocks.** Decisions use write ids, not timestamps.
  Device clocks drift and device ids are cloned by OS backups.
- **G4. DI-agnostic core.** Constructor injection, plain C# events, no attributes, no service
  locator. DI support is a set of thin optional integration assemblies.
- **G5. Replaceable backend.** The cloud is a port with declared capabilities. Unity Cloud Save is
  the first provider; it is not privileged.
- **G6. Results, not exceptions.** Operations return result objects. They throw only for programmer
  errors at composition time, or `OperationCanceledException` when the caller's own token is
  cancelled.
- **G7. Mobile-safe.** Atomic writes, flush on pause and quit, bounded allocation per mutation.

**Non-goals in 0.1.0**

- Payload compression and encryption. Client-side encryption is obfuscation when the key ships in the
  binary, and the target payloads are kilobytes. Adding a format later is an envelope `fmt` bump.
- Server-side writes. Cloud Code and currency authority belong to the game's backend.
- Multi-key transactions across slots. No provider guarantees them, and pretending otherwise misleads.
- Snapshot-style providers that pack everything into one blob. The capability model does not exclude
  them; 0.1.0 does not build one.
- Automatic sync on a timer or on resume. The game calls `RestoreAsync` when it wants to.
- Save inspection UI and analytics. Editor tooling is limited to the `Tools/Save System` menu.
- WebGL. See [ADR-001](Decisions/ADR-001-Core-Save-Model.md#platform-scope).

---

## 2. Layered shape

```
                 game code
                     |
       +-------------+--------------------------------+
       |                                              |
   SaveSlot<TData>  (typed repository, per domain)   ISaveService / SaveService  (facade)
       |                                              |
       +----------------------+-----------------------+
                              |
        internal collaborators: ProfileSession, SlotStore, SyncStateStore,
        DeviceStateStore, EnvelopeCodec, SaveScheduler, OperationGate,
        CloudGateway, CloudUploader, ReconcileDecider, RestoreOperation,
        ProfileDeleteOperation, AccountDataDeleteOperation,
        OrderedListenerDispatcher<T>, LocalWriteTracker, MutationDetector
                              |
        ports (public, replaceable):
        ISaveStorage      ICloudSaveProvider      ISaveClock      ISaveLogger
             |                    |
        AtomicFileStorage   UnityCloudSaveProvider / NullCloudSaveProvider
```

Three rules hold this together.

**The facade is thin and delegates.** `SaveService` owns the lifetime cancellation source, the
events and the composition of collaborators. It contains no business decision. It is split across
partial files by concern (`SaveService.Api`, `.Host`, `.LocalWrites`, `.Cloud`) so that the public
operations, the slot host callbacks, the local write pipeline and the cloud entry points can be read
separately.

**Slots are the only typed surface.** `SaveSlot<TData>` implements the non-generic internal members
as `sealed override`, so a game subclass sees only the hooks (`CreateDefault`, `Normalize`,
`UpgradePayload`, `IsEmpty`, `ResolveConflict`) and the access methods (`Read`, `Mutate`,
`SaveNowAsync`). A subclass cannot disable an invariant. Slots never inject the service; they receive
an internal host reference when they are registered.

**The riskiest decision is a pure function.** `ReconcileDecider` takes local state, cloud state and
sync metadata and returns a decision. It has no side effects, no IO and no async. The whole
local-versus-cloud policy is therefore testable row by row, and `RestoreOperation` is reduced to
"fetch, ask, apply, persist, report".

### Ports

| Port | Implementations | Why it is a port |
|---|---|---|
| `ISaveStorage` | `AtomicFileStorage`, in-memory fake in tests | Byte-level, synchronous, relative paths. Keeps the crash-safety sequence in one place and makes a future non-file backend (for example WebGL) possible without touching the core. |
| `ICloudSaveProvider` | `UnityCloudSaveProvider`, `NullCloudSaveProvider`, fake in tests | Three methods plus declared capabilities. A game can point the package at PlayFab or its own server. |
| `ISaveClock` | `UnitySaveClock`, manual clock in tests | One seam for `UtcNow` and `Delay`, which makes every debounce and retry deterministic in tests. |
| `ISaveLogger` | `UnityDebugSaveLogger` | Keeps `UnityEngine.Debug` out of decision code and lets a game route save logs into its own sink. |

`NullCloudSaveProvider` is a Null Object, not a nullable field: there is no `if (provider != null)`
branch anywhere in the core.

---

## 3. Responsibility table

| Type | Owns | Does | Does not |
|---|---|---|---|
| `SaveSlot<TData>` | In-memory `TData`, `Revision`, `State`, cached empty reference | Typed access, dirty marking, hook dispatch | IO, network, timing |
| `SaveSlotRegistry` | The frozen slot set | Key and mode validation at construction, lookups by mode and scope | Loading |
| `SaveService` | Lifetime CTS, events, composition | Delegates operations, raises events | Business decisions |
| `SaveContext` | References to every collaborator | Hands one bag of dependencies to each operation | Logic |
| `ProfileSession` | Active `ProfileId`, epoch number and epoch CTS, readiness | Activation sequence, directory resolution, guest claim, last-active pointer | Slot file internals |
| `SlotStore` | Nothing persistent | Load, save, delete one slot's files; recovery order; quarantine; conflict and cloud-corrupt backups | Cloud, sync metadata |
| `SyncStateStore` | `profile.json` | Read and atomically write per-slot sync metadata | Slot payloads |
| `DeviceStateStore` | `device.json` | Device id and last-active profile pointer | Profile data |
| `EnvelopeCodec` / `SaveJson` / `PayloadChecksum` | Serializer settings | Snapshot, encode, decode, checksum, upgrade dispatch | Storage |
| `AtomicFileStorage` | Per-path locks | Crash-safe bytes on disk, Windows rename retry | JSON |
| `LocalWriteTracker` | Per-slot backoff and failure counts | Health aggregation and transition detection | Writing |
| `SaveScheduler` | Local-dirty and cloud-dirty sets, deadlines | Decides *when* to write and upload | *How* to write |
| `CloudUploader` | Nothing persistent | Upload guard, envelope build, pending write id, account check, sync-state update | Timing, retries |
| `CloudGateway` | Nothing persistent | Chunking, size and quota prechecks, retry of transient and rate-limited errors | Slot semantics |
| `ReconcileDecider` | Nothing (pure) | The local-versus-cloud decision table | Side effects |
| `RestoreOperation` | The in-flight restore task | Fetch, decide, re-check account, apply, persist, report | Upload timing |
| `ProfileDeleteOperation`, `AccountDataDeleteOperation` | Nothing persistent | The two delete flows and their preconditions | Activation |
| `OperationGate` | A semaphore | Serializes operations that must not overlap | Anything else |
| `OrderedListenerDispatcher<T>` | The listener list | Order sort, dispatch, failure capture, order logging | What listeners do |
| `ListenerReentrancyGuard`, `HookScope` | Two flags | Refuse service calls made from inside a listener or a hook | |
| `MutationDetector` | Baseline hashes | Development-only detection of changes made outside `Mutate` | Saving those changes |
| `SaveLifecycleDriver` | A `DontDestroyOnLoad` GameObject | Forwards pause, focus loss and quit | Save logic |

**Data ownership.** A slot payload belongs to its slot. Sync metadata belongs to `SyncStateStore`.
Device-level state belongs to `DeviceStateStore`. Authority over content: the server for
`CloudReadOnly`, the reconcile table for `CloudSync`, the device for `LocalOnly`.

---

## 4. On-disk layout and the envelope

### Layout

```
{rootDirectory}/                      default {persistentDataPath}/saves
  device/
    device.json                       device id, last active profile
    {key}.json  {key}.json.bak        Device-scope slots (always LocalOnly)
  profiles/
    guest/
    local-{name}/                     player-named local save, never uploaded
    acc-{sanitizedId}/                account profile; hashed when the id has unsafe characters
      profile.json                    owner account id + per-slot sync state
      {key}.json                      the envelope
      {key}.json.bak                  previous generation
      {key}.json.tmp                  exists mid-write, or after a crash
      {key}.corrupt-{yyyyMMddTHHmmssZ}.json        local quarantine, newest 3 kept
      {key}.cloud-corrupt-{yyyyMMddTHHmmssZ}.json  raw corrupt cloud bytes, newest 3 kept
      {key}.newer-{yyyyMMddTHHmmssZ}.json          document from a newer build, newest 3 kept
      {key}.conflict.json             last conflict, one generation, both sides plus the resolution
```

The three backup families are independent and each has its own cap. Quarantine files survive a slot
delete on purpose: they are support evidence.

### Envelope

The same envelope is written to the local file and to the cloud value of a `CloudSync` slot.

```json
{
  "fmt": 1,
  "schema": 3,
  "rev": 42,
  "writeId": "guid-or-null",
  "savedAtUtc": "2026-01-01T00:00:00Z",
  "deviceId": "...",
  "accountId": "...",
  "dataSha256": "hex",
  "data": { }
}
```

- **`writeId`** is the decisive field: a fresh GUID per upload. It answers "did this cloud value come
  from my last write?" without trusting any clock.
- **`rev`** is the local revision, incremented by every accepted `Mutate`.
- **`savedAtUtc`** and **`deviceId`** are diagnostics only. Device backups clone ids, and clocks drift.
- **`dataSha256`** is the lowercase hex SHA-256 of the exact UTF-8 bytes of `data` **as written**.
  The codec always writes `data` last and compact; the decoder locates the first top-level `,"data":`
  marker and hashes to the closing brace. Hashing the raw bytes avoids the trap that canonical
  re-serialization is not a fixpoint (`1.10` vs `1.1`, `1E-05` vs `0.00001`).
  - Locally, a mismatch is corruption. A missing checksum or an unlocatable marker (a hand-edited or
    pretty-printed file) means "not verifiable": the file is accepted and a development-build warning
    is logged.
  - In the cloud, the checksum is verified only when the provider declares
    `CloudCapabilities.PreservesValueText`, because a backend that stores JSON documents may reorder
    keys and would make every value look corrupt. Unity Cloud Save declares `false` until verified.
- `CloudReadOnly` cloud values are **raw `TData` JSON**, not enveloped: a server writer such as Cloud
  Code will not produce this envelope. Change detection for those slots uses the provider's version
  token.

Putting the metadata inside the value (rather than in sidecar keys) means one slot is always one
cloud key. That is what makes a write atomic with its own metadata, and it is why the package never
hits a "10 keys per request" style limit with a per-slot metadata key.

### `profile.json`

Per slot: `HadContent`, `LastSyncedRevision`, `LastSyncedWriteId`, `LastSyncedProviderVersion`,
`PendingWriteId`, the tombstone `{PendingDelete, DeletedWriteId, DeletedProviderVersion}`, and flags
such as `NeedsCloudRecovery` and `SuspendedUntilReconcile`. The file also records the owner account
id, so a directory-name hash collision is detected rather than silently shared.

A corrupt or newer-than-known `profile.json` is quarantined and replaced by a **safe fallback**:
`HadContent` is treated as `true` (which can only block an empty upload) and `LastSyncedWriteId` as
`null` (whose worst case is a conflict, not a deletion). See
[ADR-003](Decisions/ADR-003-Sync-Safety-Invariants.md).

---

## 5. Flows

### 5.1 Boot

1. The game (or a DI container) constructs
   `new SaveService(options, storage, provider, slots)`. The registry validates immediately and
   **throws** on a duplicate key, an invalid key, a reserved key (`device`, `profile`), or a `Device`
   slot that is not `LocalOnly`. These are composition-time programmer errors, not runtime results.
2. `InitializeAsync(ct)` loads `device.json`, then the `Device`-scope slots, then resolves the last
   active profile (falling back to Guest) and loads its `Profile`-scope slots.
3. Every slot ends up `Ready` or `Failed`. The game can run offline immediately; nothing waits on a
   network call.
4. Readiness is signalled **after the slots load and before the listeners run**, so a listener that
   awaits `WhenReadyAsync` returns immediately instead of deadlocking.
5. The restore listeners are dispatched with `Trigger = ProfileActivated` and per-slot outcome
   `LoadedLocal` or `Failed`, then `ProfileActivated` is raised with `Previous = null`.
6. Uploads stay blocked until the slot has been reconciled in this epoch.

Step 5 exists because without it, a game service that caches during its own initialization reads
`Unloaded` defaults and nothing ever refreshes it — a restore would, but a restore needs network.

**Startup never claims guest data.** A last-active pointer to an account (or a local profile) whose
directory does not exist falls back to Guest. Guest data moves into an account only through an
explicit `ActivateProfileAsync(Account)` that the game calls after sign-in.

### 5.2 Local load of one slot

1. Read `{key}.json`.
   - A `.tmp` that parses, verifies its checksum and has a **higher `rev`** than the primary (or a
     missing primary) is promoted. This covers a crash between the two renames, which .NET cannot
     close otherwise because there is no directory fsync.
   - If the primary is corrupt, try `.tmp` first, then `.bak`.
   - An IO exception is **not** corruption: the slot becomes `Failed(IoError)`, nothing is written and
     nothing is quarantined.
2. Decode the envelope.
   - `fmt` or `schema` newer than the build: `Failed(SchemaTooNew)`, the file is left untouched, and
     `UpdateRequired` is raised once per slot, source and epoch.
   - Older `schema`: the slot's `UpgradePayload(payload, fromSchemaVersion)` runs.
   - Parse, deserialize or checksum failure: corruption, see 5.6.
3. Materialize `TData`, then run `Normalize` exactly **once**, inside a `try`. An exception puts the
   slot in `Failed(NormalizeFailed)`; memory holds a clean default and persistence and upload are
   locked for that slot.
4. If the slot is not empty and `HadContent` is false, set `HadContent`.

### 5.3 Save

**Mutate.** Main thread only. `Mutate` is refused (returns `false`) when the slot is not `Ready`, is
`CloudReadOnly`, a profile switch is in progress, the service is disposed, or the call comes from
inside a slot hook. Otherwise it runs the action, increments `Revision`, marks the slot local-dirty
and, for `CloudSync`, cloud-dirty.

**Local deadline** (default 500 ms, coalesced):

1. On the main thread, snapshot the data to a JSON token. That snapshot is the consistency point.
2. Run the `IsEmpty` check when it is needed (to set `HadContent`, or to guard an upload).
3. On the thread pool, encode and write atomically.
4. Back on the main thread, clear the dirty flag only if the revision has not moved since the
   snapshot, and update `HadContent`.
5. On failure, classify the error, delete the temp file, keep the slot dirty, advance the per-slot
   backoff (1 s, 5 s, 15 s, 30 s, then 60 s) and recompute health. `LocalWriteHealthChanged` fires
   only on a **transition**, so a "disk full" banner does not flicker once per attempt.

**`SaveNowAsync`** bypasses the scheduler and the backoff: snapshot, write, fsync, both renames. Its
success contract is "the revision at call time, or newer, is durably on disk". It also *requests* an
immediate upload but does not await it — a purchase must not block on the network. Caller
cancellation is honoured only before the write starts.

**Cloud deadline** (default 2 s debounce, 10 s maximum wait):

1. Acquire the operation gate; the batch waits behind any restore, delete or activation.
2. Per cloud-dirty slot, the upload guard refuses when the slot is not `Ready`, has not been
   reconciled this epoch, is flagged schema-too-new, is suspended after a permanent failure, is
   currently failing its local write, or when `HadContent && IsEmpty(snapshot)`.
3. **Disk first.** The local file for that revision is written synchronously if still dirty. The
   cloud never receives a revision the disk does not have.
4. Build the envelope with a fresh `writeId`, encode, precheck size, and **persist `PendingWriteId`
   before sending**. If that metadata write fails, the upload is skipped.
5. **First-upload re-read.** When the slot has no known cloud write for this account
   (`LastSyncedWriteId` and `LastSyncedProviderVersion` are both null), the uploader re-reads the key
   before writing, because the backend has no create-only write. `NotFound` proceeds; a value found
   means another device created it, so the upload is skipped, `ReconciledThisEpoch` is cleared and a
   single-slot reconcile is requested. The residual race is the few milliseconds between the read and
   the write.
6. **Account check** immediately before sending: the epoch must be unchanged and
   `provider.SignedInAccountId` must equal the account stamped in the snapshot. Otherwise the result
   is `Superseded` and nothing is sent.
7. `CloudGateway.WriteAsync`, using `ExpectedVersion = LastSyncedProviderVersion` when the provider
   supports conditional writes.
8. Per-item handling:

| Result | Action |
|---|---|
| Success | Set `LastSynced{Revision, WriteId, ProviderVersion}`, clear `PendingWriteId`, clear cloud-dirty if the revision has not moved |
| `Conflict` | Schedule a single-slot reconcile once the gate is released |
| Permanent (`PayloadTooLarge`, `QuotaExceeded`, `Unauthorized`, `Permanent`) | Raise `UploadFailed`; suspend uploads for that slot **until its next successful reconcile**; it stays dirty on disk |
| Transient, retries exhausted | Stays dirty; rescheduled with capped backoff, or on the next mutation or foreground |

**`FlushAsync` is strict.** It flushes locally, then uploads or confirms every `Profile` `CloudSync`
slot, and reports one `CloudFlushSlotResult` per slot. `IsComplete` is true only when the local flush
completed *and* every one of those slots is `Uploaded` or `AlreadyInSync`, where `AlreadyInSync`
itself requires reconciled-this-epoch, not dirty and no pending write id. Slots never attempted
because the run aborted are `Failed(Aborted)`. Guest and Local profiles report
`Skipped(ProfileNotCloudBacked)` and therefore never complete. That strictness exists so a caller
that is about to delete local data cannot read a skip as a success.

### 5.4 Restore

1. **Single flight.** A restore already in flight is joined: the caller awaits the shared task with
   its own token attached. A call from inside listener dispatch is refused with `ReentrantCall`.
2. **Acquire the gate.** In-flight upload batches finish; new ones wait.
3. **Preconditions.** Guest and Local profiles return `ProfileNotCloudBacked`. An account profile with
   the provider signed out returns `NotSignedIn`; signed in as a different account returns
   `AccountMismatch`. No side effects, no dispatch.
4. **Select** `Profile`-scope slots whose mode is not `LocalOnly`. `Failed(IoError)` slots are
   re-loaded first (the disk may have recovered). `Failed(NormalizeFailed)` `CloudSync` slots *are*
   fetched, because the cloud copy can replace the unreadable local one. `Failed(SchemaTooNew)` slots
   are reported and not fetched.
5. **Fetch** through `CloudGateway`, chunked by `MaxKeysPerRead`, retrying only transient and
   rate-limited errors. `NotFound` is a result, never an error, so it is never retried.
6. **Decide** per slot with `ReconcileDecider` (section 6).
7. **Account re-check.** On the main thread, immediately before the synchronous apply and with no
   await in between, re-verify the epoch and `provider.SignedInAccountId`. On a mismatch nothing is
   applied, nothing is persisted, no slot is marked reconciled, every slot is `Failed(AccountChanged)`,
   the status is `AccountMismatch`, listeners are not dispatched, and `RestoreCompleted` is still
   raised. Epoch cancellation alone does not cover this: the provider's session can change underneath
   the package without any call to `ActivateProfileAsync`.
8. **Apply**, synchronously on the main thread, per slot, with a **revision compare-and-swap**: if the
   slot's revision moved since the decision, re-decide with the already-fetched cloud item (no
   network).
   - `TakeCloud`: upgrade, materialize, `Normalize`. An exception here re-decides the slot as a
     corrupt cloud payload (row 3), not as a hard failure. When the decision came from a conflict, the
     conflict file is written *before* the swap.
   - `KeepLocal` or `Merged`: apply the merged data if any, adopt the cloud's write id and provider
     version so the next upload supersedes exactly that cloud write, and mark the slot dirty.
   - `NoCloudData` with a queued upload: mark dirty only.
9. **Persist** with the **epoch token, not the caller token**, so memory and disk cannot diverge if
   the caller cancels: changed slot files, the row 5 cloud deletes, the cloud-corrupt backups, then
   `profile.json` once. Set `ReconciledThisEpoch` on every slot that reached it, and clear
   `SuspendedUntilReconcile` for those slots. A slot file write failure sets `LocalPersistFailed` and
   leaves the slot dirty for the scheduler; it does not reduce completeness.
10. **Release the gate**, hand queued uploads to the scheduler.
11. **Dispatch listeners outside the gate**, so a listener that calls `FlushAsync` cannot deadlock.
    Sorted by `Order`, then by registration order; each awaited with the caller-linked token; an
    exception is recorded in `RestoreReport.ListenerFailures` and the next listener still runs;
    cancellation stops the rest and marks the report canceled.
12. **Report and event.** `Full` when nothing failed or was skipped as too new and at least one slot
    was restored, up to date, kept or merged; `Partial` when some succeeded and some failed;
    `NothingToRestore` when every slot had no cloud data; `Failed` otherwise. `RequiresAppUpdate` is
    set by any row 2 hit. `RestoreCompleted` is raised.

### 5.5 Profile activation

1. Reentrancy check. `profile == ActiveProfile` returns `AlreadyActive` without taking the gate and
   without dispatching anything.
2. **Deactivation hooks.** `IsReady` goes false and `IProfileDeactivatingListener`s run in `Order`,
   **before the gate is taken and before mutations are refused**, sharing one `DeactivationTimeout`
   budget (5 s by default). Slots are still `Ready`, so a hook may `Mutate`, `SaveNowAsync` and
   `FlushAsync`; its writes land in the **outgoing** profile. On timeout the remaining hooks are
   skipped, recorded, and the switch proceeds — a misbehaving service must not block sign-in forever.
   Caller cancellation here aborts the activation with nothing switched.
3. Acquire the gate, re-check `AlreadyActive`, set the switching flag (from here `Mutate` is refused).
4. `FlushLocalNow()`, then cancel the epoch CTS and increment the epoch. Debounce loops, retries and
   pending uploads of the old profile end. An upload already past its account check has its result
   applied only if the epoch still matches; otherwise it is dropped, and the persisted
   `PendingWriteId` makes the next reconcile correct.
5. Unload `Profile`-scope slots: state `Unloaded`, memory holds defaults, `Mutate` refused.
6. Resolve the directory. For an account: use `acc-{id}` if it exists and its owner id matches (a hash
   collision gets a fresh suffixed directory); otherwise, if `guest` holds any content, **claim** it
   with a directory move, which is atomic on one volume and is itself the claim marker; otherwise
   create an empty directory. Guest and Local profiles use their own directories and are never
   claimed or claimed from.
7. Load the slots, persist the last-active pointer.
8. Set readiness, release the gate, dispatch restore listeners with `Trigger = ProfileActivated`, then
   raise `ProfileActivated`.

The game then calls `RestoreAsync`; rows 9 to 11 of the table handle a claimed guest directory meeting
an account that already has cloud data.

### 5.6 Corruption and recovery

- **Trigger:** a parse, deserialize or verifiable-checksum failure of bytes that were read
  successfully. IO exceptions are never corruption.
- **Steps:** rename (never copy) the primary to `{key}.corrupt-{UTC}.json` and prune to the newest 3;
  try `.tmp` (valid checksum) then `.bak`, and if one parses, load it and rewrite it as the primary
  (`CorruptRecoveredFromBackup`); otherwise memory gets the default, `HadContent` is **left as is**
  so an empty upload stays blocked, and the slot is flagged `NeedsCloudRecovery` so the next restore
  takes the cloud copy (`CorruptReset`).
- **Reported** through `SlotLoadIssueDetected` with the slot key, the kind, the `CorruptionCause`
  (`ParseFailed`, `ChecksumMismatch`, `MaterializeFailed`) and the backup file name.
- **Never:** writing a default over unreadable bytes before renaming them.
- **A corrupt cloud payload** is backed up to `{key}.cloud-corrupt-{UTC}.json` (newest 3, skipped when
  that provider version is already backed up). If local has content, local is kept and a
  **conditional** repair upload is scheduled against the corrupt value's version, so a concurrent
  repair from another device is never destroyed. If local has no content, the corrupt value is
  ignored.

**Liveness invariant.** Every refusal must clear within the next restore-and-upload cycle. The two
exceptions both need a new build and both surface a signal: `SchemaTooNew` (local or cloud) raises
`UpdateRequired`, and a `NormalizeFailed` `LocalOnly` slot raises a load issue. This is why a read
failure (transient) and a corrupt cloud payload (permanent until repaired) are separate rows: an
unparseable cloud value must not block that device's uploads forever.

### 5.7 Delete

**`DeleteSlotAsync(slot, target)`** runs under the gate: the slot is removed from both dirty sets
first, so no pending debounce and no in-flight batch can contain it. Local files
(`.json`, `.bak`, `.tmp`, `.conflict.json`) are deleted; quarantine files are kept. Memory becomes a
normalized default and the revision stays monotonic (it is never reset). For the cloud, a tombstone
`{PendingDelete, DeletedWriteId, DeletedProviderVersion}` is persisted **before** the delete is sent;
success clears it, failure keeps it so the next restore finishes the job. The cloud result is awaited
and returned; nothing is fire-and-forget. `CloudReadOnly` slots refuse the cloud target.

**`DeleteProfileAsync(profile, mode)`** refuses the active profile with `ProfileActive`. Under
`RequireSynced` it reads each `CloudSync` slot's envelope header and `profile.json` and refuses with
`UnsyncedChanges` (naming the slot keys) when a revision is ahead of `LastSyncedRevision`, a pending
write id exists, or presence is undetermined. `DiscardUnsynced` deletes regardless. The cloud is not
touched, and the last-active pointer is cleared if it pointed there.

**`DeleteAccountDataAsync(accountId)`** is the account-deletion flow. The account must not be active
and the provider must be signed in as that account. It persists a tombstone for every registered
`CloudSync` slot, deletes each cloud key unconditionally (`NotFound` counts as already absent), and
deletes the local directory **only if every cloud delete succeeded**. On a partial failure the local
data and tombstones are kept and the call can simply be retried. Deleting local data after a partial
cloud failure would let the leftover cloud data come back at the next sign-in.

### 5.8 Pause, focus loss and quit

`SaveLifecycleDriver` is a `DontDestroyOnLoad` MonoBehaviour, because Unity has no static pause event
(`Application.focusChanged` and `Application.quitting` exist but do not cover pause).

| Signal | Action |
|---|---|
| `OnApplicationPause(true)` | `FlushLocalNow()` synchronously, then a best-effort cloud flush on the epoch token |
| `OnApplicationFocus(false)` | `FlushLocalNow()` only, no network (system dialogs trigger this often) |
| Quit / `OnDestroy` | `FlushLocalNow()` |
| `OnApplicationPause(false)` | Resume suspended transient retries. Whether to restore on resume is the game's call |

`FlushLocalNow()` ignores the write backoff and returns a `LocalFlushResult`. A cloud upload
interrupted by iOS suspension fails as transient and stays dirty on disk, which is exactly why best
effort is acceptable there. `Dispose()` also runs a synchronous local flush before cancelling
anything.

### 5.9 Events

All events are raised on the main thread and never from inside `Dispose`. The publisher is always
`SaveService`, and each handler is invoked in its own try/catch.

`ProfileActivated` and `RestoreCompleted` are raised after the operation finished and its listeners ran, so a
handler may read the service freely. `LocalWriteHealthChanged`, `UploadFailed`, `SlotLoadIssueDetected` and
`UpdateRequired` are notifications from inside an operation: they can arrive mid-flush or mid-activation, while
the operation gate is held. Handle them by recording the fact or updating UI; do not read `ActiveProfile` or slot
data synchronously from one, and do not call back into the service expecting it to be idle.

| Event | When |
|---|---|
| `ProfileActivated` | After slots load for a profile and activation listeners finish, including the initial profile at `InitializeAsync` |
| `RestoreCompleted` | End of every `RestoreAsync`, including failures, and every single-slot conflict reconcile |
| `SlotLoadIssueDetected` | A corrupt, unreadable or too-new payload during a load |
| `UploadFailed` | Permanent upload failure or refusal only; never a transient retry |
| `LocalWriteHealthChanged` | Health transition: healthy to failing, failing to healthy, or a change of kind |
| `UpdateRequired` | A local or cloud payload written by a newer build; once per slot, source and epoch |

---

## 6. The reconcile decision table

`ReconcileDecider` is pure: `(local snapshot, cloud state, sync state, sync mode)` in, a decision out.
`CloudState` is one of `Missing`, `Found`, `ReadFailed`, `Corrupt`, `TooNew`. `RTE` below means "the
slot counts as reconciled this epoch", which is the precondition for uploading.

The account re-check (5.4 step 7) runs before any of this is applied.

### `CloudSync`

| # | Condition | Action / outcome | RTE |
|---|---|---|---|
| 1 | Read failed after retries | Keep local; `Failed(ReadFailed)` | No — the next restore re-reads |
| 2 | Cloud `fmt`/`schema` newer than this build | Keep local; `SkippedSchemaTooNew`; raise `UpdateRequired` | No — waits for an app update |
| 3 | Cloud value corrupt (parse or envelope failure; checksum mismatch when `PreservesValueText`; upgrade, materialize or `Normalize` throws) | Back up the raw bytes; raise `SlotLoadIssue(CloudPayloadCorrupt)`. Local has content: keep local, adopt the corrupt value's version, mark dirty, conditional repair upload (`RepairingCorruptCloud`). No local content: no-op (`CorruptCloudIgnored`) | Yes |
| 4 | Cloud `NotFound`, tombstone pending | Clear the tombstone; `NoCloudData` | Yes |
| 5 | Cloud found, tombstone pending | Delete only if `cloud.writeId` equals the tombstoned write id, with `expectedVersion = cloud.version`, executed in the persist phase. Success clears the tombstone (`NoCloudData`); failure is `Failed(DeletePending)`. A different write id means another device wrote after the delete: clear the tombstone and continue the table | Yes on success, no on failure |
| 6 | Cloud `NotFound`, local present | Keep local, queue upload; `NoCloudData` | Yes |
| 7 | Cloud `NotFound`, local absent | No-op; `NoCloudData` | Yes |
| 8 | `cloud.writeId` equals `LastSyncedWriteId` or `PendingWriteId` | Promote pending to synced. Not dirty: `UpToDate`. Dirty: queue upload, `LocalKept` | Yes |
| 9 | Local absent, `NeedsCloudRecovery`, local `Failed(NormalizeFailed)` (quarantine the local primary first), or `local.Revision < LastSyncedRevision` | `TakeCloud` | Yes |
| 10 | Local not dirty and `LastSyncedWriteId != null` | `TakeCloud` | Yes |
| 11 | Otherwise (local dirty, or never synced with content on both sides) | Conflict: `slot.ResolveConflict` | Yes |
| 12 | Post-check: the result is `TakeCloud`, the cloud payload is empty and local has content | Keep local, queue upload; `SkippedToProtectLocalData` | Yes |

**Evaluation order differs from the numbering.** The decider first branches on the cloud value's state:
a failed read is row 1, a payload newer than the build is row 2, an unusable payload is row 3. Only then
does it look at the local side: the tombstone rows (4 and 5), then the row 9 clauses, then row 8, then
rows 10 and 11, and finally the row 12 post-check. Straight 8-then-9 evaluation was
wrong, and the tests caught it: when the cloud held this device's own last synced write while the
local copy was absent, reset after corruption (`NeedsCloudRecovery`) or behind `LastSyncedRevision`,
row 8 returned `UpToDate`, so the lost data was never restored **and** the recovery flag was cleared.
Row 9 now wins, and a row 9 `TakeCloud` also clears `PendingWriteId` and adopts the cloud write id, so
a lost write response is still recognised.

Row 6 carries a second subtlety. `NotFound` plus local content means "upload", but the upload itself
cannot be conditional, because there is no version to condition on and the backend has no create-only
write. The uploader therefore re-reads the key just before that first write (5.3 step 5).

### `CloudReadOnly`

The mirror is server-authoritative and never uploads, so a conflict cannot occur.

| Condition | Outcome |
|---|---|
| Found, provider version differs from `LastSyncedProviderVersion` (or the byte hash when the provider has no version) | `TakeCloud` |
| Found, same version | `UpToDate` |
| `NotFound` | Reset the mirror to the default; `NoCloudData` |
| Raw payload will not parse or materialize | Back it up, keep the mirror; `CorruptCloudIgnored` |

### The default conflict policy

`DefaultConflictPolicy` is static, not an injectable service: there is exactly one default, and
per-slot variation is already the `ResolveConflict` override. The rule is: **the empty side loses;
otherwise the cloud wins.** Revisions and timestamps are deliberately ignored, because they are not
comparable across devices. Whatever the outcome, `{key}.conflict.json` records the resolution and
both sides before the swap, so the losing data is recoverable from the device.

---

## 7. Concurrency model

Four mechanisms, each aimed at a specific class of interleaving bug.

**The operation gate** is an async mutex (`SemaphoreSlim(1)`) that serializes every operation which
may not overlap another: restore, upload batches, flush, slot delete, profile delete, account data
delete, and profile activation. `Mutate`, `Read` and `SaveNowAsync` do not take it. Listener dispatch
happens **outside** it, so a listener calling back into the service cannot deadlock.

**Epochs** scope everything to one active profile. `ProfileSession` owns an epoch number and an epoch
cancellation source linked to the service lifetime. Switching profiles cancels and replaces both.
Debounce loops, retries and in-flight uploads for the old profile end there. Work that is already past
its account check applies its result only if the epoch still matches; otherwise it is dropped, and the
persisted `PendingWriteId` makes the next reconcile recognise a write whose response was lost.

**Single-flight restore.** A second `RestoreAsync` joins the in-flight task rather than issuing a
second fetch. The joiner waits with its own token attached, so cancelling the joiner does not cancel
the shared operation.

**Revision compare-and-swap at apply.** The reconcile decision is made from a snapshot. Between the
decision and the apply the game may have mutated the slot. The apply phase compares the slot's current
revision against the one the decision was made from, and re-decides with the already-fetched cloud
item if it moved. No network call, no lost mutation.

Two guards complete the picture:

- **`ListenerReentrancyGuard`** refuses `RestoreAsync`, `InitializeAsync` and `ActivateProfileAsync`
  called from inside a restore listener or a deactivation listener, with `ReentrantCall`.
  `FlushAsync`, `SaveNowAsync` and deletes stay allowed.
- **`HookScope`** refuses every `ISaveService` call and every `Mutate` made from inside a slot hook,
  with `CalledFromHook`. Hooks are synchronous and pure by contract; this flag catches the remaining
  misuse cheaply.

---

## 8. Async, cancellation and threading

### Token ownership

| Scope | Token | Owner | Ends |
|---|---|---|---|
| Service lifetime | `_lifetimeCts` | `SaveService` | `Dispose()` |
| Profile epoch | epoch CTS linked to lifetime | `ProfileSession` | Profile switch or `Dispose()` |
| A public operation | linked (caller, epoch) in a `using` | The operation method | End of the operation |
| Joined restore waiter | The shared task plus the caller's token | Caller | The caller's wait only; the shared task continues |
| Debounce loop | Epoch token | `SaveScheduler` | The dirty set empties or the epoch is cancelled |
| Upload batch, gate wait | Epoch token | `CloudUploader` / `OperationGate` | |
| Retry delays | Operation token through `ISaveClock.Delay` | `CloudGateway` | |
| Restore apply and persist | **Epoch token only** | `RestoreOperation` | |
| Restore listener calls | The triggering operation's linked token | `OrderedListenerDispatcher<T>` | End of dispatch |
| Deactivation hooks | Caller token linked with the `DeactivationTimeout` budget | `ProfileSession` | Budget or caller |
| `WhenReadyAsync` waiter | Caller token | Caller | |
| Pause cloud flush | Epoch token, fire-and-log | `SaveService` | |

### Rules

- `OperationCanceledException` surfaces from a public operation **only** when the caller's own token
  was cancelled. Epoch cancellation returns `Superseded`; everything else becomes a `SaveError`.
- The apply-and-persist phase of a restore runs on the epoch token, never the caller's, so a caller
  that cancels mid-restore cannot leave memory and disk disagreeing.
- Debounce is a **deadline loop**, not cancel-and-recreate per mutation: marking dirty updates a
  timestamp and starts the loop if it is not running; the loop awaits
  `min(lastMutation + debounce, firstDirty + maxWait) - now` and repeats. One loop per epoch, zero
  allocation per mutation.
- No `CancellationToken.Register` plus a completion source in the core, except `ReadinessSignal`,
  which uses UniTask's external-cancellation attachment (the registration is disposed by UniTask).
  Providers that need a callback bridge use a `using` registration.
- `async` methods without an `await` are banned; synchronous completion returns a completed `UniTask`.
- `Dispose()` flushes local writes synchronously, disposes readiness, cancels the lifetime token,
  disposes the cancellation sources and the semaphore, and detaches listeners. After that, events are
  no longer raised, add and remove are no-ops, and operations return `Disposed`.

### Threading

- **Main thread only:** every `ISaveService`, `SaveSlot` and `SaveSlot<TData>` member. There is no lock
  over the public surface; calling it from a worker thread is a bug, not a slow path. Development
  builds assert this.
- **Main thread by design:** `Mutate`, the JSON snapshot (the consistency point), `IsEmpty`,
  materialize plus `Normalize` plus apply, provider calls (an SDK may use `UnityWebRequest`), events
  and listener dispatch.
- **Thread pool** when `OffloadIo` is true (the default): token-to-bytes encoding, file IO, checksum
  hashing, parsing large cloud values. Continuations return to the main thread before anything visible
  changes.
- **Synchronous inline IO:** `FlushLocalNow`, the disk-first write before an upload, and `Dispose`.
  They run under the same per-path lock as background writes, so a background write and a pause flush
  of the same file cannot interleave.
- Slot hooks may run on the thread pool. They must be synchronous, pure, and must not call the service
  or Unity APIs.

---

## 9. Assemblies

| Assembly | Folder | Namespace | Required | Activation |
|---|---|---|---|---|
| `Ecanakli.SaveSystem` | `Runtime/` | `Ecanakli.SaveSystem` | Yes | Always |
| `Ecanakli.SaveSystem.UnityCloudSave` | `UnityCloudSave/` | `Ecanakli.SaveSystem.UnityCloudSave` | No | `versionDefines` on the UGS Cloud Save package |
| `Ecanakli.SaveSystem.DependencyInjection.Zenject` | `DependencyInjection/Zenject/` | `Ecanakli.SaveSystem.DependencyInjection` | No | `ECANAKLI_SAVESYSTEM_DI_ZENJECT`, or `versionDefines` on the UPM Extenject package |
| `Ecanakli.SaveSystem.Editor` | `Editor/` | `Ecanakli.SaveSystem.EditorTools` | Editor only | Always, in the Editor |
| `Ecanakli.SaveSystem.TestUtilities` | `Tests/Shared/` | test fakes | No | `UNITY_INCLUDE_TESTS` |
| `Ecanakli.SaveSystem.Tests`, `...PlayModeTests`, `...UnityCloudSave.Tests`, `...DependencyInjection.Zenject.Tests` | `Tests/**` | tests | No | `UNITY_INCLUDE_TESTS` plus the relevant define |

Dependency direction:

```
game  ->  Ecanakli.SaveSystem.DependencyInjection.Zenject  ->  Ecanakli.SaveSystem  ->  UniTask, Newtonsoft.Json
                                                           ->  Zenject / Extenject
game  ->  Ecanakli.SaveSystem.UnityCloudSave               ->  Ecanakli.SaveSystem, UGS Cloud Save
          Ecanakli.SaveSystem.Editor                       ->  Ecanakli.SaveSystem
```

Naming rules that the guard test `AssemblyNamingGuardTests` enforces:

- **Assembly names may name a framework; namespaces never do.** Every DI integration shares the
  namespace `Ecanakli.SaveSystem.DependencyInjection`, so a game's `using` lines do not change if it
  switches container.
- No namespace segment is `Zenject`, `VContainer` or `Editor`. A child namespace with one of those
  names shadows the real one inside `Ecanakli.SaveSystem.*` and produces confusing CS0234/CS0246
  errors. The Editor namespace is `EditorTools` for exactly this reason.
- No public type name appears in two package assemblies, so two integrations can compile side by side.

For the same class of reason, the facade is `SaveService` and not `SaveSystem`: a type with the same
name as its containing namespace produces CS0118 in any code inside that namespace.

Every asmdef that compiles into a player build sets `overrideReferences` and lists its precompiled
references explicitly, so an auto-referenced DLL in the consuming project cannot leak into the package.
The Zenject integration must additionally list `Zenject-usage.dll`, because `overrideReferences` blocks
the auto-referenced DLL that holds `IInitializable`. The Editor assembly references only the core.

The optional assemblies are gated the same way: `ECANAKLI_SAVESYSTEM_UGS` with a `versionDefines` entry
on the Unity Cloud Save package, and `ECANAKLI_SAVESYSTEM_DI_ZENJECT` with a `versionDefines` entry on
the UPM Extenject package plus the Editor menu toggle for `Assets/`-installed Zenject.

**Why the sync engine lives in the core rather than the optional cloud assembly.** Reconcile, the
uploader, the gateway and the restore flow are woven into `profile.json` sync state, the scheduler,
strict flush results and delete tombstones. Splitting them would force many internal types public. A
game without a cloud provider ships this code but it has no external dependency and does no work.

---

## 10. Editor tooling

`Ecanakli.SaveSystem.Editor` references the core (rather than duplicating the path rules, which would
drift) and adds `Tools/Save System`:

- **Zenject Integration** — a checkable toggle that writes the define symbol to every non-obsolete
  build target, and refuses with a dialog when no `Zenject` assembly exists.
- **Open Save Folder** and **Delete All Saves…** — both disabled in play mode; the delete goes through
  `SaveFolders.DeleteAllLocalData`, which removes only `device/` and `profiles/` and returns `InUse`
  when a live service owns that root.
- **Validate Save Schemas**, **Update Save Schema Snapshot**, **Schema Check On Reload** — the schema
  change guard, see [ADR-004](Decisions/ADR-004-Schema-Change-Guard.md).

---

## 11. Performance notes

| Risk | Mitigation |
|---|---|
| fsync hitch on low-end Android on every write | The 500 ms local write delay coalesces writes; IO runs on the thread pool; `SaveNowAsync` is for critical changes only |
| The JSON snapshot allocates roughly 2-3x the payload | One snapshot per write, reused for the empty check, the bytes and the upload. Payloads are kilobytes; split a slot above ~64 KB |
| `IsEmpty` deep comparison on every guarded upload | The normalized-default reference is cached per slot; comparison stops at the first difference; slots may override with a cheap check; it is evaluated at most once per write |
| SHA-256 on every encode and decode | Kilobyte payloads, on the thread pool with the rest of the IO |
| Closure allocation in `Read` / `Mutate` | `TArg` overloads plus static lambdas; the samples and docs use them |
| Backend rate limits | Global 2 s debounce with a 10 s ceiling, batching by `MaxKeysPerWrite`, `RetryAfter` honoured, capped backoff |
| Boot reads N small files | Sequential reads on the thread pool; N is small (5-20 slots). Measure above ~20 |
| Windows rename retries | Up to 3 attempts at 15, 30 and 60 ms, so at most ~105 ms per contended file, Windows only. Mobile never sleeps |
| IL2CPP managed stripping removes setters used only by the serializer | Documented `[Preserve]` / `link.xml` requirement for save data types and slot classes; DI bindings use explicit `FromMethod` construction so no package type is reflected |
| Development-build checks (mutation detector, access-before-ready warning) | Default to development builds and the Editor; they hash only clean slots, on flush |
| Disk use: `.bak`, quarantine, conflict and cloud-corrupt files, orphaned account directories | Each backup family is capped; `DeleteProfileAsync` and `DeleteAccountDataAsync` clean up orphans. Documented because OS backups include `persistentDataPath` |

---

## 12. Failure classes and the mechanism that removes each

Each row is a failure class observed in a previous production save system, and the mechanism in this
package that removes it.

| # | Failure class | Mechanism |
|---|---|---|
| 1 | A corrupt blob silently overwritten with defaults | A parse failure is corruption, not "no data": rename to quarantine, try `.tmp` then `.bak`, keep `HadContent`, flag `NeedsCloudRecovery`, raise `SlotLoadIssueDetected` (5.6) |
| 2 | Writing one account's data into another's | Account-scoped directories, an epoch CTS cancelled on switch, `accountId` in the envelope, a final account check before send, guests never upload, and a post-fetch account re-check before apply |
| 3 | Overlapping operations corrupting state | The operation gate, single-flight restore, "no upload before reconcile", and the revision compare-and-swap at apply (section 7) |
| 4 | Delete racing a pending debounce; fire-and-forget cloud delete | Delete runs under the gate and clears the dirty sets; the cloud delete is awaited and returned; a persisted tombstone finishes a failed delete at the next restore |
| 5 | A load exception swallowed while the consumer is marked initialized | A `Normalize` exception sets `Failed(NormalizeFailed)`: clean defaults in memory, persistence and upload locked, the issue reported, and the cloud copy allowed to replace it |
| 6 | The load hook running twice, duplicating data | One load path; `Normalize` runs exactly once per instance entering memory. A test asserts the call count |
| 7 | Dead code and `async` methods with no `await` | Banned; the facade delegates and stays small |
| 8 | Undisposed cancellation registrations | No register-plus-completion-source in the core; linked sources in `using`; tokenized delays |
| 9 | Retrying every error indiscriminately | The provider classifies the error; only transient and rate-limited are retried, honouring `RetryAfter`; a conflict triggers a reconcile; `NotFound` is a result |
| 10 | No schema version and no conflict metadata | The envelope (`fmt`, `schema`, `rev`, `writeId`, `dataSha256`, `accountId`) plus `profile.json`; the write-id table; schema-too-new refusal; conditional writes |
| 11 | Adding a save domain means editing four or five places | One class plus one registration; every flow iterates the registry; a guard test asserts that every registered slot takes part in init, activation, flush, restore, probe and all three delete flows |
| 12 | No flush on pause or quit | The lifecycle driver, the `Dispose` flush, and dirtiness persisted as `rev > LastSyncedRevision` so nothing is lost even if a flush is missed |
| 13 | A dependent cache refreshed before its source | `IRestoreListener.Order`, documented order ranges, the final order logged at verbose level, and a warning when two listeners share an `Order` |
| 14 | Load and save using different serializer settings | One `SaveJson` settings instance for the local, cloud, snapshot, empty-reference, conflict-file and probe paths, with `ObjectCreationHandling.Replace` so field initializers are replaced rather than merged |
| 15 | Ad-hoc flush hacks for purchases | `SaveNowAsync` with a durability contract, documented as the rule for purchases and rewards |
| 16 | An init race where a consumer wrote empty data before the service was ready | `IsReady` / `WhenReadyAsync`, signalled before listener dispatch, plus a development warning on access before ready |
| 17 | Live data references handed out, mutated outside the save path | `Read` and `Mutate` callbacks, plus the development-build mutation detector that hashes clean slots on flush and logs (without saving) a change made outside `Mutate` |
| 18 | An async migration hook that raced with everything else | Hooks are synchronous and pure by contract, enforced by the hook scope; migrations needing external content belong in game services after readiness |
| 19 | Restored data replaying "newly earned" effects | `RestoreTrigger` on the report, and the documented rule that listeners refresh state and never replay earned effects |
| 20 | One mega-slot behind string keys | Documented granularity guidance: one slot per domain, split on differing sync mode, scope, write frequency or size |
| 21 | Local-only data derived from cloud-synced progress left stale after a restore | `RestoreReport.GetResult(slot)` plus the documented listener recipe that deletes the derived slot after a `Restored` or `Merged` outcome |
| 22 | A reinstall deadlock from per-install writer ids | Write-id provenance rather than a writer identity, verified by two-device reinstall scenario tests |
| 23 | A destructive identity reset to switch accounts | Account-scoped directories; nothing is swept or rewritten on a switch |

---

## 13. What changed between the design and the code

Documented because the reasoning is still useful, and because a reviewer comparing old notes to the
source will otherwise trip over these.

| Area | Designed as | Shipped as | Why |
|---|---|---|---|
| Packaging | Two packages (core plus a Zenject package) | One package, optional assemblies gated by define symbols | UPM cannot resolve a git package's git dependency. See [ADR-002](Decisions/ADR-002-DI-Integrations-As-Gated-Assemblies.md) |
| Activation API | `ActivateAccountAsync` and `ActivateGuestAsync` | A single `ActivateProfileAsync(ProfileId)` | Named local profiles made three entry points into overloads that differed only by kind |
| Reconcile row order | Rows evaluated strictly 1 to 12 | Row 9's clauses evaluated before row 8 | A test showed that the cloud holding this device's own last write returned `UpToDate` while local data was absent, corruption-reset or behind the synced revision, so lost data was never restored and the recovery flag was cleared |
| First upload of a never-synced key | Unconditional write | Re-read the key first; skip and reconcile if a value exists | The backend has no create-only write, so another device's first save could be overwritten |
| Tombstone replay | Unconditional delete at the next restore | Conditional on the tombstoned write id, with `expectedVersion` | An unconditional replay could delete a newer write made by another device |
| Conflict backup | The losing local copy written on `TakeCloud` | Both sides plus the resolution written for every conflict outcome | "The loser is always backed up" was only true for one branch |
| Upload suspension after a permanent failure | For the rest of the epoch | Until the slot's next successful reconcile | An epoch can outlive the cause; the liveness invariant requires convergence |
| Listener dispatch | After a cloud restore | Also after `InitializeAsync`, after every activation, and after a single-slot conflict reconcile | Otherwise services that cache during their own init keep showing the previous profile's data until a restore, which needs network |
| `FlushLocalNow` return type | `FlushResult` | `LocalFlushResult` | The cloud half is meaningless for a synchronous local flush |
| Zenject signals | Five | Seven | The bridge mirrors every core fact event, including local write health and update-required |
| Zenject bindings | `AsSingle` | `AsCached` for multi-contract bindings | `AsSingle` collided with a game service bound as `BindInterfacesAndSelfTo<T>().AsSingle()` |
| Zenject asmdef | References `Zenject` | Also references `Zenject-usage.dll` explicitly | `overrideReferences` blocks the auto-referenced DLL that holds `IInitializable` |
| Editor assembly | References nothing | References the core | Duplicated path rules drift; the core has no DI dependency, so the "Zenject removed" scenario still compiles |
| Editor tooling scope | Non-goal | The `Tools/Save System` menu, including the schema guard | Two real needs: a developer reset button, and catching a breaking data-shape change before it ships. See [ADR-004](Decisions/ADR-004-Schema-Change-Guard.md) |
| Test fakes | Inside the EditMode test assembly | A shared `Ecanakli.SaveSystem.TestUtilities` assembly | PlayMode tests run on all platforms and cannot reference an Editor-only assembly |
| Internal class size | ~250 lines each | Several are well above that (notably the profile session and the slot store) | Work added late in 0.1.0 (local profiles, strict flush, account deletion, checksums, liveness, deactivation hooks, write backoff) landed in existing classes. Recorded as a known follow-up rather than a late refactor |

### Known limitations in 0.1.0

- WebGL is not supported; the service logs one startup warning and otherwise behaves normally.
- There is no metadata-only restore (fetching version tokens before values). It is a later, optional
  provider interface rather than a change to `ICloudSaveProvider`.
