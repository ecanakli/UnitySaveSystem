using System;
using System.Collections.Generic;

namespace Ecanakli.SaveSystem
{
    /// <summary>Classified cloud read for one slot.</summary>
    internal enum CloudState
    {
        Missing = 0,
        Found = 1,

        /// <summary>Read failed after retries.</summary>
        ReadFailed = 2,

        /// <summary>Unparseable, checksum mismatch when verifiable, or upgrade/ToObject/Normalize threw at apply.</summary>
        Corrupt = 3,

        /// <summary>Envelope format or schema newer than this build.</summary>
        TooNew = 4,
    }

    /// <summary>What the restore apply and persist phases do for one slot.</summary>
    internal enum ReconcileAction
    {
        /// <summary>Nothing is applied to data.</summary>
        NoOp = 0,

        /// <summary>Local data stays.</summary>
        KeepLocal = 1,

        /// <summary>Materialize cloud data; sync state becomes synced to the cloud value and NeedsCloudRecovery clears.</summary>
        TakeCloud = 2,

        /// <summary>Call ResolveConflict, then ReconcileDecider.ApplyConflictResolution.</summary>
        Conflict = 3,

        /// <summary>Delete the cloud value with expectedVersion = cloud version in the persist phase, then ApplyDeleteResult.</summary>
        DeleteCloud = 4,

        /// <summary>Keep local and upload conditionally over the corrupt cloud value.</summary>
        RepairUpload = 5,

        /// <summary>CloudReadOnly mirror reset to default and sync state cleared because the server has no value.</summary>
        ResetToDefault = 6,

        /// <summary>Apply the merged data from ResolveConflict.</summary>
        Merge = 7,
    }

    /// <summary>Table row that produced a decision; for tests and diagnostics.</summary>
    internal enum ReconcileRule
    {
        LocalIoError = 0,
        LocalSchemaTooNew = 1,
        Row1ReadFailed = 2,
        Row2CloudTooNew = 3,
        Row3CorruptCloud = 4,
        Row4TombstoneCloudMissing = 5,
        Row5TombstoneDelete = 6,
        Row6CloudMissingLocalPresent = 7,
        Row7CloudMissingLocalAbsent = 8,
        Row8OwnWriteLanded = 9,
        Row9LocalNeedsCloud = 10,
        Row10CloudMovedOn = 11,
        Row11Conflict = 12,
        Row12ProtectLocalData = 13,
        LocalNormalizeFailedNoCloudValue = 14,
        ReadOnlyReadFailed = 15,
        ReadOnlyTooNew = 16,
        ReadOnlyCorrupt = 17,
        ReadOnlyMissing = 18,
        ReadOnlyChanged = 19,
        ReadOnlyUnchanged = 20,
    }

    /// <summary>Side effects the caller performs for a decision.</summary>
    [Flags]
    internal enum ReconcileFlags
    {
        None = 0,

        /// <summary>Mark local and cloud dirty and hand the slot to the scheduler.</summary>
        QueueUpload = 1 << 0,

        /// <summary>Clear the PendingDelete tombstone.</summary>
        ClearTombstone = 1 << 1,

        /// <summary>Set ReconciledThisEpoch and clear SuspendedUntilReconcile.</summary>
        ReconciledThisEpoch = 1 << 2,

        /// <summary>Raise UpdateRequired(Cloud) once per slot per epoch and set RequiresAppUpdate.</summary>
        RaiseUpdateRequired = 1 << 3,

        /// <summary>Write raw bytes to {key}.cloud-corrupt-{UTC}.json unless already backed up; raise CloudPayloadCorrupt.</summary>
        BackUpCorruptCloud = 1 << 4,

        /// <summary>Quarantine the local primary before applying; raise LocalNormalizeFailedReplacedByCloud for CloudSync.</summary>
        QuarantineLocalFirst = 1 << 5,

        /// <summary>LastSynced{WriteId, ProviderVersion, Revision} := cloud values; PendingWriteId := null.</summary>
        PromotePendingWriteId = 1 << 6,

        /// <summary>LastSyncedWriteId := cloud writeId so the next upload supersedes that exact write.</summary>
        AdoptCloudWriteId = 1 << 7,

