using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Ecanakli.SaveSystem
{
    // ISaveSchedulerTarget, strict FlushAsync (N2) and conflict reconciles deferred until the gate is released
    public sealed partial class SaveService
    {
        private readonly List<SaveSlot> _deferredReconciles = new List<SaveSlot>();

        // Uploads currently holding the gate; reconcile requests made meanwhile are deferred
        private int _uploadsHoldingGate;

        LocalWriteTarget ISaveSchedulerTarget.GetLocalWriteTarget(SaveSlot slot)
        {
            return _session.GetLocalWriteTarget(slot);
        }

        UniTask ISaveSchedulerTarget.WriteLocalAsync(IReadOnlyList<SaveSlot> slots, CancellationToken epochToken)
        {
            return WriteLocalScheduledAsync(slots, epochToken);
        }

        UniTask ISaveSchedulerTarget.UploadAsync(IReadOnlyList<SaveSlot> slots, CancellationToken epochToken)
        {
            return UploadScheduledAsync(slots, epochToken);
        }

        /// <summary>
        /// FlushLocalNow, then under the gate every Profile CloudSync slot is uploaded now, AlreadyInSync or skipped with a reason.
        /// Guest and Local profiles report Skipped(ProfileNotCloudBacked). OperationCanceledException only before the gate is held.
        /// </summary>
        public async UniTask<FlushResult> FlushAsync(CancellationToken ct)
        {
            if (HookScope.RefuseIfActive(_logger, nameof(FlushAsync)))
            {
                return CreateFlushResultRefusal(SaveErrorCode.CalledFromHook, nameof(FlushAsync) + HookRefusalSuffix);
            }

            if (_disposed)
            {
                return CreateFlushResultRefusal(SaveErrorCode.Disposed, nameof(FlushAsync) + DisposedRefusalSuffix);
            }

            if (!_session.IsInitialized)
            {
                return CreateFlushResultRefusal(SaveErrorCode.NotInitialized, "FlushAsync requires InitializeAsync first.");
            }

            ct.ThrowIfCancellationRequested();

            LocalFlushResult local = FlushLocalCore();
            IReadOnlyList<SaveSlot> cloudSlots = _registry.Select(SlotScope.Profile, SyncMode.CloudSync);
            if (!_session.ActiveProfile.IsCloudBacked)
            {
                return new FlushResult(SaveStatus.Success, null, local, CreateCloudResults(cloudSlots, CloudFlushStatus.Skipped, CloudFlushReason.ProfileNotCloudBacked));
            }

            if (cloudSlots.Count == 0)
            {
                return new FlushResult(SaveStatus.Success, null, local, null);
            }

            long epoch = _session.Epoch;
            CancellationToken epochToken = _session.EpochToken;
            OperationGate.Releaser releaser = default;
            bool gateHeld = false;
            try
            {
                using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, epochToken))
                {
                    try
                    {
                        releaser = await _gate.AcquireAsync(linked.Token);
                        gateHeld = true;
                    }
                    catch (OperationCanceledException) when (linked.IsCancellationRequested)
                    {
                    }
                    catch (ObjectDisposedException)
                    {
                    }

                    if (!gateHeld)
                    {
                        ct.ThrowIfCancellationRequested();
                        return CreateAbortedFlush(local, cloudSlots);
                    }

                    if (_disposed || _session.Epoch != epoch)
                    {
                        return CreateAbortedFlush(local, cloudSlots);
                    }

                    IReadOnlyList<CloudFlushSlotResult> uploaded;
                    _uploadsHoldingGate++;
                    try
                    {
                        // Caller cancel or a switch mid-run reports Failed(Aborted), never throws
                        uploaded = await _uploader.UploadAsync(cloudSlots, linked.Token);
                    }
                    finally
                    {
                        _uploadsHoldingGate--;
                    }

                    CloudFlushSlotResult[] results = CompleteCloudResults(uploaded, cloudSlots);
                    if (_disposed || _session.Epoch != epoch)
                    {
                        SaveErrorCode code = _disposed ? SaveErrorCode.Disposed : SaveErrorCode.Superseded;
                        return new FlushResult(SaveStatus.Failed, new SaveError(code, "The flush was interrupted by " + (_disposed ? "Dispose." : "a profile switch.")), local, results);
                    }

                    return new FlushResult(ct.IsCancellationRequested ? SaveStatus.Canceled : SaveStatus.Success, null, local, results);
                }
            }
            finally
            {
                if (gateHeld)
                {
                    releaser.Dispose();
                }

                DrainDeferredReconciles();
            }
        }

        private async UniTask WriteLocalScheduledAsync(IReadOnlyList<SaveSlot> slots, CancellationToken epochToken)
        {
            if (_disposed || slots == null || slots.Count == 0 || epochToken.IsCancellationRequested)
            {
                return;
            }

            DateTime now = _clock.UtcNow;
            long generation = _localWriteGeneration;
            List<LocalWriteJob> jobs = null;
            bool refusedHealthChanged = false;
            for (int i = 0; i < slots.Count; i++)
            {
                SaveSlot slot = slots[i];
                if (!_scheduler.IsLocalDirty(slot))
                {
                    continue;
                }

                // A dispatched slot the write path cannot serve is recorded, never dropped: backoff owns the retry
                if (slot.State != SlotState.Ready)
                {
                    RecordWriteRefusal(slot, "Slot '" + slot.Key + "' is " + slot.State + "; the scheduled local write was skipped.", ref refusedHealthChanged);
                    continue;
                }

                // Scheduled writes respect backoff; explicit requests do not
                if (!_localWriteTracker.CanAttemptScheduledWrite(_session.GetLocalWriteTarget(slot), now))
                {
                    continue;
                }

                LocalWriteJob job = PrepareLocalWrite(slot, generation, out string prepareError);
                if (job == null)
                {
                    RecordWriteRefusal(slot, prepareError, ref refusedHealthChanged);
                    continue;
                }

                (jobs ??= new List<LocalWriteJob>(slots.Count)).Add(job);
            }

            if (jobs == null)
            {
                RaiseHealthIfChanged(refusedHealthChanged);
                return;
            }

            if (_options.OffloadIo)
            {
                // Results are always applied; a superseded or stale-epoch job is ignored there
                await UniTask.RunOnThreadPool(() => ExecuteLocalWrites(jobs), true, CancellationToken.None);
            }
            else
            {
                ExecuteLocalWrites(jobs);
            }

            if (_disposed)
            {
                return;
            }

            bool healthChanged = refusedHealthChanged;
            bool persistContentMarker = false;
            for (int i = 0; i < jobs.Count; i++)
            {
                ApplyLocalWriteResult(jobs[i], ref healthChanged, ref persistContentMarker);
            }

            RaiseHealthIfChanged(healthChanged);
            if (persistContentMarker)
            {
                PersistContentMarker();
            }
        }

        // Epoch-token cancellation propagates to the scheduler, which owns that token
        private async UniTask UploadScheduledAsync(IReadOnlyList<SaveSlot> slots, CancellationToken epochToken)
        {
            if (_disposed || slots == null || slots.Count == 0 || !_session.IsInitialized || !_session.ActiveProfile.IsCloudBacked)
            {
                return;
            }

            OperationGate.Releaser releaser;
            try
            {
                releaser = await _gate.AcquireAsync(epochToken);
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            _uploadsHoldingGate++;
            try
            {
                if (_disposed || epochToken.IsCancellationRequested)
                {
                    return;
                }

                await _uploader.UploadAsync(slots, epochToken);
            }
            finally
            {
                _uploadsHoldingGate--;
                releaser.Dispose();
                DrainDeferredReconciles();
            }
        }

        // CloudUploader calls this under the gate; the reconcile is forwarded after the gate is released
        private void DeferSlotReconcile(SaveSlot slot)
        {
            if (slot == null || _disposed)
            {
                return;
            }

            if (_uploadsHoldingGate > 0)
            {
                if (!_deferredReconciles.Contains(slot))
                {
                    _deferredReconciles.Add(slot);
                }

                return;
            }

            _restore.RequestSlotReconcile(slot);
        }

        private void DrainDeferredReconciles()
        {
            if (_uploadsHoldingGate > 0 || _deferredReconciles.Count == 0)
            {
                return;
            }

            SaveSlot[] pending = _deferredReconciles.ToArray();
            _deferredReconciles.Clear();
            if (_disposed)
            {
                return;
            }

            for (int i = 0; i < pending.Length; i++)
            {
                try
                {
                    // RestoreOperation schedules it after a player-loop yield
                    _restore.RequestSlotReconcile(pending[i]);
                }
                catch (Exception exception)
                {
                    _logger.Error("[SaveSystem] Requesting a reconcile for '" + pending[i].Key + "' threw.", exception);
                }
            }
        }

        private FlushResult CreateAbortedFlush(LocalFlushResult local, IReadOnlyList<SaveSlot> cloudSlots)
        {
            SaveErrorCode code = _disposed ? SaveErrorCode.Disposed : SaveErrorCode.Superseded;
            string message = _disposed ? "The save service was disposed before the upload started." : "The active profile changed before the upload started.";
            return new FlushResult(SaveStatus.Failed, new SaveError(code, message), local, CreateCloudResults(cloudSlots, CloudFlushStatus.Failed, CloudFlushReason.Aborted));
        }

        private static FlushResult CreateFlushResultRefusal(SaveErrorCode code, string message)
        {
            return new FlushResult(SaveStatus.Failed, new SaveError(code, message), LocalFlushResult.Complete, null);
        }

        private static CloudFlushSlotResult[] CreateCloudResults(IReadOnlyList<SaveSlot> slots, CloudFlushStatus status, CloudFlushReason reason)
        {
            var results = new CloudFlushSlotResult[slots.Count];
            for (int i = 0; i < slots.Count; i++)
            {
                results[i] = new CloudFlushSlotResult(slots[i].Key, status, reason);
            }

            return results;
        }

        // Uploader results are in slot order; a missing entry is an unknown outcome
        private static CloudFlushSlotResult[] CompleteCloudResults(IReadOnlyList<CloudFlushSlotResult> uploaded, IReadOnlyList<SaveSlot> slots)
        {
            var results = new CloudFlushSlotResult[slots.Count];
            for (int i = 0; i < slots.Count; i++)
            {
                CloudFlushSlotResult entry = uploaded != null && i < uploaded.Count ? uploaded[i] : null;
                results[i] = entry ?? new CloudFlushSlotResult(slots[i].Key, CloudFlushStatus.Failed, CloudFlushReason.Aborted);
            }

            return results;
        }
    }
}
