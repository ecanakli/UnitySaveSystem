# Slots and profiles

[Back to index](index.md)

A **slot** is one named unit of save data. A **profile** is whose data it is. Together they decide
which file on disk a piece of data lives in. This page covers declaring slots, the rules for
reading and writing them, and everything about profiles: guest, account and named local saves.

---

## Declaring a slot

Subclass `SaveSlot<TData>`, give it a `Key`, and optionally override `SyncMode` and `Scope`.

```csharp
using System.Collections.Generic;
using Ecanakli.SaveSystem;
using UnityEngine.Scripting;

public sealed class InventoryData
{
    public List<string> Items { get; set; } = new List<string>();
    public int Capacity { get; set; } = 20;
}

[Preserve]
public sealed class InventorySlot : SaveSlot<InventoryData>
{
    public override string Key => "inventory";
    public override SyncMode SyncMode => SyncMode.CloudSync;
    public override SlotScope Scope => SlotScope.Profile;   // the default
}
```

`TData` must be a reference type with a public parameterless constructor (`where TData : class, new()`).

### Key rules

- Must match `^[A-Za-z0-9_-]{1,64}$`.
- Must be unique across the game, compared **case-insensitively** (`Player` and `player` collide).
- May not be `device` or `profile`: those file names are taken by the package's own state files.
- May not be a Windows device name (`con`, `prn`, `aux`, `nul`, `com1`-`com9`, `lpt1`-`lpt9`, in any case).
  Windows resolves those names to a device whatever the extension is, so the file would read back empty.
- `Key`, `SyncMode` and `Scope` must return **constant** values. They are read at composition time
  and used to build file paths; returning something that changes at runtime corrupts the layout.
- The key is also the file name (`{key}.json`) and, for cloud modes, the cloud key. Changing it
  orphans the old file and the old cloud value. Treat it as permanent.

All of these are validated when the `SaveService` is constructed, and a violation throws there
rather than at the first save.

### Sync modes

| Mode | Meaning |
|---|---|
| `LocalOnly` (default) | Device only. Never read from or written to the cloud. Device-specific settings, tutorial flags, cached state. |
| `CloudSync` | Written locally, uploaded as an envelope, reconciled by write id on restore. Player progress. |
| `CloudReadOnly` | A server-authoritative mirror. The client reads it and caches it locally, but `Mutate` is refused and nothing is ever uploaded. Live-ops configuration, server-granted entitlements. |

### Scopes

| Scope | Directory | Notes |
|---|---|---|
| `Profile` (default) | The active profile's directory | Reloaded on every profile switch. |
| `Device` | The shared `device` directory | Survives profile switches; loaded once at `InitializeAsync` and never reloaded by an activation. Must be `LocalOnly`. |

Two combinations are rejected at composition time: `Device` scope with anything other than
`LocalOnly`, and `CloudReadOnly` with anything other than `Profile` scope.

### Slot granularity

One slot per domain. A slot is the unit of writing, of uploading and of losing: a single `Mutate`
re-serializes the whole slot, and a restore that takes the cloud copy replaces the whole slot.

Split a slot when:

- the `SyncMode` or `Scope` would differ (this one is forced);
- the write frequency differs wildly — do not put a per-second position counter in the same slot as
  a purchase ledger;
- the conflict policy differs — currency wants "take the larger", settings want "take the newest";
- the payload grows past roughly 64 KB, or approaches the provider's `MaxValueBytes`.

Keep fields **together** when an invariant spans them. There are no multi-slot transactions: after a
crash, slot A can be one revision ahead of slot B. "Coins spent" and "item granted" belong in the
same slot.

Avoid one giant string-keyed bag of sub-objects. It defeats every guideline above at once. A
typical game ends up with 5-20 slots.

---

## Reading and writing

### Read

```csharp
long coins = _playerSlot.Read(static data => data.Coins);

// Non-capturing overload: pass state through the argument.
bool hasItem = _inventorySlot.Read(itemId, static (data, id) => data.Items.Contains(id));
```

### Mutate

```csharp
_inventorySlot.Mutate(itemId, static (data, id) => data.Items.Add(id));
```

`Mutate` runs the action, increments the slot's `Revision` and marks it dirty, which schedules a
local write and, for a `CloudSync` slot, an upload.

It returns `false` and **runs nothing** when:

- the slot is not `Ready` (not loaded yet, or `Failed`);
- the slot is `CloudReadOnly`;
- a profile switch is in progress;
- the service is disposed, or the slot is not registered with one;
- it was called from inside a slot hook.

