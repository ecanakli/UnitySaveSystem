using System;
using NUnit.Framework;

namespace Ecanakli.SaveSystem.Tests
{
    /// <summary>Pure reconcile table: CloudSync rows 1-12 with R1, follow-up helpers and the CloudReadOnly table.</summary>
    [TestFixture]
    public sealed class ReconcileDeciderTests
    {
        private const ReconcileFlags KeepLocalOverCloud =
            ReconcileFlags.QueueUpload | ReconcileFlags.AdoptCloudWriteId | ReconcileFlags.AdoptCloudProviderVersion | ReconcileFlags.ReconciledThisEpoch;

        private static readonly CloudState[] AllCloudStates =
        {
            CloudState.Missing, CloudState.Found, CloudState.ReadFailed, CloudState.Corrupt, CloudState.TooNew,
        };

        // Row 1

        [Test]
        public void Row1_ReadFailed_KeepsLocal_FailedReadFailed_NoRte()
        {
            ReconcileInput input = SyncInput();
            input.Cloud = CloudState.ReadFailed;

            AssertDecision(ReconcileDecider.Decide(input),
                ReconcileAction.KeepLocal, SlotRestoreOutcome.Failed, SlotRestoreFailure.ReadFailed, ReconcileRule.Row1ReadFailed, ReconcileFlags.None);
        }

        [Test]
        public void Row1_ReadFailed_WinsOverTombstoneAndLocalNormalizeFailed()
        {
            ReconcileInput input = SyncInput();
            input.Cloud = CloudState.ReadFailed;
            input.TombstonePending = true;
            input.TombstoneDeletedWriteId = "cloud-w";
            input.LocalFailure = SlotFailure.NormalizeFailed;

            AssertDecision(ReconcileDecider.Decide(input),
                ReconcileAction.KeepLocal, SlotRestoreOutcome.Failed, SlotRestoreFailure.ReadFailed, ReconcileRule.Row1ReadFailed, ReconcileFlags.None);
        }

        // Row 2

        [Test]
        public void Row2_CloudTooNew_KeepsLocal_RaisesUpdateRequired_RevokesReconciledThisEpoch()
        {
            ReconcileInput input = SyncInput();
            input.Cloud = CloudState.TooNew;

            AssertDecision(ReconcileDecider.Decide(input),
                ReconcileAction.KeepLocal, SlotRestoreOutcome.SkippedSchemaTooNew, SlotRestoreFailure.None, ReconcileRule.Row2CloudTooNew,
                ReconcileFlags.RaiseUpdateRequired | ReconcileFlags.NeedsReconcile);
        }

        // A read failure is not a refusal to sync: revoking here would stop uploads for a device that reconciled and then went offline
        [Test]
        public void Row1_ReadFailed_DoesNotRevokeReconciledThisEpoch()
        {
            ReconcileInput input = SyncInput();
            input.Cloud = CloudState.ReadFailed;

            ReconcileDecision decision = ReconcileDecider.Decide(input);

            Assert.That(decision.NeedsReconcile, Is.False, decision.ToString());
            Assert.That(decision.ReconciledThisEpoch, Is.False, decision.ToString());
        }

        // Row 3

        [Test]
        public void Row3_CorruptCloud_LocalHasContent_RepairUpload()
        {
            ReconcileInput input = SyncInput();
            input.Cloud = CloudState.Corrupt;

            AssertDecision(ReconcileDecider.Decide(input),
                ReconcileAction.RepairUpload, SlotRestoreOutcome.RepairingCorruptCloud, SlotRestoreFailure.None, ReconcileRule.Row3CorruptCloud,
                ReconcileFlags.BackUpCorruptCloud | ReconcileFlags.QueueUpload | ReconcileFlags.AdoptCloudProviderVersion | ReconcileFlags.ReconciledThisEpoch);
        }

        [Test]
        public void Row3_CorruptCloud_NoLocalContent_CorruptCloudIgnored()
        {
            ReconcileInput input = SyncInput();
            input.Cloud = CloudState.Corrupt;
            input.LocalHasContent = false;

            AssertDecision(ReconcileDecider.Decide(input),
                ReconcileAction.NoOp, SlotRestoreOutcome.CorruptCloudIgnored, SlotRestoreFailure.None, ReconcileRule.Row3CorruptCloud,
                ReconcileFlags.BackUpCorruptCloud | ReconcileFlags.ReconciledThisEpoch);
        }

        [Test]
        public void Row3_CorruptCloud_LocalNormalizeFailed_FailedWithBackupOnly()
        {
            ReconcileInput input = SyncInput();
            input.Cloud = CloudState.Corrupt;
            input.LocalFailure = SlotFailure.NormalizeFailed;

            AssertDecision(ReconcileDecider.Decide(input),
                ReconcileAction.NoOp, SlotRestoreOutcome.Failed, SlotRestoreFailure.LocalNormalizeFailed, ReconcileRule.Row3CorruptCloud,
                ReconcileFlags.BackUpCorruptCloud);
        }

        // Row 4

        [Test]
        public void Row4_CloudMissing_TombstonePending_ClearsTombstone()
        {
            ReconcileInput input = SyncInput();
            input.Cloud = CloudState.Missing;
            input.TombstonePending = true;
            input.TombstoneDeletedWriteId = "synced-w";

            AssertDecision(ReconcileDecider.Decide(input),
                ReconcileAction.NoOp, SlotRestoreOutcome.NoCloudData, SlotRestoreFailure.None, ReconcileRule.Row4TombstoneCloudMissing,
                ReconcileFlags.ClearTombstone | ReconcileFlags.ReconciledThisEpoch);
        }

