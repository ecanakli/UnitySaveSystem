using System;
using System.Collections.Generic;

namespace Ecanakli.SaveSystem
{
    // Local write pipeline shared by the scheduler, SaveNowAsync and FlushLocalNow (01 5.3, 04a A3 5.3 and 5.8)
    public sealed partial class SaveService
    {
        // Serializes slot writes across threads; a generation bump supersedes queued thread-pool writes
        private readonly object _localWriteSync = new object();
        private readonly Predicate<SaveSlot> _isLocalDirty;

        // Written only on the main thread under _localWriteSync; thread-pool reads happen under the lock
        private long _localWriteGeneration;

        public LocalFlushResult FlushLocalNow()
        {
            if (HookScope.RefuseIfActive(_logger, nameof(FlushLocalNow)))
            {
                return CreateFlushRefusal(nameof(FlushLocalNow) + HookRefusalSuffix);
            }

            if (_disposed)
            {
                // Dispose already flushed; report its outcome
                return _disposeFlushResult ?? LocalFlushResult.Complete;
            }

            return FlushLocalCore();
        }

        // Synchronous flush ignoring backoff; also SaveContext.FlushLocalNow for the profile switch (no entry checks)
        private LocalFlushResult FlushLocalCore()
        {
            if (_disposed || !_session.IsInitialized)
            {
                return LocalFlushResult.Complete;
            }

            long generation;
            lock (_localWriteSync)
            {
                // Waits out a write in progress; queued writes see the new generation and skip
                generation = ++_localWriteGeneration;
            }

            var dirty = new List<SaveSlot>();
            _scheduler.CopyLocalDirtySlots(dirty);
            List<LocalWriteFailure> failures = null;
            bool healthChanged = false;
            bool persistContentMarker = false;
            for (int i = 0; i < dirty.Count; i++)
            {
                SaveSlot slot = dirty[i];

                // Nothing to snapshot: the flush stays complete, but the skip is logged and the scheduler keeps retrying
                if (slot.State != SlotState.Ready)
                {
                    RecordWriteRefusal(slot, "Slot '" + slot.Key + "' is " + slot.State + "; the flush skipped its local write.", ref healthChanged);
                    continue;
                }

                LocalWriteJob job = PrepareLocalWrite(slot, generation, out string prepareError);
                LocalWriteFailure failure;
                if (job == null)
                {
                    // Backoff and health must see it: a slot that cannot be snapshotted stays dirty forever otherwise
                    failure = RecordWriteRefusal(slot, prepareError, ref healthChanged);
                }
                else
                {
                    ExecuteLocalWrite(job);
                    failure = ApplyLocalWriteResult(job, ref healthChanged, ref persistContentMarker);
                }

                if (failure != null)
                {
                    (failures ??= new List<LocalWriteFailure>()).Add(failure);
                }
            }

            RaiseHealthIfChanged(healthChanged);
            if (persistContentMarker)
            {
                LocalWriteFailure markerFailure = PersistContentMarker();
                if (markerFailure != null)
                {
                    (failures ??= new List<LocalWriteFailure>()).Add(markerFailure);
                }
            }

            // F6: development aid, disabled instances do nothing
            if (_mutationDetector.IsEnabled && !_disposed)
            {
                _mutationDetector.CheckAll(_registry.Slots, _isLocalDirty);
            }

            return failures == null ? LocalFlushResult.Complete : new LocalFlushResult(failures);
        }

