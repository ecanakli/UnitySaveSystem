# ADR-003: Sync safety invariants

- **Status:** Accepted, implemented and test-enforced in 0.1.0
- **Related:** [Architecture section 6](../Architecture.md#6-the-reconcile-decision-table),
  [Sync and recovery](../../Sync-And-Recovery.md)

## Context

A cloud-synced save system fails in a particular way: not with an exception, but with a player opening
the game and finding their progress gone. By the time anyone notices, the data is already overwritten
on the server and the device that held the good copy has been reconciled to the bad one.

The reconcile table ([Architecture section 6](../Architecture.md#6-the-reconcile-decision-table))
decides case by case. But a table can be extended wrongly. What is needed is a smaller set of rules
that constrain *any* future change to it — properties that must hold no matter which row is added.

This ADR states five such invariants. Each one comes from an observed failure, each is stated so that
violating it is a visible code change rather than an accident, and each is bound to named tests.

## Decision

### 1. Never force-overwrite the cloud to get past a refusal

There is no public API, option, flag or overload that pushes local data over a cloud value the package
considers unsafe to replace. Refusals are resolved by converging, not by bypassing.

**Why.** A previous system had exactly such an escape hatch, added because a device had become stuck in
a refusal loop and a "just push it" path looked like the pragmatic fix. It destroyed another device's
data, and it was removed. The lesson is that the moment a force path exists, it gets called from a
support tool, then from a retry handler, then from normal code.

The obligation this creates is the **liveness invariant**: every refusal must clear within the next
restore-and-upload cycle. Otherwise "no force path" would just mean "permanently stuck". The two
refusals that cannot converge without a new build — a payload written by a newer `fmt`/`schema`, and a
`LocalOnly` slot whose `Normalize` throws — both raise a signal (`UpdateRequired`, `SlotLoadIssue`) so
the game can tell the player to update rather than silently doing nothing.

**Tests**

- `SafetyInvariantTests.PublicApi_HasNoForceOrOverwriteMembers` — reflection over the core assembly's
  exported types, their externally visible members and those members' parameter names; any name
  containing `Force` or `Overwrite` fails the test.
- `SafetyInvariantTests.AfterConflictRefusal_NoUploadUntilReconciled`
- `LivenessTests.EveryRefusalPath_ConvergesWithinOneRestoreAndUploadCycle` — parameterized over the
  refusal paths (read failure, corrupt cloud, permanent upload failure, not reconciled, local IO error,
  tombstone pending, local write failure after the disk recovers).
- `LivenessTests.SchemaTooNew_OnlyNonConvergingRefusal_SignalsUpdateRequired`
- `LivenessTests.CorruptCloudRepair_ConflictFromOtherDeviceRepair_Reconciles`

### 2. The conflict loser is always backed up locally

Before any conflict resolution is applied, `{key}.conflict.json` is written with the resolution kind
and **both** sides. This holds for every outcome — keep local, take cloud, and a slot-supplied merge —
not only for the branch where local data is replaced.

**Why.** The original design wrote the conflict file only on `TakeCloud`, which reads as "back up the
loser" but is not: a merge can drop fields from either side, and `KeepLocal` discards the cloud copy
entirely. The invariant is only useful if it is unconditional, because the case where a developer is
sure nothing was lost is exactly the case that gets it wrong.

One generation is kept. The file is a support artefact and a last resort, not a version history.

**Tests**

- `SlotStoreTests.WriteConflictFile_HoldsResolutionLocalAndCloud_OneGeneration`
- `RestoreOperationTests.Conflict_BothSidesChanged_WritesConflictFileWithResolutionLocalAndCloud` —
  parameterized over every `ConflictResolutionKind`.
- `DefaultConflictPolicyTests.EmptyLocalSlot_CloudHasContent_TakesCloud_ConflictFileHoldsBothSides`
- `ReconcileScenarioTests.GuestClaim_OntoAccountWithCloudData_ResolvesConflict_BacksUpLoser`
- `ReconcileScenarioTests.Reinstall_StaleProfileJson_DirtyAtBackup_ResolvesConflict_BacksUpBothSides`
- `ReconcileScenarioTests.GuestClaim_EmptyGuestSlot_TakesCloud_NoConflictFile` — the negative case: no
  real conflict, no file.

The corrupt-cloud equivalent is the same idea applied to bytes that cannot be parsed:
`RestoreOperationTests.CorruptCloudValue_BackedUpAsCloudCorruptFile_ThenConditionalRepairUpload`.

### 3. Metadata never claims a write that did not happen

Sync metadata describes reality, not intent.

- `LastSyncedRevision`, `LastSyncedWriteId` and `LastSyncedProviderVersion` advance **only** after the
  provider confirms the write.
- `PendingWriteId` is persisted to disk **before** the request is sent, never after.
- `HadContent` is set only after the local write actually lands.

**Why.** Optimistic metadata is worse than no metadata. If `LastSyncedWriteId` is set before the
response arrives and the write then fails, the next reconcile sees "the cloud holds my last write",
takes the cloud copy, and silently discards local progress that was never uploaded. The ordering
around `PendingWriteId` is the mirror image: writing it first is what makes a *lost response*
recoverable, because the next restore recognises the write that did land instead of treating it as a
foreign change (table row 8).

The same reasoning is why a `profile.json` write failure blocks the upload it was supposed to describe:
an upload whose pending id could not be recorded is skipped rather than sent blind.

**Tests**

- `SafetyInvariantTests.PendingWriteId_PersistedBeforeTheHeldProviderWriteCompletes` — the provider
  write is held open and the on-disk metadata is inspected mid-flight.
- `SafetyInvariantTests.PendingWriteIds_OfABatch_OnDiskWhenTheProviderAppliesTheWrite`
- `SafetyInvariantTests.ProviderWriteError_DoesNotAdvanceLastSyncedState` — parameterized over
  `CloudErrorKind`.
- `SafetyInvariantTests.ProviderWriteThrows_DoesNotAdvanceLastSyncedState`
- `SafetyInvariantTests.LocalWriteFailure_DoesNotSetHadContentOrClearDirty`
- `LocalWriteFailureTests.ProfileJsonPendingWriteIdPersistFails_UploadSkippedLocalWriteFailed` — an
  upload whose pending write id could not be recorded is skipped rather than sent.

### 4. Unknown, corrupt or newer metadata falls back to safe behaviour, never destructive behaviour

When the package cannot trust what it reads, it chooses the option whose worst outcome is a conflict or
a redundant upload — never a deletion and never a `TakeCloud` over dirty local data.

Concretely:

- A corrupt or newer-than-known `profile.json` is quarantined and replaced by a fallback where
  `HadContent` is `true` (whose only effect is blocking an empty upload) and `LastSyncedWriteId` is
  `null` (whose worst case is a conflict).
- A cloud envelope with no `writeId` is treated as unknown provenance: it does not win over dirty local
  data.
- A tombstone is replayed only when the cloud value's write id matches the tombstoned one. Another
  device's newer write clears the tombstone instead of being deleted.
- A payload with a newer `fmt` or `schema` is never written back, never downgraded and never parsed
  optimistically.
- A local read that fails with an IO error is `Failed(IoError)`, not corruption: nothing is quarantined
  and nothing is written.

**Why.** Every one of these is a case where the "helpful" interpretation is destructive. The
reinstall case makes it concrete: an OS backup restores a stale `profile.json` claiming a write that
the cloud has long since superseded. Trusting it means overwriting the good cloud copy with old local
data; distrusting it means one conflict, with both sides on disk.

**Tests**

- `SafetyInvariantTests.CorruptProfileJson_FallsBackToHadContentTrueAndNullSyncedWriteId`
- `SafetyInvariantTests.NewerProfileJsonVersion_UsesSafeFallback_NeverTakesCloudOverDirtyLocal`
- `SafetyInvariantTests.CloudEnvelopeWithoutWriteId_IsUnknownProvenance_NotTakeCloudOverDirty`
- `SafetyInvariantTests.TombstoneForOlderWrite_CloudHasNewerWrite_DoesNotDelete`
- `SafetyInvariantTests.TombstoneForSameWrite_DeletesWithExpectedVersion` — the positive counterpart.
- `SafetyInvariantTests.ConditionalWriteSupported_EveryWriteOverAKnownCloudValueCarriesExpectedVersion`
- `SafetyInvariantTests.FirstUploadOverMissingCloudValue_ValueCreatedElsewhere_IsNotOverwritten` — the
  first upload of a never-synced key re-reads before writing, because the backend has no create-only
  write.
- `SyncStateStoreTests.Load_CorruptPrimary_SafeFallbackAndQuarantine` and
  `SyncStateStoreTests.Load_KnownVersionMalformed_IsCorruptWithSafeFallback`
- `SlotStoreTests.Load_NoValidCopy_CorruptResetWithNeedsCloudRecovery` and
  `SlotStoreTests.Load_PrimaryReadIoError_NoQuarantineNoWrite`
- `ReconcileScenarioTests.Reinstall_StalePendingWriteIdSuperseded_IsConflictNotSilentOverwrite`

### 5. Query methods never persist anything

`ProbeLocalPresence`, `GetLocalProfiles`, `Read`, `IsInitialized`, `IsReady` and `WhenReadyAsync` do not
write, rename, quarantine, promote or repair. A question must not change the answer.

**Why.** The temptation is real: while probing for a file, the code is already in a position to notice
a stray `.tmp` and promote it, or to quarantine a corrupt primary. Doing so means a diagnostic call
made from a menu, a debug overlay or an analytics hook silently mutates save state — at an arbitrary
moment, outside the operation gate, possibly before initialization. Recovery belongs to the load path,
which runs under known preconditions and reports what it did.

`ProbeLocalPresence` therefore returns `Undetermined` for a corrupt or unreadable primary rather than
resolving it.

**Tests**

- `SafetyInvariantTests.ProbeLocalPresenceAndGetLocalProfiles_PerformNoStorageWrites`
- `ProbeTests.QueryMembers_ZeroStorageWrites`
- `ProbeTests.ProbeLocalPresence_TmpOnly_DoesNotPromote`
- `ProbeTests.ProbeLocalPresence_CorruptTmpOnly_IsAbsentAndPromotesNothing`
- `ProbeTests.ProbeLocalPresence_BeforeInitialize_ReportsPresence`
- `SlotStoreTests.PeekHeader_ValidPresent_CorruptUndetermined_NeverRepairs`

## Consequences

**Positive**

- Each invariant is a one-line rule that a reviewer can hold in their head while reading a change to
  the reconcile table. "Which invariant does this break?" is a cheaper question than re-deriving the
  table's correctness.
- The failures these prevent are exactly the ones that are invisible in QA and catastrophic in
  production.
- Because invariants 1 and 4 forbid destructive shortcuts, the package converges rather than
  short-circuits — which forced the liveness work that makes refusals temporary.

**Negative / accepted costs**

- No escape hatch. A support engineer facing a genuinely stuck device cannot force a push from the
  public API; the resolution has to come through restore and reconcile, or through deleting data
  deliberately.
- Distrusting metadata produces conflicts that a more optimistic system would resolve silently. Some of
  those conflicts are "false": the two sides really were the same. The cost is a conflict file and one
  extra upload.
- Invariant 3 costs an extra metadata write before each upload batch, and an upload is skipped entirely
  when that write fails.
- Invariant 5 means `ProbeLocalPresence` can answer `Undetermined`, and callers must handle that third
  state instead of a clean boolean.

**Reviewing a change**

Any change to `ReconcileDecider`, `CloudUploader`, `SlotStore`, `SyncStateStore` or `RestoreOperation`
should be checked against all five invariants before it is checked against the table. If a new row
needs an exception to one of them, that is an ADR amendment, not a code review comment.