        [Test]
        public void Row4_CloudMissing_TombstonePending_WinsOverLocalNormalizeFailed()
        {
            ReconcileInput input = SyncInput();
            input.Cloud = CloudState.Missing;
            input.TombstonePending = true;
            input.LocalFailure = SlotFailure.NormalizeFailed;
            input.LocalHasContent = false;

            ReconcileDecision decision = ReconcileDecider.Decide(input);

            Assert.That(decision.Rule, Is.EqualTo(ReconcileRule.Row4TombstoneCloudMissing), decision.ToString());
            Assert.That(decision.ClearTombstone, Is.True, decision.ToString());
        }

        // Row 5 with R1

        [Test]
        public void Row5_CloudFound_TombstoneForSameWriteId_DeletesCloud()
        {
            ReconcileInput input = SyncInput();
            input.LocalHasContent = false;
            input.TombstonePending = true;
            input.TombstoneDeletedWriteId = "cloud-w";

            AssertDecision(ReconcileDecider.Decide(input),
                ReconcileAction.DeleteCloud, SlotRestoreOutcome.NoCloudData, SlotRestoreFailure.None, ReconcileRule.Row5TombstoneDelete,
                ReconcileFlags.ClearTombstone | ReconcileFlags.ReconciledThisEpoch);
        }

        [Test]
        public void Row5_TombstoneForSameWriteId_WinsOverLocalNormalizeFailed()
        {
            ReconcileInput input = SyncInput();
            input.TombstonePending = true;
            input.TombstoneDeletedWriteId = "cloud-w";
            input.LocalFailure = SlotFailure.NormalizeFailed;

            Assert.That(ReconcileDecider.Decide(input).Action, Is.EqualTo(ReconcileAction.DeleteCloud));
        }

        [Test]
        public void Row5_TombstoneForOlderWrite_CloudIsOwnSyncedWrite_ClearsTombstone_ContinuesAtRow8()
        {
            ReconcileInput input = SyncInput();
            input.CloudWriteId = "synced-w";
            input.TombstonePending = true;
            input.TombstoneDeletedWriteId = "older-w";

            AssertDecision(ReconcileDecider.Decide(input),
                ReconcileAction.NoOp, SlotRestoreOutcome.UpToDate, SlotRestoreFailure.None, ReconcileRule.Row8OwnWriteLanded,
                ReconcileFlags.ClearTombstone | ReconcileFlags.AdoptCloudProviderVersion | ReconcileFlags.ReconciledThisEpoch);
        }

        [Test]
        public void Row5_TombstoneForOlderWrite_CloudHasNewerWrite_DoesNotDelete()
        {
            ReconcileInput input = SyncInput();
            input.CloudWriteId = "other-device-w";
            input.LocalPresence = LocalPresence.Absent;
            input.LocalHasContent = false;
            input.TombstonePending = true;
            input.TombstoneDeletedWriteId = "synced-w";

            AssertDecision(ReconcileDecider.Decide(input),
                ReconcileAction.TakeCloud, SlotRestoreOutcome.Restored, SlotRestoreFailure.None, ReconcileRule.Row9LocalNeedsCloud,
                ReconcileFlags.ClearTombstone | ReconcileFlags.ReconciledThisEpoch);
        }

        [Test]
        public void Row5_TombstoneForOlderWrite_ConflictCarriesClearTombstone()
        {
            ReconcileInput input = ConflictInput();
            input.TombstonePending = true;
            input.TombstoneDeletedWriteId = "synced-w";

            AssertDecision(ReconcileDecider.Decide(input),
                ReconcileAction.Conflict, SlotRestoreOutcome.LocalKept, SlotRestoreFailure.None, ReconcileRule.Row11Conflict,
                ReconcileFlags.ClearTombstone | ReconcileFlags.ReconciledThisEpoch);
        }

        [Test]
        public void Row5_NullDeletedWriteId_NeverDeletes()
        {
            foreach (string cloudWriteId in new[] { "cloud-w", null })
            {
                ReconcileInput input = SyncInput();
                input.CloudWriteId = cloudWriteId;
                input.TombstonePending = true;
                input.TombstoneDeletedWriteId = null;

                ReconcileDecision decision = ReconcileDecider.Decide(input);

                string context = "cloudWriteId=" + (cloudWriteId ?? "null") + " " + decision;
                Assert.That(decision.Action, Is.Not.EqualTo(ReconcileAction.DeleteCloud), context);
                Assert.That(decision.Rule, Is.Not.EqualTo(ReconcileRule.Row5TombstoneDelete), context);
                Assert.That(decision.ClearTombstone, Is.True, context);
            }
        }

        // Row 6 and row 7

        [Test]
        public void Row6_CloudMissing_LocalPresent_KeepsLocalAndQueuesUpload()
        {
            ReconcileInput input = SyncInput();
            input.Cloud = CloudState.Missing;

            AssertDecision(ReconcileDecider.Decide(input),
                ReconcileAction.KeepLocal, SlotRestoreOutcome.NoCloudData, SlotRestoreFailure.None, ReconcileRule.Row6CloudMissingLocalPresent,
                ReconcileFlags.QueueUpload | ReconcileFlags.ReconciledThisEpoch);
        }

        [Test]
        public void Row7_CloudMissing_LocalAbsentOrUndetermined_NoOp()
        {
            foreach (LocalPresence presence in new[] { LocalPresence.Absent, LocalPresence.Undetermined })
            {
                ReconcileInput input = SyncInput();
                input.Cloud = CloudState.Missing;
                input.LocalPresence = presence;
                input.LocalHasContent = false;

                AssertDecision(ReconcileDecider.Decide(input),
                    ReconcileAction.NoOp, SlotRestoreOutcome.NoCloudData, SlotRestoreFailure.None, ReconcileRule.Row7CloudMissingLocalAbsent,
                    ReconcileFlags.ReconciledThisEpoch);
            }
        }

