using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Ecanakli.SaveSystem
{
    /// <summary>Outcome of one ordered dispatch.</summary>
    internal sealed class ListenerDispatchResult
    {
        internal static readonly ListenerDispatchResult Empty =
            new ListenerDispatchResult(Array.Empty<ListenerFailure>(), 0, false, false);

        internal ListenerDispatchResult(IReadOnlyList<ListenerFailure> failures, int completedCount, bool wasCanceled, bool timedOut)
        {
            Failures = failures ?? Array.Empty<ListenerFailure>();
            CompletedCount = completedCount;
            WasCanceled = wasCanceled;
            TimedOut = timedOut;
        }

        /// <summary>Threw, TimedOut and Skipped listeners in dispatch order.</summary>
        public IReadOnlyList<ListenerFailure> Failures { get; }

        public int CompletedCount { get; }

        /// <summary>The caller token was cancelled; remaining listeners were skipped.</summary>
        public bool WasCanceled { get; }

        /// <summary>The shared time budget ran out; remaining listeners were skipped.</summary>
        public bool TimedOut { get; }
    }

    /// <summary>Ordered listener list for IRestoreListener and IProfileDeactivatingListener. Main thread only.</summary>
    internal sealed class OrderedListenerDispatcher<TListener> : IDisposable
        where TListener : class
    {
        private readonly List<TListener> _listeners = new List<TListener>();
        private readonly string _kind;
        private readonly Func<TListener, int> _orderOf;
        private readonly ListenerReentrancyGuard _guard;
        private readonly ISaveLogger _logger;
        private readonly ISaveClock _clock;
        private bool _disposed;

        /// <param name="kind">Log label, for example "restore" or "deactivation".</param>
        /// <param name="orderOf">Reads the listener's Order.</param>
        public OrderedListenerDispatcher(string kind, Func<TListener, int> orderOf, ListenerReentrancyGuard guard, ISaveLogger logger, ISaveClock clock)
        {
            _kind = string.IsNullOrEmpty(kind) ? "listener" : kind;
            _orderOf = orderOf ?? throw new ArgumentNullException(nameof(orderOf));
            _guard = guard ?? throw new ArgumentNullException(nameof(guard));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public int Count => _listeners.Count;

        public bool IsDisposed => _disposed;

        public bool Contains(TListener listener)
        {
            return IndexOf(listener) >= 0;
        }

        /// <summary>Adds a listener; duplicates are ignored and add after Dispose is a no-op. Warns on equal Order.</summary>
        public bool Add(TListener listener)
        {
            if (listener == null)
            {
                throw new ArgumentNullException(nameof(listener));
            }

            if (_disposed)
            {
                return false;
            }

            if (IndexOf(listener) >= 0)
            {
                if (_logger.IsVerboseEnabled)
                {
                    _logger.Verbose("[SaveSystem] Duplicate " + _kind + " listener ignored: " + listener.GetType().Name + ".");
                }

                return false;
            }

            WarnOnEqualOrder(listener);
            _listeners.Add(listener);
            return true;
        }

        /// <summary>Removes a listener; no-op after Dispose. A listener removed during dispatch is not called.</summary>
        public bool Remove(TListener listener)
        {
            if (listener == null || _disposed)
            {
                return false;
            }

            int index = IndexOf(listener);
            if (index < 0)
            {
                return false;
            }

            _listeners.RemoveAt(index);
            return true;
        }

        /// <summary>
        /// Awaits listeners by Order then registration order. Exceptions are recorded, never thrown.
        /// Caller cancellation or an exhausted budget skips the rest and is reported, not thrown.
        /// </summary>
        /// <param name="trigger">Logged with the final order.</param>
        /// <param name="budget">Shared time budget for all listeners; null means unlimited.</param>
        public async UniTask<ListenerDispatchResult> DispatchAsync<TArg>(
            TArg arg,
            string trigger,
            Func<TListener, TArg, CancellationToken, UniTask> invoke,
            TimeSpan? budget,
            CancellationToken ct)
        {
            if (invoke == null)
            {
                throw new ArgumentNullException(nameof(invoke));
            }

            if (_disposed || _listeners.Count == 0)
            {
                return ListenerDispatchResult.Empty;
            }

            var failures = new List<ListenerFailure>();
            Entry[] ordered = BuildOrder(failures);
            LogOrder(ordered, trigger);

            CancellationTokenSource budgetCts = null;
            CancellationTokenSource timerStopCts = null;
            CancellationTokenSource linkedCts = null;
            bool canceled = false;
            bool timedOut = false;
            int completed = 0;

            _guard.Enter();
            try
            {
                CancellationToken listenerToken = ct;
                if (budget.HasValue)
                {
                    budgetCts = new CancellationTokenSource();
                    timerStopCts = new CancellationTokenSource();
                    linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, budgetCts.Token);
                    listenerToken = linkedCts.Token;
                    CancelAfterAsync(_clock, budget.Value, budgetCts, timerStopCts.Token, _logger).Forget();
                }

                for (int i = 0; i < ordered.Length; i++)
                {
                    Entry entry = ordered[i];
                    if (IndexOf(entry.Listener) < 0)
                    {
                        continue;
                    }

                    if (!canceled && !timedOut)
                    {
                        if (ct.IsCancellationRequested)
                        {
                            canceled = true;
                        }
                        else if (budgetCts != null && budgetCts.IsCancellationRequested)
                        {
                            timedOut = true;
                        }
                    }

                    if (canceled || timedOut)
                    {
                        failures.Add(new ListenerFailure(entry.Type, entry.Order, ListenerFailureKind.Skipped));
                        continue;
                    }

                    try
                    {
                        await invoke(entry.Listener, arg, listenerToken).AttachExternalCancellation(listenerToken);
                        completed++;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        canceled = true;
                        failures.Add(new ListenerFailure(entry.Type, entry.Order, ListenerFailureKind.Skipped));
                    }
                    catch (OperationCanceledException) when (budgetCts != null && budgetCts.IsCancellationRequested)
                    {
                        timedOut = true;
                        failures.Add(new ListenerFailure(entry.Type, entry.Order, ListenerFailureKind.TimedOut));
                        _logger.Warning("[SaveSystem] " + _kind + " listener " + entry.Type.Name + " exceeded the shared budget of " +
                                        budget.GetValueOrDefault().TotalMilliseconds + " ms; remaining listeners are skipped.");
                    }
                    catch (Exception exception)
                    {
                        failures.Add(new ListenerFailure(entry.Type, entry.Order, ListenerFailureKind.Threw, exception));
                        _logger.Error("[SaveSystem] " + _kind + " listener " + entry.Type.Name + " (Order " + entry.Order + ") threw.", exception);
                    }
                }
            }
            finally
            {
                _guard.Exit();
                timerStopCts?.Cancel();
                linkedCts?.Dispose();
                budgetCts?.Dispose();
                timerStopCts?.Dispose();
            }

            return new ListenerDispatchResult(failures, completed, canceled, timedOut);
        }

        /// <summary>Clears the list; later Add, Remove and Dispatch are no-ops.</summary>
        public void Dispose()
        {
            _disposed = true;
            _listeners.Clear();
        }

        private static async UniTaskVoid CancelAfterAsync(
            ISaveClock clock, TimeSpan delay, CancellationTokenSource target, CancellationToken stopToken, ISaveLogger logger)
        {
            try
            {
                bool stopped = await clock.Delay(delay, stopToken).SuppressCancellationThrow();
                if (stopped || stopToken.IsCancellationRequested)
                {
                    return;
                }

                target.Cancel();
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception exception)
            {
                logger.Error("[SaveSystem] Listener budget timer failed.", exception);
            }
        }

        private int IndexOf(TListener listener)
        {
            for (int i = 0; i < _listeners.Count; i++)
            {
                if (ReferenceEquals(_listeners[i], listener))
                {
                    return i;
                }
            }

            return -1;
        }

        private bool TryGetOrder(TListener listener, out int order, out Exception exception)
        {
            try
            {
                order = _orderOf(listener);
                exception = null;
                return true;
            }
            catch (Exception caught)
            {
                order = 0;
                exception = caught;
                return false;
            }
        }

        private void WarnOnEqualOrder(TListener listener)
        {
            if (!TryGetOrder(listener, out int order, out Exception exception))
            {
                _logger.Error("[SaveSystem] Order of " + _kind + " listener " + listener.GetType().Name + " threw.", exception);
                return;
            }

            StringBuilder builder = null;
            for (int i = 0; i < _listeners.Count; i++)
            {
                if (!TryGetOrder(_listeners[i], out int existing, out _) || existing != order)
                {
                    continue;
                }

                builder = builder ?? new StringBuilder();
                if (builder.Length > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(_listeners[i].GetType().Name);
            }

            if (builder != null)
            {
                _logger.Warning("[SaveSystem] " + _kind + " listener " + listener.GetType().Name + " has Order " + order +
                                ", equal to " + builder + ". Ties run in registration order.");
            }
        }

        private Entry[] BuildOrder(List<ListenerFailure> failures)
        {
            var entries = new List<Entry>(_listeners.Count);
            for (int i = 0; i < _listeners.Count; i++)
            {
                TListener listener = _listeners[i];
                if (!TryGetOrder(listener, out int order, out Exception exception))
                {
                    failures.Add(new ListenerFailure(listener.GetType(), 0, ListenerFailureKind.Threw, exception));
                    _logger.Error("[SaveSystem] Order of " + _kind + " listener " + listener.GetType().Name + " threw; listener skipped.", exception);
                    continue;
                }

                entries.Add(new Entry(listener, listener.GetType(), order, i));
            }

            Entry[] ordered = entries.ToArray();
            Array.Sort(ordered, EntryComparer.Instance);
            return ordered;
        }

        private void LogOrder(Entry[] ordered, string trigger)
        {
            if (!_logger.IsVerboseEnabled)
            {
                return;
            }

            var builder = new StringBuilder();
            builder.Append("[SaveSystem] Dispatching ").Append(_kind).Append(" listeners (").Append(trigger ?? "<none>").Append("): ");
            for (int i = 0; i < ordered.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(ordered[i].Order).Append(' ').Append(ordered[i].Type.Name);
            }

            _logger.Verbose(builder.ToString());
        }

        private readonly struct Entry
        {
            public Entry(TListener listener, Type type, int order, int registrationIndex)
            {
                Listener = listener;
                Type = type;
                Order = order;
                RegistrationIndex = registrationIndex;
            }

            public TListener Listener { get; }

            public Type Type { get; }

            public int Order { get; }

            public int RegistrationIndex { get; }
        }

        private sealed class EntryComparer : IComparer<Entry>
        {
            public static readonly EntryComparer Instance = new EntryComparer();

            public int Compare(Entry x, Entry y)
            {
                int byOrder = x.Order.CompareTo(y.Order);
                return byOrder != 0 ? byOrder : x.RegistrationIndex.CompareTo(y.RegistrationIndex);
            }
        }
    }
}
