using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Ecanakli.SaveSystem.Tests
{
    /// <summary>Provider call kinds for faults, gates and the call log.</summary>
    internal enum CloudOperation
    {
        Read = 0,
        Write = 1,
        Delete = 2,
    }

    /// <summary>One stored cloud value.</summary>
    internal sealed class FakeCloudEntry
    {
        private readonly byte[] _value;

        public FakeCloudEntry(byte[] value, string version, CloudAccess access)
        {
            _value = value ?? throw new ArgumentNullException(nameof(value));
            Version = version;
            Access = access;
        }

        /// <summary>Copy of the stored bytes.</summary>
        public byte[] Value => (byte[])_value.Clone();

        public int ByteCount => _value.Length;

        public string Version { get; }

        public CloudAccess Access { get; }
    }

    /// <summary>Cloud data per account, shareable by several providers (devices). Thread-safe.</summary>
    internal sealed class FakeCloudStore
    {
        private readonly object _sync = new object();
        private readonly Dictionary<string, Dictionary<string, FakeCloudEntry>> _accounts =
            new Dictionary<string, Dictionary<string, FakeCloudEntry>>(StringComparer.Ordinal);

        private long _nextVersion;

        /// <summary>Lock for compound check-and-write sequences; store members take the same lock.</summary>
        internal object SyncRoot => _sync;

        public bool TryGet(string accountId, string key, out FakeCloudEntry entry)
        {
            lock (_sync)
            {
                entry = null;
                return _accounts.TryGetValue(accountId, out Dictionary<string, FakeCloudEntry> entries) && entries.TryGetValue(key, out entry);
            }
        }

        /// <summary>Stores bytes verbatim and returns the new unique version token.</summary>
        public string Put(string accountId, string key, byte[] value, CloudAccess access = CloudAccess.ClientOwned)
        {
            if (string.IsNullOrEmpty(accountId))
            {
                throw new ArgumentException("Account id must not be null or empty.", nameof(accountId));
            }

            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentException("Key must not be null or empty.", nameof(key));
            }

            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            lock (_sync)
            {
                if (!_accounts.TryGetValue(accountId, out Dictionary<string, FakeCloudEntry> entries))
                {
                    entries = new Dictionary<string, FakeCloudEntry>(StringComparer.Ordinal);
                    _accounts[accountId] = entries;
                }

                _nextVersion++;
                string version = "v" + _nextVersion;
                entries[key] = new FakeCloudEntry((byte[])value.Clone(), version, access);
                return version;
            }
        }

        public string PutText(string accountId, string key, string text, CloudAccess access = CloudAccess.ClientOwned)
        {
            return Put(accountId, key, new UTF8Encoding(false).GetBytes(text ?? throw new ArgumentNullException(nameof(text))), access);
        }

        public bool Remove(string accountId, string key)
        {
            lock (_sync)
            {
                return _accounts.TryGetValue(accountId, out Dictionary<string, FakeCloudEntry> entries) && entries.Remove(key);
            }
        }

        public bool Contains(string accountId, string key)
        {
            return TryGet(accountId, key, out _);
        }

        public byte[] GetValue(string accountId, string key)
        {
            return TryGet(accountId, key, out FakeCloudEntry entry) ? entry.Value : null;
        }

        public string GetText(string accountId, string key)
        {
            byte[] value = GetValue(accountId, key);
            return value == null ? null : Encoding.UTF8.GetString(value);
        }

        public string GetVersion(string accountId, string key)
        {
            return TryGet(accountId, key, out FakeCloudEntry entry) ? entry.Version : null;
        }

        /// <summary>Keys of one account, ordinal sorted.</summary>
        public IReadOnlyList<string> GetKeys(string accountId)
        {
            lock (_sync)
            {
                var keys = _accounts.TryGetValue(accountId, out Dictionary<string, FakeCloudEntry> entries)
                    ? new List<string>(entries.Keys)
                    : new List<string>();
                keys.Sort(StringComparer.Ordinal);
                return keys;
            }
        }

        public long GetTotalBytes(string accountId)
        {
            lock (_sync)
            {
                long total = 0;
                if (_accounts.TryGetValue(accountId, out Dictionary<string, FakeCloudEntry> entries))
                {
                    foreach (FakeCloudEntry entry in entries.Values)
                    {
                        total += entry.ByteCount;
                    }
                }

                return total;
            }
        }

        public void ClearAccount(string accountId)
        {
            lock (_sync)
            {
                _accounts.Remove(accountId);
            }
        }

        public void Clear()
        {
            lock (_sync)
            {
                _accounts.Clear();
            }
        }
    }

    /// <summary>Scripted provider fault. Key null matches any key.</summary>
    internal sealed class FakeCloudFault
    {
        private int _remaining;

        internal FakeCloudFault(CloudOperation operation, string key, CloudError error, Exception exception, bool applyBeforeFailing, int times)
        {
            if (times == 0 || times < -1)
            {
                throw new ArgumentOutOfRangeException(nameof(times), "Use a positive count or -1 for unlimited.");
            }

            Operation = operation;
            Key = key;
            Error = error;
            Exception = exception;
            ApplyBeforeFailing = applyBeforeFailing;
            _remaining = times;
        }

        public CloudOperation Operation { get; }

        public string Key { get; }

        /// <summary>Per-key error result; null for a throwing fault.</summary>
        public CloudError Error { get; }

        /// <summary>Thrown from the whole call; null for an error result.</summary>
        public Exception Exception { get; }

        /// <summary>Lost response: the write or delete is applied, then the error is returned.</summary>
        public bool ApplyBeforeFailing { get; }

        public int HitCount { get; private set; }

        public bool IsExhausted => _remaining == 0;

        internal bool Matches(CloudOperation operation, string key, bool throwing)
        {
            return _remaining != 0 && Operation == operation && (Exception != null) == throwing && (Key == null || string.Equals(Key, key, StringComparison.Ordinal));
        }

        internal void Hit()
        {
            if (_remaining > 0)
            {
                _remaining--;
            }

            HitCount++;
        }
    }

    /// <summary>One logged provider call; the account is captured when the call starts.</summary>
    internal sealed class CloudCall
    {
        private static readonly CloudReadRequest[] NoReads = new CloudReadRequest[0];
        private static readonly CloudWriteRequest[] NoWrites = new CloudWriteRequest[0];

        private CloudCall(CloudOperation operation, string accountId, string[] keys, CloudReadRequest[] reads, CloudWriteRequest[] writes, string expectedVersion)
        {
            Operation = operation;
            AccountId = accountId;
            Keys = keys;
            ReadRequests = reads;
            WriteRequests = writes;
            DeleteExpectedVersion = expectedVersion;
        }

        public CloudOperation Operation { get; }

        public string AccountId { get; }

        public IReadOnlyList<string> Keys { get; }

        public IReadOnlyList<CloudReadRequest> ReadRequests { get; }

        public IReadOnlyList<CloudWriteRequest> WriteRequests { get; }

        public string DeleteExpectedVersion { get; }

        public override string ToString()
        {
            return Operation + "(" + string.Join(",", Keys) + ") account=" + (AccountId ?? "<none>");
        }

        internal static CloudCall ForRead(string accountId, IReadOnlyList<CloudReadRequest> requests)
        {
            var copy = requests.Count == 0 ? NoReads : new CloudReadRequest[requests.Count];
            var keys = new string[requests.Count];
            for (int i = 0; i < requests.Count; i++)
            {
                copy[i] = requests[i];
                keys[i] = requests[i].Key;
            }

            return new CloudCall(CloudOperation.Read, accountId, keys, copy, NoWrites, null);
        }

        internal static CloudCall ForWrite(string accountId, IReadOnlyList<CloudWriteRequest> requests)
        {
            var copy = requests.Count == 0 ? NoWrites : new CloudWriteRequest[requests.Count];
            var keys = new string[requests.Count];
            for (int i = 0; i < requests.Count; i++)
            {
                copy[i] = requests[i];
                keys[i] = requests[i].Key;
            }

            return new CloudCall(CloudOperation.Write, accountId, keys, NoReads, copy, null);
        }

        internal static CloudCall ForDelete(string accountId, string key, string expectedVersion)
        {
            return new CloudCall(CloudOperation.Delete, accountId, new[] { key }, NoReads, NoWrites, expectedVersion);
        }
    }

    /// <summary>
    /// In-memory ICloudSaveProvider over a FakeCloudStore. Completes synchronously unless a gate is held.
    /// Versions are returned only when SupportsConditionalWrite; PreservesValueText false re-indents stored JSON.
    /// </summary>
    internal sealed class FakeCloudSaveProvider : ICloudSaveProvider
    {
        private readonly object _sync = new object();
        private readonly List<FakeCloudFault> _faults = new List<FakeCloudFault>();
        private readonly List<CloudCall> _calls = new List<CloudCall>();
        private CloudCapabilities _capabilities;
        private string _signedInAccountId;
        private UniTaskCompletionSource _readGate;
        private UniTaskCompletionSource _writeGate;
        private UniTaskCompletionSource _deleteGate;
        private int _heldCallCount;
        private int _batchLimitViolationCount;

        public FakeCloudSaveProvider(FakeCloudStore store = null, CloudCapabilities capabilities = null, string signedInAccountId = null)
        {
            Store = store ?? new FakeCloudStore();
            _capabilities = capabilities ?? CreateCapabilities();
            _signedInAccountId = signedInAccountId;
        }

        public FakeCloudStore Store { get; }

        public CloudCapabilities Capabilities
        {
            get
            {
                lock (_sync)
                {
                    return _capabilities;
                }
            }

            set
            {
                lock (_sync)
                {
                    _capabilities = value ?? throw new ArgumentNullException(nameof(value));
                }
            }
        }

        /// <summary>Switchable at any time; null means signed out.</summary>
        public string SignedInAccountId
        {
            get
            {
                lock (_sync)
                {
                    return _signedInAccountId;
                }
            }

            set
            {
                lock (_sync)
                {
                    _signedInAccountId = value;
                }
            }
        }

        /// <summary>Invoked after read results are built, before they are returned (e.g. switch account).</summary>
        public Action<FakeCloudSaveProvider, IReadOnlyList<CloudReadRequest>> AfterRead { get; set; }

        /// <summary>Invoked after write results are built, before they are returned.</summary>
        public Action<FakeCloudSaveProvider, IReadOnlyList<CloudWriteRequest>> AfterWrite { get; set; }

        /// <summary>Invoked after a delete result is built, before it is returned.</summary>
        public Action<FakeCloudSaveProvider, string> AfterDelete { get; set; }

        public IReadOnlyList<CloudCall> Calls
        {
            get
            {
                lock (_sync)
                {
                    return _calls.ToArray();
                }
            }
        }

        public int ReadCallCount => CountCalls(CloudOperation.Read, null);

        public int WriteCallCount => CountCalls(CloudOperation.Write, null);

        public int DeleteCallCount => CountCalls(CloudOperation.Delete, null);

        public int TotalCallCount
        {
            get
            {
                lock (_sync)
                {
                    return _calls.Count;
                }
            }
        }

        /// <summary>Calls currently waiting on a held gate.</summary>
        public int HeldCallCount => Interlocked.CompareExchange(ref _heldCallCount, 0, 0);

        /// <summary>Held calls ignore their CancellationToken and return only on Release (a wedged SDK call).</summary>
        public bool IgnoresCancellationWhileHeld { get; set; }

        /// <summary>Batches larger than MaxKeysPerRead or MaxKeysPerWrite (every key failed Permanent).</summary>
        public int BatchLimitViolationCount
        {
            get
            {
                lock (_sync)
                {
                    return _batchLimitViolationCount;
                }
            }
        }

        public static CloudCapabilities CreateCapabilities(
            int maxValueBytes = 1000000,
            int maxKeysPerWrite = 20,
            int maxKeysPerRead = 20,
            long maxTotalBytes = 0,
            bool supportsConditionalWrite = true,
            bool preservesValueText = true)
        {
            return new CloudCapabilities(maxValueBytes, maxKeysPerWrite, maxKeysPerRead, maxTotalBytes, supportsConditionalWrite, preservesValueText);
        }

        public UniTask<IReadOnlyList<CloudReadResult>> ReadAsync(IReadOnlyList<CloudReadRequest> requests, CancellationToken ct)
        {
            if (requests == null)
            {
                return UniTask.FromException<IReadOnlyList<CloudReadResult>>(new ArgumentNullException(nameof(requests)));
            }

            if (ct.IsCancellationRequested)
            {
                return UniTask.FromCanceled<IReadOnlyList<CloudReadResult>>(ct);
            }

            string accountId;
            UniTaskCompletionSource gate;
            Exception thrown;
            lock (_sync)
            {
                accountId = _signedInAccountId;
                _calls.Add(CloudCall.ForRead(accountId, requests));
                thrown = TakeThrow(CloudOperation.Read);
                gate = _readGate;
            }

            if (thrown != null)
            {
                return UniTask.FromException<IReadOnlyList<CloudReadResult>>(thrown);
            }

            return gate == null ? UniTask.FromResult(CompleteRead(accountId, requests)) : ReadAfterGateAsync(gate, accountId, requests, ct);
        }

        public UniTask<IReadOnlyList<CloudWriteResult>> WriteAsync(IReadOnlyList<CloudWriteRequest> requests, CancellationToken ct)
        {
            if (requests == null)
            {
                return UniTask.FromException<IReadOnlyList<CloudWriteResult>>(new ArgumentNullException(nameof(requests)));
            }

            if (ct.IsCancellationRequested)
            {
                return UniTask.FromCanceled<IReadOnlyList<CloudWriteResult>>(ct);
            }

            string accountId;
            UniTaskCompletionSource gate;
            Exception thrown;
            lock (_sync)
            {
                accountId = _signedInAccountId;
                _calls.Add(CloudCall.ForWrite(accountId, requests));
                thrown = TakeThrow(CloudOperation.Write);
                gate = _writeGate;
            }

            if (thrown != null)
            {
                return UniTask.FromException<IReadOnlyList<CloudWriteResult>>(thrown);
            }

            return gate == null ? UniTask.FromResult(CompleteWrite(accountId, requests)) : WriteAfterGateAsync(gate, accountId, requests, ct);
        }

        public UniTask<CloudDeleteResult> DeleteAsync(string key, string expectedVersion, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(key))
            {
                return UniTask.FromException<CloudDeleteResult>(new ArgumentException("Key must not be null or empty.", nameof(key)));
            }

            if (ct.IsCancellationRequested)
            {
                return UniTask.FromCanceled<CloudDeleteResult>(ct);
            }

            string accountId;
            UniTaskCompletionSource gate;
            Exception thrown;
            lock (_sync)
            {
                accountId = _signedInAccountId;
                _calls.Add(CloudCall.ForDelete(accountId, key, expectedVersion));
                thrown = TakeThrow(CloudOperation.Delete);
                gate = _deleteGate;
            }

            if (thrown != null)
            {
                return UniTask.FromException<CloudDeleteResult>(thrown);
            }

            return gate == null ? UniTask.FromResult(CompleteDelete(accountId, key, expectedVersion)) : DeleteAfterGateAsync(gate, accountId, key, expectedVersion, ct);
        }

        /// <summary>Stores raw bytes verbatim (e.g. corrupt values); returns the version.</summary>
        public string SetRawValue(string accountId, string key, byte[] value, CloudAccess access = CloudAccess.ClientOwned)
        {
            return Store.Put(accountId, key, value, access);
        }

        public string SetRawText(string accountId, string key, string text, CloudAccess access = CloudAccess.ClientOwned)
        {
            return Store.PutText(accountId, key, text, access);
        }

        /// <summary>Per-key error result; times -1 means unlimited.</summary>
        public FakeCloudFault EnqueueError(CloudOperation operation, string key, CloudError error, int times = 1)
        {
            return AddFault(new FakeCloudFault(operation, key, error ?? throw new ArgumentNullException(nameof(error)), null, false, times));
        }

        public FakeCloudFault EnqueueError(CloudOperation operation, string key, CloudErrorKind kind, int times = 1, TimeSpan? retryAfter = null)
        {
            return EnqueueError(operation, key, new CloudError(kind, "Scripted " + kind + " on " + (key ?? "any key") + ".", retryAfter), times);
        }

        /// <summary>Write or delete is applied, then a Transient error is returned.</summary>
        public FakeCloudFault EnqueueLostResponse(CloudOperation operation, string key, int times = 1)
        {
            if (operation == CloudOperation.Read)
            {
                throw new ArgumentException("Lost responses apply to Write and Delete only.", nameof(operation));
            }

            var error = new CloudError(CloudErrorKind.Transient, "Scripted lost response on " + (key ?? "any key") + ".");
            return AddFault(new FakeCloudFault(operation, key, error, null, true, times));
        }

        /// <summary>The whole call throws (contract violation by a provider).</summary>
        public FakeCloudFault EnqueueThrow(CloudOperation operation, Exception exception, int times = 1)
        {
            return AddFault(new FakeCloudFault(operation, null, null, exception ?? throw new ArgumentNullException(nameof(exception)), false, times));
        }

        public void ClearFaults()
        {
            lock (_sync)
            {
                _faults.Clear();
            }
        }

        /// <summary>Holds reads until ReleaseReads; results are built from the store at release time.</summary>
        public void HoldReads()
        {
            lock (_sync)
            {
                _readGate = _readGate ?? new UniTaskCompletionSource();
            }
        }

        public void ReleaseReads()
        {
            Release(ref _readGate);
        }

        public void HoldWrites()
        {
            lock (_sync)
            {
                _writeGate = _writeGate ?? new UniTaskCompletionSource();
            }
        }

        public void ReleaseWrites()
        {
            Release(ref _writeGate);
        }

        public void HoldDeletes()
        {
            lock (_sync)
            {
                _deleteGate = _deleteGate ?? new UniTaskCompletionSource();
            }
        }

        public void ReleaseDeletes()
        {
            Release(ref _deleteGate);
        }

        /// <summary>Calls of operation whose keys contain key (null counts every call).</summary>
        public int CountCalls(CloudOperation operation, string key)
        {
            lock (_sync)
            {
                int count = 0;
                foreach (CloudCall call in _calls)
                {
                    if (call.Operation != operation)
                    {
                        continue;
                    }

                    if (key == null)
                    {
                        count++;
                        continue;
                    }

                    for (int i = 0; i < call.Keys.Count; i++)
                    {
                        if (string.Equals(call.Keys[i], key, StringComparison.Ordinal))
                        {
                            count++;
                            break;
                        }
                    }
                }

                return count;
            }
        }

        /// <summary>Every write request ever sent, in order.</summary>
        public IReadOnlyList<CloudWriteRequest> GetAllWriteRequests()
        {
            lock (_sync)
            {
                var requests = new List<CloudWriteRequest>();
                foreach (CloudCall call in _calls)
                {
                    requests.AddRange(call.WriteRequests);
                }

                return requests;
            }
        }

        public void ClearCalls()
        {
            lock (_sync)
            {
                _calls.Clear();
                _batchLimitViolationCount = 0;
            }
        }

        private async UniTask<IReadOnlyList<CloudReadResult>> ReadAfterGateAsync(
            UniTaskCompletionSource gate, string accountId, IReadOnlyList<CloudReadRequest> requests, CancellationToken ct)
        {
            await WaitGateAsync(gate, ct);
            return CompleteRead(accountId, requests);
        }

        private async UniTask<IReadOnlyList<CloudWriteResult>> WriteAfterGateAsync(
            UniTaskCompletionSource gate, string accountId, IReadOnlyList<CloudWriteRequest> requests, CancellationToken ct)
        {
            await WaitGateAsync(gate, ct);
            return CompleteWrite(accountId, requests);
        }

        private async UniTask<CloudDeleteResult> DeleteAfterGateAsync(
            UniTaskCompletionSource gate, string accountId, string key, string expectedVersion, CancellationToken ct)
        {
            await WaitGateAsync(gate, ct);
            return CompleteDelete(accountId, key, expectedVersion);
        }

        private async UniTask WaitGateAsync(UniTaskCompletionSource gate, CancellationToken ct)
        {
            Interlocked.Increment(ref _heldCallCount);
            try
            {
                if (IgnoresCancellationWhileHeld)
                {
                    await gate.Task;
                    return;
                }

                await gate.Task.AttachExternalCancellation(ct);
            }
            finally
            {
                Interlocked.Decrement(ref _heldCallCount);
            }
        }

        private IReadOnlyList<CloudReadResult> CompleteRead(string accountId, IReadOnlyList<CloudReadRequest> requests)
        {
            CloudCapabilities capabilities = Capabilities;
            var results = new CloudReadResult[requests.Count];
            bool overLimit = requests.Count > capabilities.MaxKeysPerRead;
            if (overLimit)
            {
                CountBatchViolation();
            }

            for (int i = 0; i < requests.Count; i++)
            {
                string key = requests[i].Key;
                if (overLimit)
                {
                    results[i] = CloudReadResult.Failed(key, BatchTooLarge(requests.Count, capabilities.MaxKeysPerRead));
                    continue;
                }

                FakeCloudFault fault = TakeFault(CloudOperation.Read, key);
                if (fault != null)
                {
                    results[i] = CloudReadResult.Failed(key, fault.Error);
                    continue;
                }

                if (accountId == null)
                {
                    results[i] = CloudReadResult.Failed(key, NotSignedIn());
                    continue;
                }

                results[i] = Store.TryGet(accountId, key, out FakeCloudEntry entry)
                    ? CloudReadResult.Found(key, entry.Value, capabilities.SupportsConditionalWrite ? entry.Version : null)
                    : CloudReadResult.NotFound(key);
            }

            AfterRead?.Invoke(this, requests);
            return results;
        }

        private IReadOnlyList<CloudWriteResult> CompleteWrite(string accountId, IReadOnlyList<CloudWriteRequest> requests)
        {
            CloudCapabilities capabilities = Capabilities;
            var results = new CloudWriteResult[requests.Count];
            bool overLimit = requests.Count > capabilities.MaxKeysPerWrite;
            if (overLimit)
            {
                CountBatchViolation();
            }

            for (int i = 0; i < requests.Count; i++)
            {
                CloudWriteRequest request = requests[i];
                if (overLimit)
                {
                    results[i] = CloudWriteResult.Failed(request.Key, BatchTooLarge(requests.Count, capabilities.MaxKeysPerWrite));
                    continue;
                }

                FakeCloudFault fault = TakeFault(CloudOperation.Write, request.Key);
                if (fault != null && !fault.ApplyBeforeFailing)
                {
                    results[i] = CloudWriteResult.Failed(request.Key, fault.Error);
                    continue;
                }

                CloudWriteResult result = ApplyWrite(accountId, request, capabilities);
                results[i] = fault != null && result.IsSuccess ? CloudWriteResult.Failed(request.Key, fault.Error) : result;
            }

            AfterWrite?.Invoke(this, requests);
            return results;
        }

        private CloudWriteResult ApplyWrite(string accountId, CloudWriteRequest request, CloudCapabilities capabilities)
        {
            string key = request.Key;
            if (accountId == null)
            {
                return CloudWriteResult.Failed(key, NotSignedIn());
            }

            if (request.Access == CloudAccess.ServerOwned)
            {
                return CloudWriteResult.Failed(key, new CloudError(CloudErrorKind.Unauthorized, "Clients cannot write server-owned key " + key + "."));
            }

            if (request.Value.Length > capabilities.MaxValueBytes)
            {
                return CloudWriteResult.Failed(
                    key, new CloudError(CloudErrorKind.PayloadTooLarge, "Value of " + request.Value.Length + " bytes exceeds " + capabilities.MaxValueBytes + "."));
            }

            byte[] stored = capabilities.PreservesValueText ? request.Value : Reformat(request.Value);
            lock (Store.SyncRoot)
            {
                bool exists = Store.TryGet(accountId, key, out FakeCloudEntry existing);
                if (capabilities.SupportsConditionalWrite && request.ExpectedVersion != null
                                                          && (!exists || !string.Equals(existing.Version, request.ExpectedVersion, StringComparison.Ordinal)))
                {
                    return CloudWriteResult.Failed(
                        key, new CloudError(CloudErrorKind.Conflict, "Expected version " + request.ExpectedVersion + " but found " + (exists ? existing.Version : "none") + "."));
                }

                if (capabilities.MaxTotalBytes > 0)
                {
                    long total = Store.GetTotalBytes(accountId) - (exists ? existing.ByteCount : 0) + stored.Length;
                    if (total > capabilities.MaxTotalBytes)
                    {
                        return CloudWriteResult.Failed(key, new CloudError(CloudErrorKind.QuotaExceeded, "Quota of " + capabilities.MaxTotalBytes + " bytes exceeded."));
                    }
                }

                string version = Store.Put(accountId, key, stored, request.Access);
                return CloudWriteResult.Succeeded(key, capabilities.SupportsConditionalWrite ? version : null);
            }
        }

        private CloudDeleteResult CompleteDelete(string accountId, string key, string expectedVersion)
        {
            CloudCapabilities capabilities = Capabilities;
            CloudDeleteResult result;
            FakeCloudFault fault = TakeFault(CloudOperation.Delete, key);
            if (fault != null && !fault.ApplyBeforeFailing)
            {
                result = CloudDeleteResult.Failed(key, fault.Error);
            }
            else
            {
                result = ApplyDelete(accountId, key, expectedVersion, capabilities);
                if (fault != null && (result.IsSuccess || result.IsNotFound))
                {
                    result = CloudDeleteResult.Failed(key, fault.Error);
                }
            }

            AfterDelete?.Invoke(this, key);
            return result;
        }

        private CloudDeleteResult ApplyDelete(string accountId, string key, string expectedVersion, CloudCapabilities capabilities)
        {
            if (accountId == null)
            {
                return CloudDeleteResult.Failed(key, NotSignedIn());
            }

            lock (Store.SyncRoot)
            {
                if (!Store.TryGet(accountId, key, out FakeCloudEntry existing))
                {
                    return CloudDeleteResult.NotFound(key);
                }

                if (capabilities.SupportsConditionalWrite && expectedVersion != null
                                                          && !string.Equals(existing.Version, expectedVersion, StringComparison.Ordinal))
                {
                    return CloudDeleteResult.Failed(
                        key, new CloudError(CloudErrorKind.Conflict, "Expected version " + expectedVersion + " but found " + existing.Version + "."));
                }

                Store.Remove(accountId, key);
                return CloudDeleteResult.Deleted(key);
            }
        }

        private FakeCloudFault AddFault(FakeCloudFault fault)
        {
            lock (_sync)
            {
                _faults.Add(fault);
            }

            return fault;
        }

        private FakeCloudFault TakeFault(CloudOperation operation, string key)
        {
            lock (_sync)
            {
                for (int i = 0; i < _faults.Count; i++)
                {
                    if (_faults[i].Matches(operation, key, false))
                    {
                        _faults[i].Hit();
                        return _faults[i];
                    }
                }

                return null;
            }
        }

        // Caller holds _sync
        private Exception TakeThrow(CloudOperation operation)
        {
            for (int i = 0; i < _faults.Count; i++)
            {
                if (_faults[i].Matches(operation, null, true))
                {
                    _faults[i].Hit();
                    return _faults[i].Exception;
                }
            }

            return null;
        }

        private void Release(ref UniTaskCompletionSource gateField)
        {
            UniTaskCompletionSource gate;
            lock (_sync)
            {
                gate = gateField;
                gateField = null;
            }

            gate?.TrySetResult();
        }

        private void CountBatchViolation()
        {
            lock (_sync)
            {
                _batchLimitViolationCount++;
            }
        }

        private static CloudError NotSignedIn()
        {
            return new CloudError(CloudErrorKind.NotSignedIn, "No account is signed in.");
        }

        private static CloudError BatchTooLarge(int count, int limit)
        {
            return new CloudError(CloudErrorKind.Permanent, "Batch of " + count + " keys exceeds the limit of " + limit + ".");
        }

        // Simulates a backend that stores parsed JSON: bytes come back re-formatted
        private static byte[] Reformat(byte[] value)
        {
            try
            {
                string text = new UTF8Encoding(false, true).GetString(value);
                using (var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None })
                {
                    JToken token = JToken.ReadFrom(reader);
                    return new UTF8Encoding(false).GetBytes(token.ToString(Formatting.Indented));
                }
            }
            catch (Exception)
            {
                return value;
            }
        }
    }
}