        [Test]
        public void CloudMissing_LocalNormalizeFailed_StaysFailed_NoRte()
        {
            ReconcileInput input = SyncInput();
            input.Cloud = CloudState.Missing;
            input.LocalFailure = SlotFailure.NormalizeFailed;
            input.LocalHasContent = false;

            AssertDecision(ReconcileDecider.Decide(input),
                ReconcileAction.NoOp, SlotRestoreOutcome.Failed, SlotRestoreFailure.LocalNormalizeFailed, ReconcileRule.LocalNormalizeFailedNoCloudValue,
                ReconcileFlags.None);
        }

        // Row 8

        [Test]
        public void Row8_CloudMatchesPendingWriteId_NotDirty_UpToDate_PromotesPending()
        {
            ReconcileInput input = SyncInput();
            input.PendingWriteId = "cloud-w";

            AssertDecision(ReconcileDecider.Decide(input),
                ReconcileAction.NoOp, SlotRestoreOutcome.UpToDate, SlotRestoreFailure.None, ReconcileRule.Row8OwnWriteLanded,
                ReconcileFlags.PromotePendingWriteId | ReconcileFlags.ReconciledThisEpoch);
        }

        [Test]
        public void Row8_CloudMatchesPendingWriteId_Dirty_LocalKept_QueuesUpload()
        {
            ReconcileInput input = SyncInput();
            input.PendingWriteId = "cloud-w";
            input.LocalDirty = true;
            input.LocalRevision = 7;

            AssertDecision(ReconcileDecider.Decide(input),
                ReconcileAction.KeepLocal, SlotRestoreOutcome.LocalKept, SlotRestoreFailure.None, ReconcileRule.Row8OwnWriteLanded,
                ReconcileFlags.PromotePendingWriteId | ReconcileFlags.QueueUpload | ReconcileFlags.ReconciledThisEpoch);
        }

        [Test]
        public void Row8_CloudMatchesLastSyncedWriteId_NotDirty_UpToDate_AdoptsVersion()
        {
            ReconcileInput input = SyncInput();
            input.CloudWriteId = "synced-w";

            AssertDecision(ReconcileDecider.Decide(input),
                ReconcileAction.NoOp, SlotRestoreOutcome.UpToDate, SlotRestoreFailure.None, ReconcileRule.Row8OwnWriteLanded,
                ReconcileFlags.AdoptCloudProviderVersion | ReconcileFlags.ReconciledThisEpoch);
        }

        [Test]
        public void Row8_CloudMatchesLastSyncedWriteId_Dirty_LocalKept_QueuesUpload()
        {
            ReconcileInput input = SyncInput();
            input.CloudWriteId = "synced-w";
            input.LocalDirty = true;
            input.LocalRevision = 7;

            AssertDecision(ReconcileDecider.Decide(input),
                ReconcileAction.KeepLocal, SlotRestoreOutcome.LocalKept, SlotRestoreFailure.None, ReconcileRule.Row8OwnWriteLanded,
                ReconcileFlags.AdoptCloudProviderVersion | ReconcileFlags.QueueUpload | ReconcileFlags.ReconciledThisEpoch);
        }

        // B2: the write whose response was lost can be older than the newest unconfirmed id
        [Test]
        public void Row8_CloudMatchesAnOlderUnconfirmedWriteId_NotDirty_UpToDate_PromotesPending()
        {
            ReconcileInput input = SyncInput();
            input.PendingWriteId = "newer-w";
            input.PendingWriteIds = new[] { "newer-w", "cloud-w" };

            AssertDecision(ReconcileDecider.Decide(input),
                ReconcileAction.NoOp, SlotRestoreOutcome.UpToDate, SlotRestoreFailure.None, ReconcileRule.Row8OwnWriteLanded,
                ReconcileFlags.PromotePendingWriteId | ReconcileFlags.ReconciledThisEpoch);
        }

        [Test]
        public void Row8_CloudMatchesAnOlderUnconfirmedWriteId_Dirty_LocalKept_QueuesUpload()
        {
            ReconcileInput input = SyncInput();
            input.PendingWriteId = "newer-w";
            input.PendingWriteIds = new[] { "newer-w", "cloud-w" };
            input.LocalDirty = true;
            input.LocalRevision = 7;

            AssertDecision(ReconcileDecider.Decide(input),
                ReconcileAction.KeepLocal, SlotRestoreOutcome.LocalKept, SlotRestoreFailure.None, ReconcileRule.Row8OwnWriteLanded,
                ReconcileFlags.PromotePendingWriteId | ReconcileFlags.QueueUpload | ReconcileFlags.ReconciledThisEpoch);
        }

        [Test]
        public void Row8_UnconfirmedWriteIdsWithoutAMatch_IsAConflict()
        {
            ReconcileInput input = SyncInput();
            input.PendingWriteId = "newer-w";
            input.PendingWriteIds = new[] { "newer-w", "older-w" };
            input.LocalDirty = true;
            input.LocalRevision = 7;

            ReconcileDecision decision = ReconcileDecider.Decide(input);

            Assert.That(decision.Rule, Is.EqualTo(ReconcileRule.Row11Conflict), decision.ToString());
        }

        [Test]
        public void Row8_NullCloudWriteId_NeverMatchesNullSyncedIds()
        {
            ReconcileInput input = SyncInput();
            input.CloudWriteId = null;
            input.LastSyncedWriteId = null;
            input.PendingWriteId = null;

            ReconcileDecision decision = ReconcileDecider.Decide(input);

            Assert.That(decision.Rule, Is.Not.EqualTo(ReconcileRule.Row8OwnWriteLanded), decision.ToString());
        }

        [Test]
        public void LocalNormalizeFailed_CheckedBeforeRow8_TakesCloudWithQuarantine()
        {
            foreach (bool matchPending in new[] { false, true })
            {
                ReconcileInput input = SyncInput();
                input.CloudWriteId = matchPending ? "pending-w" : "synced-w";
                input.PendingWriteId = "pending-w";
                input.LocalFailure = SlotFailure.NormalizeFailed;
                input.LocalHasContent = false;

                AssertDecision(ReconcileDecider.Decide(input),
                    ReconcileAction.TakeCloud, SlotRestoreOutcome.Restored, SlotRestoreFailure.None, ReconcileRule.Row9LocalNeedsCloud,
                    ReconcileFlags.QuarantineLocalFirst | ReconcileFlags.ReconciledThisEpoch);
            }
        }

