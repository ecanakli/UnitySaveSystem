using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Ecanakli.SaveSystem
{
    /// <summary>
    /// Uploads a batch of Profile CloudSync slots for the active account (01 5.3 with 04a deltas, 04b N6).
    /// The caller holds the OperationGate; retry timing after exhaustion belongs to SaveScheduler. Main thread only.
    /// </summary>
    internal sealed class CloudUploader
    {
        private static readonly IReadOnlyDictionary<string, long> NoStoredBytes = new Dictionary<string, long>(StringComparer.Ordinal);

        private readonly SaveContext _context;
        private readonly ProfileSession _session;
        private readonly Action<SaveSlot> _requestSlotReconcile;

        /// <param name="requestSlotReconcile">Schedules a single-slot reconcile to run after the gate is released; must not run it synchronously.</param>
        public CloudUploader(SaveContext context, ProfileSession session, Action<SaveSlot> requestSlotReconcile)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _requestSlotReconcile = requestSlotReconcile ?? throw new ArgumentNullException(nameof(requestSlotReconcile));
        }

        /// <summary>
        /// One result per Profile CloudSync slot in input order (other slots are ignored): Uploaded, AlreadyInSync, Skipped(reason) or
        /// Failed(CloudError | Aborted). Cancellation of ct is reported as Failed(Aborted), never thrown.
        /// </summary>
        public async UniTask<IReadOnlyList<CloudFlushSlotResult>> UploadAsync(IReadOnlyList<SaveSlot> slots, CancellationToken ct)
        {
            if (slots == null)
            {
                throw new ArgumentNullException(nameof(slots));
            }

            List<SaveSlot> eligible = SelectEligible(slots);
            var results = new CloudFlushSlotResult[eligible.Count];
            if (eligible.Count == 0)
            {
                return results;
            }

            ProfileId profile = _session.ActiveProfile;
            long epoch = _session.Epoch;
            CloudFlushReason refusal = CheckBatch(profile, epoch, ct);
            if (refusal != CloudFlushReason.None)
            {
                for (int i = 0; i < eligible.Count; i++)
                {
                    results[i] = Create(eligible[i], refusal);
                    RescheduleIfRetryable(eligible[i], refusal);
                }

                return results;
            }

            List<PendingUpload> pending = Prepare(eligible, results, profile);
            if (pending.Count == 0)
            {
                return results;
            }

            pending = await ExcludeValuesCreatedElsewhereAsync(pending, results, profile, epoch, ct);
            if (pending.Count == 0 || !PersistPendingWriteIds(pending, results))
            {
                return results;
            }

            // Account check immediately before send; nothing awaited since the guard
            refusal = CheckAccount(profile, epoch, ct);
            if (refusal != CloudFlushReason.None)
            {
                FillPending(results, pending, refusal);
                return results;
            }

            IReadOnlyList<CloudWriteResult> responses;
            try
            {
                responses = await _context.CloudGateway.WriteAsync(BuildRequests(pending), BuildStoredBytes(), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                FillPending(results, pending, CloudFlushReason.Aborted);
                return results;
            }
            catch (Exception exception)
            {
                _context.Logger.Error("[SaveSystem] Cloud upload batch threw unexpectedly.", exception);
                var error = new CloudError(CloudErrorKind.Transient, "Upload batch threw: " + exception.Message, null, exception);
                for (int k = 0; k < pending.Count; k++)
                {
                    _context.Scheduler.RescheduleTransient(pending[k].Slot);
                    results[pending[k].Index] = new CloudFlushSlotResult(pending[k].Slot.Key, CloudFlushStatus.Failed, CloudFlushReason.CloudError, error);
                }

                return results;
            }

            // Superseded epoch: drop results; the persisted PendingWriteId lets reconcile recognize a landed write
            if (_session.IsDisposed || _session.Epoch != epoch || _session.ActiveProfile != profile)
            {
                FillPending(results, pending, CloudFlushReason.Aborted);
                return results;
            }

            ApplyResults(pending, responses, results, profile);
            return results;
        }

        private static List<SaveSlot> SelectEligible(IReadOnlyList<SaveSlot> slots)
        {
            var eligible = new List<SaveSlot>(slots.Count);
            for (int i = 0; i < slots.Count; i++)
            {
                SaveSlot slot = slots[i];
                if (slot != null && slot.Scope == SlotScope.Profile && slot.SyncMode == SyncMode.CloudSync && !eligible.Contains(slot))
                {
                    eligible.Add(slot);
                }
            }

            return eligible;
        }

        private CloudFlushReason CheckBatch(ProfileId profile, long epoch, CancellationToken ct)
        {
            if (ct.IsCancellationRequested || _session.IsDisposed || _session.Epoch != epoch)
            {
                return CloudFlushReason.Aborted;
            }

            if (!_session.IsInitialized || _session.IsSwitchingProfile || _session.ActiveProfileDirectory == null)
            {
                return CloudFlushReason.SlotNotReady;
            }

            if (!profile.IsCloudBacked)
            {
                return CloudFlushReason.ProfileNotCloudBacked;
            }

            return CheckAccount(profile, epoch, ct);
        }

        private CloudFlushReason CheckAccount(ProfileId profile, long epoch, CancellationToken ct)
        {
            if (ct.IsCancellationRequested || _session.IsDisposed || _session.Epoch != epoch || _session.ActiveProfile != profile)
            {
                return CloudFlushReason.Aborted;
            }

            string signedIn;
            try
            {
                signedIn = _context.CloudGateway.SignedInAccountId;
            }
            catch (Exception exception)
            {
                _context.Logger.Error("[SaveSystem] ICloudSaveProvider.SignedInAccountId threw; treated as signed out.", exception);
                signedIn = null;
            }

            if (string.IsNullOrEmpty(signedIn))
            {
                return CloudFlushReason.NotSignedIn;
            }

            return string.Equals(signedIn, profile.AccountId, StringComparison.Ordinal) ? CloudFlushReason.None : CloudFlushReason.AccountMismatch;
        }

        // Guard, snapshot, envelope, size precheck and disk-first write per slot
        private List<PendingUpload> Prepare(List<SaveSlot> eligible, CloudFlushSlotResult[] results, ProfileId profile)
        {
            var pending = new List<PendingUpload>(eligible.Count);
            CloudCapabilities capabilities;
            try
            {
                capabilities = _context.CloudGateway.Capabilities;
            }
            catch (Exception exception)
            {
                _context.Logger.Error("[SaveSystem] Cloud capabilities are unavailable; nothing was uploaded.", exception);
                var error = new CloudError(CloudErrorKind.Permanent, exception.Message, null, exception);
                for (int i = 0; i < eligible.Count; i++)
                {
                    results[i] = new CloudFlushSlotResult(eligible[i].Key, CloudFlushStatus.Failed, CloudFlushReason.CloudError, error);
                }

                return pending;
            }

            string directory = _session.ActiveProfileDirectory;
            for (int i = 0; i < eligible.Count; i++)
            {
                SaveSlot slot = eligible[i];
                string key = slot.Key;
                CloudFlushSlotResult guard = EvaluateGuard(slot, directory);
                if (guard != null)
                {
                    results[i] = guard;
                    RescheduleIfRetryable(slot, guard.Reason);
                    continue;
                }

                SlotSnapshot snapshot = slot.CaptureSnapshot(true);
                if (!snapshot.IsSuccess)
                {
                    string reason = snapshot.Exception != null ? snapshot.Exception.Message : "unknown error";
                    results[i] = FailPermanent(slot, profile, new CloudError(CloudErrorKind.Permanent, "Snapshot of '" + key + "' failed: " + reason, null, snapshot.Exception));
                    continue;
                }

                SlotSyncState syncState = _session.PeekSyncState(slot);
                if (syncState.HadContent && snapshot.IsEmpty == true)
                {
                    const string message = "Empty data was not uploaded over content that existed before.";
                    _context.Logger.Warning("[SaveSystem] Slot '" + key + "': " + message);
                    results[i] = Create(slot, CloudFlushReason.EmptyOverContent);
                    _context.RaiseUploadFailed(new UploadFailure(profile, key, CloudFlushReason.EmptyOverContent, null, message));
                    continue;
                }

                string writeId = Guid.NewGuid().ToString("N");
                var envelope = new SaveEnvelope
                {
                    Format = SaveEnvelope.CurrentFormat,
                    Schema = slot.SupportedSchema,
                    Revision = snapshot.Revision,
                    WriteId = writeId,
                    SavedAtUtc = _context.Clock.UtcNow,
                    DeviceId = _session.DeviceId,
                    AccountId = profile.AccountId,
                    Data = snapshot.Data,
                };

                EncodedEnvelope encoded;
                try
                {
                    encoded = EnvelopeCodec.Encode(envelope);
                }
                catch (Exception exception)
                {
                    results[i] = FailPermanent(slot, profile, new CloudError(CloudErrorKind.Permanent, "Encoding '" + key + "' failed: " + exception.Message, null, exception));
                    continue;
                }

                if (encoded.Bytes.Length > capabilities.MaxValueBytes)
                {
                    results[i] = FailPermanent(slot, profile, new CloudError(
                        CloudErrorKind.PayloadTooLarge,
                        "Value of '" + key + "' is " + encoded.Bytes.Length + " bytes; the provider limit is " + capabilities.MaxValueBytes + "."));
                    continue;
                }

                // Disk first: the cloud never gets a revision the disk does not have
                if (_context.Scheduler.IsLocalDirty(slot))
                {
                    SlotWriteResult written = _context.SlotStore.WriteEncoded(directory, key, encoded, snapshot.Revision);

                    // A refusal is not a disk failure: it never reaches LocalWriteTracker or the health event
                    if (written.IsRefused)
                    {
                        results[i] = Create(slot, RefusalReason(slot, key, written));
                        continue;
                    }

                    _session.ReportLocalWrite(_session.GetLocalWriteTarget(slot), written.IsSuccess, written.ErrorKind, written.Message);
                    if (!written.IsSuccess)
                    {
                        results[i] = Create(slot, CloudFlushReason.LocalWriteFailed);
                        _context.Scheduler.RescheduleTransient(slot);
                        continue;
                    }

                    _context.Scheduler.ClearLocalIfRevisionUnchanged(slot, snapshot.Revision);
                    _context.MutationDetector.RecordBaseline(slot, snapshot.Revision, written.DataSha256);
                }

                if (snapshot.IsEmpty == false && !syncState.HadContent)
                {
                    _session.GetSyncState(slot).HadContent = true;
                }

                string expectedVersion = capabilities.SupportsConditionalWrite ? syncState.LastSyncedProviderVersion : null;
                pending.Add(new PendingUpload(i, slot, snapshot.Revision, writeId, encoded.Bytes, expectedVersion));
            }

            return pending;
        }

        // Skip reason of a refused disk-first write; a too-new file needs a new build, a stale revision only a fresh snapshot
        private CloudFlushReason RefusalReason(SaveSlot slot, string key, in SlotWriteResult written)
        {
            if (written.Status == SlotWriteStatus.RefusedSchemaTooNew)
            {
                return CloudFlushReason.SchemaTooNew;
            }

            if (_context.Logger.IsVerboseEnabled)
            {
                _context.Logger.Verbose(
                    "[SaveSystem] Slot '" + key + "': revision " + written.DurableRevision + " is already durable; the upload of revision "
                    + written.Revision + " is skipped and retried with the newer revision.");
            }

            _context.Scheduler.RescheduleTransient(slot);
            return CloudFlushReason.SlotNotReady;
        }

        // Null means upload; otherwise AlreadyInSync or a skip
        private CloudFlushSlotResult EvaluateGuard(SaveSlot slot, string directory)
        {
            if (slot.State != SlotState.Ready)
            {
                return Create(slot, slot.Failure == SlotFailure.SchemaTooNew ? CloudFlushReason.SchemaTooNew : CloudFlushReason.SlotNotReady);
            }

            if (_context.SlotStore.IsKnownSchemaTooNew(directory, slot.Key))
            {
                return Create(slot, CloudFlushReason.SchemaTooNew);
            }

            if (!_session.IsReconciledThisEpoch(slot))
            {
                return Create(slot, CloudFlushReason.NotReconciled);
            }

            // Tombstones and lost local copies are settled by the next restore
            SlotSyncState syncState = _session.PeekSyncState(slot);
            if (syncState.PendingDelete || syncState.NeedsCloudRecovery)
            {
                return Create(slot, CloudFlushReason.NotReconciled);
            }

            bool dirty = _context.Scheduler.IsCloudDirty(slot) || slot.Revision > syncState.LastSyncedRevision;
            if (!dirty && syncState.PendingWriteId == null)
            {
                return new CloudFlushSlotResult(slot.Key, CloudFlushStatus.AlreadyInSync, CloudFlushReason.None);
            }

            if (_context.Scheduler.IsUploadSuspended(slot))
            {
                return Create(slot, CloudFlushReason.SuspendedUntilReconcile);
            }

            if (_context.LocalWriteTracker.IsFailing(_session.GetLocalWriteTarget(slot)))
            {
                return Create(slot, CloudFlushReason.LocalWriteFailed);
            }

            return null;
        }

        // R9 asks whether another device created the value; an id still in our ring answers no
        private bool IsOwnUnconfirmedValue(PendingUpload upload, CloudReadResult read)
        {
            SlotSyncState sync = _session.PeekSyncState(upload.Slot);
            if (sync == null || read.Value == null)
            {
                return false;
            }

            // Identity only, so the checksum is not verified here; a corrupt value simply fails to match
            EnvelopeDecodeResult header = EnvelopeCodec.PeekHeader(read.Value, upload.Slot.SupportedSchema, false);
            return header.Envelope != null && sync.IsPendingWriteId(header.Envelope.WriteId);
        }

        // N6: the pending write ids are on disk before the provider call; a failed persist skips the uploads
        private bool PersistPendingWriteIds(List<PendingUpload> pending, CloudFlushSlotResult[] results)
        {
            var previous = new string[pending.Count][];
            for (int k = 0; k < pending.Count; k++)
            {
                SlotSyncState syncState = _session.GetSyncState(pending[k].Slot);

                // The earlier ids stay: an upload whose response was lost may still be the value in the cloud
                previous[k] = ToArray(syncState.PendingWriteIds);
                syncState.PendingWriteId = pending[k].WriteId;
            }

            StateWriteResult persisted = _session.PersistSyncState();
            if (persisted.IsSuccess)
            {
                return true;
            }

            for (int k = 0; k < pending.Count; k++)
            {
                PendingUpload upload = pending[k];
                _session.GetSyncState(upload.Slot).SetPendingWriteIds(previous[k]);
                results[upload.Index] = Create(upload.Slot, CloudFlushReason.LocalWriteFailed);
                _context.Scheduler.RescheduleTransient(upload.Slot);
            }

            _context.Logger.Warning("[SaveSystem] PendingWriteId could not be persisted; " + pending.Count + " upload(s) skipped. " + persisted.Message);
            return false;
        }

        private static string[] ToArray(IReadOnlyList<string> values)
        {
            var copy = new string[values.Count];
            for (int i = 0; i < values.Count; i++)
            {
                copy[i] = values[i];
            }

            return copy;
        }

        // R9: a slot never synced for this account re-reads its key first; a value created elsewhere is reconciled, not overwritten
        private async UniTask<List<PendingUpload>> ExcludeValuesCreatedElsewhereAsync(
            List<PendingUpload> pending, CloudFlushSlotResult[] results, ProfileId profile, long epoch, CancellationToken ct)
        {
            List<PendingUpload> firstWrites = null;
            for (int k = 0; k < pending.Count; k++)
            {
                SlotSyncState sync = _session.PeekSyncState(pending[k].Slot);
                bool neverSynced = sync == null || (sync.LastSyncedWriteId == null && sync.LastSyncedProviderVersion == null);
                if (pending[k].ExpectedVersion == null && neverSynced)
                {
                    firstWrites = firstWrites ?? new List<PendingUpload>();
                    firstWrites.Add(pending[k]);
                }
            }

            if (firstWrites == null)
            {
                return pending;
            }

            var requests = new CloudReadRequest[firstWrites.Count];
            for (int k = 0; k < firstWrites.Count; k++)
            {
                requests[k] = new CloudReadRequest(firstWrites[k].Slot.Key, CloudAccess.ClientOwned);
            }

            IReadOnlyList<CloudReadResult> reads;
            try
            {
                reads = await _context.CloudGateway.ReadAsync(requests, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                FillPending(results, pending, CloudFlushReason.Aborted);
                return new List<PendingUpload>();
            }
            catch (Exception exception)
            {
                _context.Logger.Error("[SaveSystem] Pre-upload read threw unexpectedly.", exception);
                reads = null;
            }

            if (_session.IsDisposed || _session.Epoch != epoch || _session.ActiveProfile != profile)
            {
                FillPending(results, pending, CloudFlushReason.Aborted);
                return new List<PendingUpload>();
            }

            var kept = new List<PendingUpload>(pending.Count);
            List<SaveSlot> reconcile = null;
            for (int k = 0; k < pending.Count; k++)
            {
                PendingUpload upload = pending[k];
                if (!firstWrites.Contains(upload))
                {
                    kept.Add(upload);
                    continue;
                }

                CloudReadResult read = FindRead(reads, upload.Slot.Key);
                if (read != null && read.Status == CloudReadStatus.NotFound)
                {
                    kept.Add(upload);
                    continue;
                }

                if (read != null && read.Status == CloudReadStatus.Found)
                {
                    // A landed write whose response was lost is still ours; without this the slot cannot upload again until a reconcile
                    if (IsOwnUnconfirmedValue(upload, read))
                    {
                        kept.Add(upload);
                        continue;
                    }

                    _session.MarkNeedsReconcile(upload.Slot);
                    reconcile = reconcile ?? new List<SaveSlot>();
                    reconcile.Add(upload.Slot);
                    results[upload.Index] = Create(upload.Slot, CloudFlushReason.NotReconciled);
                    _context.Logger.Info("[SaveSystem] '" + upload.Slot.Key + "' was created in the cloud by another device; reconciling instead of uploading.");
                    continue;
                }

                CloudError error = read?.Error ?? new CloudError(CloudErrorKind.Transient, "Pre-upload read of '" + upload.Slot.Key + "' returned no result.");
                _context.Scheduler.RescheduleTransient(upload.Slot);
                results[upload.Index] = new CloudFlushSlotResult(upload.Slot.Key, CloudFlushStatus.Failed, CloudFlushReason.CloudError, error);
            }

            if (reconcile != null)
            {
                for (int i = 0; i < reconcile.Count; i++)
                {
                    try
                    {
                        _requestSlotReconcile(reconcile[i]);
                    }
                    catch (Exception exception)
                    {
                        _context.Logger.Error("[SaveSystem] Requesting a reconcile for '" + reconcile[i].Key + "' threw.", exception);
                    }
                }
            }

            return kept;
        }

        private static CloudReadResult FindRead(IReadOnlyList<CloudReadResult> reads, string key)
        {
            if (reads == null)
            {
                return null;
            }

            for (int i = 0; i < reads.Count; i++)
            {
                if (reads[i] != null && string.Equals(reads[i].Key, key, StringComparison.Ordinal))
                {
                    return reads[i];
                }
            }

            return null;
        }

        private static CloudWriteRequest[] BuildRequests(List<PendingUpload> pending)
        {
            var requests = new CloudWriteRequest[pending.Count];
            for (int k = 0; k < pending.Count; k++)
            {
                PendingUpload upload = pending[k];
                requests[k] = new CloudWriteRequest(upload.Slot.Key, upload.Bytes, upload.ExpectedVersion, CloudAccess.ClientOwned);
            }

            return requests;
        }

        // Last known stored sizes of the profile's CloudSync keys for the gateway quota precheck
        private IReadOnlyDictionary<string, long> BuildStoredBytes()
        {
            IReadOnlyList<SaveSlot> cloudSlots = _context.Registry.Select(SlotScope.Profile, SyncMode.CloudSync);
            Dictionary<string, long> stored = null;
            for (int i = 0; i < cloudSlots.Count; i++)
            {
                long bytes = _session.PeekSyncState(cloudSlots[i]).LastUploadedBytes;
                if (bytes > 0)
                {
                    stored = stored ?? new Dictionary<string, long>(StringComparer.Ordinal);
                    stored[cloudSlots[i].Key] = bytes;
                }
            }

            return stored ?? NoStoredBytes;
        }

        private void ApplyResults(List<PendingUpload> pending, IReadOnlyList<CloudWriteResult> responses, CloudFlushSlotResult[] results, ProfileId profile)
        {
            bool persist = false;
            int uploaded = 0;
            List<SaveSlot> reconcile = null;
            for (int k = 0; k < pending.Count; k++)
            {
                PendingUpload upload = pending[k];
                CloudWriteResult response = responses != null && k < responses.Count ? responses[k] : null;
                if (response != null && response.IsSuccess)
                {
                    // LastSynced only after confirmation
                    SlotSyncState syncState = _session.GetSyncState(upload.Slot);
                    syncState.LastSyncedRevision = upload.Revision;
                    syncState.LastSyncedWriteId = upload.WriteId;
                    syncState.LastSyncedProviderVersion = response.NewVersion;
                    syncState.LastUploadedBytes = upload.Bytes.Length;

                    // The confirmed write supersedes every older unconfirmed one
                    syncState.ConfirmPendingWriteId(upload.WriteId);

                    persist = true;
                    uploaded++;
                    _context.Scheduler.ClearCloudIfRevisionUnchanged(upload.Slot, upload.Revision);
                    results[upload.Index] = new CloudFlushSlotResult(upload.Slot.Key, CloudFlushStatus.Uploaded, CloudFlushReason.None);
                    continue;
                }

                CloudError error = response?.Error ?? new CloudError(CloudErrorKind.Permanent, "The provider returned no result for '" + upload.Slot.Key + "'.");

                // A refused write never reaches the cloud, so its id must not hide an older unconfirmed one
                if (IsRefusal(error.Kind) && _session.GetSyncState(upload.Slot).DropPendingWriteId(upload.WriteId))
                {
                    persist = true;
                }

                results[upload.Index] = HandleWriteFailure(upload.Slot, profile, error, ref reconcile);
            }

            if (persist)
            {
                StateWriteResult persisted = _session.PersistSyncState();
                if (!persisted.IsSuccess)
                {
                    _context.Logger.Warning("[SaveSystem] Upload bookkeeping is recorded in memory only; reconcile recognizes a landed write by PendingWriteId. " + persisted.Message);
                }
            }

            if (_context.Logger.IsVerboseEnabled)
            {
                _context.Logger.Verbose("[SaveSystem] Upload batch for " + profile + ": " + uploaded + "/" + pending.Count + " uploaded.");
            }

            if (reconcile != null)
            {
                for (int i = 0; i < reconcile.Count; i++)
                {
                    try
                    {
                        _requestSlotReconcile(reconcile[i]);
                    }
                    catch (Exception exception)
                    {
                        _context.Logger.Error("[SaveSystem] Requesting a reconcile for '" + reconcile[i].Key + "' threw.", exception);
                    }
                }
            }
        }

        private CloudFlushSlotResult HandleWriteFailure(SaveSlot slot, ProfileId profile, CloudError error, ref List<SaveSlot> reconcile)
        {
            switch (error.Kind)
            {
                case CloudErrorKind.Conflict:
                    _session.MarkNeedsReconcile(slot);
                    reconcile = reconcile ?? new List<SaveSlot>();
                    reconcile.Add(slot);
                    _context.Logger.Info("[SaveSystem] Upload of '" + slot.Key + "' hit a version conflict; a single-slot reconcile was requested.");
                    return new CloudFlushSlotResult(slot.Key, CloudFlushStatus.Failed, CloudFlushReason.CloudError, error);
                case CloudErrorKind.PayloadTooLarge:
                case CloudErrorKind.QuotaExceeded:
                case CloudErrorKind.Unauthorized:
                case CloudErrorKind.Permanent:
                    return FailPermanent(slot, profile, error);
                default:
                    TimeSpan delay = _context.Scheduler.RescheduleTransient(slot);
                    if (_context.Logger.IsVerboseEnabled)
                    {
                        _context.Logger.Verbose("[SaveSystem] Upload of '" + slot.Key + "' failed (" + error + "); retry in " + delay.TotalSeconds + " s.");
                    }

                    return new CloudFlushSlotResult(slot.Key, CloudFlushStatus.Failed, CloudFlushReason.CloudError, error);
            }
        }

        // Raise UploadFailed and suspend until the next successful reconcile; the slot stays dirty on disk
        private CloudFlushSlotResult FailPermanent(SaveSlot slot, ProfileId profile, CloudError error)
        {
            _context.Logger.Error("[SaveSystem] Upload of '" + slot.Key + "' failed permanently (" + error + "); uploads are suspended until the next reconcile.", error.Exception);
            _context.Scheduler.SuspendUntilReconcile(slot);
            _context.RaiseUploadFailed(new UploadFailure(profile, slot.Key, CloudFlushReason.CloudError, error));
            return new CloudFlushSlotResult(slot.Key, CloudFlushStatus.Failed, CloudFlushReason.CloudError, error);
        }

        // Dirty slots skipped for a reason that clears without a new mutation get the capped transient backoff
        private void RescheduleIfRetryable(SaveSlot slot, CloudFlushReason reason)
        {
            bool retryable = reason == CloudFlushReason.LocalWriteFailed || reason == CloudFlushReason.NotSignedIn || reason == CloudFlushReason.AccountMismatch;
            if (retryable && _context.Scheduler.IsCloudDirty(slot))
            {
                _context.Scheduler.RescheduleTransient(slot);
            }
        }

        private static void FillPending(CloudFlushSlotResult[] results, List<PendingUpload> pending, CloudFlushReason reason)
        {
            for (int k = 0; k < pending.Count; k++)
            {
                results[pending[k].Index] = Create(pending[k].Slot, reason);
            }
        }

        // Only the Precheck kinds: they are decided before any provider call, so the write can never be in the cloud.
        // Conflict and Unauthorized are excluded: the gateway retries with the same write id, so they can answer a write that already landed.
        private static bool IsRefusal(CloudErrorKind kind)
        {
            return kind == CloudErrorKind.PayloadTooLarge || kind == CloudErrorKind.QuotaExceeded;
        }

        private static CloudFlushSlotResult Create(SaveSlot slot, CloudFlushReason reason)
        {
            CloudFlushStatus status = reason == CloudFlushReason.Aborted || reason == CloudFlushReason.CloudError ? CloudFlushStatus.Failed : CloudFlushStatus.Skipped;
            return new CloudFlushSlotResult(slot.Key, status, reason);
        }

        private sealed class PendingUpload
        {
            public PendingUpload(int index, SaveSlot slot, long revision, string writeId, byte[] bytes, string expectedVersion)
            {
                Index = index;
                Slot = slot;
                Revision = revision;
                WriteId = writeId;
                Bytes = bytes;
                ExpectedVersion = expectedVersion;
            }

            public int Index { get; }

            public SaveSlot Slot { get; }

            public long Revision { get; }

            public string WriteId { get; }

            public byte[] Bytes { get; }

            public string ExpectedVersion { get; }
        }
    }
}
