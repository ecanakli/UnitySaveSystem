using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Ecanakli.SaveSystem.Tests
{
    /// <summary>
    /// Deterministic ISaveClock. Delays complete only when Advance passes their deadline (stepwise, in deadline order);
    /// cancellation cancels the delay. Continuations run synchronously inside Advance.
    /// </summary>
    internal sealed class ManualSaveClock : ISaveClock
    {
        public static readonly DateTime DefaultStartUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private readonly object _sync = new object();
        private readonly List<Waiter> _waiters = new List<Waiter>();
        private readonly List<TimeSpan> _requestedDelays = new List<TimeSpan>();
        private DateTime _utcNow;
        private long _sequence;

        public ManualSaveClock()
            : this(DefaultStartUtc)
        {
        }

        public ManualSaveClock(DateTime startUtc)
        {
            _utcNow = ToUtc(startUtc);
        }

        public DateTime UtcNow
        {
            get
            {
                lock (_sync)
                {
                    return _utcNow;
                }
            }
        }

        public int PendingDelayCount
        {
            get
            {
                lock (_sync)
                {
                    return _waiters.Count;
                }
            }
        }

        /// <summary>Every Delay call, including zero and cancelled ones.</summary>
        public int DelayCallCount
        {
            get
            {
                lock (_sync)
                {
                    return _requestedDelays.Count;
                }
            }
        }

        public IReadOnlyList<TimeSpan> RequestedDelays
        {
            get
            {
                lock (_sync)
                {
                    return _requestedDelays.ToArray();
                }
            }
        }

        /// <summary>Earliest pending deadline; null when nothing waits.</summary>
        public DateTime? NextDueUtc
        {
            get
            {
                lock (_sync)
                {
                    Waiter next = FindNext(DateTime.MaxValue);
                    return next?.Due;
                }
            }
        }

        public UniTask Delay(TimeSpan delay, CancellationToken ct)
        {
            if (ct.IsCancellationRequested)
            {
                lock (_sync)
                {
                    _requestedDelays.Add(delay);
                }

                return UniTask.FromCanceled(ct);
            }

            Waiter waiter;
            lock (_sync)
            {
                _requestedDelays.Add(delay);
                if (delay <= TimeSpan.Zero)
                {
                    return UniTask.CompletedTask;
                }

                _sequence++;
                waiter = new Waiter(this, _utcNow + delay, _sequence, ct);
                _waiters.Add(waiter);
            }

            if (ct.CanBeCanceled)
            {
                waiter.Registration = ct.Register(state => ((Waiter)state).Cancel(), waiter);
            }

            return waiter.Source.Task;
        }

        public void Advance(TimeSpan by)
        {
            if (by < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(by), "Use SetUtcNow to move backwards.");
            }

            AdvanceTo(UtcNow + by);
        }

        /// <summary>Moves forward to targetUtc, completing each due delay with UtcNow set to its deadline.</summary>
        public void AdvanceTo(DateTime targetUtc)
        {
            targetUtc = ToUtc(targetUtc);
            lock (_sync)
            {
                if (targetUtc < _utcNow)
                {
                    throw new ArgumentOutOfRangeException(nameof(targetUtc), "Target is before UtcNow; use SetUtcNow to move backwards.");
                }
            }

            while (true)
            {
                Waiter next;
                lock (_sync)
                {
                    next = FindNext(targetUtc);
                    if (next == null)
                    {
                        if (targetUtc > _utcNow)
                        {
                            _utcNow = targetUtc;
                        }

                        return;
                    }

                    _waiters.Remove(next);
                    if (next.Due > _utcNow)
                    {
                        _utcNow = next.Due;
                    }
                }

                next.Registration.Dispose();
                next.Source.TrySetResult();
            }
        }

        /// <summary>Jumps to the earliest pending deadline; false when nothing waits.</summary>
        public bool AdvanceToNextDue()
        {
            DateTime? due = NextDueUtc;
            if (!due.HasValue)
            {
                return false;
            }

            AdvanceTo(due.Value);
            return true;
        }

        /// <summary>Forward behaves like AdvanceTo; backward only moves UtcNow (pending deadlines stay absolute).</summary>
        public void SetUtcNow(DateTime utcNow)
        {
            utcNow = ToUtc(utcNow);
            lock (_sync)
            {
                if (utcNow < _utcNow)
                {
                    _utcNow = utcNow;
                    return;
                }
            }

            AdvanceTo(utcNow);
        }

        public void ClearRequestedDelays()
        {
            lock (_sync)
            {
                _requestedDelays.Clear();
            }
        }

        // Caller holds _sync
        private Waiter FindNext(DateTime limit)
        {
            Waiter best = null;
            foreach (Waiter waiter in _waiters)
            {
                if (waiter.Due > limit)
                {
                    continue;
                }

                if (best == null || waiter.Due < best.Due || (waiter.Due == best.Due && waiter.Sequence < best.Sequence))
                {
                    best = waiter;
                }
            }

            return best;
        }

        private void CancelWaiter(Waiter waiter)
        {
            bool removed;
            lock (_sync)
            {
                removed = _waiters.Remove(waiter);
            }

            if (removed)
            {
                waiter.Source.TrySetCanceled(waiter.Token);
            }
        }

        private static DateTime ToUtc(DateTime value)
        {
            return value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }

        private sealed class Waiter
        {
            private readonly ManualSaveClock _clock;

            public Waiter(ManualSaveClock clock, DateTime due, long sequence, CancellationToken token)
            {
                _clock = clock;
                Due = due;
                Sequence = sequence;
                Token = token;
            }

            public DateTime Due { get; }

            public long Sequence { get; }

            public CancellationToken Token { get; }

            public UniTaskCompletionSource Source { get; } = new UniTaskCompletionSource();

            public CancellationTokenRegistration Registration { get; set; }

            public void Cancel()
            {
                _clock.CancelWaiter(this);
            }
        }
    }
}