        [Test]
        public void LocalNormalizeFailed_EmptyCloud_Row12DoesNotProtectUnusableLocal()
        {
            ReconcileInput input = SyncInput();
            input.LocalFailure = SlotFailure.NormalizeFailed;
            input.LocalHasContent = true;
            input.CloudIsEmpty = true;

            ReconcileDecision decision = ReconcileDecider.Decide(input);

            Assert.That(decision.Action, Is.EqualTo(ReconcileAction.TakeCloud), decision.ToString());
            Assert.That(decision.QuarantineLocalFirst, Is.True, decision.ToString());
        }

        [Test]
        public void LocalNormalizeFailed_TombstoneForOlderWrite_CarriesClearTombstone()
        {
            ReconcileInput input = SyncInput();
            input.LocalFailure = SlotFailure.NormalizeFailed;
            input.LocalHasContent = false;
            input.TombstonePending = true;
            input.TombstoneDeletedWriteId = "older-w";

            AssertDecision(ReconcileDecider.Decide(input),
                ReconcileAction.TakeCloud, SlotRestoreOutcome.Restored, SlotRestoreFailure.None, ReconcileRule.Row9LocalNeedsCloud,
                ReconcileFlags.ClearTombstone | ReconcileFlags.QuarantineLocalFirst | ReconcileFlags.ReconciledThisEpoch);
        }

        // Row 9

        [Test]
        public void Row9_LocalAbsent_TakesCloud()
        {
            ReconcileInput input = SyncInput();
            input.LocalPresence = LocalPresence.Absent;
            input.LocalHasContent = false;

            AssertTakeCloudRow9(ReconcileDecider.Decide(input));
        }

        [Test]
        public void Row9_NeedsCloudRecovery_TakesCloud()
        {
            ReconcileInput input = SyncInput();
            input.NeedsCloudRecovery = true;
            input.LocalHasContent = false;

            AssertTakeCloudRow9(ReconcileDecider.Decide(input));
        }

        [Test]
        public void Row9_LocalRevisionBehindLastSynced_TakesCloud()
        {
            ReconcileInput input = SyncInput();
            input.LocalRevision = 3;

            AssertTakeCloudRow9(ReconcileDecider.Decide(input));
        }

        [Test]
        public void Row9_WinsOverRow11_WhenLocalDirtyButNeedsCloudRecovery()
        {
            ReconcileInput input = ConflictInput();
            input.NeedsCloudRecovery = true;

            Assert.That(ReconcileDecider.Decide(input).Rule, Is.EqualTo(ReconcileRule.Row9LocalNeedsCloud));
        }

        // Row 8 is ordered before row 9, so these recovery cases never reach row 9 when the cloud holds our own last write
        [Test]
        public void Row9_NeedsCloudRecovery_CloudIsOwnLastSyncedWrite_TakesCloud()
        {
            ReconcileInput input = SyncInput();
            input.CloudWriteId = "synced-w";
            input.NeedsCloudRecovery = true;
            input.LocalHasContent = false;

            ReconcileDecision decision = ReconcileDecider.Decide(input);

            Assert.That(decision.Action, Is.EqualTo(ReconcileAction.TakeCloud), "Corrupt-reset local data must be recovered from our own cloud write. " + decision);
        }

        [Test]
        public void Row9_LocalRevisionBehindLastSynced_CloudIsOwnLastSyncedWrite_TakesCloud()
        {
            ReconcileInput input = SyncInput();
            input.CloudWriteId = "synced-w";
            input.LocalRevision = 3;

            ReconcileDecision decision = ReconcileDecider.Decide(input);

            Assert.That(decision.Action, Is.EqualTo(ReconcileAction.TakeCloud), "Disk lost writes that already reached the cloud. " + decision);
        }

        [Test]
        public void Row9_LocalAbsent_CloudIsOwnLastSyncedWrite_TakesCloud()
        {
            ReconcileInput input = SyncInput();
            input.CloudWriteId = "synced-w";
            input.LocalPresence = LocalPresence.Absent;
            input.LocalHasContent = false;

            ReconcileDecision decision = ReconcileDecider.Decide(input);

            Assert.That(decision.Action, Is.EqualTo(ReconcileAction.TakeCloud), "Missing local primary must be rebuilt from our own cloud write. " + decision);
        }

        // Row 10 and row 11

        [Test]
        public void Row10_LocalNotDirty_PreviouslySynced_CloudMovedOn_TakesCloud()
        {
            AssertDecision(ReconcileDecider.Decide(SyncInput()),
                ReconcileAction.TakeCloud, SlotRestoreOutcome.Restored, SlotRestoreFailure.None, ReconcileRule.Row10CloudMovedOn,
                ReconcileFlags.ReconciledThisEpoch);
        }

        [Test]
        public void Row11_LocalDirty_CloudMovedOn_Conflict()
        {
            AssertDecision(ReconcileDecider.Decide(ConflictInput()),
                ReconcileAction.Conflict, SlotRestoreOutcome.LocalKept, SlotRestoreFailure.None, ReconcileRule.Row11Conflict,
                ReconcileFlags.ReconciledThisEpoch);
        }

        [Test]
        public void Row11_NeverSynced_BothSidesPresent_Conflict()
        {
            ReconcileInput input = SyncInput();
            input.LastSyncedWriteId = null;
            input.LastSyncedProviderVersion = null;
            input.LastSyncedRevision = 0;

            ReconcileDecision decision = ReconcileDecider.Decide(input);

            Assert.That(decision.Action, Is.EqualTo(ReconcileAction.Conflict), decision.ToString());
            Assert.That(decision.Rule, Is.EqualTo(ReconcileRule.Row11Conflict), decision.ToString());
        }

