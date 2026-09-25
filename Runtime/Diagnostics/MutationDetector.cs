using System;
using System.Collections.Generic;

namespace Ecanakli.SaveSystem
{
    /// <summary>
    /// Development aid (F6): detects data changed outside Mutate by hashing clean Ready slots on flush.
    /// Main thread only. Disabled instances do nothing and take no snapshots.
    /// </summary>
    internal sealed class MutationDetector
    {
        private readonly ISaveLogger _logger;
        private readonly Dictionary<SaveSlot, Baseline> _baselines = new Dictionary<SaveSlot, Baseline>();
        private readonly HashSet<SaveSlot> _reportedThisEpoch = new HashSet<SaveSlot>();

        /// <summary>Pass options.DetectMutationsOutsideMutate as enabled.</summary>
        public MutationDetector(bool enabled, ISaveLogger logger)
        {
            IsEnabled = enabled;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public bool IsEnabled { get; }

        /// <summary>Baseline from a fresh snapshot; call at load end (and after restore apply).</summary>
        public void CaptureBaseline(SaveSlot slot)
        {
            if (!IsEnabled || slot == null)
            {
                return;
            }

            if (slot.State != SlotState.Ready || !TryHash(slot, out string hash, out long revision))
            {
                _baselines.Remove(slot);
                return;
            }

            _baselines[slot] = new Baseline(revision, hash);
        }

        /// <summary>Baseline from a successful write: the written revision and SlotWriteResult.DataSha256.</summary>
        public void RecordBaseline(SaveSlot slot, long revision, string dataSha256)
        {
            if (!IsEnabled || slot == null || string.IsNullOrEmpty(dataSha256))
            {
                return;
            }

            _baselines[slot] = new Baseline(revision, dataSha256);
        }

        public void Forget(SaveSlot slot)
        {
            if (!IsEnabled || slot == null)
            {
                return;
            }

            _baselines.Remove(slot);
            _reportedThisEpoch.Remove(slot);
        }

        /// <summary>Re-arms the once-per-slot error for a new profile epoch.</summary>
        public void BeginEpoch()
        {
            if (IsEnabled)
            {
                _reportedThisEpoch.Clear();
            }
        }

        public void Clear()
        {
            _baselines.Clear();
            _reportedThisEpoch.Clear();
        }

        /// <summary>
        /// Checks one Ready, non-dirty slot. Skips when no baseline exists or the revision moved (a legitimate Mutate is pending).
        /// On mismatch logs an error once per slot per epoch, resets the baseline and returns true. The change is not saved.
        /// </summary>
        public bool Check(SaveSlot slot)
        {
            if (!IsEnabled || slot == null || slot.State != SlotState.Ready)
            {
                return false;
            }

            if (!_baselines.TryGetValue(slot, out Baseline baseline) || baseline.Revision != slot.Revision)
            {
                return false;
            }

            if (!TryHash(slot, out string hash, out long revision) || string.Equals(hash, baseline.DataSha256, StringComparison.Ordinal))
            {
                return false;
            }

            _baselines[slot] = new Baseline(revision, hash);
            if (_reportedThisEpoch.Add(slot))
            {
                _logger.Error(
                    "Slot '" + slot.Key + "' data changed outside Mutate (revision " + revision + "). The change is not saved. "
                    + "Do not keep references returned from Read or modify Data outside Mutate.");
            }

            return true;
        }

        /// <summary>Checks every Ready slot for which isDirty returns false; returns the number of detections.</summary>
        public int CheckAll(IReadOnlyList<SaveSlot> slots, Predicate<SaveSlot> isDirty)
        {
            if (!IsEnabled || slots == null)
            {
                return 0;
            }

            int detected = 0;
            for (int i = 0; i < slots.Count; i++)
            {
                SaveSlot slot = slots[i];
                if (slot == null || (isDirty != null && isDirty(slot)))
                {
                    continue;
                }

                if (Check(slot))
                {
                    detected++;
                }
            }

            return detected;
        }

        // Same bytes and hash as EnvelopeCodec.Encode computes over the data token
        private static bool TryHash(SaveSlot slot, out string hash, out long revision)
        {
            SlotSnapshot snapshot = slot.CaptureSnapshot(false);
            revision = snapshot.Revision;
            if (!snapshot.IsSuccess)
            {
                hash = null;
                return false;
            }

            hash = PayloadChecksum.Compute(SaveJson.ToUtf8Bytes(snapshot.Data));
            return true;
        }

        private readonly struct Baseline
        {
            public Baseline(long revision, string dataSha256)
            {
                Revision = revision;
                DataSha256 = dataSha256;
            }

            public long Revision { get; }

            public string DataSha256 { get; }
        }
    }
}