        /// <summary>LastSyncedProviderVersion := cloud version (conditional next upload).</summary>
        AdoptCloudProviderVersion = 1 << 8,

        /// <summary>Write {key}.conflict.json with both sides and the resolution before the swap.</summary>
        WriteConflictFile = 1 << 9,

        /// <summary>Clear ReconciledThisEpoch: the slot was not reconciled, so uploads wait for the next successful one.</summary>
        NeedsReconcile = 1 << 10,
    }

    /// <summary>Decision facts for one slot; the caller fills every field.</summary>
    internal struct ReconcileInput
    {
        /// <summary>CloudSync or CloudReadOnly.</summary>
        public SyncMode Mode;

        public CloudState Cloud;

        /// <summary>Envelope writeId of a Found CloudSync value.</summary>
        public string CloudWriteId;

        public string CloudProviderVersion;

        /// <summary>Raw byte hash; CloudReadOnly compares it when the provider has no version.</summary>
        public string CloudContentHash;

        /// <summary>IsEmpty of the Found cloud payload.</summary>
        public bool CloudIsEmpty;

        public LocalPresence LocalPresence;

        public SlotFailure LocalFailure;

        /// <summary>In-memory local data is not empty.</summary>
        public bool LocalHasContent;

        /// <summary>Local revision is ahead of LastSyncedRevision or not yet uploaded.</summary>
        public bool LocalDirty;

        public long LocalRevision;

        public bool NeedsCloudRecovery;

        public long LastSyncedRevision;

        public string LastSyncedWriteId;

        public string LastSyncedProviderVersion;

        /// <summary>Newest unconfirmed write id.</summary>
        public string PendingWriteId;

        /// <summary>Every unconfirmed write id, newest first; null falls back to PendingWriteId alone.</summary>
        public IReadOnlyList<string> PendingWriteIds;

        public bool TombstonePending;

        public string TombstoneDeletedWriteId;
    }

    /// <summary>Result of ReconcileDecider; immutable.</summary>
    internal readonly struct ReconcileDecision
    {
        public ReconcileDecision(
            ReconcileAction action,
            SlotRestoreOutcome outcome,
            SlotRestoreFailure failure,
            ReconcileRule rule,
            ReconcileFlags flags)
        {
            Action = action;
            Outcome = outcome;
            Failure = failure;
            Rule = rule;
            Flags = flags;
        }

        public ReconcileAction Action { get; }

        /// <summary>Provisional for Conflict (LocalKept) and DeleteCloud (NoCloudData) until the follow-up helper runs.</summary>
        public SlotRestoreOutcome Outcome { get; }

        public SlotRestoreFailure Failure { get; }

        public ReconcileRule Rule { get; }

        public ReconcileFlags Flags { get; }

        public bool QueueUpload => Has(ReconcileFlags.QueueUpload);

        public bool ClearTombstone => Has(ReconcileFlags.ClearTombstone);

        /// <summary>For DeleteCloud, applies only after the delete succeeds.</summary>
        public bool ReconciledThisEpoch => Has(ReconcileFlags.ReconciledThisEpoch);

        /// <summary>Revokes a ReconciledThisEpoch granted earlier in the same epoch.</summary>
        public bool NeedsReconcile => Has(ReconcileFlags.NeedsReconcile);

        public bool RaiseUpdateRequired => Has(ReconcileFlags.RaiseUpdateRequired);

        public bool BackUpCorruptCloud => Has(ReconcileFlags.BackUpCorruptCloud);

        public bool QuarantineLocalFirst => Has(ReconcileFlags.QuarantineLocalFirst);

        public bool PromotePendingWriteId => Has(ReconcileFlags.PromotePendingWriteId);

        public bool AdoptCloudWriteId => Has(ReconcileFlags.AdoptCloudWriteId);

        public bool AdoptCloudProviderVersion => Has(ReconcileFlags.AdoptCloudProviderVersion);

        public bool WriteConflictFile => Has(ReconcileFlags.WriteConflictFile);

        public bool Has(ReconcileFlags flag)
        {
            return (Flags & flag) == flag;
        }

        public override string ToString()
        {
            return Rule + ": " + Action + " -> " + Outcome + (Failure != SlotRestoreFailure.None ? "(" + Failure + ")" : string.Empty) + " [" + Flags + "]";
        }
    }

