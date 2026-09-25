using System;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Ecanakli.SaveSystem
{
    /// <summary>Async mutex serializing restore, upload batches, deletes and activation. Not reentrant.</summary>
    internal sealed class OperationGate : IDisposable
    {
        // Never disposed: it owns no wait handle, and pending waiters must drain safely after Dispose
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
        private readonly object _sync = new object();
        private long _nextLeaseId;
        private long _heldLeaseId;
        private bool _disposed;

        /// <summary>True while an operation holds the gate.</summary>
        public bool IsHeld
        {
            get
            {
                lock (_sync)
                {
                    return _heldLeaseId != 0;
                }
            }
        }

        public bool IsDisposed
        {
            get
            {
                lock (_sync)
                {
                    return _disposed;
                }
            }
        }

        /// <summary>Waits for exclusive access. Throws OperationCanceledException on ct, ObjectDisposedException after Dispose.</summary>
        public async UniTask<Releaser> AcquireAsync(CancellationToken ct)
        {
            ThrowIfDisposed();
            ct.ThrowIfCancellationRequested();

            // Uncontended fast path completes synchronously
            if (!_semaphore.Wait(0))
            {
                await _semaphore.WaitAsync(ct);
            }

            return Lease();
        }

        /// <summary>Cancel every waiter's token before calling; waiters that still acquire get ObjectDisposedException.</summary>
        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                if (_heldLeaseId != 0)
                {
                    // Release on behalf of the holder so waiters cascade out
                    _heldLeaseId = 0;
                    _semaphore.Release();
                }
            }
        }

        private Releaser Lease()
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    // Pass the permit on so the next waiter also observes disposal
                    _semaphore.Release();
                    throw new ObjectDisposedException(nameof(OperationGate));
                }

                _nextLeaseId++;
                _heldLeaseId = _nextLeaseId;
                return new Releaser(this, _heldLeaseId);
            }
        }

        private void Release(long leaseId)
        {
            lock (_sync)
            {
                // Stale or double release is ignored
                if (_disposed || leaseId == 0 || leaseId != _heldLeaseId)
                {
                    return;
                }

                _heldLeaseId = 0;
                _semaphore.Release();
            }
        }

        private void ThrowIfDisposed()
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(OperationGate));
                }
            }
        }

        /// <summary>Releases the gate once; further Dispose calls on copies are no-ops.</summary>
        internal readonly struct Releaser : IDisposable
        {
            private readonly OperationGate _gate;
            private readonly long _leaseId;

            internal Releaser(OperationGate gate, long leaseId)
            {
                _gate = gate;
                _leaseId = leaseId;
            }

            public void Dispose()
            {
                _gate?.Release(_leaseId);
            }
        }
    }
}
