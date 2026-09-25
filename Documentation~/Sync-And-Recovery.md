# Sync and recovery

[Back to index](index.md)

This page is about what happens to save data between the `Mutate` call and the bytes being safe:
when an upload actually goes out, how a local and a cloud copy are reconciled, what the files on
disk are, and how corruption, reinstalls and other backup systems are handled.

---

## What syncs

Only slots that are **`SyncMode.CloudSync`**, **`SlotScope.Profile`**, and belong to an
**`Account`** profile. Guest and Local profiles never upload. `LocalOnly` slots never upload.
`CloudReadOnly` slots are read from the cloud and cached locally, but never written.

---

## When an upload happens

### Three preconditions

An upload for a slot is only attempted when all three hold:

1. **The account is live.** The active profile is an `Account`, and the provider reports it is
   signed in as *that* account. Otherwise the result is `Skipped(ProfileNotCloudBacked)`,
   `Skipped(NotSignedIn)` or `Skipped(AccountMismatch)`.
2. **The slot was reconciled this epoch.** A restore must have decided this slot at least once
   since the profile became active. Until then the result is `Skipped(NotReconciled)`. This is what
   makes a first sign-in safe: the package refuses to push local data over cloud data it has not
   looked at yet.
3. **There is something to send, and sending is allowed.** The slot is `Ready`, it is dirty (or has
   an upload that was started and never confirmed), it is not suspended after a permanent failure
   (`Skipped(SuspendedUntilReconcile)`), and its local file is writing successfully
   (`Skipped(LocalWriteFailed)` otherwise — disk before cloud, always).

### Four triggers

| Trigger | Timing |
|---|---|
| A `Mutate` marks the slot dirty | The debounce timer below. |
| `SaveSlot<TData>.SaveNowAsync` | Requests an immediate upload after the local write lands. It does **not** await the upload. |
| `ISaveService.FlushAsync` | Immediate, no debounce. Awaits every upload and reports per slot. |
| Application pause | The lifecycle driver flushes locally, then starts a best-effort `FlushAsync` and awaits it until the app resumes. |

Cancelling the caller's token on `FlushAsync` stops the driver waiting; it does not abort an upload
already in flight, because a half-sent write is worse than a slow one.

### The debounce and max-wait windows