If the action itself throws, the partial change is still marked dirty and the exception propagates.

### Never keep the reference

This is the single most common way to lose data with this package:

```csharp
// WRONG. The change is invisible to the save system and is never written.
var data = _playerSlot.Read(static d => d);
data.Coins += 100;
```

`Read` hands you the live instance so that reading is allocation-free. Nothing stops you from
keeping it, and nothing will save what you do to it. Take **values** out of `Read`, never the object
or one of its sub-objects.

In the Editor and in development builds, `SaveServiceOptions.DetectMutationsOutsideMutate` catches
this: on every flush, each clean slot is hashed and compared to the hash taken at its last write. A
mismatch is logged as an error once per slot, and the change is deliberately **not** saved, because
saving it would hide the bug.

### Durable writes

```csharp
SaveResult result = await _playerSlot.SaveNowAsync(ct);
```

`SaveNowAsync` completes only after the slot's revision at call time (or newer) has been written to
a temp file, fsynced, and renamed into place. Use it for anything the player paid for or earned.
It also asks for an immediate upload but does not await it; if the cloud copy must exist before you
continue, `await ISaveService.FlushAsync(ct)` and check `IsComplete`.

Caller cancellation is honoured only *before* the write starts. Once the bytes are going down, the
write finishes and the result is returned.

### Resetting a slot

There is no "clear" method on the slot. Deleting is a service operation, because it also has to
deal with the cloud copy:

```csharp
// Wipes the local files and resets the in-memory data to CreateDefault + Normalize.
await _save.DeleteSlotAsync(_inventorySlot, DeleteTarget.LocalOnly, ct);

// Also deletes the cloud value (writes a tombstone first, so an offline delete still completes later).
await _save.DeleteSlotAsync(_inventorySlot, DeleteTarget.LocalAndCloud, ct);
```

`DeleteTarget.LocalAndCloud` on a `CloudSync` slot persists a tombstone before touching the network.
If the delete cannot reach the provider, the tombstone stays and the next successful restore
finishes the job instead of downloading the data back. Quarantine files (`{key}.corrupt-*.json`) are
deliberately **not** removed by a slot delete.

---

## Hooks

`SaveSlot<TData>` has five hooks. All of them are optional.

| Hook | When it runs |
|---|---|
| `TData CreateDefault()` | A new instance is needed: fresh install, after a reset, as the "empty" reference. Default is `new TData()`. |
| `void Normalize(TData data)` | Exactly once whenever any instance enters memory (after `CreateDefault`, after deserialization, after a merge). Repair invariants here: null lists, clamps, backfilled fields. |
| `JObject UpgradePayload(JObject payload, int fromSchemaVersion)` | A stored payload is older than this build's `SchemaVersion`. One step per call. See [Schema changes](Schema-Changes.md). |
| `bool IsEmpty(TData data)` | Deciding whether this data carries player progress. The default compares the JSON of `data` with the normalized default. |
| `ConflictResolution<TData> ResolveConflict(in ConflictContext<TData> context)` | A cloud reconcile could not decide. Default: the empty side loses, otherwise the cloud wins. |

### Hook rules

**Hooks must be synchronous, pure and must not call the save service.**

- They may run on the thread pool. Do not touch Unity APIs, `Time`, scene objects or Addressables.
- While a hook runs, the core sets a flag. Any `Mutate`, `SaveNowAsync` or `ISaveService` call made
  during that window is refused and logged as an error (`SaveErrorCode.CalledFromHook`). `Mutate`
  simply returns `false`.
- They should not throw. A throw from `Normalize` or `UpgradePayload` fails the load and the slot
  ends up `Failed(NormalizeFailed)`; a throw from `IsEmpty` is treated as "not empty", which is the
  safe answer; a throw from `ResolveConflict` fails that slot's reconcile.
- `ResolveConflict` must not modify the objects it is given. Build the merged instance instead.

### Migrations that need the outside world

If an upgrade needs something a hook cannot have (an Addressables catalog, a remote config, a
localization table), do not try to do it in `UpgradePayload`. Do it in a game service after the
service is ready:

```csharp
if (!await _save.WhenReadyAsync(ct))
{
    return;
}

bool done = _playerSlot.Read(static d => d.SkinCatalogMigrationDone);
if (!done)
{
    IReadOnlyList<string> resolved = await ResolveSkinIdsAsync(ct);
    _playerSlot.Mutate(resolved, static (d, ids) =>
    {
        d.UnlockedSkins = new List<string>(ids);
        d.SkinCatalogMigrationDone = true;
    });
    await _playerSlot.SaveNowAsync(ct);
}
```