        // Main thread: snapshot at the current revision plus the envelope; null when the directory or snapshot is unavailable
        private LocalWriteJob PrepareLocalWrite(SaveSlot slot, long generation, out string error)
        {
            error = null;
            string directory;
            try
            {
                directory = _session.GetSlotDirectory(slot);
            }
            catch (Exception exception)
            {
                error = "No directory for '" + slot.Key + "': " + exception.Message;
                return null;
            }

            bool evaluateIsEmpty = TracksContent(slot) && !_session.PeekSyncState(slot).HadContent;
            SlotSnapshot snapshot = slot.CaptureSnapshot(evaluateIsEmpty);
            if (!snapshot.IsSuccess)
            {
                error = "Snapshot of '" + slot.Key + "' failed: " + (snapshot.Exception != null ? snapshot.Exception.Message : "unknown error");
                _logger.Error("[SaveSystem] " + error + " The slot stays dirty.", snapshot.Exception);
                return null;
            }

            ProfileId? profile = ProfileOf(slot);
            var envelope = new SaveEnvelope
            {
                Format = SaveEnvelope.CurrentFormat,
                Schema = slot.SupportedSchema,
                Revision = snapshot.Revision,
                WriteId = null,
                SavedAtUtc = _clock.UtcNow,
                DeviceId = _session.DeviceId,
                AccountId = profile.HasValue ? profile.Value.AccountId : null,
                Data = snapshot.Data,
            };

            return new LocalWriteJob(slot, directory, _session.GetLocalWriteTarget(slot), envelope, snapshot.Revision, snapshot.IsEmpty, generation, _session.Epoch);
        }

        // Any thread: encode and atomic write under the write lock unless superseded
        private void ExecuteLocalWrite(LocalWriteJob job)
        {
            lock (_localWriteSync)
            {
                if (job.Generation != _localWriteGeneration)
                {
                    job.Superseded = true;
                    return;
                }

                try
                {
                    EncodedEnvelope encoded = EnvelopeCodec.Encode(job.Envelope);
                    job.Result = _slotStore.WriteEncoded(job.Directory, job.Slot.Key, in encoded, job.Revision);

                    // The store already holds a newer revision; SaveNowAsync retries with a fresh snapshot
                    job.Superseded = job.Result.Status == SlotWriteStatus.RefusedStaleRevision;
                }
                catch (Exception exception)
                {
                    // default(SlotWriteResult) would read as Written; a thrown write must never look successful
                    job.Result = SlotWriteResult.Failed(job.Revision, exception);
                    job.Exception = exception;
                }

                job.Completed = true;
            }
        }

        private void ExecuteLocalWrites(List<LocalWriteJob> jobs)
        {
            for (int i = 0; i < jobs.Count; i++)
            {
                ExecuteLocalWrite(jobs[i]);
            }
        }

        // Main thread: tracker, dirty flag, F6 baseline and HadContent; returns the failure or null. Health is raised by the caller.
        private LocalWriteFailure ApplyLocalWriteResult(LocalWriteJob job, ref bool healthChanged, ref bool persistContentMarker)
        {
            SaveSlot slot = job.Slot;
            if (!job.Completed || _disposed)
            {
                return null;
            }

            // A Profile slot write that finished after a switch belongs to the previous profile
            if (slot.Scope == SlotScope.Profile && job.Epoch != _session.Epoch)
            {
                return null;
            }

            // A throwing encode wrote nothing: without a recorded refusal the slot is never due again
            if (job.Exception != null)
            {
                _logger.Error("[SaveSystem] Writing '" + slot.Key + "' threw; the slot stays dirty.", job.Exception);
                return RecordWriteRefusal(slot, job.Exception.Message, ref healthChanged);
            }

            SlotWriteResult result = job.Result;
            if (result.Status == SlotWriteStatus.RefusedSchemaTooNew)
            {
                return new LocalWriteFailure(ProfileOf(slot), slot.Key, LocalWriteErrorKind.IoError, 0, result.Message);
            }

            // Nothing was written and a newer revision is durable: the dirty flag belongs to the job that wrote it
            if (result.Status == SlotWriteStatus.RefusedStaleRevision)
            {
                return null;
            }

            if (!result.IsSuccess)
            {
                _logger.Error("[SaveSystem] Local write of '" + slot.Key + "' failed (" + result.ErrorKind + "): " + result.Message, result.Exception);
                healthChanged |= _localWriteTracker.RecordFailure(job.Target, result.ErrorKind, result.Message, _clock.UtcNow);
                return _localWriteTracker.GetFailure(job.Target)
                       ?? new LocalWriteFailure(ProfileOf(slot), slot.Key, result.ErrorKind, 1, result.Message);
            }

            // The bytes landed: a stale job never gets here, SlotStore refuses it as RefusedStaleRevision above
            healthChanged |= _localWriteTracker.RecordSuccess(job.Target);
            _scheduler.ClearLocalIfRevisionUnchanged(slot, job.Revision);

            // F6 only compares a baseline whose revision still matches the slot; an older one would just mask a real detection
            if (slot.Revision == job.Revision)
            {
                _mutationDetector.RecordBaseline(slot, job.Revision, result.DataSha256);
            }

            if (job.IsEmpty == false && TracksContent(slot) && !_session.PeekSyncState(slot).HadContent)
            {
                _session.GetSyncState(slot).HadContent = true;
                persistContentMarker = true;
            }

            return null;
        }

