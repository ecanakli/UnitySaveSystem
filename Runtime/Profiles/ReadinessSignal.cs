using System;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Ecanakli.SaveSystem
{
    /// <summary>Resettable service readiness (F5). Main thread only.</summary>
    internal sealed class ReadinessSignal : IDisposable
    {
        private UniTaskCompletionSource<bool> _source = new UniTaskCompletionSource<bool>();
        private bool _isReady;
        private bool _disposed;

        public bool IsReady => _isReady && !_disposed;

        public bool IsDisposed => _disposed;

        /// <summary>Completes every pending waiter with true.</summary>
        public void SetReady()
        {
            if (_disposed || _isReady)
            {
                return;
            }

            _isReady = true;
            _source.TrySetResult(true);
        }

        /// <summary>Completes every pending waiter with false without becoming ready (a failed initialization).</summary>
        public void Fail()
        {
            if (_disposed || _isReady)
            {
                return;
            }

            _source.TrySetResult(false);
        }

        /// <summary>Not ready again (activation start); new waiters block until SetReady.</summary>
        public void Reset()
        {
            if (_disposed || !_isReady)
            {
                return;
            }

            _isReady = false;
            _source = new UniTaskCompletionSource<bool>();
        }

        /// <summary>True when ready, false after Dispose; caller cancellation throws OperationCanceledException.</summary>
        public UniTask<bool> WhenReadyAsync(CancellationToken ct)
        {
            if (_disposed)
            {
                return UniTask.FromResult(false);
            }

            if (ct.IsCancellationRequested)
            {
                return UniTask.FromCanceled<bool>(ct);
            }

            if (_isReady)
            {
                return UniTask.FromResult(true);
            }

            // UniTask disposes the registration when either side completes
            return _source.Task.AttachExternalCancellation(ct);
        }

        /// <summary>Completes pending waiters with false.</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _isReady = false;
            _source.TrySetResult(false);
        }
    }
}