        // Row 12

        [Test]
        public void Row12_TakeCloudOfEmptyCloudOverLocalContent_KeepsLocal()
        {
            ReconcileInput input = SyncInput();
            input.CloudIsEmpty = true;

            AssertDecision(ReconcileDecider.Decide(input),
                ReconcileAction.KeepLocal, SlotRestoreOutcome.SkippedToProtectLocalData, SlotRestoreFailure.None, ReconcileRule.Row12ProtectLocalData,
                KeepLocalOverCloud);
        }

        [Test]
        public void Row12_EmptyCloud_LocalEmpty_StillTakesCloud()
        {
            ReconcileInput input = SyncInput();
            input.CloudIsEmpty = true;
            input.LocalPresence = LocalPresence.Absent;
            input.LocalHasContent = false;

            AssertTakeCloudRow9(ReconcileDecider.Decide(input));
        }

        [Test]
        public void Row12_CarriesClearTombstone_FromRow5Fallthrough()
        {
            ReconcileInput input = SyncInput();
            input.CloudIsEmpty = true;
            input.TombstonePending = true;
            input.TombstoneDeletedWriteId = "older-w";

            AssertDecision(ReconcileDecider.Decide(input),
                ReconcileAction.KeepLocal, SlotRestoreOutcome.SkippedToProtectLocalData, SlotRestoreFailure.None, ReconcileRule.Row12ProtectLocalData,
                KeepLocalOverCloud | ReconcileFlags.ClearTombstone);
        }

        [Test]
        public void PostCheck_NonTakeCloudOrReadOnly_ReturnsDecisionUnchanged()
        {
            ReconcileInput syncInput = SyncInput();
            syncInput.CloudIsEmpty = true;
            var keepLocal = new ReconcileDecision(
                ReconcileAction.KeepLocal, SlotRestoreOutcome.LocalKept, SlotRestoreFailure.None, ReconcileRule.Row8OwnWriteLanded, ReconcileFlags.QueueUpload);
            AssertSame(keepLocal, ReconcileDecider.PostCheck(syncInput, keepLocal));

            ReconcileInput readOnlyInput = ReadOnlyInput();
            readOnlyInput.CloudIsEmpty = true;
            var takeCloud = new ReconcileDecision(
                ReconcileAction.TakeCloud, SlotRestoreOutcome.Restored, SlotRestoreFailure.None, ReconcileRule.ReadOnlyChanged, ReconcileFlags.ReconciledThisEpoch);
            AssertSame(takeCloud, ReconcileDecider.PostCheck(readOnlyInput, takeCloud));
        }

        [Test]
        public void PostCheck_TakeCloudNonEmptyCloud_ReturnsDecisionUnchanged()
        {
            ReconcileInput input = SyncInput();
            var takeCloud = new ReconcileDecision(
                ReconcileAction.TakeCloud, SlotRestoreOutcome.Restored, SlotRestoreFailure.None, ReconcileRule.Row10CloudMovedOn, ReconcileFlags.ReconciledThisEpoch);

            AssertSame(takeCloud, ReconcileDecider.PostCheck(input, takeCloud));
        }

        [Test]
        public void PostCheck_EmptyCloudOverContent_CarriesTombstoneAndConflictFileOnly()
        {
            ReconcileInput input = SyncInput();
            input.CloudIsEmpty = true;
            var takeCloud = new ReconcileDecision(
                ReconcileAction.TakeCloud, SlotRestoreOutcome.Restored, SlotRestoreFailure.None, ReconcileRule.Row11Conflict,
                ReconcileFlags.ReconciledThisEpoch | ReconcileFlags.ClearTombstone | ReconcileFlags.WriteConflictFile | ReconcileFlags.QuarantineLocalFirst);

            AssertDecision(ReconcileDecider.PostCheck(input, takeCloud),
                ReconcileAction.KeepLocal, SlotRestoreOutcome.SkippedToProtectLocalData, SlotRestoreFailure.None, ReconcileRule.Row12ProtectLocalData,
                KeepLocalOverCloud | ReconcileFlags.ClearTombstone | ReconcileFlags.WriteConflictFile);
        }

        // Conflict resolution follow-up

        [Test]
        public void ApplyConflictResolution_KeepLocal()
        {
            ReconcileInput input = ConflictInput();
            ReconcileDecision conflict = ReconcileDecider.Decide(input);

            AssertDecision(ReconcileDecider.ApplyConflictResolution(input, conflict, ConflictResolutionKind.KeepLocal),
                ReconcileAction.KeepLocal, SlotRestoreOutcome.LocalKept, SlotRestoreFailure.None, ReconcileRule.Row11Conflict,
                KeepLocalOverCloud | ReconcileFlags.WriteConflictFile);
        }

        [Test]
        public void ApplyConflictResolution_Merged()
        {
            ReconcileInput input = ConflictInput();
            ReconcileDecision conflict = ReconcileDecider.Decide(input);

            AssertDecision(ReconcileDecider.ApplyConflictResolution(input, conflict, ConflictResolutionKind.Merged),
                ReconcileAction.Merge, SlotRestoreOutcome.Merged, SlotRestoreFailure.None, ReconcileRule.Row11Conflict,
                KeepLocalOverCloud | ReconcileFlags.WriteConflictFile);
        }

        [Test]
        public void ApplyConflictResolution_TakeCloud()
        {
            ReconcileInput input = ConflictInput();
            ReconcileDecision conflict = ReconcileDecider.Decide(input);

            AssertDecision(ReconcileDecider.ApplyConflictResolution(input, conflict, ConflictResolutionKind.TakeCloud),
                ReconcileAction.TakeCloud, SlotRestoreOutcome.Restored, SlotRestoreFailure.None, ReconcileRule.Row11Conflict,
                ReconcileFlags.ReconciledThisEpoch | ReconcileFlags.WriteConflictFile);
        }

