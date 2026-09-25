using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Ecanakli.SaveSystem
{
    /// <summary>Work the scheduler delegates; implemented by SaveService. Called on the main thread and must resume on it.</summary>
    internal interface ISaveSchedulerTarget
    {
        /// <summary>LocalWriteTracker key of the slot's local file.</summary>
        LocalWriteTarget GetLocalWriteTarget(SaveSlot slot);

        /// <summary>Writes the slots; reports through ClearLocalIfRevisionUnchanged and LocalWriteTracker. The list is only valid during the call.</summary>
        UniTask WriteLocalAsync(IReadOnlyList<SaveSlot> slots, CancellationToken epochToken);

        /// <summary>Uploads the slots; reports through ClearCloudIfRevisionUnchanged, RescheduleTransient or SuspendUntilReconcile.</summary>
        UniTask UploadAsync(IReadOnlyList<SaveSlot> slots, CancellationToken epochToken);
    }

    /// <summary>
    /// Local-dirty and cloud-dirty sets with deadline coalescing. One local lane and one cloud lane per epoch, both on the
    /// epoch token; no CTS per mutation. Main thread only. A dispatched slot that stays dirty without a new trigger is parked.
    /// </summary>
    internal sealed class SaveScheduler : IDisposable
    {
        private readonly ISaveSchedulerTarget _target;
        private readonly LocalWriteTracker _tracker;
        private readonly ISaveClock _clock;
        private readonly ISaveLogger _logger;
        private readonly TimeSpan _localWriteDelay;
        private readonly TimeSpan _cloudDebounce;
        private readonly TimeSpan _cloudMaxWait;
        private readonly TimeSpan[] _uploadRetryBackoff;

        // Slots are a small fixed set; entries are kept to avoid allocation per dirty period
        private readonly Dictionary<SaveSlot, SlotEntry> _entryBySlot = new Dictionary<SaveSlot, SlotEntry>();
        private readonly List<SlotEntry> _entries = new List<SlotEntry>();
        private readonly Lane _localLane = new Lane(LaneKind.Local);
        private readonly Lane _cloudLane = new Lane(LaneKind.Cloud);

        private DateTime? _localDueAt;
        private DateTime? _cloudFirstDirtyAt;
        private DateTime _cloudLastMarkAt;
        private long _epoch;
        private bool _epochActive;
        private CancellationToken _epochToken;
        private bool _disposed;

        public SaveScheduler(ISaveSchedulerTarget target, LocalWriteTracker tracker, SaveServiceOptions options)
        {
            _target = target ?? throw new ArgumentNullException(nameof(target));
            _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            _clock = options.Clock ?? throw new ArgumentException("Options.Clock must not be null.", nameof(options));
            _logger = options.Logger ?? throw new ArgumentException("Options.Logger must not be null.", nameof(options));
            _localWriteDelay = options.LocalWriteDelay;
            _cloudDebounce = options.CloudDebounce;
            _cloudMaxWait = options.CloudMaxWait;

            IReadOnlyList<TimeSpan> backoff = options.UploadRetryBackoff;
            if (backoff == null || backoff.Count == 0)
            {
                throw new ArgumentException("Options.UploadRetryBackoff must contain at least one value.", nameof(options));
            }

            _uploadRetryBackoff = new TimeSpan[backoff.Count];
            for (int i = 0; i < backoff.Count; i++)
            {
                _uploadRetryBackoff[i] = backoff[i];
            }
        }

        private enum LaneKind
        {
            Local = 0,
            Cloud = 1,
        }

        public bool IsEpochActive => _epochActive;

        /// <summary>Starts lanes for pending work on the epoch token; the caller owns and cancels the token. Stops the previous epoch.</summary>
        public void BeginEpoch(CancellationToken epochToken)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SaveScheduler));
            }

            StopEpoch();
            _epoch++;
            _epochToken = epochToken;
            _epochActive = true;
            Poke(_localLane);
            Poke(_cloudLane);
        }

        /// <summary>Detaches running lanes; dirty sets are kept (use RemoveWhere for unloaded slots).</summary>
        public void StopEpoch()
        {
            if (!_epochActive)
            {
                return;
            }

            _epochActive = false;
            _epoch++;
            _epochToken = CancellationToken.None;
            DetachLane(_localLane);
            DetachLane(_cloudLane);
        }

        public void MarkLocalDirty(SaveSlot slot)
        {
            if (_disposed)
            {
                return;
            }

            SlotEntry entry = GetOrAdd(slot);
            entry.LocalDirty = true;
            entry.LocalMarkSeq++;
            if (!_localDueAt.HasValue)
            {
                _localDueAt = _clock.UtcNow + _localWriteDelay;
            }

            Poke(_localLane);
        }

        /// <summary>Starts or extends the debounce window; also ends a pending transient retry wait for the slot.</summary>
        public void MarkCloudDirty(SaveSlot slot)
        {
            if (_disposed)
            {
                return;
            }

            MarkCloudDirtyCore(GetOrAdd(slot), _clock.UtcNow);
            Poke(_cloudLane);
        }

        /// <summary>Uploads the slot at the next dispatch, skipping debounce and transient retry waits; suspension still applies.</summary>
        public void RequestImmediateUpload(SaveSlot slot)
        {
            if (_disposed)
            {
                return;
            }

            SlotEntry entry = GetOrAdd(slot);
            entry.CloudDirty = true;
            entry.CloudImmediate = true;
            entry.CloudRetryPending = false;
            Poke(_cloudLane);
        }

        /// <summary>
        /// The write path refused a dispatched slot (no snapshot, encode threw): it counts as triggered again so the next
        /// deadline dispatches it, instead of staying parked until another mutation. The tracker deadline paces the retry.
        /// </summary>
        public void ReportLocalWriteRefused(SaveSlot slot)
        {
            if (_disposed || !TryGetEntry(slot, out SlotEntry entry) || !entry.LocalDirty)
            {
                return;
            }

            entry.LocalMarkSeq++;
            entry.LocalDispatchedRetryAt = null;
            Poke(_localLane);
        }

        /// <summary>
        /// The write path skipped a dispatched slot because it is not Ready. That is no disk failure, so the retry is paced
        /// here with the local write backoff instead of through LocalWriteTracker (which would report a fabricated IO error).
        /// </summary>
        public void ReportLocalWriteNotReady(SaveSlot slot)
        {
            if (_disposed || !TryGetEntry(slot, out SlotEntry entry) || !entry.LocalDirty)
            {
                return;
            }

            if (entry.LocalSkips < int.MaxValue)
            {
                entry.LocalSkips++;
            }

            entry.LocalSkipRetryAt = _clock.UtcNow + _tracker.GetBackoffDelay(entry.LocalSkips);
            entry.LocalMarkSeq++;
            entry.LocalDispatchedRetryAt = null;
            Poke(_localLane);
        }

        /// <summary>Clears local dirty when the slot revision is not newer than the written one; true when the slot is clean.</summary>
        public bool ClearLocalIfRevisionUnchanged(SaveSlot slot, long writtenRevision)
        {
            if (!TryGetEntry(slot, out SlotEntry entry) || !entry.LocalDirty)
            {
                return true;
            }

            if (slot.Revision > writtenRevision)
            {
                return false;
            }

            entry.LocalDirty = false;
            entry.LocalSkips = 0;
            entry.LocalSkipRetryAt = null;
            return true;
        }

        /// <summary>Upload succeeded: resets transient backoff and clears cloud dirty when the revision is unchanged.</summary>
        public bool ClearCloudIfRevisionUnchanged(SaveSlot slot, long uploadedRevision)
        {
            if (!TryGetEntry(slot, out SlotEntry entry))
            {
                return true;
            }

            entry.CloudTransientFailures = 0;
            if (!entry.CloudDirty)
            {
                return true;
            }

            if (slot.Revision > uploadedRevision)
            {
                return false;
            }

            entry.CloudDirty = false;
            entry.CloudImmediate = false;
            entry.CloudRetryPending = false;
            return true;
        }

        /// <summary>Transient failure after retries: retry after the capped backoff, the next mutation, or ResumeTransientRetries.</summary>
        public TimeSpan RescheduleTransient(SaveSlot slot)
        {
            if (_disposed)
            {
                return TimeSpan.Zero;
            }

            TimeSpan delay = RescheduleCore(GetOrAdd(slot), _clock.UtcNow);
            Poke(_cloudLane);
            return delay;
        }

        /// <summary>Permanent failure: no scheduled upload for the slot until OnSlotReconciled.</summary>
        public void SuspendUntilReconcile(SaveSlot slot)
        {
            if (_disposed)
            {
                return;
            }

            SlotEntry entry = GetOrAdd(slot);
            entry.UploadSuspended = true;
            entry.CloudImmediate = false;
            entry.CloudRetryPending = false;
        }

        /// <summary>Slot reached ReconciledThisEpoch: lifts suspension and re-triggers a dirty slot.</summary>
        public void OnSlotReconciled(SaveSlot slot)
        {
            if (_disposed || !TryGetEntry(slot, out SlotEntry entry))
            {
                return;
            }

            entry.UploadSuspended = false;
            if (!entry.CloudDirty)
            {
                return;
            }

            MarkCloudDirtyCore(entry, _clock.UtcNow);
            Poke(_cloudLane);
        }

        /// <summary>Foreground: pending transient retries become due now.</summary>
        public void ResumeTransientRetries()
        {
            if (_disposed)
            {
                return;
            }

            DateTime now = _clock.UtcNow;
            bool any = false;
            for (int i = 0; i < _entries.Count; i++)
            {
                SlotEntry entry = _entries[i];
                if (entry.CloudRetryPending && entry.CloudRetryAt > now)
                {
                    entry.CloudRetryAt = now;
                    any = true;
                }
            }

            if (any)
            {
                Poke(_cloudLane);
            }
        }

        public bool IsLocalDirty(SaveSlot slot)
        {
            return TryGetEntry(slot, out SlotEntry entry) && entry.LocalDirty;
        }

        public bool IsCloudDirty(SaveSlot slot)
        {
            return TryGetEntry(slot, out SlotEntry entry) && entry.CloudDirty;
        }

        public bool IsUploadSuspended(SaveSlot slot)
        {
            return TryGetEntry(slot, out SlotEntry entry) && entry.UploadSuspended;
        }

        /// <summary>Appends local-dirty slots in registration order.</summary>
        public void CopyLocalDirtySlots(List<SaveSlot> destination)
        {
            if (destination == null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].LocalDirty)
                {
                    destination.Add(_entries[i].Slot);
                }
            }
        }

        /// <summary>Appends cloud-dirty slots, suspended ones included.</summary>
        public void CopyCloudDirtySlots(List<SaveSlot> destination)
        {
            if (destination == null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].CloudDirty)
                {
                    destination.Add(_entries[i].Slot);
                }
            }
        }

        /// <summary>Forgets the slot: both dirty flags, backoff and suspension.</summary>
        public bool Remove(SaveSlot slot)
        {
            if (!TryGetEntry(slot, out SlotEntry entry))
            {
                return false;
            }

            _entryBySlot.Remove(slot);
            _entries.Remove(entry);
            return true;
        }

        /// <summary>Forgets every matching slot, e.g. Profile slots on a switch.</summary>
        public int RemoveWhere(Predicate<SaveSlot> match)
        {
            if (match == null)
            {
                throw new ArgumentNullException(nameof(match));
            }

            int removed = 0;
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                SlotEntry entry = _entries[i];
                if (match(entry.Slot))
                {
                    _entryBySlot.Remove(entry.Slot);
                    _entries.RemoveAt(i);
                    removed++;
                }
            }

            return removed;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            StopEpoch();
            _disposed = true;
            _entryBySlot.Clear();
            _entries.Clear();
            _localDueAt = null;
            _cloudFirstDirtyAt = null;
        }

        private void MarkCloudDirtyCore(SlotEntry entry, DateTime now)
        {
            entry.CloudDirty = true;
            entry.CloudMarkSeq++;
            entry.CloudRetryPending = false;
            if (entry.UploadSuspended)
            {
                return;
            }

            // Suspended marks do not open a window, so they cannot shorten another slot's debounce
            if (!_cloudFirstDirtyAt.HasValue)
            {
                _cloudFirstDirtyAt = now;
            }

            _cloudLastMarkAt = now;
        }

        private TimeSpan RescheduleCore(SlotEntry entry, DateTime now)
        {
            if (entry.CloudTransientFailures < _uploadRetryBackoff.Length)
            {
                entry.CloudTransientFailures++;
            }

            TimeSpan delay = _uploadRetryBackoff[entry.CloudTransientFailures - 1];
            entry.CloudDirty = true;
            entry.CloudRetryAt = now + delay;
            entry.CloudRetryPending = true;
            return delay;
        }

        private void Poke(Lane lane)
        {
            if (_disposed || !_epochActive || _epochToken.IsCancellationRequested)
            {
                return;
            }

            DateTime now = _clock.UtcNow;
            if (lane.RunningEpoch != _epoch)
            {
                if (ComputeDue(lane, now).HasValue)
                {
                    lane.RunningEpoch = _epoch;
                    RunLaneAsync(lane, _epoch, _epochToken).Forget();
                }

                return;
            }

            // Lane busy in a callback recomputes afterwards; a sleeping lane wakes only for an earlier deadline
            UniTaskCompletionSource wake = lane.Wake;
            if (wake == null)
            {
                return;
            }

            DateTime? due = ComputeDue(lane, now);
            if (due.HasValue && due.Value < lane.SleepUntil)
            {
                lane.Wake = null;
                wake.TrySetResult();
            }
        }

        private async UniTask RunLaneAsync(Lane lane, long epoch, CancellationToken ct)
        {
            try
            {
                bool yielded = false;
                while (IsCurrent(epoch) && !ct.IsCancellationRequested)
                {
                    DateTime now = _clock.UtcNow;
                    DateTime? due = ComputeDue(lane, now);
                    if (!due.HasValue)
                    {
                        break;
                    }

                    if (due.Value > now)
                    {
                        yielded = false;
                        await SleepAsync(lane, due.Value, due.Value - now, ct);
                        continue;
                    }

                    // Never run callbacks inside the frame that marked or woke the lane
                    if (!yielded)
                    {
                        yielded = true;
                        await UniTask.Yield(PlayerLoopTiming.Update, ct);
                        continue;
                    }

                    yielded = false;
                    if (lane.Kind == LaneKind.Local)
                    {
                        await DispatchLocalAsync(now, ct);
                    }
                    else
                    {
                        await DispatchCloudAsync(now, ct);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Epoch ended
            }
            catch (Exception exception)
            {
                _logger.Error("[SaveSystem] Save scheduler " + lane.Kind + " lane stopped unexpectedly; it restarts on the next change.", exception);
            }
            finally
            {
                if (lane.RunningEpoch == epoch)
                {
                    lane.RunningEpoch = 0;
                }
            }
        }

        private async UniTask SleepAsync(Lane lane, DateTime until, TimeSpan delay, CancellationToken ct)
        {
            var wake = new UniTaskCompletionSource();
            lane.Wake = wake;
            lane.SleepUntil = until;
            try
            {
                // An early wake leaves the delay running on the epoch token; WhenAny observes its result
                await UniTask.WhenAny(_clock.Delay(delay, ct), wake.Task);
            }
            finally
            {
                if (lane.Wake == wake)
                {
                    lane.Wake = null;
                }
            }
        }

        private async UniTask DispatchLocalAsync(DateTime now, CancellationToken ct)
        {
            List<SaveSlot> batch = null;
            for (int i = 0; i < _entries.Count; i++)
            {
                SlotEntry entry = _entries[i];
                if (!entry.LocalDirty)
                {
                    continue;
                }

                DateTime? candidate = LocalCandidate(entry, now, out DateTime? retryAt);
                if (!candidate.HasValue || candidate.Value > now)
                {
                    continue;
                }

                entry.LocalDispatchedSeq = entry.LocalMarkSeq;
                entry.LocalDispatchedRetryAt = retryAt;
                (batch ??= new List<SaveSlot>()).Add(entry.Slot);
            }

            _localDueAt = null;
            if (batch == null)
            {
                return;
            }

            LogDispatch("local write", batch.Count);
            try
            {
                await _target.WriteLocalAsync(batch, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.Error("[SaveSystem] Scheduled local write threw; the slots stay dirty until the next change or flush.", exception);
            }
        }

        private async UniTask DispatchCloudAsync(DateTime now, CancellationToken ct)
        {
            List<SaveSlot> batch = null;
            for (int i = 0; i < _entries.Count; i++)
            {
                SlotEntry entry = _entries[i];
                DateTime? candidate = CloudCandidate(entry, now);
                if (!candidate.HasValue)
                {
                    continue;
                }

                // Debounced slots join any dispatch so one provider call carries them
                bool triggered = entry.CloudMarkSeq != entry.CloudDispatchedSeq;
                if (candidate.Value > now && !triggered)
                {
                    continue;
                }

                entry.CloudDispatchedSeq = entry.CloudMarkSeq;
                entry.CloudImmediate = false;
                entry.CloudRetryPending = false;
                (batch ??= new List<SaveSlot>()).Add(entry.Slot);
            }

            _cloudFirstDirtyAt = null;
            if (batch == null)
            {
                return;
            }

            LogDispatch("upload", batch.Count);
            try
            {
                await _target.UploadAsync(batch, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.Error("[SaveSystem] Scheduled upload threw; the slots are rescheduled with transient backoff.", exception);
                DateTime failedAt = _clock.UtcNow;
                for (int i = 0; i < batch.Count; i++)
                {
                    if (TryGetEntry(batch[i], out SlotEntry entry) && entry.CloudDirty && !entry.UploadSuspended && !entry.CloudRetryPending &&
                        entry.CloudMarkSeq == entry.CloudDispatchedSeq)
                    {
                        RescheduleCore(entry, failedAt);
                    }
                }
            }
        }

        private DateTime? ComputeDue(Lane lane, DateTime now)
        {
            DateTime? due = null;
            for (int i = 0; i < _entries.Count; i++)
            {
                SlotEntry entry = _entries[i];
                DateTime? candidate = lane.Kind == LaneKind.Local
                    ? (entry.LocalDirty ? LocalCandidate(entry, now, out _) : null)
                    : CloudCandidate(entry, now);
                if (candidate.HasValue && (!due.HasValue || candidate.Value < due.Value))
                {
                    due = candidate;
                }
            }

            return due;
        }

        // Matches LocalWriteTracker.CanAttemptScheduledWrite: no attempt before the retry deadline, even after a mutation
        private DateTime? LocalCandidate(SlotEntry entry, DateTime now, out DateTime? retryAt)
        {
            retryAt = _tracker.GetRetryDeadline(_target.GetLocalWriteTarget(entry.Slot));

            // A not-Ready skip has no tracker entry, so its own deadline paces the retry
            if (entry.LocalSkipRetryAt.HasValue && (!retryAt.HasValue || entry.LocalSkipRetryAt.Value > retryAt.Value))
            {
                retryAt = entry.LocalSkipRetryAt;
            }

            bool triggered = entry.LocalMarkSeq != entry.LocalDispatchedSeq;
            if (!triggered)
            {
                // One retry per new backoff deadline; a skipped slot is parked until the next mark
                if (!retryAt.HasValue || retryAt == entry.LocalDispatchedRetryAt)
                {
                    return null;
                }

                return retryAt.Value;
            }

            DateTime earliest = _localDueAt ?? now;
            return retryAt.HasValue && retryAt.Value > earliest ? retryAt.Value : earliest;
        }

        private DateTime? CloudCandidate(SlotEntry entry, DateTime now)
        {
            if (!entry.CloudDirty || entry.UploadSuspended)
            {
                return null;
            }

            if (entry.CloudImmediate)
            {
                return now;
            }

            if (entry.CloudMarkSeq != entry.CloudDispatchedSeq)
            {
                if (!_cloudFirstDirtyAt.HasValue)
                {
                    return now;
                }

                DateTime quiet = _cloudLastMarkAt + _cloudDebounce;
                DateTime capped = _cloudFirstDirtyAt.Value + _cloudMaxWait;
                return quiet < capped ? quiet : capped;
            }

            return entry.CloudRetryPending ? entry.CloudRetryAt : (DateTime?)null;
        }

        private bool IsCurrent(long epoch)
        {
            return !_disposed && _epochActive && _epoch == epoch;
        }

        private static void DetachLane(Lane lane)
        {
            lane.RunningEpoch = 0;
            UniTaskCompletionSource wake = lane.Wake;
            lane.Wake = null;
            wake?.TrySetResult();
        }

        private SlotEntry GetOrAdd(SaveSlot slot)
        {
            if (slot == null)
            {
                throw new ArgumentNullException(nameof(slot));
            }

            if (!_entryBySlot.TryGetValue(slot, out SlotEntry entry))
            {
                entry = new SlotEntry(slot);
                _entryBySlot.Add(slot, entry);
                _entries.Add(entry);
            }

            return entry;
        }

        private bool TryGetEntry(SaveSlot slot, out SlotEntry entry)
        {
            if (slot == null)
            {
                entry = null;
                return false;
            }

            return _entryBySlot.TryGetValue(slot, out entry);
        }

        private void LogDispatch(string operation, int count)
        {
            if (_logger.IsVerboseEnabled)
            {
                _logger.Verbose("[SaveSystem] Scheduler " + operation + " for " + count + " slot(s).");
            }
        }

        private sealed class SlotEntry
        {
            public readonly SaveSlot Slot;
            public bool LocalDirty;
            public long LocalMarkSeq;
            public long LocalDispatchedSeq;
            public DateTime? LocalDispatchedRetryAt;

            // Consecutive not-Ready skips and their own retry deadline; reset when the slot goes clean
            public int LocalSkips;
            public DateTime? LocalSkipRetryAt;
            public bool CloudDirty;
            public long CloudMarkSeq;
            public long CloudDispatchedSeq;
            public bool CloudImmediate;
            public bool CloudRetryPending;
            public DateTime CloudRetryAt;
            public int CloudTransientFailures;
            public bool UploadSuspended;

            public SlotEntry(SaveSlot slot)
            {
                Slot = slot;
            }
        }

        private sealed class Lane
        {
            public readonly LaneKind Kind;
            public long RunningEpoch;
            public UniTaskCompletionSource Wake;
            public DateTime SleepUntil;

            public Lane(LaneKind kind)
            {
                Kind = kind;
            }
        }
    }
}
