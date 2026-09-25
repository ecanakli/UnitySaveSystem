using System;
using System.Collections.Generic;

namespace Ecanakli.SaveSystem
{
    /// <summary>Identity of a local write target: a slot file, profile.json or device.json.</summary>
    internal readonly struct LocalWriteTarget : IEquatable<LocalWriteTarget>
    {
        /// <summary>Creates a target; null slotKey means the state file of the profile (or device.json when profile is null).</summary>
        public LocalWriteTarget(ProfileId? profile, string slotKey)
        {
            Profile = profile;
            SlotKey = slotKey;
        }

        /// <summary>Owning profile; null for device-level files.</summary>
        public ProfileId? Profile { get; }

        /// <summary>Slot key; null for profile.json and device.json.</summary>
        public string SlotKey { get; }

        /// <summary>device.json.</summary>
        public static LocalWriteTarget DeviceState => new LocalWriteTarget(null, null);

        /// <summary>profile.json of a profile.</summary>
        public static LocalWriteTarget ProfileState(ProfileId profile)
        {
            return new LocalWriteTarget(profile, null);
        }

        /// <summary>Slot file; profile is null for Device-scope slots.</summary>
        public static LocalWriteTarget Slot(ProfileId? profile, string slotKey)
        {
            if (string.IsNullOrEmpty(slotKey))
            {
                throw new ArgumentException("Slot key must not be null or empty.", nameof(slotKey));
            }

            return new LocalWriteTarget(profile, slotKey);
        }

        public bool Equals(LocalWriteTarget other)
        {
            bool sameProfile = Profile.HasValue == other.Profile.HasValue && (!Profile.HasValue || Profile.Value.Equals(other.Profile.Value));
            return sameProfile && string.Equals(SlotKey, other.SlotKey, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            return obj is LocalWriteTarget other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = Profile.HasValue ? Profile.Value.GetHashCode() : 0;
                return (hash * 397) ^ (SlotKey == null ? 0 : StringComparer.Ordinal.GetHashCode(SlotKey));
            }
        }

        public override string ToString()
        {
            return (Profile.HasValue ? Profile.Value.ToString() : "device") + "/" + (SlotKey ?? "<state file>");
        }
    }

    /// <summary>Per-target local write backoff and service-wide health with transition detection. Main thread only.</summary>
    internal sealed class LocalWriteTracker
    {
        private readonly TimeSpan[] _backoff;
        private readonly List<Entry> _entries = new List<Entry>();

        /// <summary>Copies the backoff sequence (options LocalWriteBackoff); the last value repeats.</summary>
        public LocalWriteTracker(IReadOnlyList<TimeSpan> backoff)
        {
            if (backoff == null || backoff.Count == 0)
            {
                throw new ArgumentException("Backoff must contain at least one value.", nameof(backoff));
            }

            _backoff = new TimeSpan[backoff.Count];
            for (int i = 0; i < backoff.Count; i++)
            {
                if (backoff[i] < TimeSpan.Zero)
                {
                    throw new ArgumentException("Backoff values must not be negative.", nameof(backoff));
                }

                _backoff[i] = backoff[i];
            }
        }

        /// <summary>Current health snapshot; rebuilt on every change.</summary>
        public LocalWriteHealth Health { get; private set; } = LocalWriteHealth.Healthy;

        /// <summary>True when no target is failing.</summary>
        public bool IsHealthy => _entries.Count == 0;

        /// <summary>True when the target's last write failed.</summary>
        public bool IsFailing(LocalWriteTarget target)
        {
            return IndexOf(target) >= 0;
        }

        /// <summary>Consecutive failures of the target; 0 when healthy.</summary>
        public int GetConsecutiveFailures(LocalWriteTarget target)
        {
            int index = IndexOf(target);
            return index < 0 ? 0 : _entries[index].ConsecutiveFailures;
        }

