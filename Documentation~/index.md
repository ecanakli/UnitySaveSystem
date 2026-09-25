# Save System

A save system for Unity games that keeps player progress on disk without corrupting it, keeps each
account's progress in its own folder, and can optionally mirror that progress to a cloud backend.
You describe your data as plain C# classes, declare one *slot* per domain (player, settings,
inventory), and the package handles writing, crash safety, schema upgrades, profile switching and
cloud reconciliation. It has no dependency on any DI framework; Zenject and Unity Gaming Services
are optional assemblies that turn themselves on when those packages are present.

- [Slots and profiles](Slots-And-Profiles.md)
- [Sync and recovery](Sync-And-Recovery.md)
- [Events and listeners](Events-And-Listeners.md)
- [Schema changes](Schema-Changes.md)
- [Dependency injection](Dependency-Injection.md)
- [Unity Cloud Save](Unity-Cloud-Save.md)
- [Troubleshooting](Troubleshooting.md)

### Design and decisions

For reviewers and contributors: why the package is shaped the way it is.

- [Architecture](Design/Architecture.md) — the layered shape, the flows, the reconcile table, the
  concurrency and threading model, and what changed between the design and the code
- [ADR-001 Core save model](Design/Decisions/ADR-001-Core-Save-Model.md) — DI-agnostic core, sync
  modes, account-scoped storage, write-id reconcile, platform scope
- [ADR-002 DI integrations as gated assemblies](Design/Decisions/ADR-002-DI-Integrations-As-Gated-Assemblies.md)
  — one package, optional assemblies behind define symbols
- [ADR-003 Sync safety invariants](Design/Decisions/ADR-003-Sync-Safety-Invariants.md) — the five rules
  that constrain any change to the sync logic, and the tests that enforce them
- [ADR-004 Schema change guard](Design/Decisions/ADR-004-Schema-Change-Guard.md) — the Editor-time
  check for a breaking save data shape change

---

## How a game saves

This section walks through a whole session: bootstrap, reading, writing, signing in, signing out.
The code is plain C# with no DI container. The Zenject equivalent is in
[Dependency injection](Dependency-Injection.md).

### 1. Describe the data

A **slot** is one named unit of save data. It owns a data class, a file on disk and (if it syncs)
one cloud key. Subclass `SaveSlot<TData>` and give it a `Key`.

```csharp
using Ecanakli.SaveSystem;
using UnityEngine.Scripting;

public sealed class PlayerData
{
    public int Level { get; set; } = 1;
    public long Coins { get; set; }
    public List<string> UnlockedSkins { get; set; } = new List<string>();
}

[Preserve]
public sealed class PlayerSlot : SaveSlot<PlayerData>
{
    public override string Key => "player";
    public override SyncMode SyncMode => SyncMode.CloudSync;

    // Runs whenever an instance enters memory; repairs invariants such as null lists.
    protected override void Normalize(PlayerData data)
    {
        data.UnlockedSkins ??= new List<string>();
        if (data.Level < 1)
        {
            data.Level = 1;
        }
    }
}
```

