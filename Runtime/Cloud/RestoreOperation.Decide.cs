using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Ecanakli.SaveSystem
{
    // Steps 4-6: reload, select, fetch, classify and decide
    internal sealed partial class RestoreOperation
    {
        // Step 4 (N4): Failed(IoError) slots reload before anything is selected
        private void ReloadIoErrorSlots(RestoreRun run, string directory)
        {
            IReadOnlyList<SaveSlot> slots = run.OnlySlots ?? _context.Registry.Select(SlotScope.Profile);
            bool syncDirty = false;
            for (int i = 0; i < slots.Count; i++)
            {
                SaveSlot slot = slots[i];
                if (slot == null || slot.Scope != SlotScope.Profile || slot.State != SlotState.Failed || slot.Failure != SlotFailure.IoError)
                {
                    continue;
                }

                SlotLoadResult result;
                try
                {
                    result = _context.SlotStore.Load(slot, directory);
                }
                catch (Exception exception)
                {
                    _context.Logger.Error("[SaveSystem] Reloading slot '" + slot.Key + "' threw; it stays Failed.", exception);
                    continue;
                }

                slot.ApplyLoadResult(in result);
                IReadOnlyList<SlotLoadIssue> issues = result.CreateIssues(run.Profile, slot.Key);
                for (int j = 0; j < issues.Count; j++)
                {
                    run.Issues.Add(issues[j]);
                }

                if (result.Failure == SlotFailure.SchemaTooNew && _session.TryMarkUpdateRequired(slot, PayloadSource.Local))
                {
                    run.Updates.Add(new UpdateRequiredInfo(
                        run.Profile, slot.Key, PayloadSource.Local, result.File.FoundFormat, result.File.FoundSchema, SaveEnvelope.CurrentFormat,
                        slot.SupportedSchema));
                }

                if (!result.IsReady)
                {
                    continue;
                }

                _context.MutationDetector.CaptureBaseline(slot);
                LogVerbose("Slot '" + slot.Key + "' reloaded after an IO error.");
                if (slot.SyncMode == SyncMode.LocalOnly)
                {
                    continue;
                }

                if (result.NeedsCloudRecovery && !_session.PeekSyncState(slot).NeedsCloudRecovery)
                {
                    _session.GetSyncState(slot).NeedsCloudRecovery = true;
                    syncDirty = true;
                }

                if (result.Presence == LocalPresence.Present && !_session.PeekSyncState(slot).HadContent && !slot.EvaluateIsEmpty(slot.DataBox))
                {
                    _session.GetSyncState(slot).HadContent = true;
                    syncDirty = true;
                }
            }

            if (syncDirty && _session.CanPersistSyncState)
            {
                _session.PersistSyncState();
            }
        }

        // Profile slots that are not LocalOnly; local SchemaTooNew and IoError are listed but not fetched
        private void SelectSlots(RestoreRun run)
        {
            IReadOnlyList<SaveSlot> candidates = run.OnlySlots ?? _context.Registry.Select(SlotScope.Profile);
            for (int i = 0; i < candidates.Count; i++)
            {
                SaveSlot slot = candidates[i];
                if (slot == null || slot.Scope != SlotScope.Profile || slot.SyncMode == SyncMode.LocalOnly || !_context.Registry.Contains(slot)
                    || ContainsWork(run, slot))
                {
                    continue;
                }

                if (run.IsSlotRun && _session.IsReconciledThisEpoch(slot))
                {
                    continue;
                }

                var work = new SlotWork(slot);
                run.Work.Add(work);
                if (slot.State == SlotState.Ready)
                {
                    work.Fetch = true;
                    continue;
                }

                if (slot.State != SlotState.Failed)
                {
                    work.Preset(SlotRestoreOutcome.Failed, SlotRestoreFailure.ApplyFailed);
                    continue;
                }

                switch (slot.Failure)
                {
                    case SlotFailure.NormalizeFailed:
                        // Row 9 replaces an unreadable local copy with a usable cloud value
                        work.Fetch = true;
                        break;
                    case SlotFailure.SchemaTooNew:
                        work.Preset(SlotRestoreOutcome.SkippedSchemaTooNew, SlotRestoreFailure.LocalSchemaTooNew);
                        run.RequiresAppUpdate = true;
                        break;
                    default:
                        work.Preset(SlotRestoreOutcome.Failed, SlotRestoreFailure.LocalIoError);
                        break;
                }
            }
        }

        private static bool ContainsWork(RestoreRun run, SaveSlot slot)
        {
            for (int i = 0; i < run.Work.Count; i++)
            {
                if (ReferenceEquals(run.Work[i].Slot, slot))
                {
                    return true;
                }
            }

            return false;
        }

        // Step 5; false when the run ended (caller cancel or epoch change)
        private async UniTask<bool> FetchAsync(RestoreRun run, CancellationToken callerCt)
        {
            var fetched = new List<SlotWork>(run.Work.Count);
            var requests = new List<CloudReadRequest>(run.Work.Count);
            for (int i = 0; i < run.Work.Count; i++)
            {
                SlotWork work = run.Work[i];
                if (!work.Fetch)
                {
                    continue;
                }

                CloudAccess access = work.Slot.SyncMode == SyncMode.CloudReadOnly ? CloudAccess.ServerOwned : CloudAccess.ClientOwned;
                fetched.Add(work);
                requests.Add(new CloudReadRequest(work.Slot.Key, access));
            }

            if (requests.Count == 0)
            {
                return true;
            }

            IReadOnlyList<CloudReadResult> responses = null;
            CloudError batchError = null;
            using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(callerCt, run.EpochToken))
            {
                try
                {
                    responses = await _context.CloudGateway.ReadAsync(requests, linked.Token);
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested)
                {
                    if (callerCt.IsCancellationRequested)
                    {
                        run.Cancel();
                    }
                    else
                    {
                        FailAfterFetch(run);
                    }

                    return false;
                }
                catch (Exception exception)
                {
                    _context.Logger.Error("[SaveSystem] Cloud read for the restore threw.", exception);
                    batchError = new CloudError(CloudErrorKind.Permanent, "Cloud read threw: " + exception.Message, null, exception);
                }
            }

            for (int i = 0; i < fetched.Count; i++)
            {
                SlotWork work = fetched[i];
                if (batchError != null)
                {
                    work.ReadResult = CloudReadResult.Failed(work.Slot.Key, batchError);
                }
                else
                {
                    work.ReadResult = responses != null && i < responses.Count ? responses[i] : null;
                }
            }

            return true;
        }

        // Step 6: classify every fetched value and decide with the table
        private void ClassifyAndDecide(RestoreRun run, string directory)
        {
            bool preservesValueText = false;
            try
            {
                preservesValueText = _context.CloudGateway.Capabilities.PreservesValueText;
            }
            catch (Exception exception)
            {
                _context.Logger.Warning("[SaveSystem] Cloud capabilities are unavailable; cloud checksums are not verified. " + exception.Message);
            }

            for (int i = 0; i < run.Work.Count; i++)
            {
                SlotWork work = run.Work[i];
                if (!work.Fetch)
                {
                    continue;
                }

                work.Cloud = Classify(work.Slot, work.ReadResult, preservesValueText);
                DecideSlot(work, directory);
            }
        }

        // Presence is part of the decision, so the revision compare-and-swap must see it again
        private void DecideSlot(SlotWork work, string directory)
        {
            work.Presence = DeterminePresence(work.Slot, directory);
            work.Input = BuildInput(work);
            work.Decision = ReconcileDecider.Decide(in work.Input);
            work.DecidedRevision = work.Slot.Revision;
            if (_context.Logger.IsVerboseEnabled)
            {
                _context.Logger.Verbose("[SaveSystem] Restore decision for '" + work.Slot.Key + "': " + work.Decision);
            }
        }

        private CloudItem Classify(SaveSlot slot, CloudReadResult read, bool preservesValueText)
        {
            var item = new CloudItem();
            if (read == null)
            {
                item.State = CloudState.ReadFailed;
                item.Error = new CloudError(CloudErrorKind.Permanent, "No read result for '" + slot.Key + "'.");
                return item;
            }

            switch (read.Status)
            {
                case CloudReadStatus.NotFound:
                    item.State = CloudState.Missing;
                    return item;
                case CloudReadStatus.Found:
                    break;
                default:
                    item.State = CloudState.ReadFailed;
                    item.Error = read.Error ?? new CloudError(CloudErrorKind.Permanent, "Reading '" + slot.Key + "' failed.");
                    return item;
            }

            item.Bytes = read.Value;
            item.Version = read.Version;
            if (slot.SyncMode == SyncMode.CloudReadOnly)
            {
                ClassifyRaw(slot, item);
            }
            else
            {
                ClassifyEnvelope(slot, item, preservesValueText);
            }

            return item;
        }

        private static void ClassifyEnvelope(SaveSlot slot, CloudItem item, bool preservesValueText)
        {
            EnvelopeDecodeResult decoded = EnvelopeCodec.Decode(item.Bytes, slot.SupportedSchema);
            if (decoded.IsTooNew)
            {
                item.State = CloudState.TooNew;
                item.FoundFormat = decoded.Envelope?.Format ?? 0;
                item.FoundSchema = decoded.Envelope?.Schema ?? 0;
                return;
            }

            // Cloud checksums count only when the provider returns the stored text unchanged
            if (!decoded.IsOk && decoded.Cause == CorruptionCause.ChecksumMismatch && !preservesValueText)
            {
                decoded = DecodeUnverified(item.Bytes, slot.SupportedSchema);
            }

            if (!decoded.IsOk)
            {
                MarkCorrupt(item, decoded.Cause == CorruptionCause.None ? CorruptionCause.ParseFailed : decoded.Cause, decoded.Message, decoded.Exception);
                return;
            }

            SaveEnvelope envelope = decoded.Envelope;
            item.Envelope = envelope;
            item.FoundFormat = envelope.Format;
            item.FoundSchema = envelope.Schema;
            JToken payload = envelope.Data;
            if (envelope.Schema < slot.SupportedSchema)
            {
                PayloadUpgradeResult upgrade = slot.RunUpgrade(payload, envelope.Schema);
                if (!upgrade.IsSuccess)
                {
                    MarkCorrupt(item, CorruptionCause.MaterializeFailed, "Upgrade failed at schema " + upgrade.FailedFromSchema + ": " + upgrade.Message, upgrade.Exception);
                    return;
                }

                payload = upgrade.Payload;
            }

            Materialize(slot, item, payload);
        }

        // Header plus data without checksum verification
        private static EnvelopeDecodeResult DecodeUnverified(byte[] bytes, int supportedSchema)
        {
            EnvelopeDecodeResult header = EnvelopeCodec.PeekHeader(bytes, supportedSchema, false);
            if (!header.IsOk)
            {
                return header;
            }

            try
            {
                if (SaveJson.Parse(bytes) is JObject root)
                {
                    JToken data = root[SaveEnvelope.DataProperty];
                    if (data != null && data.Type != JTokenType.Null)
                    {
                        header.Envelope.Data = data;
                        return EnvelopeDecodeResult.Ok(header.Envelope, ChecksumStatus.Unverifiable);
                    }
                }
            }
            catch (Exception exception)
            {
                return EnvelopeDecodeResult.Corrupt(CorruptionCause.ParseFailed, exception.Message, exception);
            }

            return EnvelopeDecodeResult.Corrupt(CorruptionCause.ParseFailed, "data is missing.", null);
        }

        private static void ClassifyRaw(SaveSlot slot, CloudItem item)
        {
            item.ContentHash = PayloadChecksum.Compute(item.Bytes);
            RawPayloadDecodeResult decoded = EnvelopeCodec.DecodeRaw(item.Bytes);
            if (!decoded.IsOk)
            {
                MarkCorrupt(item, decoded.Cause, decoded.Message, decoded.Exception);
                return;
            }

            Materialize(slot, item, decoded.Data);
        }

        // ToObject and Normalize once; a throw classifies the value as corrupt (row 3)
        private static void Materialize(SaveSlot slot, CloudItem item, JToken payload)
        {
            SlotMaterializeResult materialized = slot.MaterializeNormalized(payload);
            if (!materialized.IsSuccess)
            {
                MarkCorrupt(item, CorruptionCause.MaterializeFailed, materialized.FailedStage + ": " + materialized.Message, materialized.Exception);
                return;
            }

            item.Data = materialized.Data;
            item.IsEmpty = slot.EvaluateIsEmpty(materialized.Data);
            item.State = CloudState.Found;
        }

        private static void MarkCorrupt(CloudItem item, CorruptionCause cause, string message, Exception exception)
        {
            item.State = CloudState.Corrupt;
            item.Cause = cause;
            item.Message = message;
            item.Exception = exception;
            item.Data = null;
        }

        // Unwritten local changes count as present; otherwise the file header decides
        private LocalPresence DeterminePresence(SaveSlot slot, string directory)
        {
            if (_context.Scheduler.IsLocalDirty(slot))
            {
                return LocalPresence.Present;
            }

            try
            {
                return _context.SlotStore.PeekHeader(directory, slot.Key, slot.SupportedSchema).Presence;
            }
            catch (Exception exception)
            {
                _context.Logger.Error("[SaveSystem] Peeking the local header of '" + slot.Key + "' failed; presence is undetermined.", exception);
                return LocalPresence.Undetermined;
            }
        }

        private ReconcileInput BuildInput(SlotWork work)
        {
            SaveSlot slot = work.Slot;
            CloudItem cloud = work.Cloud;
            SlotSyncState sync = _session.PeekSyncState(slot);

            // A landed pending write counts as synced, so a lost response is not a local change
            long syncedRevision = sync.LastSyncedRevision;
            if (cloud.Envelope != null && sync.IsPendingWriteId(cloud.Envelope.WriteId))
            {
                syncedRevision = Math.Max(syncedRevision, cloud.Envelope.Revision);
            }

            return new ReconcileInput
            {
                Mode = slot.SyncMode,
                Cloud = cloud.State,
                CloudWriteId = cloud.Envelope?.WriteId,
                CloudProviderVersion = cloud.Version,
                CloudContentHash = cloud.ContentHash,
                CloudIsEmpty = cloud.IsEmpty,
                LocalPresence = work.Presence,
                LocalFailure = slot.State == SlotState.Failed ? slot.Failure : SlotFailure.None,
                LocalHasContent = slot.State == SlotState.Ready && !slot.EvaluateIsEmpty(slot.DataBox),
                LocalDirty = slot.Revision > syncedRevision,
                LocalRevision = slot.Revision,
                NeedsCloudRecovery = sync.NeedsCloudRecovery,
                LastSyncedRevision = sync.LastSyncedRevision,
                LastSyncedWriteId = sync.LastSyncedWriteId,
                LastSyncedProviderVersion = sync.LastSyncedProviderVersion,
                PendingWriteId = sync.PendingWriteId,

                // Row 8 must see every unconfirmed id, not only the newest one
                PendingWriteIds = sync.PendingWriteIds,
                TombstonePending = slot.SyncMode == SyncMode.CloudSync && sync.PendingDelete,
                TombstoneDeletedWriteId = sync.DeletedWriteId,
            };
        }

        /// <summary>Classified cloud value of one slot.</summary>
        private sealed class CloudItem
        {
            public CloudState State;
            public byte[] Bytes;
            public string Version;
            public CloudError Error;

            /// <summary>Decoded CloudSync envelope; null for raw payloads and unusable values.</summary>
            public SaveEnvelope Envelope;

            /// <summary>Normalized cloud instance when Found.</summary>
            public object Data;

            public bool IsEmpty;
            public string ContentHash;
            public CorruptionCause Cause;
            public string Message;
            public Exception Exception;
            public int FoundFormat;
            public int FoundSchema;
        }

        /// <summary>Per-slot progress through decide, apply and persist.</summary>
        private sealed class SlotWork
        {
            public readonly SaveSlot Slot;
            public bool Fetch;
            public CloudReadResult ReadResult;
            public CloudItem Cloud;
            public LocalPresence Presence;
            public ReconcileInput Input;
            public ReconcileDecision Decision;
            public long DecidedRevision;
            public SlotRestoreOutcome Outcome = SlotRestoreOutcome.Failed;
            public SlotRestoreFailure Failure = SlotRestoreFailure.ApplyFailed;
            public CloudError CloudError;
            public bool LocalPersistFailed;

            /// <summary>Slot data changed in memory; its file is written in the persist phase.</summary>
            public bool WriteData;

            /// <summary>Write id recorded in the local envelope (the cloud write id after TakeCloud).</summary>
            public string LocalWriteId;

            public bool DeleteLocalFiles;
            public bool BackUpCorruptCloud;
            public bool DeleteCloud;
            public bool SyncChanged;
            public bool QueueUpload;
            public bool Reconciled;

            /// <summary>Row 2: revoke a ReconciledThisEpoch granted earlier in this epoch.</summary>
            public bool NeedsReconcile;

            public SlotWork(SaveSlot slot)
            {
                Slot = slot;
            }

            public bool IsSuccess => Outcome != SlotRestoreOutcome.Failed && Outcome != SlotRestoreOutcome.SkippedSchemaTooNew;

            public void Preset(SlotRestoreOutcome outcome, SlotRestoreFailure failure)
            {
                Fetch = false;
                Outcome = outcome;
                Failure = failure;
            }

            // Keeps side effects already on disk (backups); drops everything not yet persisted
            public void Fail(SlotRestoreFailure failure)
            {
                Outcome = SlotRestoreOutcome.Failed;
                Failure = failure;
                WriteData = false;
                DeleteLocalFiles = false;
                DeleteCloud = false;
                QueueUpload = false;
                Reconciled = false;
            }

            public SlotRestoreResult ToResult()
            {
                return new SlotRestoreResult(Slot.Key, Outcome, Failure, LocalPersistFailed, CloudError);
            }
        }
    }
}
