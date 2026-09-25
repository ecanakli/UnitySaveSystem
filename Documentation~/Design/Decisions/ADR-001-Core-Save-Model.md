# ADR-001: Core save model

- **Status:** Accepted, implemented in 0.1.0
- **Related:** [Architecture](../Architecture.md),
  [ADR-002](ADR-002-DI-Integrations-As-Gated-Assemblies.md),
  [ADR-003](ADR-003-Sync-Safety-Invariants.md)

## Context

The package exists because of a post-mortem on a save system in a shipped free-to-play mobile game.
The concrete failures that drove this ADR were:

- Player data lost when a corrupt local blob was treated as "no data" and overwritten with defaults.
- Data crossing between accounts on the same device, because local storage was global and only the
  cloud was account-aware.
- Conflicts resolved by comparing timestamps, on devices whose clocks drift and whose ids are cloned
  by OS backups.
- No schema version on the payload, so a data class that changed shape silently broke saves.
- A per-call "also save to cloud" flag that was occasionally forgotten, after which two devices
  diverged with nothing to detect it.

A new game needed a save layer, and the same mistakes were about to be repeated by hand. The
alternative to building this was to keep writing per-project save code.

## Decision

### A DI-agnostic core

`SaveService` is constructed with explicit arguments: options, an `ISaveStorage`, an
`ICloudSaveProvider` and the slot instances. No attributes, no service locator, no static entry
point. Events are plain C# events; ordered callbacks are a one-method interface (`IRestoreListener`,
`IProfileDeactivatingListener`). A game using no container writes about six lines of composition;
a game using a container writes an installer that calls the same constructor.

This is what makes the package usable in any project, and it is also what makes it testable: every
collaborator is a constructor argument or an interface with an in-memory fake.

### Three sync modes, declared per slot

| Mode | Meaning |
|---|---|
| `LocalOnly` | Device only. Never uploaded, never fetched. |
| `CloudSync` | Uploaded and reconciled. The package owns the value. |
| `CloudReadOnly` | A server-authoritative mirror. Read, never written; `Mutate` is refused. |

Sync behaviour is a property of the **data**, not of the call site. There is deliberately no "save to
cloud just this once" parameter: that flag is what let two devices diverge in the previous system.
The knobs that remain are the slot's `SyncMode`, `FlushLocalNow()` for an explicit local-only flush,
and the debounce and max-wait options for traffic.

`CloudReadOnly` exists so that server-owned values (a live-ops calendar, server-granted entitlements)
can flow through the same slot, listener and readiness machinery without the client ever being able to
write them. Its cloud values are raw JSON, not enveloped, because a server writer will not produce the
package's envelope.

### Account-scoped local storage

Local data lives under `profiles/{guest | acc-{id} | local-{name}}/`, one directory per profile, plus a
shared `device/` directory for `Device`-scope slots. Switching accounts is a directory switch, not a
data sweep.

This is what removes the whole class of cross-account bugs at the root: there is no shared mutable
location for two accounts to fight over. It also means switching back to a previous account is instant
and works offline, because that account's files were never deleted. Cleanup is explicit
(`DeleteProfileAsync`, `DeleteAccountDataAsync`) rather than automatic on switch.

A first sign-in on a device **claims** the guest directory by moving it, which is atomic on one volume
and is its own claim marker. Local (player-named) profiles are never claimed and never upload.

### Write-id reconcile