`[Preserve]` keeps the type and its members from being stripped by IL2CPP managed code stripping.
See [Slots and profiles](Slots-And-Profiles.md#il2cpp-and-code-stripping).

### 2. Compose the service at bootstrap

```csharp
var options = new SaveServiceOptions();          // defaults are fine to start with
var storage = new AtomicFileStorage(options.RootDirectory);

_playerSlot = new PlayerSlot();
_settingsSlot = new SettingsSlot();

ISaveService save = new SaveService(
    options,
    storage,
    NullCloudSaveProvider.Instance,               // no cloud yet
    new SaveSlot[] { _playerSlot, _settingsSlot });

// Forwards Unity's pause, focus-loss and quit callbacks into the service.
IDisposable lifecycle = SaveLifecycleDriver.Attach(save);
```

`SaveLifecycleDriver.Attach` creates a hidden `DontDestroyOnLoad` GameObject. Dispose the returned
handle when you dispose the service; the driver flushes one last time on the way out.

### 3. Initialize

```csharp
InitializeResult result = await save.InitializeAsync(ct);
if (!result.IsSuccess)
{
    Debug.LogError($"Save init failed: {result.Error}");
}
```

`InitializeAsync` loads the device state, the `Device`-scope slots and the profile that was active
when the game last closed (Guest on a fresh install). When it returns, every slot is either `Ready`
or `Failed`, and the restore listeners have already run.

### 4. Wait before touching a slot

Anything that reads a slot before initialization sees defaults. If a system starts in parallel with
the bootstrap, await readiness first:

```csharp
if (await save.WhenReadyAsync(ct))
{
    int level = _playerSlot.Read(static data => data.Level);
}
```

`WhenReadyAsync` returns `false` after the service is disposed. In development builds, reading or
mutating a slot that is not loaded logs a warning once.

### 5. Read and write

Reads go through `Read`, writes through `Mutate`. Never keep the `TData` reference past the
callback: a change made outside `Mutate` is not marked dirty and is never saved.

```csharp
long coins = _playerSlot.Read(static data => data.Coins);

_playerSlot.Mutate(50, static (data, amount) => data.Coins += amount);
```

`Mutate` bumps the slot revision and schedules a local write (500 ms of coalescing by default) and,
for a `CloudSync` slot, an upload. It returns `false` and runs nothing when the slot is not ready,
is `CloudReadOnly`, a profile switch is in progress, the service is disposed, or it was called from
inside a slot hook.

### 6. Make a purchase durable

For anything the player paid for or earned, do not wait for the scheduler:

```csharp
_playerSlot.Mutate(static data => data.Coins += 1000);

SaveResult result = await _playerSlot.SaveNowAsync(ct);
if (!result.IsSuccess)
{
    // result.Error.Code is DiskFull, AccessDenied, IoError, SlotNotReady, ...
}
```

`SaveNowAsync` returns after the bytes are fsynced and promoted on disk. It asks for an upload but
does not await it. When the cloud copy also has to be there before you continue (for example before
a server call that reads the save), use `await save.FlushAsync(ct)` and check `IsComplete`.

### 7. Sign in

When the player signs into an account, activate that account's profile:

```csharp
ProfileActivationResult activation =
    await save.ActivateProfileAsync(ProfileId.Account(playerId), ct);

if (activation.ClaimedGuestData)
{
    // The guest folder was moved into this account: first sign-in on this device.
}
```

Activation flushes the outgoing profile, unloads the `Profile`-scope slots, opens the account's
directory, loads the slots again and dispatches the restore listeners. `Device`-scope slots are not
touched. There is one entry point for all three profile kinds; Guest is
`ActivateProfileAsync(ProfileId.Guest, ct)` and a named local profile is
`ActivateProfileAsync(ProfileId.Local("slot-1"), ct)`.

### 8. Pull the cloud copy

```csharp
RestoreReport report = await save.RestoreAsync(ct);
```

`RestoreAsync` reads every cloud-backed `Profile` slot of the active account, decides per slot
whether the local or the cloud copy wins, applies the decision and dispatches the restore
listeners. It is only valid for an `Account` profile; Guest and Local profiles return
`ProfileNotCloudBacked`. See [Sync and recovery](Sync-And-Recovery.md).

### 9. Sign out

```csharp
FlushResult flush = await save.FlushAsync(ct);
if (!flush.IsComplete)
{
    // Warn the player that some progress is not in the cloud yet.
}

await save.ActivateProfileAsync(ProfileId.Guest, ct);
// Only now call the backend sign-out.
```

`FlushAsync` is strict: it writes every dirty slot locally, then uploads or confirms every
`Profile` `CloudSync` slot. `IsComplete` is true only when the local flush completed *and* every
one of those slots is `Uploaded` or `AlreadyInSync`. Sign out of the backend after the profile
switch, not before: once the provider reports a different account, uploads for the old one are
refused.

### 10. Shut down

```csharp
lifecycle.Dispose();
save.Dispose();
```

`Dispose` runs a final synchronous local flush, cancels everything in flight and releases the save
folder. Pause, focus loss and quit are already handled by the lifecycle driver.

---

## Concepts

| Term | What it is |
|---|---|
| **Slot** | One named unit of save data: a `SaveSlot<TData>` subclass with a `Key`. One file on disk, and for cloud modes one cloud key. A game typically has 5-20. |
| **Profile** | Whose save this is. `ProfileId.Guest` (signed out), `ProfileId.Account(id)` (signed in, cloud-backed), `ProfileId.Local(name)` (a named local save, never uploaded). Each profile has its own directory. |
| **Sync mode** | A slot's relation to the cloud: `LocalOnly` (default, device only), `CloudSync` (uploaded and reconciled), `CloudReadOnly` (a server-authoritative mirror the game may read but never change). |
| **Scope** | Where a slot's files live: `Profile` (default, inside the active profile's directory) or `Device` (in the shared device directory, survives profile switches; must be `LocalOnly`). |
| **Revision** | A per-slot counter that every accepted `Mutate` increments. It is what "newer" means locally. |
| **Envelope** | The JSON wrapper around a payload: format, schema version, revision, write id, checksum and the `data` object. The same envelope is used for the local file and the cloud value. |
| **Write id** | A random id attached to an upload. It is how the package knows whether a cloud value came from this device's last write, which is what makes conflict detection reliable. |
| **Epoch** | The lifetime of one active profile. Switching profiles ends the epoch and cancels the operations belonging to it. |