        [Test]
        public void ApplyConflictResolution_TakeCloudOfEmptyCloudOverContent_Row12()
        {
            ReconcileInput input = ConflictInput();
            input.CloudIsEmpty = true;
            ReconcileDecision conflict = ReconcileDecider.Decide(input);
            Assert.That(conflict.Action, Is.EqualTo(ReconcileAction.Conflict), conflict.ToString());

            AssertDecision(ReconcileDecider.ApplyConflictResolution(input, conflict, ConflictResolutionKind.TakeCloud),
                ReconcileAction.KeepLocal, SlotRestoreOutcome.SkippedToProtectLocalData, SlotRestoreFailure.None, ReconcileRule.Row12ProtectLocalData,
                KeepLocalOverCloud | ReconcileFlags.WriteConflictFile);
        }

        [Test]
        public void ApplyConflictResolution_CarriesClearTombstone()
        {
            ReconcileInput input = ConflictInput();
            input.TombstonePending = true;
            input.TombstoneDeletedWriteId = "synced-w";
            ReconcileDecision conflict = ReconcileDecider.Decide(input);

            foreach (ConflictResolutionKind kind in new[] { ConflictResolutionKind.KeepLocal, ConflictResolutionKind.TakeCloud, ConflictResolutionKind.Merged })
            {
                ReconcileDecision resolved = ReconcileDecider.ApplyConflictResolution(input, conflict, kind);
                Assert.That(resolved.ClearTombstone, Is.True, kind + " " + resolved);
                Assert.That(resolved.WriteConflictFile, Is.True, kind + " " + resolved);
            }
        }

        [Test]
        public void ApplyConflictResolution_NonConflictDecision_Throws()
        {
            ReconcileInput input = SyncInput();
            ReconcileDecision takeCloud = ReconcileDecider.Decide(input);

            Assert.Throws<InvalidOperationException>(() => ReconcileDecider.ApplyConflictResolution(input, takeCloud, ConflictResolutionKind.KeepLocal));
        }

        // Delete follow-up

        [Test]
        public void ApplyDeleteResult_Success_ClearsTombstoneAndReconciles()
        {
            ReconcileDecision delete = ReconcileDecider.Decide(DeleteInput());

            AssertDecision(ReconcileDecider.ApplyDeleteResult(delete, true),
                ReconcileAction.NoOp, SlotRestoreOutcome.NoCloudData, SlotRestoreFailure.None, ReconcileRule.Row5TombstoneDelete,
                ReconcileFlags.ClearTombstone | ReconcileFlags.ReconciledThisEpoch);
        }

        [Test]
        public void ApplyDeleteResult_Failure_DeletePendingWithoutRte()
        {
            ReconcileDecision delete = ReconcileDecider.Decide(DeleteInput());

            AssertDecision(ReconcileDecider.ApplyDeleteResult(delete, false),
                ReconcileAction.NoOp, SlotRestoreOutcome.Failed, SlotRestoreFailure.DeletePending, ReconcileRule.Row5TombstoneDelete,
                ReconcileFlags.None);
        }

        [Test]
        public void ApplyDeleteResult_NonDeleteDecision_Throws()
        {
            ReconcileDecision takeCloud = ReconcileDecider.Decide(SyncInput());

            Assert.Throws<InvalidOperationException>(() => ReconcileDecider.ApplyDeleteResult(takeCloud, true));
        }

        // Local failure guards

        [Test]
        public void LocalIoError_GuardWinsForEveryCloudStateAndMode()
        {
            foreach (SyncMode mode in new[] { SyncMode.CloudSync, SyncMode.CloudReadOnly })
            {
                foreach (CloudState cloud in AllCloudStates)
                {
                    ReconcileInput input = mode == SyncMode.CloudSync ? SyncInput() : ReadOnlyInput();
                    input.Cloud = cloud;
                    input.LocalFailure = SlotFailure.IoError;
                    input.TombstonePending = true;
                    input.TombstoneDeletedWriteId = input.CloudWriteId;

                    AssertDecision(ReconcileDecider.Decide(input),
                        ReconcileAction.KeepLocal, SlotRestoreOutcome.Failed, SlotRestoreFailure.LocalIoError, ReconcileRule.LocalIoError,
                        ReconcileFlags.None, mode + "/" + cloud);
                }
            }
        }

        [Test]
        public void LocalSchemaTooNew_GuardWinsForEveryCloudStateAndMode()
        {
            foreach (SyncMode mode in new[] { SyncMode.CloudSync, SyncMode.CloudReadOnly })
            {
                foreach (CloudState cloud in AllCloudStates)
                {
                    ReconcileInput input = mode == SyncMode.CloudSync ? SyncInput() : ReadOnlyInput();
                    input.Cloud = cloud;
                    input.LocalFailure = SlotFailure.SchemaTooNew;

                    AssertDecision(ReconcileDecider.Decide(input),
                        ReconcileAction.KeepLocal, SlotRestoreOutcome.SkippedSchemaTooNew, SlotRestoreFailure.LocalSchemaTooNew, ReconcileRule.LocalSchemaTooNew,
                        ReconcileFlags.None, mode + "/" + cloud);
                }
            }
        }

        // CloudReadOnly table

        [Test]
        public void ReadOnly_ReadFailed_KeepsLocal_Failed()
        {
            ReconcileInput input = ReadOnlyInput();
            input.Cloud = CloudState.ReadFailed;

            AssertDecision(ReconcileDecider.Decide(input),
                ReconcileAction.KeepLocal, SlotRestoreOutcome.Failed, SlotRestoreFailure.ReadFailed, ReconcileRule.ReadOnlyReadFailed, ReconcileFlags.None);
        }