    /// <summary>Pure reconcile table (04a A3 with 04 R1). No side effects, no logging, no Unity API.</summary>
    internal static class ReconcileDecider
    {
        private const ReconcileFlags KeepLocalOverCloud =
            ReconcileFlags.QueueUpload | ReconcileFlags.AdoptCloudWriteId | ReconcileFlags.AdoptCloudProviderVersion | ReconcileFlags.ReconciledThisEpoch;

        /// <summary>Decides one slot; first match wins. Throws ArgumentException for LocalOnly.</summary>
        public static ReconcileDecision Decide(in ReconcileInput input)
        {
            switch (input.Mode)
            {
                case SyncMode.CloudSync:
                    return DecideCloudSync(in input);
                case SyncMode.CloudReadOnly:
                    return DecideCloudReadOnly(in input);
                default:
                    throw new ArgumentException("LocalOnly slots are never reconciled.", nameof(input));
            }
        }

        /// <summary>Row 12: a TakeCloud of an empty cloud payload over local content keeps local and uploads.</summary>
        public static ReconcileDecision PostCheck(in ReconcileInput input, in ReconcileDecision decision)
        {
            if (input.Mode != SyncMode.CloudSync || decision.Action != ReconcileAction.TakeCloud)
            {
                return decision;
            }

            if (!input.CloudIsEmpty || !HasUsableLocalContent(in input))
            {
                return decision;
            }

            // Carry tombstone and conflict-file effects of the original decision
            ReconcileFlags carried = decision.Flags & (ReconcileFlags.ClearTombstone | ReconcileFlags.WriteConflictFile);
            return new ReconcileDecision(
                ReconcileAction.KeepLocal,
                SlotRestoreOutcome.SkippedToProtectLocalData,
                SlotRestoreFailure.None,
                ReconcileRule.Row12ProtectLocalData,
                KeepLocalOverCloud | carried);
        }

        /// <summary>Turns a Conflict decision into its final form for the ResolveConflict result, including row 12.</summary>
        public static ReconcileDecision ApplyConflictResolution(in ReconcileInput input, in ReconcileDecision decision, ConflictResolutionKind resolution)
        {
            if (decision.Action != ReconcileAction.Conflict)
            {
                throw new InvalidOperationException("ApplyConflictResolution requires a Conflict decision, got " + decision.Action + ".");
            }

            ReconcileFlags carried = (decision.Flags & ReconcileFlags.ClearTombstone) | ReconcileFlags.WriteConflictFile;
            switch (resolution)
            {
                case ConflictResolutionKind.KeepLocal:
                    return new ReconcileDecision(
                        ReconcileAction.KeepLocal, SlotRestoreOutcome.LocalKept, SlotRestoreFailure.None, ReconcileRule.Row11Conflict, KeepLocalOverCloud | carried);
                case ConflictResolutionKind.Merged:
                    return new ReconcileDecision(
                        ReconcileAction.Merge, SlotRestoreOutcome.Merged, SlotRestoreFailure.None, ReconcileRule.Row11Conflict, KeepLocalOverCloud | carried);
                case ConflictResolutionKind.TakeCloud:
                    var takeCloud = new ReconcileDecision(
                        ReconcileAction.TakeCloud, SlotRestoreOutcome.Restored, SlotRestoreFailure.None, ReconcileRule.Row11Conflict,
                        ReconcileFlags.ReconciledThisEpoch | carried);
                    return PostCheck(in input, in takeCloud);
                default:
                    throw new ArgumentOutOfRangeException(nameof(resolution), resolution, "Unknown conflict resolution.");
            }
        }

        /// <summary>Row 5 persist result: success clears the tombstone and reconciles; failure is Failed(DeletePending) without RTE.</summary>
        public static ReconcileDecision ApplyDeleteResult(in ReconcileDecision decision, bool succeeded)
        {
            if (decision.Action != ReconcileAction.DeleteCloud)
            {
                throw new InvalidOperationException("ApplyDeleteResult requires a DeleteCloud decision, got " + decision.Action + ".");
            }

            return succeeded
                ? new ReconcileDecision(
                    ReconcileAction.NoOp, SlotRestoreOutcome.NoCloudData, SlotRestoreFailure.None, ReconcileRule.Row5TombstoneDelete,
                    ReconcileFlags.ClearTombstone | ReconcileFlags.ReconciledThisEpoch)
                : new ReconcileDecision(
                    ReconcileAction.NoOp, SlotRestoreOutcome.Failed, SlotRestoreFailure.DeletePending, ReconcileRule.Row5TombstoneDelete,
                    ReconcileFlags.None);
        }