---

## Public API map

One line per public type. Everything lives in the `Ecanakli.SaveSystem` namespace unless stated.

### Entry points

| Type | Purpose |
|---|---|
| `ISaveService` | The facade: initialize, activate a profile, restore, flush, delete, add listeners. Main thread only. |
| `SaveService` | The implementation. Constructed with options, a storage, a cloud provider and the slots. |
| `SaveServiceOptions` | Every tunable: root directory, write delay, cloud debounce and max wait, retry counts, converters, logger, clock, timeouts, development checks. |
| `SaveLifecycleDriver` | `Attach(service)` returns a handle; forwards pause, focus loss and quit to the service. |
| `SaveFolders` | `DefaultRootDirectory` and `DeleteAllLocalData(root)`, for tools and "reset the game" buttons. |

### Slots

| Type | Purpose |
|---|---|
| `SaveSlot` | Non-generic base: `Key`, `SyncMode`, `Scope`, `State`, `Revision`. Do not derive from it directly. |
| `SaveSlot<TData>` | What you subclass. `Read`, `Mutate`, `SaveNowAsync`, plus the hooks `CreateDefault`, `Normalize`, `UpgradePayload`, `IsEmpty`, `ResolveConflict` and the `SchemaVersion` property. |
| `SyncMode` | `LocalOnly`, `CloudSync`, `CloudReadOnly`. |
| `SlotScope` | `Profile`, `Device`. |
| `SlotState` | `Unloaded`, `Ready`, `Failed`. |

### Profiles

| Type | Purpose |
|---|---|
| `ProfileId` | Value type identifying a profile; `Guest`, `Account(id)`, `Local(name)`, `IsValidLocalName(name)`, `Kind`, `AccountId`, `LocalName`, `IsCloudBacked`. |
| `ProfileKind` | `Guest`, `Account`, `Local`. |
| `ProfileDeleteMode` | `RequireSynced` (refuse when unsynced changes exist) or `DiscardUnsynced`. |
| `LocalPresence` | `Absent`, `Present`, `Undetermined`; returned by `ProbeLocalPresence`. |

### Listeners

| Type | Purpose |
|---|---|
| `IRestoreListener` | `Order` plus `OnRestoredAsync(report, ct)`; runs after slots load and after a cloud restore. |
| `IProfileDeactivatingListener` | `Order` plus `OnProfileDeactivatingAsync(context, ct)`; runs before a profile switch, while the old profile is still writable. |
| `ProfileDeactivation` | `Outgoing` and `Incoming` profiles of a pending switch. |
| `ListenerFailure` | Which listener threw or timed out. |

### Results and events

| Type | Purpose |
|---|---|
| `SaveStatus` | `Success`, `Failed`, `Canceled`. |
| `SaveError`, `SaveErrorCode` | The detailed reason: `NotInitialized`, `Disposed`, `Superseded`, `ReentrantCall`, `CalledFromHook`, `NotSignedIn`, `AccountMismatch`, `ProfileActive`, `ProfileNotCloudBacked`, `UnsyncedChanges`, `SlotNotReady`, `SlotNotRegistered`, `SlotReadOnly`, `DiskFull`, `AccessDenied`, `IoError`, `InUse`, `CloudError`. |
| `InitializeResult` | Status, the profile that ended up active, and the load issues seen on the way. |
| `ProfileActivationResult` | Status, new and previous profile, `AlreadyActive`, `ClaimedGuestData`, load issues, deactivation failures and timeout flag. |
| `SaveResult` | Result of `SaveNowAsync`; carries `DurableRevision`. |
| `LocalFlushResult` | Result of `FlushLocalNow`: `IsComplete` plus the per-file failures. |
| `FlushResult` | Result of `FlushAsync`: the local part, one `CloudFlushSlotResult` per cloud slot, and `IsComplete`. |
| `CloudFlushSlotResult`, `CloudFlushStatus`, `CloudFlushReason` | Per-slot upload outcome and why it was skipped or failed. |
| `RestoreReport`, `RestoreTrigger`, `RestoreCompleteness` | What a restore or activation did; `GetResult(slot)` looks up one slot. |
| `SlotRestoreResult`, `SlotRestoreOutcome`, `SlotRestoreFailure` | Per-slot restore outcome (`Restored`, `UpToDate`, `LocalKept`, `Merged`, `NoCloudData`, ...). |
| `SlotLoadIssue`, `SlotLoadIssueKind`, `CorruptionCause` | A corrupt, unreadable or too-new payload, and the backup file written for it. |
| `UploadFailure` | A permanent upload failure or refusal; never a transient retry. |
| `LocalWriteHealth`, `LocalWriteFailure`, `LocalWriteErrorKind` | Disk health transitions: disk full, access denied, IO error. |
| `UpdateRequiredInfo`, `PayloadSource` | A payload written by a newer build than this one. |
| `DeleteResult`, `DeleteTarget` | Result of `DeleteSlotAsync` / `DeleteProfileAsync`. |
| `AccountDataDeleteResult`, `CloudDeleteSlotResult`, `CloudDeleteStatus` | Result of `DeleteAccountDataAsync`. |
| `LocalWipeStatus` | Result of `SaveFolders.DeleteAllLocalData`. |