        [Test]
        public void ReadOnly_TooNew_KeepsLocal_RaisesUpdateRequired()
        {
            ReconcileInput input = ReadOnlyInput();
            input.Cloud = CloudState.TooNew;

            AssertDecision(ReconcileDecider.Decide(input),
                ReconcileAction.KeepLocal, SlotRestoreOutcome.SkippedSchemaTooNew, SlotRestoreFailure.None, ReconcileRule.ReadOnlyTooNew,
                ReconcileFlags.RaiseUpdateRequired);
        }

        [Test]
        public void ReadOnly_Corrupt_KeepsMirror_BacksUp_CorruptCloudIgnored()
        {
            ReconcileInput input = ReadOnlyInput();
            input.Cloud = CloudState.Corrupt;

            AssertDecision(ReconcileDecider.Decide(input),
                ReconcileAction.NoOp, SlotRestoreOutcome.CorruptCloudIgnored, SlotRestoreFailure.None, ReconcileRule.ReadOnlyCorrupt,
                ReconcileFlags.BackUpCorruptCloud | ReconcileFlags.ReconciledThisEpoch);
        }

        [Test]
        public void ReadOnly_Corrupt_LocalNormalizeFailed_StaysFailed()
        {
            ReconcileInput input = ReadOnlyInput();
            input.Cloud = CloudState.Corrupt;
            input.LocalFailure = SlotFailure.NormalizeFailed;

            AssertDecision(ReconcileDecider.Decide(input),
                ReconcileAction.NoOp, SlotRestoreOutcome.Failed, SlotRestoreFailure.LocalNormalizeFailed, ReconcileRule.ReadOnlyCorrupt,
                ReconcileFlags.BackUpCorruptCloud);
        }

        [Test]
        public void ReadOnly_Missing_LocalPresentOrUndetermined_ResetsToDefault()
        {
            foreach (LocalPresence presence in new[] { LocalPresence.Present, LocalPresence.Undetermined })
            {
                ReconcileInput input = ReadOnlyInput();
                input.Cloud = CloudState.Missing;
                input.LocalPresence = presence;

                AssertDecision(ReconcileDecider.Decide(input),
                    ReconcileAction.ResetToDefault, SlotRestoreOutcome.NoCloudData, SlotRestoreFailure.None, ReconcileRule.ReadOnlyMissing,
                    ReconcileFlags.ReconciledThisEpoch, presence.ToString());
            }
        }

        [Test]
        public void ReadOnly_Missing_LocalAbsent_NoOp()
        {
            ReconcileInput input = ReadOnlyInput();
            input.Cloud = CloudState.Missing;
            input.LocalPresence = LocalPresence.Absent;
            input.LocalHasContent = false;

            AssertDecision(ReconcileDecider.Decide(input),
                ReconcileAction.NoOp, SlotRestoreOutcome.NoCloudData, SlotRestoreFailure.None, ReconcileRule.ReadOnlyMissing, ReconcileFlags.ReconciledThisEpoch);
        }

        [Test]
        public void ReadOnly_Missing_LocalNormalizeFailed_ResetsWithQuarantine()
        {
            foreach (LocalPresence presence in new[] { LocalPresence.Absent, LocalPresence.Undetermined })
            {
                ReconcileInput input = ReadOnlyInput();
                input.Cloud = CloudState.Missing;
                input.LocalPresence = presence;
                input.LocalFailure = SlotFailure.NormalizeFailed;

                AssertDecision(ReconcileDecider.Decide(input),
                    ReconcileAction.ResetToDefault, SlotRestoreOutcome.NoCloudData, SlotRestoreFailure.None, ReconcileRule.ReadOnlyMissing,
                    ReconcileFlags.QuarantineLocalFirst | ReconcileFlags.ReconciledThisEpoch, presence.ToString());
            }
        }

        [Test]
        public void ReadOnly_Found_ChangedProviderVersion_TakesCloud()
        {
            ReconcileInput input = ReadOnlyInput();
            input.CloudProviderVersion = "v2";

            AssertReadOnlyChanged(ReconcileDecider.Decide(input), ReconcileFlags.ReconciledThisEpoch);
        }

        [Test]
        public void ReadOnly_Found_NoProviderVersion_ChangedContentHash_TakesCloud()
        {
            ReconcileInput input = ReadOnlyInput();
            input.CloudProviderVersion = null;
            input.CloudContentHash = "h2";
            input.LastSyncedProviderVersion = "h1";

            AssertReadOnlyChanged(ReconcileDecider.Decide(input), ReconcileFlags.ReconciledThisEpoch);
        }

        [Test]
        public void ReadOnly_Found_ProviderVersionWinsOverMatchingContentHash()
        {
            ReconcileInput input = ReadOnlyInput();
            input.CloudProviderVersion = "v2";
            input.CloudContentHash = "v1";

            AssertReadOnlyChanged(ReconcileDecider.Decide(input), ReconcileFlags.ReconciledThisEpoch);
        }

        [Test]
        public void ReadOnly_Found_UnchangedProviderVersion_UpToDate()
        {
            AssertDecision(ReconcileDecider.Decide(ReadOnlyInput()),
                ReconcileAction.NoOp, SlotRestoreOutcome.UpToDate, SlotRestoreFailure.None, ReconcileRule.ReadOnlyUnchanged, ReconcileFlags.ReconciledThisEpoch);
        }

        [Test]
        public void ReadOnly_Found_NoProviderVersion_UnchangedContentHash_UpToDate()
        {
            ReconcileInput input = ReadOnlyInput();
            input.CloudProviderVersion = null;
            input.CloudContentHash = "h1";
            input.LastSyncedProviderVersion = "h1";

            AssertDecision(ReconcileDecider.Decide(input),
                ReconcileAction.NoOp, SlotRestoreOutcome.UpToDate, SlotRestoreFailure.None, ReconcileRule.ReadOnlyUnchanged, ReconcileFlags.ReconciledThisEpoch);
        }