Such a migration must be idempotent, must record its own done-marker inside the slot data, and must
simply retry next session if it fails.

### Field initializers are replaced, not merged

The shared serializer uses `ObjectCreationHandling.Replace`. A field like
`public List<string> Items { get; set; } = new List<string> { "sword" };` is **replaced** by what
was saved, not appended to. That is on purpose: with Newtonsoft's default `Auto`, the saved items
are added to the default items on every single load, which duplicates data forever. If you see
duplicates, see [Troubleshooting](Troubleshooting.md#list-items-duplicated-after-load).

Other pinned settings: `TypeNameHandling.None` (no polymorphic type names in save files),
`NullValueHandling.Ignore`, `MissingMemberHandling.Ignore` (an unknown property in an old file is
skipped, not an error).

### IL2CPP and code stripping

Managed code stripping can remove types and members that are only ever constructed through
reflection, which is exactly what a JSON deserializer does. Mark your data classes and your slot
types with `[UnityEngine.Scripting.Preserve]`, or add a `link.xml` that preserves your save
assembly. The symptom of getting this wrong is a build where every save loads as defaults while the
Editor works fine. See [Troubleshooting](Troubleshooting.md#saves-are-empty-in-an-il2cpp-build).

---

## Readiness

Before `InitializeAsync` finishes, every slot is `Unloaded` and reads see defaults. The hazard is a
read-modify-write that runs during startup: it reads a default, adds to it, and writes that over
real progress.

```csharp
// True when ready; false after Dispose. Caller cancellation throws OperationCanceledException.
if (await _save.WhenReadyAsync(ct))
{
    // Every slot is Ready or Failed here.
}
```

`IsReady` means: initialized, no activation in progress, and every slot is `Ready` or `Failed`. It
goes back to `false` during a profile switch and becomes true again once the new profile's slots are
loaded — **before** the restore listeners are dispatched, so a listener can await readiness without
deadlocking itself.

In the Editor and development builds, the first `Read` or `Mutate` on an unloaded slot logs a
warning naming the slot (`SaveServiceOptions.WarnOnAccessBeforeReady`). It is logged once per slot
per unloaded period, so a switch re-arms it.

---

## Profiles

A profile decides which directory the `Profile`-scope slots are loaded from and written to.

| Kind | Created with | Directory | Cloud |
|---|---|---|---|
| Guest | `ProfileId.Guest` | `profiles/guest/` | Never uploads. |
| Account | `ProfileId.Account(playerId)` | `profiles/acc-{id}/` | Uploads and restores. |
| Local | `ProfileId.Local("slot-1")` | `profiles/local-slot-1/` | Never uploads. |

- `ProfileId` is a value type; `ProfileId.Guest` is also its `default`.
- `Account(id)` throws `ArgumentException` on a null or empty id. If the id contains characters that
  are unsafe in a file name, or is longer than 64 characters, the directory name uses a hash of the
  id instead; the real id is stored inside the directory's `profile.json`.
- `Local(name)` throws unless the name matches `^[a-z0-9_-]{1,32}$`. **Lower case only**, because
  Windows and macOS file systems are case-insensitive and `Save1` / `save1` would be the same
  directory. Use `ProfileId.IsValidLocalName(name)` to validate player input before calling it.
- The display name a player sees ("Save 1", "Nightmare run") is game data, not part of `ProfileId`.
  Store it in one of your own slots.
- Local profiles are siblings of `guest/`, not children of an account. They do not disappear when
  the player signs in.

### Switching

```csharp
ProfileActivationResult result = await _save.ActivateProfileAsync(ProfileId.Account(playerId), ct);
```

One method handles all three kinds. What happens, in order:

1. If the requested profile is already active, it returns immediately with `AlreadyActive = true`.
   Nothing is switched and no listener runs.
   A second call made while another activation is still running is refused with
   `SaveErrorCode.ReentrantCall`: one activation at a time, so the deactivation hooks cannot run twice
   or write into the profile that is being switched to.
2. `IsReady` goes false and the **deactivation listeners** run in `Order`. Slots are still `Ready`
   here, so a listener can `Mutate`, `SaveNowAsync` and even `FlushAsync` to get final state into the
   outgoing profile. They share one timeout budget (`SaveServiceOptions.DeactivationTimeout`,
   5 seconds by default); on timeout the remaining listeners are skipped, the failure is recorded in
   `DeactivationFailures` / `DeactivationTimedOut`, and **the switch proceeds**.
3. Local writes are flushed synchronously, then the epoch is cancelled: in-flight uploads and
   restores for the old profile stop.
4. `Profile`-scope slots are unloaded (memory reset to defaults). `Device`-scope slots are untouched.
5. The new directory is resolved and the slots are loaded from it.
6. `IsReady` goes true, the restore listeners are dispatched with `RestoreTrigger.ProfileActivated`,
   and the `ProfileActivated` event is raised.

Cancelling the caller's token during step 2 aborts the whole activation with nothing switched.

### The guest claim

When a player signs in for the first time on a device, their guest progress should become the
account's progress. That is automatic:

**Activating an `Account` profile, when the current profile is `guest` and the account has no
directory yet, moves the whole `guest/` directory to `profiles/acc-{id}/`.** The result's
`ClaimedGuestData` is `true`.

Rules around it:

- Only an `Account` activation claims, and only from `guest`. A `Local` profile is never claimed,
  and never claims anything.
- A claim only moves the files. Whether the claimed data or the account's existing cloud data wins
  is decided by the next restore, through the normal conflict path.
- Nothing is uploaded before that first reconcile, so a claim cannot overwrite cloud progress the
  player made on another device.

### Signing out

```csharp
FlushResult flush = await _save.FlushAsync(ct);
if (!flush.IsComplete)
{
    // Ask the player whether to sign out anyway; the unsynced slots are listed in flush.Cloud.
}

await _save.ActivateProfileAsync(ProfileId.Guest, ct);
await AuthenticationService.Instance.SignOutAsync();   // backend sign-out last
```

Switch the profile **before** signing out of the backend. Once the provider reports a different
signed-in account (or none), uploads for the old account are refused, and anything still dirty
waits on disk until that account is active again.

### Enumerating and probing

```csharp
IReadOnlyList<ProfileId> locals = _save.GetLocalProfiles();     // reads profiles/local-* ; never writes
LocalPresence presence = _save.ProbeLocalPresence(ProfileId.Guest);
```

Both are pure queries. `ProbeLocalPresence` answers "does any `Profile` slot have readable local
content for this profile" with `Present`, `Absent` or `Undetermined`, by peeking at file headers. It
never repairs, promotes or quarantines anything, so it is safe to call from a "continue?" screen.

### Deleting a profile

```csharp
DeleteResult result = await _save.DeleteProfileAsync(
    ProfileId.Local("slot-1"), ProfileDeleteMode.RequireSynced, ct);

if (result.Error?.Code == SaveErrorCode.UnsyncedChanges)
{
    // result.UnsyncedSlotKeys names the slots that would lose data.
}
```

- The **active** profile cannot be deleted; the call returns `ProfileActive`. Activate another
  profile first.
- `RequireSynced` refuses when any `CloudSync` slot has a local revision newer than its last synced
  revision, has an upload in flight, or has undetermined presence. Guest and Local profiles with
  cloud-mode content always refuse under this mode, since they can never be synced.
- `DiscardUnsynced` deletes regardless.
- Either way this deletes the **local directory only**. Cloud values are untouched.

### Deleting a player's account data

For a "delete my data" request, the cloud copy has to go too:

```csharp
await _save.ActivateProfileAsync(ProfileId.Guest, ct);          // must not be active
AccountDataDeleteResult result = await _save.DeleteAccountDataAsync(playerId, ct);

if (!result.IsComplete)
{
    // Partial: local data and tombstones are kept on purpose. Call it again later.
}
```

Preconditions: the account must not be the active profile, the provider must be signed in, and it
must be signed in **as that account**. The flow persists a tombstone for every registered
`CloudSync` slot, deletes each cloud value (a missing key counts as already absent), and deletes the
local directory only when every cloud delete succeeded. On a partial failure nothing local is
removed, so retrying is safe and idempotent. `CloudReadOnly` (server-owned) keys are out of scope:
the backend's own player-deletion flow is authoritative for those.

---

## Related pages

- [Sync and recovery](Sync-And-Recovery.md) — what happens to a `CloudSync` slot between the
  `Mutate` and the cloud.
- [Events and listeners](Events-And-Listeners.md) — reacting to activations and restores.
- [Schema changes](Schema-Changes.md) — changing `TData` after you have shipped.