        /// <summary>Earliest time a scheduled write may retry; null when healthy.</summary>
        public DateTime? GetRetryDeadline(LocalWriteTarget target)
        {
            int index = IndexOf(target);
            return index < 0 ? (DateTime?)null : _entries[index].RetryAtUtc;
        }

        /// <summary>False while the target's backoff has not passed. Explicit flushes ignore this.</summary>
        public bool CanAttemptScheduledWrite(LocalWriteTarget target, DateTime utcNow)
        {
            int index = IndexOf(target);
            return index < 0 || utcNow >= _entries[index].RetryAtUtc;
        }

        /// <summary>Failure snapshot of the target; null when healthy.</summary>
        public LocalWriteFailure GetFailure(LocalWriteTarget target)
        {
            int index = IndexOf(target);
            return index < 0 ? null : _entries[index].ToFailure();
        }

        /// <summary>Backoff after the given number of consecutive failures (1-based; the last value repeats).</summary>
        public TimeSpan GetBackoffDelay(int consecutiveFailures)
        {
            int index = Math.Min(Math.Max(consecutiveFailures, 1) - 1, _backoff.Length - 1);
            return _backoff[index];
        }

        /// <summary>Records a failed write and advances backoff; true when LocalWriteHealthChanged must fire.</summary>
        public bool RecordFailure(LocalWriteTarget target, LocalWriteErrorKind kind, string message, DateTime utcNow)
        {
            int index = IndexOf(target);
            Entry entry;
            if (index < 0)
            {
                entry = new Entry(target);
                _entries.Add(entry);
            }
            else
            {
                entry = _entries[index];
            }

            entry.ConsecutiveFailures++;
            entry.Kind = kind;
            entry.Message = message;
            entry.RetryAtUtc = utcNow + GetBackoffDelay(entry.ConsecutiveFailures);
            return RebuildHealth();
        }

        /// <summary>Records a successful write (resets backoff); true when LocalWriteHealthChanged must fire.</summary>
        public bool RecordSuccess(LocalWriteTarget target)
        {
            int index = IndexOf(target);
            if (index < 0)
            {
                return false;
            }

            _entries.RemoveAt(index);
            return RebuildHealth();
        }

        /// <summary>Drops every target of a profile (deleted or unloaded); true when LocalWriteHealthChanged must fire.</summary>
        public bool RemoveProfile(ProfileId profile)
        {
            int removed = _entries.RemoveAll(entry => entry.Target.Profile.HasValue && entry.Target.Profile.Value.Equals(profile));
            return removed > 0 && RebuildHealth();
        }

        /// <summary>Drops every target; true when LocalWriteHealthChanged must fire.</summary>
        public bool Clear()
        {
            if (_entries.Count == 0)
            {
                return false;
            }

            _entries.Clear();
            return RebuildHealth();
        }

        private int IndexOf(LocalWriteTarget target)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Target.Equals(target))
                {
                    return i;
                }
            }

            return -1;
        }

        // Transition = healthy flag flip or a change of the most severe kind
        private bool RebuildHealth()
        {
            LocalWriteHealth previous = Health;
            if (_entries.Count == 0)
            {
                Health = LocalWriteHealth.Healthy;
            }
            else
            {
                var failures = new LocalWriteFailure[_entries.Count];
                for (int i = 0; i < _entries.Count; i++)
                {
                    failures[i] = _entries[i].ToFailure();
                }

                Health = new LocalWriteHealth(failures);
            }

            return previous.IsHealthy != Health.IsHealthy || previous.Kind != Health.Kind;
        }

        private sealed class Entry
        {
            public Entry(LocalWriteTarget target)
            {
                Target = target;
            }

            public LocalWriteTarget Target { get; }

            public LocalWriteErrorKind Kind { get; set; }

            public int ConsecutiveFailures { get; set; }

            public string Message { get; set; }

            public DateTime RetryAtUtc { get; set; }

            public LocalWriteFailure ToFailure()
            {
                return new LocalWriteFailure(Target.Profile, Target.SlotKey, Kind, ConsecutiveFailures, Message);
            }
        }
    }
}
