using System;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Ecanakli.SaveSystem
{
    // Steps 8-9: apply on the main thread, then persist with the epoch token
    internal sealed partial class RestoreOperation
    {
        // Waits out background local writes so a stale revision cannot land over applied cloud data
        private void FlushBeforeApply(RestoreRun run)
        {
            bool swapsData = false;
            for (int i = 0; i < run.Work.Count && !swapsData; i++)
            {
                SlotWork work = run.Work[i];
                ReconcileAction action = work.Decision.Action;
                swapsData = work.Fetch && (action == ReconcileAction.TakeCloud || action == ReconcileAction.Conflict || action == ReconcileAction.ResetToDefault);
            }

            if (!swapsData)
            {
                return;
            }

            try
            {
                LocalFlushResult flushed = _context.FlushLocalNow();
                if (flushed != null && !flushed.IsComplete)
                {
                    _context.Logger.Warning("[SaveSystem] Local flush before the restore apply was incomplete: " + flushed + ".");
                }
            }
            catch (Exception exception)
            {
                _context.Logger.Error("[SaveSystem] Local flush before the restore apply threw.", exception);
            }
        }

        private void ApplyAll(RestoreRun run, string directory)
        {
            for (int i = 0; i < run.Work.Count; i++)
            {
                SlotWork work = run.Work[i];
                if (!work.Fetch)
                {
                    continue;
                }

                try
                {
                    ApplySlot(run, work, directory);
                }
                catch (Exception exception)
                {
                    _context.Logger.Error("[SaveSystem] Applying the restore to slot '" + work.Slot.Key + "' threw.", exception);
                    work.Fail(SlotRestoreFailure.ApplyFailed);
                }
            }
        }

        private void ApplySlot(RestoreRun run, SlotWork work, string directory)
        {
            SaveSlot slot = work.Slot;

            // Revision compare-and-swap: re-decide with the fetched value and the current local presence, no network
            if (slot.Revision != work.DecidedRevision)
            {
                LogVerbose("Slot '" + slot.Key + "' changed since the decision; re-deciding.");
                DecideSlot(work, directory);
            }

            ReconcileDecision decision = work.Decision;
            if (decision.Action == ReconcileAction.Conflict)
            {
                ReconcileDecision conflict = decision;
                if (!TryResolveConflict(work, directory, in conflict, out decision))
                {
                    return;
                }
            }

            work.Decision = decision;
            work.Outcome = decision.Outcome;
            work.Failure = decision.Failure;
            if (decision.Failure == SlotRestoreFailure.ReadFailed)
            {
                work.CloudError = work.Cloud.Error;
            }

            switch (decision.Action)
            {
                case ReconcileAction.TakeCloud:
                    if (!TryTakeCloud(run, work, directory, decision))
                    {
                        return;
                    }

                    break;
                case ReconcileAction.ResetToDefault:
                    if (!TryResetMirror(run, work, directory, decision))
                    {
                        return;
                    }

                    break;
                case ReconcileAction.DeleteCloud:
                    work.DeleteCloud = true;
                    break;
            }

            ApplyFlags(run, work, decision);
        }

        private void ApplyFlags(RestoreRun run, SlotWork work, in ReconcileDecision decision)
        {
            SaveSlot slot = work.Slot;
            CloudItem cloud = work.Cloud;
            if (decision.BackUpCorruptCloud)
            {
                work.BackUpCorruptCloud = true;
            }

            if (decision.NeedsReconcile)
            {
                work.NeedsReconcile = true;
            }

            if (decision.RaiseUpdateRequired)
            {
                run.RequiresAppUpdate = true;
                if (_session.TryMarkUpdateRequired(slot, PayloadSource.Cloud))
                {
                    run.Updates.Add(new UpdateRequiredInfo(
                        run.Profile, slot.Key, PayloadSource.Cloud, cloud.FoundFormat, cloud.FoundSchema, SaveEnvelope.CurrentFormat, slot.SupportedSchema));
                }
            }

            // Row 5: tombstone and ReconciledThisEpoch wait for the delete result
            if (decision.Action == ReconcileAction.DeleteCloud)
            {
                return;
            }

            string cloudWriteId = cloud.Envelope?.WriteId;
            if (decision.ClearTombstone && _session.PeekSyncState(slot).PendingDelete)
            {
                GetSync(work).ClearTombstone();
            }

            if (decision.PromotePendingWriteId)
            {
                SlotSyncState sync = GetSync(work);
                sync.LastSyncedWriteId = cloudWriteId;
                sync.LastSyncedProviderVersion = cloud.Version;
                if (cloud.Envelope != null)
                {
                    sync.LastSyncedRevision = cloud.Envelope.Revision;

                    // The landed write already covers this revision, so no retry upload is needed
                    _context.Scheduler.ClearCloudIfRevisionUnchanged(slot, cloud.Envelope.Revision);
                }

                // Confirms this id and older ones only; a newer write may still be in flight
                sync.ConfirmPendingWriteId(cloudWriteId);
            }

            if (decision.AdoptCloudWriteId)
            {
                GetSync(work).LastSyncedWriteId = cloudWriteId;
            }

            if (decision.AdoptCloudProviderVersion)
            {
                GetSync(work).LastSyncedProviderVersion = cloud.Version;
            }

            if (decision.QueueUpload)
            {
                work.QueueUpload = true;
            }

            if (decision.ReconciledThisEpoch && work.IsSuccess)
            {
                work.Reconciled = true;

                // N4: a reconciled slot never stays blocked on recovery
                if (_session.PeekSyncState(slot).NeedsCloudRecovery)
                {
                    GetSync(work).NeedsCloudRecovery = false;
                }
            }
        }

        // Row 11: ResolveConflict, conflict file with both sides (N6), then the final decision
        private bool TryResolveConflict(SlotWork work, string directory, in ReconcileDecision decision, out ReconcileDecision final)
        {
            final = decision;
            SaveSlot slot = work.Slot;
            CloudItem cloud = work.Cloud;
            SaveEnvelope envelope = cloud.Envelope;
            SlotSyncState peek = _session.PeekSyncState(slot);
            var metadata = new ConflictMetadata(
                slot.Revision, envelope != null ? envelope.Revision : 0, envelope?.SavedAtUtc, envelope?.DeviceId,
                peek.LastSyncedWriteId != null || peek.LastSyncedRevision > 0);

            object localData = slot.DataBox;
            SlotConflictDecision resolved = slot.ResolveConflictBoxed(localData, cloud.Data, in metadata);
            if (!resolved.IsSuccess)
            {
                _context.Logger.Error("[SaveSystem] Conflict resolution for '" + slot.Key + "' failed (" + resolved.Message + "); local data kept.", resolved.Exception);
                work.Fail(SlotRestoreFailure.ApplyFailed);
                return false;
            }

            final = ReconcileDecider.ApplyConflictResolution(in work.Input, in decision, resolved.Kind);
            if (!TryWriteConflictFile(work, directory, resolved.Kind, localData))
            {
                return false;
            }

            if (final.Action == ReconcileAction.Merge)
            {
                slot.ReplaceData(resolved.MergedData);
                slot.SetRevision(slot.Revision + 1);
                work.WriteData = true;
                if (!slot.EvaluateIsEmpty(resolved.MergedData))
                {
                    GetSync(work).HadContent = true;
                }
            }

            return true;
        }

        // Written before any swap; without it the swap does not happen
        private bool TryWriteConflictFile(SlotWork work, string directory, ConflictResolutionKind kind, object localData)
        {
            SaveSlot slot = work.Slot;
            JToken local;
            JToken cloud;
            try
            {
                local = slot.SnapshotOf(localData);
                cloud = slot.SnapshotOf(work.Cloud.Data);
            }
            catch (Exception exception)
            {
                _context.Logger.Error("[SaveSystem] Snapshot for the conflict file of '" + slot.Key + "' failed; local data kept.", exception);
                work.Fail(SlotRestoreFailure.ApplyFailed);
                return false;
            }

            SlotFileOpResult written = _context.SlotStore.WriteConflictFile(directory, slot.Key, kind, local, cloud);
            if (written.IsSuccess)
            {
                return true;
            }

            _context.Logger.Error("[SaveSystem] " + written.Message + " Local data kept; the next restore retries.", written.Exception);
            work.Fail(SlotRestoreFailure.ApplyFailed);
            return false;
        }

        // Swap in cloud data; sync state becomes synced to that value (LastSyncedRevision == slot.Revision)
        private bool TryTakeCloud(RestoreRun run, SlotWork work, string directory, in ReconcileDecision decision)
        {
            SaveSlot slot = work.Slot;
            CloudItem cloud = work.Cloud;
            if (cloud.Data == null)
            {
                _context.Logger.Error("[SaveSystem] TakeCloud for '" + slot.Key + "' has no materialized cloud data.");
                work.Fail(SlotRestoreFailure.ApplyFailed);
                return false;
            }

            if (decision.QuarantineLocalFirst && !TryQuarantineLocal(run, work, directory))
            {
                return false;
            }

            long cloudRevision = slot.SyncMode == SyncMode.CloudSync && cloud.Envelope != null ? cloud.Envelope.Revision : 0;
            long revision = Math.Max(slot.Revision + 1, cloudRevision);
            slot.ReplaceData(cloud.Data);
            slot.SetRevision(revision);
            slot.MarkReady();

            SlotSyncState sync = GetSync(work);
            sync.LastSyncedRevision = revision;
            sync.NeedsCloudRecovery = false;
            if (slot.SyncMode == SyncMode.CloudSync)
            {
                string takenWriteId = cloud.Envelope?.WriteId;
                sync.LastSyncedWriteId = takenWriteId;
                sync.LastSyncedProviderVersion = cloud.Version;

                // Our own landed write confirms that id and older ones, leaving a newer one in flight;
                // a foreign value supersedes every unconfirmed id, so keeping one would fake ownership later
                if (sync.IsPendingWriteId(takenWriteId))
                {
                    sync.ConfirmPendingWriteId(takenWriteId);
                }
                else
                {
                    sync.PendingWriteId = null;
                }

                work.LocalWriteId = takenWriteId;
            }
            else
            {
                sync.LastSyncedProviderVersion = cloud.Version ?? cloud.ContentHash;
            }

            if (!cloud.IsEmpty)
            {
                sync.HadContent = true;
            }

            work.WriteData = true;
            _context.Scheduler.ClearCloudIfRevisionUnchanged(slot, revision);
            return true;
        }

        // CloudReadOnly mirror whose server value is gone
        private bool TryResetMirror(RestoreRun run, SlotWork work, string directory, in ReconcileDecision decision)
        {
            SaveSlot slot = work.Slot;
            if (decision.QuarantineLocalFirst && !TryQuarantineLocal(run, work, directory))
            {
                return false;
            }

            SlotMaterializeResult created = slot.CreateNormalizedDefault();
            if (created.IsSuccess)
            {
                slot.ReplaceData(created.Data);
            }
            else
            {
                slot.ResetToDefault();
            }

            slot.SetRevision(slot.Revision + 1);
            slot.MarkReady();
            GetSync(work).ClearSyncHistory();
            work.DeleteLocalFiles = true;
            return true;
        }

        private bool TryQuarantineLocal(RestoreRun run, SlotWork work, string directory)
        {
            SaveSlot slot = work.Slot;
            SlotFileOpResult quarantined = _context.SlotStore.QuarantinePrimary(directory, slot.Key, CorruptionCause.None);
            if (!quarantined.IsSuccess)
            {
                _context.Logger.Error("[SaveSystem] " + quarantined.Message + " Cloud data was not applied.", quarantined.Exception);
                work.Fail(SlotRestoreFailure.ApplyFailed);
                return false;
            }

            if (slot.SyncMode == SyncMode.CloudSync)
            {
                run.Issues.Add(new SlotLoadIssue(
                    run.Profile, slot.Key, SlotLoadIssueKind.LocalNormalizeFailedReplacedByCloud, CorruptionCause.None, quarantined.FileName,
                    "The unreadable local copy was quarantined and replaced by cloud data."));
            }

            return true;
        }

        // Synchronous persist: backups, slot files, profile.json once, then ReconciledThisEpoch
        private void PersistLocal(RestoreRun run, string directory)
        {
            bool syncChanged = false;
            for (int i = 0; i < run.Work.Count; i++)
            {
                SlotWork work = run.Work[i];
                if (work.BackUpCorruptCloud)
                {
                    BackUpCorruptCloud(run, work, directory);
                }

                if (work.WriteData)
                {
                    WriteSlotFile(run, work, directory);
                }
                else if (work.DeleteLocalFiles)
                {
                    DeleteMirrorFiles(work, directory);
                }

                syncChanged |= work.SyncChanged;
            }

            if (syncChanged)
            {
                StateWriteResult persisted = _session.PersistSyncState();
                if (!persisted.IsSuccess)
                {
                    _context.Logger.Warning("[SaveSystem] Restore sync state is applied in memory only: " + persisted.Message);
                    for (int i = 0; i < run.Work.Count; i++)
                    {
                        if (run.Work[i].SyncChanged)
                        {
                            run.Work[i].LocalPersistFailed = true;
                        }
                    }
                }
            }

            for (int i = 0; i < run.Work.Count; i++)
            {
                SlotWork work = run.Work[i];
                if (work.Reconciled && work.IsSuccess)
                {
                    _session.MarkReconciledThisEpoch(work.Slot);
                }

                // Row 2: refusing the cloud value also withdraws a grant from earlier in this epoch
                if (work.NeedsReconcile)
                {
                    _session.MarkNeedsReconcile(work.Slot);
                }
            }
        }

        private void BackUpCorruptCloud(RestoreRun run, SlotWork work, string directory)
        {
            CloudItem cloud = work.Cloud;
            if (cloud == null || cloud.Bytes == null)
            {
                return;
            }

            string key = work.Slot.Key;
            SlotFileOpResult backup = _context.SlotStore.WriteCloudCorruptBackup(directory, key, cloud.Bytes);
            switch (backup.Status)
            {
                case SlotFileOpStatus.Done:
                    run.Issues.Add(new SlotLoadIssue(run.Profile, key, SlotLoadIssueKind.CloudPayloadCorrupt, cloud.Cause, backup.FileName, cloud.Message, cloud.Exception));
                    break;
                case SlotFileOpStatus.Failed:
                    _context.Logger.Error("[SaveSystem] " + backup.Message, backup.Exception);
                    run.Issues.Add(new SlotLoadIssue(
                        run.Profile, key, SlotLoadIssueKind.CloudPayloadCorrupt, cloud.Cause, null, cloud.Message + " Backup failed: " + backup.Message,
                        cloud.Exception));
                    break;
            }
        }

        // A failed write keeps memory, sets LocalPersistFailed and leaves the slot local-dirty for the scheduler backoff
        private void WriteSlotFile(RestoreRun run, SlotWork work, string directory)
        {
            SaveSlot slot = work.Slot;
            long revision = slot.Revision;
            JToken data;
            try
            {
                data = slot.SnapshotOf(slot.DataBox);
            }
            catch (Exception exception)
            {
                _context.Logger.Error("[SaveSystem] Snapshot of restored slot '" + slot.Key + "' failed; it stays local-dirty.", exception);
                work.LocalPersistFailed = true;
                _context.Scheduler.MarkLocalDirty(slot);
                return;
            }

            var envelope = new SaveEnvelope
            {
                Format = SaveEnvelope.CurrentFormat,
                Schema = slot.SupportedSchema,
                Revision = revision,
                WriteId = work.LocalWriteId,
                SavedAtUtc = _context.Clock.UtcNow,
                DeviceId = _session.DeviceId,
                AccountId = run.Profile.AccountId,
                Data = data,
            };

            SlotWriteResult written = _context.SlotStore.WriteEnvelope(directory, slot.Key, envelope);

            // Every refusal (too new, stale revision) keeps the disk intact, so it is no disk failure for write health
            if (!written.IsRefused)
            {
                _session.ReportLocalWrite(_session.GetLocalWriteTarget(slot), written.IsSuccess, written.ErrorKind, written.Message);
            }

            if (written.IsSuccess)
            {
                _context.Scheduler.ClearLocalIfRevisionUnchanged(slot, revision);
                _context.MutationDetector.RecordBaseline(slot, revision, written.DataSha256);
                return;
            }

            work.LocalPersistFailed = true;
            _context.Scheduler.MarkLocalDirty(slot);
        }

        private void DeleteMirrorFiles(SlotWork work, string directory)
        {
            SaveSlot slot = work.Slot;
            SlotFileOpResult deleted = _context.SlotStore.DeleteSlotFiles(directory, slot.Key);
            if (deleted.IsSuccess)
            {
                _context.MutationDetector.CaptureBaseline(slot);
                return;
            }

            _context.Logger.Error("[SaveSystem] " + deleted.Message + " The reset mirror is rewritten by the scheduler.", deleted.Exception);
            work.LocalPersistFailed = true;
            _context.Scheduler.MarkLocalDirty(slot);
        }

        // Row 5 (R1): conditional delete with the epoch token; session state changes only while the epoch is unchanged
        private async UniTask DeleteTombstonedCloudValuesAsync(RestoreRun run)
        {
            bool changed = false;
            for (int i = 0; i < run.Work.Count; i++)
            {
                SlotWork work = run.Work[i];
                if (!work.DeleteCloud)
                {
                    continue;
                }

                CloudDeleteResult result = null;
                CloudError error = null;
                try
                {
                    result = await _context.CloudGateway.DeleteAsync(work.Slot.Key, work.Cloud.Version, run.EpochToken);
                }
                catch (OperationCanceledException)
                {
                    error = new CloudError(CloudErrorKind.Transient, "The cloud delete was aborted because the profile changed.");
                }
                catch (Exception exception)
                {
                    _context.Logger.Error("[SaveSystem] Cloud delete of '" + work.Slot.Key + "' threw.", exception);
                    error = new CloudError(CloudErrorKind.Permanent, "Cloud delete threw: " + exception.Message, null, exception);
                }

                bool succeeded = result != null && (result.IsSuccess || result.IsNotFound);
                ReconcileDecision applied = ReconcileDecider.ApplyDeleteResult(in work.Decision, succeeded);
                work.Decision = applied;
                work.Outcome = applied.Outcome;
                work.Failure = applied.Failure;
                work.DeleteCloud = false;
                if (!succeeded)
                {
                    work.CloudError = error ?? result?.Error ?? new CloudError(CloudErrorKind.Permanent, "The cloud delete failed.");
                    continue;
                }

                // A superseded epoch keeps the tombstone on disk; row 4 clears it at the next restore
                if (IsDisposed() || _session.Epoch != run.Epoch)
                {
                    continue;
                }

                GetSync(work).ClearTombstone();
                if (_session.PeekSyncState(work.Slot).NeedsCloudRecovery)
                {
                    GetSync(work).NeedsCloudRecovery = false;
                }

                _session.MarkReconciledThisEpoch(work.Slot);
                changed = true;
            }

            if (!changed || IsDisposed() || _session.Epoch != run.Epoch)
            {
                return;
            }

            StateWriteResult persisted = _session.PersistSyncState();
            if (persisted.IsSuccess)
            {
                return;
            }

            _context.Logger.Warning("[SaveSystem] Cleared tombstones are recorded in memory only: " + persisted.Message);
            for (int i = 0; i < run.Work.Count; i++)
            {
                SlotWork work = run.Work[i];
                if (work.Decision.Rule == ReconcileRule.Row5TombstoneDelete && work.IsSuccess)
                {
                    work.LocalPersistFailed = true;
                }
            }
        }

        private SlotSyncState GetSync(SlotWork work)
        {
            work.SyncChanged = true;
            return _session.GetSyncState(work.Slot);
        }
    }
}