Every upload carries a fresh GUID `writeId` inside the envelope, and `profile.json` records
`LastSyncedWriteId`, `PendingWriteId`, `LastSyncedRevision` and `LastSyncedProviderVersion` per slot.
The reconcile decision is a pure function over that metadata plus the local and cloud state, expressed
as an ordered table (see [Architecture section 6](../Architecture.md#6-the-reconcile-decision-table)).

Write ids answer the only question that actually matters — *did this cloud value come from my last
write?* — without trusting a clock or a device identity. The `PendingWriteId`, persisted **before** the
request is sent, is what makes a lost response recoverable: the next restore recognises the write that
did land and promotes it instead of treating it as a foreign change.

The revision is the local ordering signal, and the provider version token is used for conditional
writes where the backend supports them.

### Default conflict policy: the empty side loses, otherwise the cloud wins

`DefaultConflictPolicy` is a static rule, not an injectable service, because there is exactly one
default and per-slot variation is already the `ResolveConflict` hook. It ignores revisions and
timestamps deliberately: neither is comparable across devices.

Whatever the outcome — keep local, take cloud, or a slot-supplied merge — `{key}.conflict.json` records
the resolution and **both** sides before the swap. A player who lost data to a conflict has it on the
device, and support can ask for it.

An empty payload never wins. That rule is enforced twice: in the conflict policy, and again as a
post-check that turns a `TakeCloud` of an empty cloud value over local content into "keep local and
upload".

### Unity 6000.0 floor

`package.json` declares `"unity": "6000.0"`. The development host runs a 6000.3 patch, and a
compatibility run on the lowest available 6000.0 LTS patch is part of the release checklist. A public
package should not claim a floor nobody has tested. Lowering it to 2022.3 is possible later, but only
behind an actual test run on that version.

### Platform scope

Supported: Android, iOS, Windows, macOS and Linux players, plus the Editor on those desktop systems.
Linux and Windows players are "supported by design, not verified on hardware" for 0.1.0.

**WebGL is excluded in 0.1.0.** `Application.persistentDataPath` there is an IndexedDB-backed emulation
that only flushes on request, and the package moves serialization and file IO onto the thread pool,
which WebGL does not have. The enforcement is one `ISaveLogger` warning from the `SaveService`
constructor, nothing else: the assembly definitions deliberately do **not** exclude WebGL, because
excluding it would turn a platform switch into a wall of `CS0246` errors in the game's own code.
`ISaveStorage` is the seam a WebGL backend would plug into later.

Consoles, tvOS and visionOS are untested and nothing is claimed about them.

### The package never manages OS or Steam backup configuration

The package does not edit an Android manifest, does not set an iOS no-backup flag, and does not
configure Steam Auto-Cloud. It documents all three and leaves the choice to the app.

Backup policy is an app-level decision with real trade-offs in both directions: leaving Android Auto
Backup and iCloud device backup on keeps guest and `LocalOnly` progress across a reinstall, at the cost
of a stale `profile.json` coming back. That stale-metadata case is handled safely by write-id
reconcile (it becomes a conflict or a take-cloud, never a silent overwrite) and is covered by
reinstall scenario tests. Silently changing an app's backup behaviour from inside a package would be a
surprise with legal and store-listing implications.

The Steam Auto-Cloud guidance is the same shape: it is documented, including the rule that Steam Cloud
and a cloud provider must never both manage the same slot, because two sync layers cannot both enforce
write-id provenance.

## Alternatives considered

### `PlayerPrefs`

Rejected. It is a single global key-value store with no account scoping, no atomicity, platform-
specific size limits (notably a 1 MB ceiling on some platforms), and no way to quarantine a corrupt
value — a failed parse simply yields the default. The previous system used it and hit every one of
those. It is also included in OS backups with no per-key control.

### Timestamp last-writer-wins

Rejected. Device clocks drift and can be changed by the player; an OS backup restored onto a second
device clones both the device id and the clock. "Newer wins" then silently discards the better copy,
and the failure is invisible until a player complains. Write ids answer a factual question
(*is this my write?*) instead of an unreliable comparative one.

### One snapshot blob for the whole game

Rejected. A single value means every write re-serializes and re-uploads everything, one conflict loses
the whole game state rather than one domain, and a corrupt blob takes everything with it. It also
makes per-domain schema versions impossible. The previous system's largest data class was effectively
this, and it was the worst part of it. The counter-rule is documented granularity: one slot per domain,
split on differing sync mode, scope, write frequency, conflict policy or size.

Snapshot-shaped **providers** (for example a platform "saved games" API) are not excluded by the
capability model; 0.1.0 simply does not implement one.

### A commercial save asset

Rejected for this use. The mature options concentrate on local serialization convenience —
encryption, binary formats, inspector-driven setup, auto-saving MonoBehaviour state. None of them owns
the part that actually caused the production incidents: account-scoped reconcile against a backend
with write-id provenance, a strict flush contract, and refusal semantics that converge. Layering that
on top of a third-party serializer would mean owning the hard half anyway, without control over the
on-disk format or the write sequence.

Encryption is also explicitly a non-goal: a key that ships in the binary is obfuscation, not security,
and it would make the corruption-recovery story worse.

### Injectable `IConflictPolicy`

Rejected. One default implementation plus a per-slot virtual already covers every real case. A second
extension point for the same decision is a fake abstraction that makes the policy harder to find.

## Consequences

**Positive**

- Cross-account data loss is structurally impossible for local data, not merely guarded against.
- Conflict decisions are a pure function with a row-per-case test suite, so the riskiest logic is the
  most cheaply verified part of the package.
- A game can adopt the package with no backend at all (`NullCloudSaveProvider`) and add the cloud later
  by changing one constructor argument and one slot's `SyncMode`.
- Losing data to a conflict always leaves a recoverable copy on the device.

**Negative / accepted costs**

- Slots load eagerly at boot and on every profile activation. A large, rarely used payload sits in
  memory for the whole session. The documented answer is slot granularity; lazy slots would be a design
  change.
- No "local only, just this once" escape hatch for a `CloudSync` slot.
- Keeping other accounts' directories on a shared device uses disk and keeps data that a
  privacy-minded product may prefer to delete. The delete APIs exist; the default is to keep.
- The reconcile table is genuinely intricate. It is mitigated by being pure, ordered, and documented in
  one place — but it is not simple, and a contributor must read the table before touching the restore
  flow.