        [Test]
        public void ReadOnly_Found_NoVersionAndNoHash_AlwaysTakesCloud()
        {
            ReconcileInput input = ReadOnlyInput();
            input.CloudProviderVersion = null;
            input.CloudContentHash = null;
            input.LastSyncedProviderVersion = null;

            AssertReadOnlyChanged(ReconcileDecider.Decide(input), ReconcileFlags.ReconciledThisEpoch);
        }

        [Test]
        public void ReadOnly_Found_SameVersion_LocalAbsentOrNeedsRecovery_TakesCloud()
        {
            ReconcileInput absent = ReadOnlyInput();
            absent.LocalPresence = LocalPresence.Absent;
            absent.LocalHasContent = false;
            AssertReadOnlyChanged(ReconcileDecider.Decide(absent), ReconcileFlags.ReconciledThisEpoch);

            ReconcileInput recovery = ReadOnlyInput();
            recovery.NeedsCloudRecovery = true;
            AssertReadOnlyChanged(ReconcileDecider.Decide(recovery), ReconcileFlags.ReconciledThisEpoch);
        }

        [Test]
        public void ReadOnly_Found_LocalNormalizeFailed_TakesCloudWithQuarantine()
        {
            ReconcileInput input = ReadOnlyInput();
            input.LocalFailure = SlotFailure.NormalizeFailed;

            AssertReadOnlyChanged(ReconcileDecider.Decide(input), ReconcileFlags.QuarantineLocalFirst | ReconcileFlags.ReconciledThisEpoch);
        }

        [Test]
        public void ReadOnly_Found_EmptyCloudOverContent_NotProtectedByRow12()
        {
            ReconcileInput input = ReadOnlyInput();
            input.CloudProviderVersion = "v2";
            input.CloudIsEmpty = true;

            AssertReadOnlyChanged(ReconcileDecider.Decide(input), ReconcileFlags.ReconciledThisEpoch);
        }

        // Mode guard

        [Test]
        public void LocalOnlyInput_Throws()
        {
            ReconcileInput input = SyncInput();
            input.Mode = SyncMode.LocalOnly;

            Assert.Throws<ArgumentException>(() => ReconcileDecider.Decide(input));
        }

        // Healthy CloudSync slot previously synced; defaults land on row 10
        private static ReconcileInput SyncInput()
        {
            return new ReconcileInput
            {
                Mode = SyncMode.CloudSync,
                Cloud = CloudState.Found,
                CloudWriteId = "cloud-w",
                CloudProviderVersion = "cv",
                CloudContentHash = "ch",
                CloudIsEmpty = false,
                LocalPresence = LocalPresence.Present,
                LocalFailure = SlotFailure.None,
                LocalHasContent = true,
                LocalDirty = false,
                LocalRevision = 5,
                NeedsCloudRecovery = false,
                LastSyncedRevision = 5,
                LastSyncedWriteId = "synced-w",
                LastSyncedProviderVersion = "sv",
                PendingWriteId = null,
                TombstonePending = false,
                TombstoneDeletedWriteId = null,
            };
        }

        // Dirty local over a cloud value written elsewhere
        private static ReconcileInput ConflictInput()
        {
            ReconcileInput input = SyncInput();
            input.LocalDirty = true;
            input.LocalRevision = 6;
            return input;
        }

        private static ReconcileInput DeleteInput()
        {
            ReconcileInput input = SyncInput();
            input.LocalHasContent = false;
            input.TombstonePending = true;
            input.TombstoneDeletedWriteId = "cloud-w";
            return input;
        }

        // Mirror synced at provider version v1
        private static ReconcileInput ReadOnlyInput()
        {
            return new ReconcileInput
            {
                Mode = SyncMode.CloudReadOnly,
                Cloud = CloudState.Found,
                CloudWriteId = null,
                CloudProviderVersion = "v1",
                CloudContentHash = "h1",
                CloudIsEmpty = false,
                LocalPresence = LocalPresence.Present,
                LocalFailure = SlotFailure.None,
                LocalHasContent = true,
                LocalDirty = false,
                LocalRevision = 3,
                NeedsCloudRecovery = false,
                LastSyncedRevision = 3,
                LastSyncedWriteId = null,
                LastSyncedProviderVersion = "v1",
                PendingWriteId = null,
                TombstonePending = false,
                TombstoneDeletedWriteId = null,
            };
        }

        private static void AssertTakeCloudRow9(ReconcileDecision decision)
        {
            AssertDecision(decision,
                ReconcileAction.TakeCloud, SlotRestoreOutcome.Restored, SlotRestoreFailure.None, ReconcileRule.Row9LocalNeedsCloud, ReconcileFlags.ReconciledThisEpoch);
        }

        private static void AssertReadOnlyChanged(ReconcileDecision decision, ReconcileFlags flags)
        {
            AssertDecision(decision, ReconcileAction.TakeCloud, SlotRestoreOutcome.Restored, SlotRestoreFailure.None, ReconcileRule.ReadOnlyChanged, flags);
        }

        private static void AssertSame(ReconcileDecision expected, ReconcileDecision actual)
        {
            AssertDecision(actual, expected.Action, expected.Outcome, expected.Failure, expected.Rule, expected.Flags);
        }

        private static void AssertDecision(
            ReconcileDecision decision,
            ReconcileAction action,
            SlotRestoreOutcome outcome,
            SlotRestoreFailure failure,
            ReconcileRule rule,
            ReconcileFlags flags,
            string context = null)
        {
            string message = (context != null ? context + ": " : string.Empty) + decision;
            Assert.That(decision.Action, Is.EqualTo(action), message);
            Assert.That(decision.Outcome, Is.EqualTo(outcome), message);
            Assert.That(decision.Failure, Is.EqualTo(failure), message);
            Assert.That(decision.Rule, Is.EqualTo(rule), message);
            Assert.That(decision.Flags, Is.EqualTo(flags), message);
        }
    }
}