| Option | Default | What it does |
|---|---|---|
| `SaveServiceOptions.LocalWriteDelay` | 500 ms | Quiet time after a mutation before the slot is written to disk. Coalesces a burst of mutations into one file write. |
| `SaveServiceOptions.CloudDebounce` | 2 s | Quiet time after the **last** mutation before an upload starts. |
| `SaveServiceOptions.CloudMaxWait` | 10 s | Upper bound between the moment the slot first became dirty and the upload, no matter how much the player keeps mutating. |
| `SaveServiceOptions.CloudCallTimeout` | 30 s | Upper bound on a single **attempt** at a provider call. A backend that never answers and ignores its token is abandoned and reported as a transient error, so it cannot hold the operation gate for the rest of the session — but that bound is minutes, not `CloudCallTimeout` itself; see [below](#the-real-bound-on-the-operation-gate). Zero disables the timeout. |

Concretely: a player tapping a button every second keeps resetting the 2-second debounce, but
`CloudMaxWait` forces an upload 10 seconds after the first tap. `CloudMaxWait` must be greater than
or equal to `CloudDebounce`; the options validator throws otherwise.

**The defaults are eager.** They are tuned for "no progress is more than ten seconds old", which is
the right answer while you are building the game and the wrong answer for a shipped live game with
a per-write cost. See [cost](#cost) below.

### Retries

A transient provider error (no connection, service unavailable, rate limited) is retried inside the
call, `CloudRetryCount` times (default 3), starting at `CloudRetryBaseDelay` (500 ms) and doubling
up to `CloudRetryMaxDelay` (8 s), unless the provider returns an explicit retry-after. When those
are exhausted the upload is rescheduled through `UploadRetryBackoff` (30 s, 60 s, 2 min, 5 min; the
last value repeats). Regaining focus makes a pending retry due immediately.

A **permanent** failure suspends uploads for that slot until its next successful reconcile, and
raises `UploadFailed`. It does not stop other slots, and it does not lose the data: the slot stays
dirty on disk.

### The real bound on the operation gate

`CloudCallTimeout` bounds one **attempt**, not one gateway call. A transient result — including a
timeout — is retried inside the gateway, so the actual worst case for one `ReadAsync` chunk, one
`WriteAsync` chunk, or the single key of a `DeleteAsync` is:

```
(CloudRetryCount + 1) x CloudCallTimeout + sum of the retry delays between attempts
```

With the defaults (`CloudCallTimeout` 30 s, `CloudRetryCount` 3, delays 0.5 s / 1 s / 2 s) that is
4 x 30 s + 3.5 s ≈ **123 s** per gate acquisition, not 30 s. Two things multiply it further:

- **`ReadAsync` and `WriteAsync` restart this budget per chunk.** A profile with more slots than the
  provider reports for `MaxKeysPerRead` / `MaxKeysPerWrite` (20 for the bundled Unity Cloud Save
  provider) sends more than one chunk; a provider wedged on every chunk holds the gate for roughly
  `chunkCount x 123 s`. `DeleteAsync` always targets one key, so it is never multiplied this way.
- **The first-ever upload of a key re-reads it first.** A slot with no known cloud write for the
  account sends a read before its first write, because the provider may not support a create-only
  write and another device could have created the key first. A provider wedged on both the read and
  the write roughly doubles the bound for that key, to ~246 s.

This is still bounded — a wedged provider cannot hold the gate for the rest of the session, which is
what the timeout exists to guarantee — but "bounded" means minutes with the shipped defaults, not
`CloudCallTimeout` itself. If a shipped game needs a tighter worst case, lower `CloudCallTimeout`
and/or `CloudRetryCount` deliberately with this multiplication in mind. Note that `CloudRetryCount`
also recovers ordinary transient errors that answer well inside the timeout, so lowering it trades
resilience for a shorter worst case. The shipped defaults are unchanged by this note.

---

## Reconcile, in plain language

`RestoreAsync` (and the automatic reconcile that follows a conflict) answers one question per slot:
**does the local copy or the cloud copy win, and does anything need to be written back?**

The important idea is **write-id provenance**. Every upload carries a random write id, which is
stored both in the uploaded envelope and in the profile's `profile.json`. So when the package reads
a cloud value, it can tell the difference between:

- "this is the value *this device* uploaded last" — nothing happened elsewhere, no conflict; and
- "this is a value I have never seen" — someone else wrote it, so the two sides have diverged.

That is why the package does not use timestamps. Device clocks are wrong, and "last write wins"
silently discards whichever device had the slower clock.

The decision, roughly in the order it is made:

| Situation | Result |
|---|---|
| The cloud read failed | Keep local, mark the slot failed for this run, try again at the next restore. Nothing is written. |
| The cloud value's format or schema is newer than this build | Keep local, upload nothing, raise `UpdateRequired`. See [Schema changes](Schema-Changes.md#a-save-from-a-newer-build). |
| The cloud value is corrupt | Back up its raw bytes to `{key}.cloud-corrupt-{timestamp}.json`, raise `SlotLoadIssueDetected`. If local has content, keep local and repair the cloud with a conditional write; if it does not, do nothing. |
| A delete is pending for this slot | Finish the delete instead of downloading the data back. |
| There is no cloud value | Keep local. If local has content, it is queued for upload. |
| The cloud value's write id is the one this device wrote (or is currently writing) | Not a conflict. Promote it to "synced"; upload again only if the slot is dirty. |
| Local is absent, unreadable, or older than the last synced revision | Take cloud. |
| Local is not dirty and this device has uploaded before | Take cloud. |
| Anything else | **Conflict.** The slot's `ResolveConflict` hook decides. |
| Post-check: the decision was "take cloud", the cloud payload is empty, and local has content | Refuse it. Keep local and upload. `SkippedToProtectLocalData`. |

Every per-slot outcome ends up in `RestoreReport.Slots` as a `SlotRestoreOutcome`
(`Restored`, `UpToDate`, `LocalKept`, `Merged`, `NoCloudData`, `SkippedToProtectLocalData`,
`SkippedSchemaTooNew`, `RepairingCorruptCloud`, `CorruptCloudIgnored`, `Failed`, `LoadedLocal`), and
`RestoreReport.Completeness` summarises them.

### One safety rule worth stating on its own

The account is re-checked on the main thread **immediately before** anything is applied, with no
await in between. If the player signed out or signed into a different account while the fetch was in
flight, nothing is applied, nothing is persisted, and the report comes back with
`Status = AccountMismatch`. A restore can never write one player's data into another player's
profile.

---

## Conflicts

When the decision table reaches "conflict", the slot's `ResolveConflict` hook runs:

```csharp
protected override ConflictResolution<PlayerData> ResolveConflict(in ConflictContext<PlayerData> context)
{
    // Keep whichever side has more progress; never add the two together.
    return context.Local.Level >= context.Cloud.Level
        ? ConflictResolution<PlayerData>.KeepLocal
        : ConflictResolution<PlayerData>.TakeCloud;
}
```

The default policy, if you do not override it: the empty side loses; otherwise the cloud wins.

`ConflictContext<TData>` gives you `SlotKey`, the normalized `Local` and `Cloud` instances,
`LocalIsEmpty` / `CloudIsEmpty`, `LocalRevision` / `CloudRevision`, `HasSyncedBefore` (false when
this device never synced the slot, for example right after a guest claim), plus `CloudSavedAtUtc`
and `CloudDeviceId` for diagnostics only — clocks drift and device backups clone device ids, so do
not base a decision on either. `ConflictResolution<TData>` is `KeepLocal`, `TakeCloud` or
`Merged(data)`.

Rules for the hook: it is synchronous and pure, it must not mutate either input (build a new
instance for `Merged`), and it must not call the service. Merged data goes through `Normalize`
before it is accepted.

**Do not merge by adding.** A merge that sums two coin balances turns every reconnect into free
currency. Merge set-like data (unlocked levels: union) and take a side for counters.

### The conflict file

Before the winner is applied, `{key}.conflict.json` is written next to the slot file. It holds:

```json
{ "resolution": "TakeCloud", "savedAtUtc": "...", "local": { }, "cloud": { } }
```

This is written for **every** resolution, not only when the cloud wins, so the losing side is always
recoverable by hand. Only one generation is kept: the next conflict for that slot overwrites it. It
is deleted when the slot is deleted.

---

## The upload guard

One check sits in front of every upload and is worth knowing about, because it shows up in logs:

> Empty data was not uploaded over content that existed before.

If a slot's data is currently empty (`IsEmpty` returns true) but the package has recorded that this
slot had content before, the upload is skipped with `Skipped(EmptyOverContent)` and `UploadFailed`
is raised. That pattern is what a load bug looks like from the outside: something reset the slot to
defaults, and without the guard those defaults would be pushed over the player's real save.

If a slot really is supposed to become empty, delete it explicitly with `DeleteSlotAsync`. That
clears the "had content" record properly.

---

## On disk

Everything lives under `SaveServiceOptions.RootDirectory`, which defaults to
`{Application.persistentDataPath}/saves`.

```
saves/
  device/
    device.json                       device id and last active profile
    {key}.json                        Device-scope slots
  profiles/
    guest/
      profile.json                    sync state: last synced write id, revision, tombstones
      player.json                     a slot
      player.json.bak                 the previous version of that slot
      player.json.tmp                 only present mid-write or after a crash
      player.conflict.json            last conflict for that slot, both sides
      player.corrupt-20260918T101530Z.json      quarantined unreadable local file (max 3 per slot)
      player.cloud-corrupt-20260918T101530Z.json  raw corrupt cloud bytes (max 3 per slot)
      profile.newer-20260918T101530Z.json       document from a newer build (max 3, own quota)
    acc-a1b2c3d4/                     an account profile
    local-slot-1/                     a named local profile
```

Each slot file is one **envelope**:

```json
{"fmt":1,"schema":3,"rev":42,"writeId":"8f3c...","savedAtUtc":"...","deviceId":"...","accountId":"...","dataSha256":"9ab1...","data":{"Level":7,"Coins":1200}}
```

`data` is always the last property and is always written compactly, because `dataSha256` is the
SHA-256 of its exact bytes as written. That makes verification a substring hash with no re-parse and
no re-serialization — re-serializing is not a fixpoint (`1.10` and `1.1`, `0.00001` and `1E-05`), so
hashing a canonical form would produce false corruption reports.

The same envelope is what goes into the cloud value, so a slot uses exactly one cloud key.

### How a write happens

1. Serialize into a temp file `{key}.json.tmp`.
2. `Flush(true)` — the bytes are on the device, not in an OS buffer.
3. Rename the current `{key}.json` to `{key}.json.bak`.
4. Rename the temp file to `{key}.json`.
5. Delete the old `.bak`.

A crash at any point leaves a recoverable state, which is the point of the sequence.

On **Windows only** (Editor, standalone, UWP), each rename or delete in that sequence is retried up
to three times with 15 ms, 30 ms and 60 ms pauses when it fails with an `IOException` or
`UnauthorizedAccessException`. Antivirus and search indexers hold file handles for a few
milliseconds. This costs at most 105 ms per contended file, and it is deliberately not enabled on
mobile, where the pause and quit path cannot afford to sleep.

---

## Corruption

A file is considered corrupt when bytes that were read successfully will not parse, will not
deserialize, or fail their checksum. The distinction matters: an IO error is *not* corruption, and
is never "repaired" by throwing data away.

The load order for a slot:

1. If a `.tmp` exists with a valid checksum and a **higher** revision than the primary, promote it.
   That covers a crash between step 3 and step 4 above.
2. Read the primary. If it parses and its checksum matches, done.
3. Primary corrupt: try the `.tmp`, then the `.bak`. A recovered copy is rewritten as the new
   primary. `SlotLoadIssueDetected` fires with `CorruptRecoveredFromTmp` or
   `CorruptRecoveredFromBackup`.
4. Nothing valid: the corrupt primary is renamed to `{key}.corrupt-{timestamp}.json` (the newest
   three are kept), the slot is reset to defaults, and it is marked as needing cloud recovery. The
   issue kind is `CorruptReset`.

If the file is missing the checksum field, or is pretty-printed so the exact bytes cannot be
located, it is **accepted** and a warning is logged in development builds. Hand-editing a save for
debugging still works; it just cannot be verified.

`profile.json` and `device.json` written by a **newer build** are a separate case. They are never
trusted blindly and never silently downgraded: the file is copied verbatim to
`profile.newer-{timestamp}.json` (or `device.newer-{timestamp}.json`) before this build rewrites it in
its own format, the fields this build knows are loaded, and the unknown ones fall back to the safe
side. The backup is what support needs when a player rolls back to an older build.

These copies have their own quota of 3, separate from the corruption quarantine, so rolling back does
not evict evidence of real corruption. An identical copy is never written twice. If the copy cannot be
written at all, this build **refuses to overwrite** the newer file and leaves it untouched; the same
applies when a slot is read in read-only mode, where no copy is taken.

Other load issues that are not corruption:

| Kind | Meaning |
|---|---|
| `IoError` | The read failed. The slot is `Failed`, nothing is written, the next restore retries it. |
| `NormalizeFailed` | `UpgradePayload`, deserialization or `Normalize` threw. The file is left untouched — this is a code bug, not a data problem, and destroying the file would destroy the evidence. A `CloudSync` slot in this state can be repaired from the cloud. |
| `SchemaTooNew` | Written by a newer build. Untouched, and `UpdateRequired` is raised. |
| `SyncStateCorrupt` | `profile.json` is unreadable. Safe fallbacks are used: "had content" is assumed true and the last synced write id is treated as unknown, so nothing destructive follows. |

---

## Pause and quit

`SaveLifecycleDriver.Attach(service)` wires Unity's callbacks:

| Callback | What the driver does |
|---|---|
| `OnApplicationPause(true)` | `FlushLocalNow()` synchronously, then starts a best-effort `FlushAsync` and awaits it until the app comes back. |
| `OnApplicationPause(false)` | Stops waiting on that upload and makes pending transient upload retries due immediately. |
| `OnApplicationFocus(false)` | `FlushLocalNow()` only. **No network**, because system dialogs and notification shades cause frequent focus loss. |
| `OnApplicationFocus(true)` | Stops waiting on the pause upload. |
| `OnApplicationQuit` / destroy / last handle disposed | One final `FlushLocalNow()`. |

`FlushLocalNow()` is synchronous and ignores the local write backoff: on the way out there is no
next frame to retry in. It returns a `LocalFlushResult` listing any files that could not be written.

On Android, quit is not guaranteed to run; pause is. That is why the pause path does the real work.

---

## OS backups and reinstall

**The package never touches OS backup configuration.** It documents what those systems do and lets
you decide.

- **Android Auto Backup** is on by default (API 23+). It backs up app files including
  `persistentDataPath`, up to 25 MB, and restores them when the app is reinstalled.
- **iOS** device backups (iCloud or a computer) include `Documents`, where `persistentDataPath`
  lives.

The effect: after a reinstall, a **stale** `profile.json` and stale slot files can reappear, dated
from whenever the backup was taken.

The package is built for this. Because reconciliation uses write ids and not timestamps, a restored
stale copy is recognised as "a copy whose last upload is not the current cloud value", which is a
conflict or a clean take-cloud, not a silent overwrite. A restored stale tombstone will not delete a
cloud value that has since been rewritten elsewhere.

**The recommended default is to leave OS backups on.** It is the only thing that carries guest and
`LocalOnly` data across a reinstall.

If you must opt out:

| Platform | How |
|---|---|
| Android API 31+ | `android:dataExtractionRules` with a `file` exclusion for the `saves/` path. |
| Android API 23-30 | `android:fullBackupContent` with the same exclusion. |
| Android, everything | `android:allowBackup="false"` in the manifest. |
| iOS | `UnityEngine.iOS.Device.SetNoBackupFlag` on the **`saves` directory**, after `InitializeAsync`. Never per file: the atomic write replaces files, and the flag does not follow. Verify on a device. |

The trade-off is explicit: opting out loses guest progress and every `LocalOnly` slot on reinstall.

---

## Steam Auto-Cloud

Steam Auto-Cloud syncs files by pattern, with no knowledge of what is inside them. It can be used
with this package, under rules:

- **Root:** `saves/profiles/`. Never include `saves/device/`, which holds the device id and the
  last-active pointer; syncing that across machines confuses both ends.
- **Pattern:** `*.json`. This deliberately excludes `.tmp` and `.bak`, which are mid-write artefacts
  and must never be synced.
- **Slots covered by Steam must be `LocalOnly`.**

**Never enable Steam Cloud and a cloud provider for the same slot.** Two sync layers over the same
bytes cannot both enforce write-id provenance; Steam's own file-conflict dialog replaces the
package's reconcile, and the package has no way to see that it happened. Pick one per slot.

---

## Cost

The defaults do not consider money. A live game should.

- Every `Mutate` that survives the debounce is one provider write. With `CloudDebounce = 2 s` and
  `CloudMaxWait = 10 s`, a busy session can produce a write every ten seconds per slot.
- Raise both for a shipped game. Something like `CloudDebounce = 30 s` and `CloudMaxWait = 5 min`
  turns idle drift into a handful of writes per session, and costs you at most five minutes of
  progress on a hard crash — which is exactly the progress the **local** write already has.
- Then flush explicitly at the moments that matter:

```csharp
// End of a level, after a purchase, before a leaderboard submit, before sign-out.
FlushResult flush = await _save.FlushAsync(ct);
if (!flush.IsComplete)
{
    foreach (CloudFlushSlotResult slot in flush.Cloud)
    {
        if (!slot.IsSynced)
        {
            Debug.LogWarning($"{slot.SlotKey}: {slot.Status} ({slot.Reason})");
        }
    }
}
```

- `SaveNowAsync` after a purchase costs a local write and one upload request. Use it; it is the
  cheap half of the guarantee.
- Splitting a high-frequency counter out of a slot that also holds rarely-changing data is usually a
  bigger saving than any timer change, because the counter stops dragging the rest of the payload
  up with it on every write.

---

## Related pages

- [Slots and profiles](Slots-And-Profiles.md) — `ResolveConflict` and the other hooks.
- [Events and listeners](Events-And-Listeners.md) — reacting to `RestoreCompleted` and `UploadFailed`.
- [Unity Cloud Save](Unity-Cloud-Save.md) — the bundled provider and its limits.
- [Troubleshooting](Troubleshooting.md) — symptoms and fixes.