        // HadContent reached disk; profile.json write reports its own health transition
        private LocalWriteFailure PersistContentMarker()
        {
            if (_disposed || !_session.CanPersistSyncState || _session.ActiveProfileDirectory == null)
            {
                return null;
            }

            StateWriteResult written = _session.PersistSyncState();
            if (written.IsSuccess)
            {
                return null;
            }

            ProfileId profile = _session.ActiveProfile;
            return new LocalWriteFailure(
                profile, null, written.ErrorKind, _localWriteTracker.GetConsecutiveFailures(LocalWriteTarget.ProfileState(profile)), written.Message);
        }

        // A dispatched slot the write path never wrote: record it so backoff retries instead of parking it forever
        private LocalWriteFailure RecordWriteRefusal(SaveSlot slot, string message, ref bool healthChanged)
        {
            // A slot that is not Ready has nothing to snapshot; that is no disk problem, so it stays out of write health
            if (slot.State != SlotState.Ready)
            {
                _logger.Warning("[SaveSystem] " + message);
                _scheduler.ReportLocalWriteNotReady(slot);
                return null;
            }

            LocalWriteTarget target = _session.GetLocalWriteTarget(slot);
            healthChanged |= _localWriteTracker.RecordFailure(target, LocalWriteErrorKind.IoError, message, _clock.UtcNow);
            _scheduler.ReportLocalWriteRefused(slot);
            return _localWriteTracker.GetFailure(target) ?? new LocalWriteFailure(ProfileOf(slot), slot.Key, LocalWriteErrorKind.IoError, 1, message);
        }

        private void RaiseHealthIfChanged(bool changed)
        {
            if (changed && !_disposed)
            {
                RaiseLocalWriteHealthChanged(_localWriteTracker.Health);
            }
        }

        private bool IsSlotLocalDirty(SaveSlot slot)
        {
            return _scheduler.IsLocalDirty(slot);
        }

        private ProfileId? ProfileOf(SaveSlot slot)
        {
            return slot.Scope == SlotScope.Device ? (ProfileId?)null : _session.ActiveProfile;
        }

        // HadContent is tracked for Profile CloudSync slots only
        private static bool TracksContent(SaveSlot slot)
        {
            return slot.Scope == SlotScope.Profile && slot.SyncMode == SyncMode.CloudSync;
        }

        private static LocalFlushResult CreateFlushRefusal(string message)
        {
            return new LocalFlushResult(new[] { new LocalWriteFailure(null, null, LocalWriteErrorKind.IoError, 0, message) });
        }

        private sealed class LocalWriteJob
        {
            public LocalWriteJob(
                SaveSlot slot, string directory, LocalWriteTarget target, SaveEnvelope envelope, long revision, bool? isEmpty, long generation, long epoch)
            {
                Slot = slot;
                Directory = directory;
                Target = target;
                Envelope = envelope;
                Revision = revision;
                IsEmpty = isEmpty;
                Generation = generation;
                Epoch = epoch;
            }

            public SaveSlot Slot { get; }

            public string Directory { get; }

            public LocalWriteTarget Target { get; }

            public SaveEnvelope Envelope { get; }

            public long Revision { get; }

            public bool? IsEmpty { get; }

            public long Generation { get; }

            public long Epoch { get; }

            // Set under _localWriteSync, read on the main thread after the write returns
            public bool Completed;
            public bool Superseded;
            public SlotWriteResult Result;
            public Exception Exception;
        }
    }
}