        private static ReconcileDecision DecideCloudSync(in ReconcileInput input)
        {
            if (TryLocalFailureGuard(in input, out ReconcileDecision guard))
            {
                return guard;
            }

            switch (input.Cloud)
            {
                case CloudState.ReadFailed:
                    // Row 1
                    return Make(ReconcileAction.KeepLocal, SlotRestoreOutcome.Failed, ReconcileRule.Row1ReadFailed, ReconcileFlags.None, SlotRestoreFailure.ReadFailed);
                case CloudState.TooNew:
                    // Row 2: the slot was not reconciled, so an earlier grant of this epoch is revoked (row 1 keeps it: a read failure is not a refusal to sync)
                    return Make(ReconcileAction.KeepLocal, SlotRestoreOutcome.SkippedSchemaTooNew, ReconcileRule.Row2CloudTooNew,
                        ReconcileFlags.RaiseUpdateRequired | ReconcileFlags.NeedsReconcile);
                case CloudState.Corrupt:
                    return DecideCorruptCloud(in input);
                case CloudState.Missing:
                    return DecideCloudMissing(in input);
                case CloudState.Found:
                    return DecideCloudFound(in input);
                default:
                    throw new ArgumentOutOfRangeException(nameof(input), input.Cloud, "Unknown cloud state.");
            }
        }

        // Row 3
        private static ReconcileDecision DecideCorruptCloud(in ReconcileInput input)
        {
            if (input.LocalFailure == SlotFailure.NormalizeFailed)
            {
                // Neither side is usable; keep the local file for a fixed build
                return Make(ReconcileAction.NoOp, SlotRestoreOutcome.Failed, ReconcileRule.Row3CorruptCloud,
                    ReconcileFlags.BackUpCorruptCloud, SlotRestoreFailure.LocalNormalizeFailed);
            }

            if (HasUsableLocalContent(in input))
            {
                return Make(ReconcileAction.RepairUpload, SlotRestoreOutcome.RepairingCorruptCloud, ReconcileRule.Row3CorruptCloud,
                    ReconcileFlags.BackUpCorruptCloud | ReconcileFlags.QueueUpload | ReconcileFlags.AdoptCloudProviderVersion | ReconcileFlags.ReconciledThisEpoch);
            }

            return Make(ReconcileAction.NoOp, SlotRestoreOutcome.CorruptCloudIgnored, ReconcileRule.Row3CorruptCloud,
                ReconcileFlags.BackUpCorruptCloud | ReconcileFlags.ReconciledThisEpoch);
        }

        private static ReconcileDecision DecideCloudMissing(in ReconcileInput input)
        {
            if (input.TombstonePending)
            {
                // Row 4
                return Make(ReconcileAction.NoOp, SlotRestoreOutcome.NoCloudData, ReconcileRule.Row4TombstoneCloudMissing,
                    ReconcileFlags.ClearTombstone | ReconcileFlags.ReconciledThisEpoch);
            }

            if (input.LocalFailure == SlotFailure.NormalizeFailed)
            {
                // Nothing to recover from; stays Failed until a fixed build
                return Make(ReconcileAction.NoOp, SlotRestoreOutcome.Failed, ReconcileRule.LocalNormalizeFailedNoCloudValue,
                    ReconcileFlags.None, SlotRestoreFailure.LocalNormalizeFailed);
            }

            if (input.LocalPresence == LocalPresence.Present)
            {
                // Row 6
                return Make(ReconcileAction.KeepLocal, SlotRestoreOutcome.NoCloudData, ReconcileRule.Row6CloudMissingLocalPresent,
                    ReconcileFlags.QueueUpload | ReconcileFlags.ReconciledThisEpoch);
            }

            // Row 7 (Absent, or Undetermined after a corrupt reset)
            return Make(ReconcileAction.NoOp, SlotRestoreOutcome.NoCloudData, ReconcileRule.Row7CloudMissingLocalAbsent, ReconcileFlags.ReconciledThisEpoch);
        }