### Cloud and storage extension points

| Type | Purpose |
|---|---|
| `ICloudSaveProvider` | The backend contract: `Capabilities`, `SignedInAccountId`, `ReadAsync`, `WriteAsync`, `DeleteAsync`. |
| `NullCloudSaveProvider` | The no-cloud implementation; pass `NullCloudSaveProvider.Instance` when the game is local-only. |
| `CloudCapabilities` | Value and key limits, conditional-write support, `PreservesValueText`. |
| `CloudReadRequest`, `CloudReadResult`, `CloudWriteRequest`, `CloudWriteResult`, `CloudDeleteResult`, `CloudAccess`, `CloudError`, `CloudErrorKind` | The provider's data types. |
| `ConflictContext<TData>`, `ConflictResolution<TData>`, `ConflictResolutionKind`, `DefaultConflictPolicy` | Inputs and outputs of the `ResolveConflict` hook. |
| `ISaveStorage`, `AtomicFileStorage`, `SaveStorageException` | The file layer: write to temp, fsync, rotate `.bak`, rename. Replaceable. |
| `ISaveLogger`, `UnityDebugSaveLogger` | Logging seam; swap it to route save logs into your own logger. |
| `ISaveClock`, `UnitySaveClock` | Time and delay seam; tests replace it to make scheduling deterministic. |

### Optional assemblies

| Type | Assembly |
|---|---|
| `UnityCloudSaveProvider`, `UnityCloudSaveOptions` (`Ecanakli.SaveSystem.UnityCloudSave`) | `Ecanakli.SaveSystem.UnityCloudSave`, active when the UGS Cloud Save package is installed. See [Unity Cloud Save](Unity-Cloud-Save.md). |
| `SaveServiceInstaller`, `SaveSignalsInstaller`, `SaveBindingExtensions`, the seven signals (`Ecanakli.SaveSystem.DependencyInjection`) | `Ecanakli.SaveSystem.DependencyInjection.Zenject`, active when Zenject/Extenject is present and the define is set. See [Dependency injection](Dependency-Injection.md). |

---

## Internals map

You do not need these to use the package. They are listed so the source reads in a sensible order.
All of them are `internal`.

| Type | Role |
|---|---|
| `SaveService.Api / .Host / .LocalWrites / .Cloud` | The facade split by concern: public operations, the slot host callbacks, the local write pipeline, the cloud entry points. |
| `SaveContext` | The bag of collaborators every operation gets, so no operation news up its own dependencies. |
| `ProfileSession` | Owns the active profile, the epoch, directory resolution, the guest claim and the activation sequence. |
| `SaveSlotRegistry` | Validates slot keys and modes at construction and freezes the set. |
| `SlotStore` | Loads and saves one slot file: recovery order, checksum verification, quarantine and backup files. |
| `SyncStateStore` | Reads and writes `profile.json` (last synced write id, revision, provider version, tombstones). |
| `DeviceStateStore` | Reads and writes `device.json` (device id, last active profile). |
| `SaveLayout` | Every path rule in one place: directory names, `.tmp`, `.bak`, `.conflict.json`, quarantine names. |
| `EnvelopeCodec` | Encodes and decodes the envelope, runs the upgrade chain, locates the exact `data` bytes for the checksum. |
| `PayloadChecksum` | SHA-256 hex over a byte span. |
| `SaveJson` | The one shared serializer. `TypeNameHandling.None`, `ObjectCreationHandling.Replace`, `NullValueHandling.Ignore`, `MissingMemberHandling.Ignore`. |
| `SaveScheduler` | The debounce timers: local write delay, cloud debounce and max wait, upload retry backoff. |
| `OperationGate` | Serializes the operations that may not overlap (activate, restore, flush, delete). |
| `LocalWriteTracker` | Per-slot write backoff, consecutive failure counts, health aggregation and transition detection. |
| `CloudGateway` | Wraps the provider with retry policy and error classification. |
| `RestoreOperation` (`.Decide`, `.Apply`) | The restore flow: fetch, decide per slot, re-check the account, apply, persist, report. |
| `ReconcileDecider` | The pure decision table: given local and cloud state, what happens to this slot. |
| `CloudUploader` | Builds and sends uploads, tracks pending write ids and upload suspension. |
| `ProfileDeleteOperation`, `AccountDataDeleteOperation` | The two delete flows and their preconditions. |
| `OrderedListenerDispatcher<T>` | Sorts by `Order`, dispatches, logs the final order, records failures and timeouts. |
| `ListenerReentrancyGuard` | Refuses service calls made from inside a listener. |
| `HookScope` | Refuses service calls made from inside a slot hook. |
| `MutationDetector` | Development-only: hashes clean slots on flush to catch changes made outside `Mutate`. |
| `ReadinessSignal` | The resettable completion source behind `IsReady` and `WhenReadyAsync`. |
| `StorageErrorClassifier`, `FileOps` | Exception to `LocalWriteErrorKind`, and the file primitives behind the Windows rename retry. |

---

## Platforms

| Platform | Status in 0.1.0 |
|---|---|
| Android | Supported. Verified in the Editor and by the automated tests; no device-specific code paths. |
| iOS | Supported. See the iCloud backup note in [Sync and recovery](Sync-And-Recovery.md#os-backups-and-reinstall). |
| Windows (player and Editor) | Supported by design; not verified on hardware for 0.1.0. The file replace step retries a few times on Windows only, because antivirus and indexer processes hold file handles briefly. |
| macOS (player and Editor) | Supported. |
| Linux (player and Editor) | Supported by design; not verified on hardware for 0.1.0. |
| WebGL | **Not supported in 0.1.0.** The service logs one warning on startup and otherwise behaves normally, which means saves may be lost. |
| Consoles, tvOS, visionOS | Untested. Nothing is claimed either way. |

WebGL is excluded because the browser has no real file system behind `Application.persistentDataPath`
(it is an IndexedDB-backed emulation that only flushes on request), and because the package moves
serialization and file IO onto the thread pool, which WebGL does not have. The `ISaveStorage`
interface is the seam a WebGL backend would plug into later; nothing else in the core assumes files.

The assembly definitions do **not** exclude WebGL. Excluding it would turn a platform switch into a
wall of `CS0246` compile errors in game code that references the package.

---

## Threading

- **Every member of `ISaveService`, `SaveSlot` and `SaveSlot<TData>` is main-thread only.** There is
  no lock protecting the public surface; calling it from a worker thread is a bug, not a slow path.
- Serialization and file IO are moved to the thread pool internally (`SaveServiceOptions.OffloadIo`,
  default `true`). Results come back on the main thread before anything visible changes.
- `FlushLocalNow()` is the one synchronous write path. It blocks the calling frame, on purpose: it
  exists for pause and quit, where there is no next frame to await into.
- Slot hooks (`CreateDefault`, `Normalize`, `UpgradePayload`, `IsEmpty`, `ResolveConflict`) may run
  on the thread pool. They must be synchronous, pure and must not call the service or Unity APIs.
- `SaveFolders.DefaultRootDirectory` reads `Application.persistentDataPath`, so its **first** read
  must happen on the main thread. After that it is cached.

## Limits

- **Payloads are kilobytes, not megabytes.** Every write re-serializes the whole slot, and a cloud
  round trip carries the whole value. Split a slot once it approaches ~64 KB. See
  [slot granularity](Slots-And-Profiles.md#slot-granularity).
- **One slot per domain, one file per slot.** There are no multi-slot transactions: two slots
  written in the same frame can end up in different states after a crash. Fields that must agree
  with each other belong in the same slot.
- **Slot keys** must match `^[A-Za-z0-9_-]{1,64}$`, are unique case-insensitively, and may not be
  `device` or `profile` (those file names are taken).
- **Local profile names** must match `^[a-z0-9_-]{1,32}$`. Lower case only, because Windows and
  macOS file systems are case-insensitive.
- **Cloud limits** are declared by the provider through `CloudCapabilities` and enforced before a
  write is attempted. For Unity Cloud Save the numbers are in
  [Unity Cloud Save](Unity-Cloud-Save.md#capabilities).
- **No forced overwrite.** There is deliberately no API that pushes local data over a cloud value
  the package considers unsafe to replace. Refusals resolve through the normal restore-and-upload
  cycle.