        private static ReconcileDecision DecideCloudFound(in ReconcileInput input)
        {
            ReconcileFlags carried = ReconcileFlags.None;
            if (input.TombstonePending)
            {
                // Row 5 (R1): delete only our own tombstoned write
                if (input.CloudWriteId != null && string.Equals(input.CloudWriteId, input.TombstoneDeletedWriteId, StringComparison.Ordinal))
                {
                    return Make(ReconcileAction.DeleteCloud, SlotRestoreOutcome.NoCloudData, ReconcileRule.Row5TombstoneDelete,
                        ReconcileFlags.ClearTombstone | ReconcileFlags.ReconciledThisEpoch);
                }

                // Another device wrote after the delete; continue at row 8
                carried = ReconcileFlags.ClearTombstone;
            }

            if (input.LocalFailure == SlotFailure.NormalizeFailed)
            {
                // Row 9 clause checked before row 8 so a Failed slot never reports UpToDate
                return FinishTakeCloud(in input, ReconcileRule.Row9LocalNeedsCloud, carried | ReconcileFlags.QuarantineLocalFirst);
            }

            // Row 9 before row 8 (R8): our own last write in the cloud must still restore lost or reset local data
            if (input.LocalPresence == LocalPresence.Absent || input.NeedsCloudRecovery || input.LocalRevision < input.LastSyncedRevision)
            {
                return FinishTakeCloud(in input, ReconcileRule.Row9LocalNeedsCloud, carried);
            }

            // Row 8: any unconfirmed write id matches, not just the newest
            bool matchesPending = IsPendingWriteId(in input, input.CloudWriteId);
            bool matchesSynced = input.CloudWriteId != null && string.Equals(input.CloudWriteId, input.LastSyncedWriteId, StringComparison.Ordinal);
            if (matchesPending || matchesSynced)
            {
                ReconcileFlags landed = carried | ReconcileFlags.ReconciledThisEpoch |
                                        (matchesPending ? ReconcileFlags.PromotePendingWriteId : ReconcileFlags.AdoptCloudProviderVersion);
                return input.LocalDirty
                    ? Make(ReconcileAction.KeepLocal, SlotRestoreOutcome.LocalKept, ReconcileRule.Row8OwnWriteLanded, landed | ReconcileFlags.QueueUpload)
                    : Make(ReconcileAction.NoOp, SlotRestoreOutcome.UpToDate, ReconcileRule.Row8OwnWriteLanded, landed);
            }

            // Row 10
            if (!input.LocalDirty && input.LastSyncedWriteId != null)
            {
                return FinishTakeCloud(in input, ReconcileRule.Row10CloudMovedOn, carried);
            }

            // Row 11
            return Make(ReconcileAction.Conflict, SlotRestoreOutcome.LocalKept, ReconcileRule.Row11Conflict, carried | ReconcileFlags.ReconciledThisEpoch);
        }

        private static ReconcileDecision FinishTakeCloud(in ReconcileInput input, ReconcileRule rule, ReconcileFlags flags)
        {
            var takeCloud = Make(ReconcileAction.TakeCloud, SlotRestoreOutcome.Restored, rule, flags | ReconcileFlags.ReconciledThisEpoch);
            return PostCheck(in input, in takeCloud);
        }

        private static ReconcileDecision DecideCloudReadOnly(in ReconcileInput input)
        {
            if (TryLocalFailureGuard(in input, out ReconcileDecision guard))
            {
                return guard;
            }

            bool localFailed = input.LocalFailure == SlotFailure.NormalizeFailed;
            switch (input.Cloud)
            {
                case CloudState.ReadFailed:
                    return Make(ReconcileAction.KeepLocal, SlotRestoreOutcome.Failed, ReconcileRule.ReadOnlyReadFailed, ReconcileFlags.None, SlotRestoreFailure.ReadFailed);
                case CloudState.TooNew:
                    return Make(ReconcileAction.KeepLocal, SlotRestoreOutcome.SkippedSchemaTooNew, ReconcileRule.ReadOnlyTooNew, ReconcileFlags.RaiseUpdateRequired);
                case CloudState.Corrupt:
                    return localFailed
                        ? Make(ReconcileAction.NoOp, SlotRestoreOutcome.Failed, ReconcileRule.ReadOnlyCorrupt,
                            ReconcileFlags.BackUpCorruptCloud, SlotRestoreFailure.LocalNormalizeFailed)
                        : Make(ReconcileAction.NoOp, SlotRestoreOutcome.CorruptCloudIgnored, ReconcileRule.ReadOnlyCorrupt,
                            ReconcileFlags.BackUpCorruptCloud | ReconcileFlags.ReconciledThisEpoch);
                case CloudState.Missing:
                    if (input.LocalPresence == LocalPresence.Absent && !localFailed)
                    {
                        return Make(ReconcileAction.NoOp, SlotRestoreOutcome.NoCloudData, ReconcileRule.ReadOnlyMissing, ReconcileFlags.ReconciledThisEpoch);
                    }

                    return Make(ReconcileAction.ResetToDefault, SlotRestoreOutcome.NoCloudData, ReconcileRule.ReadOnlyMissing,
                        ReconcileFlags.ReconciledThisEpoch | (localFailed ? ReconcileFlags.QuarantineLocalFirst : ReconcileFlags.None));
                case CloudState.Found:
                    if (localFailed)
                    {
                        return Make(ReconcileAction.TakeCloud, SlotRestoreOutcome.Restored, ReconcileRule.ReadOnlyChanged,
                            ReconcileFlags.QuarantineLocalFirst | ReconcileFlags.ReconciledThisEpoch);
                    }

                    string cloudVersion = input.CloudProviderVersion ?? input.CloudContentHash;
                    bool unchanged = cloudVersion != null &&
                                     input.LocalPresence != LocalPresence.Absent &&
                                     !input.NeedsCloudRecovery &&
                                     string.Equals(cloudVersion, input.LastSyncedProviderVersion, StringComparison.Ordinal);
                    return unchanged
                        ? Make(ReconcileAction.NoOp, SlotRestoreOutcome.UpToDate, ReconcileRule.ReadOnlyUnchanged, ReconcileFlags.ReconciledThisEpoch)
                        : Make(ReconcileAction.TakeCloud, SlotRestoreOutcome.Restored, ReconcileRule.ReadOnlyChanged, ReconcileFlags.ReconciledThisEpoch);
                default:
                    throw new ArgumentOutOfRangeException(nameof(input), input.Cloud, "Unknown cloud state.");
            }
        }

        // Local IoError and SchemaTooNew slots are normally not fetched; decided defensively here
        private static bool TryLocalFailureGuard(in ReconcileInput input, out ReconcileDecision decision)
        {
            switch (input.LocalFailure)
            {
                case SlotFailure.IoError:
                    decision = Make(ReconcileAction.KeepLocal, SlotRestoreOutcome.Failed, ReconcileRule.LocalIoError, ReconcileFlags.None, SlotRestoreFailure.LocalIoError);
                    return true;
                case SlotFailure.SchemaTooNew:
                    decision = Make(ReconcileAction.KeepLocal, SlotRestoreOutcome.SkippedSchemaTooNew, ReconcileRule.LocalSchemaTooNew, ReconcileFlags.None,
                        SlotRestoreFailure.LocalSchemaTooNew);
                    return true;
                default:
                    decision = default;
                    return false;
            }
        }

        private static bool HasUsableLocalContent(in ReconcileInput input)
        {
            return input.LocalHasContent && input.LocalFailure == SlotFailure.None;
        }

        // An upload whose response was lost can be older than the newest unconfirmed id
        private static bool IsPendingWriteId(in ReconcileInput input, string writeId)
        {
            if (writeId == null)
            {
                return false;
            }

            if (string.Equals(writeId, input.PendingWriteId, StringComparison.Ordinal))
            {
                return true;
            }

            IReadOnlyList<string> pending = input.PendingWriteIds;
            for (int i = 0; pending != null && i < pending.Count; i++)
            {
                if (string.Equals(writeId, pending[i], StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static ReconcileDecision Make(
            ReconcileAction action,
            SlotRestoreOutcome outcome,
            ReconcileRule rule,
            ReconcileFlags flags,
            SlotRestoreFailure failure = SlotRestoreFailure.None)
        {
            return new ReconcileDecision(action, outcome, failure, rule, flags);
        }
    }
}
