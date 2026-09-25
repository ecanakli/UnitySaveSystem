using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Ecanakli.SaveSystem
{
    /// <summary>Caller-side precondition the gateway re-evaluates before every attempt, so a retry after a backoff cannot outlive it.</summary>
    internal interface ICloudCallGuard
    {
        /// <summary>Null when the attempt may proceed; otherwise the error the gateway returns without calling the provider.</summary>
        CloudError CheckBeforeAttempt();
    }

    /// <summary>
    /// Capability-aware provider wrapper: chunking, size and quota prechecks, retries.
    /// Returns one result per request in request order; throws only OperationCanceledException on ct.
    /// </summary>
    internal sealed class CloudGateway
    {
        private readonly ICloudSaveProvider _provider;
        private readonly RetryPolicy _retryPolicy;
        private readonly ISaveLogger _logger;
        private readonly TimeSpan _callTimeout;

        /// <param name="callTimeout">Budget for one provider call; zero or less disables the timeout.</param>
        public CloudGateway(ICloudSaveProvider provider, RetryPolicy retryPolicy, ISaveLogger logger, TimeSpan callTimeout = default)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _retryPolicy = retryPolicy ?? throw new ArgumentNullException(nameof(retryPolicy));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _callTimeout = callTimeout;
        }

        public ICloudSaveProvider Provider => _provider;

        public RetryPolicy RetryPolicy => _retryPolicy;

        /// <summary>Budget for one provider call; TimeSpan.Zero means no timeout.</summary>
        public TimeSpan CallTimeout => _callTimeout;

        /// <summary>Provider capabilities; throws InvalidOperationException when the provider returns null.</summary>
        public CloudCapabilities Capabilities
        {
            get
            {
                CloudCapabilities capabilities = _provider.Capabilities;
                if (capabilities == null)
                {
                    throw new InvalidOperationException("ICloudSaveProvider.Capabilities returned null.");
                }

                return capabilities;
            }
        }

        public string SignedInAccountId => _provider.SignedInAccountId;

        /// <summary>Reads in MaxKeysPerRead chunks; NotFound is a result and never retried.</summary>
        public async UniTask<IReadOnlyList<CloudReadResult>> ReadAsync(IReadOnlyList<CloudReadRequest> requests, CancellationToken ct)
        {
            ValidateRequests(requests);
            var results = new CloudReadResult[requests.Count];
            if (requests.Count == 0)
            {
                return results;
            }

            int chunkSize = Capabilities.MaxKeysPerRead;
            var pending = new List<int>(chunkSize);
            var retry = new List<int>(chunkSize);
            var batch = new List<CloudReadRequest>(chunkSize);

            for (int start = 0; start < requests.Count; start += chunkSize)
            {
                pending.Clear();
                int end = Math.Min(start + chunkSize, requests.Count);
                for (int i = start; i < end; i++)
                {
                    pending.Add(i);
                }

                int retriesDone = 0;
                while (pending.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    batch.Clear();
                    for (int i = 0; i < pending.Count; i++)
                    {
                        batch.Add(requests[pending[i]]);
                    }

                    IReadOnlyList<CloudReadResult> response = await CallReadAsync(batch, ct);
                    retry.Clear();
                    TimeSpan delay = TimeSpan.Zero;
                    for (int i = 0; i < pending.Count; i++)
                    {
                        int index = pending[i];
                        CloudReadResult result = response[i] ?? CloudReadResult.Failed(requests[index].Key, NullResultError());
                        results[index] = result;
                        if (result.Status == CloudReadStatus.Failed && _retryPolicy.ShouldRetry(result.Error, retriesDone))
                        {
                            retry.Add(index);
                            delay = Max(delay, _retryPolicy.GetDelay(result.Error, retriesDone + 1));
                        }
                    }

                    if (retry.Count == 0)
                    {
                        break;
                    }

                    retriesDone++;
                    LogRetry("read", retry.Count, retriesDone, delay);
                    await _retryPolicy.DelayAsync(delay, ct);
                    Swap(ref pending, ref retry);
                }
            }

            return results;
        }

        /// <summary>Writes without a quota precheck.</summary>
        public UniTask<IReadOnlyList<CloudWriteResult>> WriteAsync(IReadOnlyList<CloudWriteRequest> requests, CancellationToken ct)
        {
            return WriteAsync(requests, null, ct);
        }

        /// <summary>
        /// Writes in MaxKeysPerWrite chunks. Oversize values fail with PayloadTooLarge and, when storedBytesByKey is given
        /// and MaxTotalBytes is set, growth past the quota fails with QuotaExceeded; neither reaches the provider.
        /// ExpectedVersion is dropped when the provider does not support conditional writes.
        /// </summary>
        /// <param name="storedBytesByKey">Last known stored size per cloud key of the profile; null skips the quota precheck.</param>
        public async UniTask<IReadOnlyList<CloudWriteResult>> WriteAsync(
            IReadOnlyList<CloudWriteRequest> requests,
            IReadOnlyDictionary<string, long> storedBytesByKey,
            CancellationToken ct)
        {
            ValidateRequests(requests);
            var results = new CloudWriteResult[requests.Count];
            if (requests.Count == 0)
            {
                return results;
            }

            CloudCapabilities capabilities = Capabilities;
            var accepted = new List<int>(requests.Count);
            var effective = new CloudWriteRequest[requests.Count];
            Precheck(requests, storedBytesByKey, capabilities, results, effective, accepted);

            int chunkSize = capabilities.MaxKeysPerWrite;
            var pending = new List<int>(chunkSize);
            var retry = new List<int>(chunkSize);
            var batch = new List<CloudWriteRequest>(chunkSize);

            for (int start = 0; start < accepted.Count; start += chunkSize)
            {
                pending.Clear();
                int end = Math.Min(start + chunkSize, accepted.Count);
                for (int i = start; i < end; i++)
                {
                    pending.Add(accepted[i]);
                }

                int retriesDone = 0;
                while (pending.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    batch.Clear();
                    for (int i = 0; i < pending.Count; i++)
                    {
                        batch.Add(effective[pending[i]]);
                    }

                    IReadOnlyList<CloudWriteResult> response = await CallWriteAsync(batch, ct);
                    retry.Clear();
                    TimeSpan delay = TimeSpan.Zero;
                    for (int i = 0; i < pending.Count; i++)
                    {
                        int index = pending[i];
                        CloudWriteResult result = response[i] ?? CloudWriteResult.Failed(requests[index].Key, NullResultError());
                        results[index] = result;
                        if (!result.IsSuccess && _retryPolicy.ShouldRetry(result.Error, retriesDone))
                        {
                            retry.Add(index);
                            delay = Max(delay, _retryPolicy.GetDelay(result.Error, retriesDone + 1));
                        }
                    }

                    if (retry.Count == 0)
                    {
                        break;
                    }

                    retriesDone++;
                    LogRetry("write", retry.Count, retriesDone, delay);
                    await _retryPolicy.DelayAsync(delay, ct);
                    Swap(ref pending, ref retry);
                }
            }

            return results;
        }

        /// <summary>Deletes one key with retries; expectedVersion is dropped when conditional writes are unsupported.</summary>
        public UniTask<CloudDeleteResult> DeleteAsync(string key, string expectedVersion, CancellationToken ct)
        {
            return DeleteAsync(key, expectedVersion, null, ct);
        }

        /// <summary>Deletes one key with retries; guard is evaluated before every attempt, including the first.</summary>
        public async UniTask<CloudDeleteResult> DeleteAsync(string key, string expectedVersion, ICloudCallGuard guard, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentException("Key must not be null or empty.", nameof(key));
            }

            string version = Capabilities.SupportsConditionalWrite ? expectedVersion : null;
            int retriesDone = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                // A retry runs after a backoff, so the caller's precondition is re-checked here and not only at the call site
                CloudError refused = guard?.CheckBeforeAttempt();
                if (refused != null)
                {
                    return CloudDeleteResult.Failed(key, refused);
                }

                CloudDeleteResult result = await CallDeleteAsync(key, version, ct);
                if (result.IsSuccess || result.IsNotFound || !_retryPolicy.ShouldRetry(result.Error, retriesDone))
                {
                    return result;
                }

                retriesDone++;
                TimeSpan delay = _retryPolicy.GetDelay(result.Error, retriesDone);
                LogRetry("delete", 1, retriesDone, delay);
                await _retryPolicy.DelayAsync(delay, ct);
            }
        }

        private static void Precheck(
            IReadOnlyList<CloudWriteRequest> requests,
            IReadOnlyDictionary<string, long> storedBytesByKey,
            CloudCapabilities capabilities,
            CloudWriteResult[] results,
            CloudWriteRequest[] effective,
            List<int> accepted)
        {
            bool checkQuota = storedBytesByKey != null && capabilities.MaxTotalBytes > 0;
            long total = 0;
            Dictionary<string, long> projected = null;
            if (checkQuota)
            {
                projected = new Dictionary<string, long>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, long> pair in storedBytesByKey)
                {
                    long bytes = Math.Max(0, pair.Value);
                    projected[pair.Key] = bytes;
                    total += bytes;
                }
            }

            for (int i = 0; i < requests.Count; i++)
            {
                CloudWriteRequest request = requests[i];
                long size = request.Value.Length;
                if (size > capabilities.MaxValueBytes)
                {
                    results[i] = CloudWriteResult.Failed(request.Key, new CloudError(
                        CloudErrorKind.PayloadTooLarge,
                        "Value of '" + request.Key + "' is " + size + " bytes; the provider limit is " + capabilities.MaxValueBytes + "."));
                    continue;
                }

                if (checkQuota)
                {
                    projected.TryGetValue(request.Key, out long previous);
                    long growth = size - previous;
                    if (growth > 0 && total + growth > capabilities.MaxTotalBytes)
                    {
                        results[i] = CloudWriteResult.Failed(request.Key, new CloudError(
                            CloudErrorKind.QuotaExceeded,
                            "Writing '" + request.Key + "' would store " + (total + growth) + " bytes; the quota is " + capabilities.MaxTotalBytes + "."));
                        continue;
                    }

                    total += growth;
                    projected[request.Key] = size;
                }

                effective[i] = capabilities.SupportsConditionalWrite || request.ExpectedVersion == null
                    ? request
                    : new CloudWriteRequest(request.Key, request.Value, null, request.Access);
                accepted.Add(i);
            }
        }

        private UniTask<IReadOnlyList<CloudReadResult>> CallReadAsync(List<CloudReadRequest> batch, CancellationToken ct)
        {
            return WithTimeoutAsync<IReadOnlyList<CloudReadResult>>(
                token => InvokeReadAsync(batch, token),
                error => FailAll(batch, error, static (request, e) => CloudReadResult.Failed(request.Key, e)),
                "ReadAsync",
                ct);
        }

        private UniTask<IReadOnlyList<CloudWriteResult>> CallWriteAsync(List<CloudWriteRequest> batch, CancellationToken ct)
        {
            return WithTimeoutAsync<IReadOnlyList<CloudWriteResult>>(
                token => InvokeWriteAsync(batch, token),
                error => FailAll(batch, error, static (request, e) => CloudWriteResult.Failed(request.Key, e)),
                "WriteAsync",
                ct);
        }

        private UniTask<CloudDeleteResult> CallDeleteAsync(string key, string expectedVersion, CancellationToken ct)
        {
            return WithTimeoutAsync<CloudDeleteResult>(
                token => InvokeDeleteAsync(key, expectedVersion, token),
                error => CloudDeleteResult.Failed(key, error),
                "DeleteAsync",
                ct);
        }

        /// <summary>
        /// S3: a provider call that neither returns nor honours its token is abandoned after CallTimeout and reported as
        /// a transient error, so the caller releases the operation gate and the existing retry paths apply.
        /// </summary>
        private async UniTask<TResult> WithTimeoutAsync<TResult>(
            Func<CancellationToken, UniTask<TResult>> call, Func<CloudError, TResult> onTimeout, string operation, CancellationToken ct)
        {
            if (_callTimeout <= TimeSpan.Zero)
            {
                return await call(ct);
            }

            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                UniTask<TResult> pending = call(linked.Token);
                try
                {
                    // A provider that answers synchronously needs no timer
                    if (pending.Status.IsCompleted())
                    {
                        return await pending;
                    }

                    (bool completed, TResult result) = await UniTask.WhenAny(pending, _retryPolicy.Clock.Delay(_callTimeout, linked.Token));
                    if (completed)
                    {
                        return result;
                    }

                    // The call is abandoned; whatever it returns later is dropped by WhenAny
                    _logger.Error("[SaveSystem] ICloudSaveProvider." + operation + " did not complete within " + _callTimeout.TotalSeconds + " s; it was abandoned.");
                    return onTimeout(new CloudError(
                        CloudErrorKind.Transient, "ICloudSaveProvider." + operation + " timed out after " + _callTimeout.TotalSeconds + " s."));
                }
                finally
                {
                    // Cancels the timeout delay, and the abandoned call when the provider honours its token
                    CancelQuietly(linked, operation);
                }
            }
        }

        private void CancelQuietly(CancellationTokenSource cts, string operation)
        {
            try
            {
                cts.Cancel();
            }
            catch (Exception exception)
            {
                _logger.Error("[SaveSystem] Cancelling the " + operation + " timeout threw.", exception);
            }
        }

        private async UniTask<IReadOnlyList<CloudReadResult>> InvokeReadAsync(List<CloudReadRequest> batch, CancellationToken ct)
        {
            try
            {
                IReadOnlyList<CloudReadResult> response = await _provider.ReadAsync(batch.ToArray(), ct);
                if (response != null && response.Count == batch.Count)
                {
                    return response;
                }

                CloudError error = CountMismatchError("ReadAsync", response == null ? -1 : response.Count, batch.Count);
                return FailAll(batch, error, static (request, e) => CloudReadResult.Failed(request.Key, e));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                CloudError error = ProviderExceptionError("ReadAsync", exception);
                return FailAll(batch, error, static (request, e) => CloudReadResult.Failed(request.Key, e));
            }
        }

        private async UniTask<IReadOnlyList<CloudWriteResult>> InvokeWriteAsync(List<CloudWriteRequest> batch, CancellationToken ct)
        {
            try
            {
                IReadOnlyList<CloudWriteResult> response = await _provider.WriteAsync(batch.ToArray(), ct);
                if (response != null && response.Count == batch.Count)
                {
                    return response;
                }

                CloudError error = CountMismatchError("WriteAsync", response == null ? -1 : response.Count, batch.Count);
                return FailAll(batch, error, static (request, e) => CloudWriteResult.Failed(request.Key, e));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                CloudError error = ProviderExceptionError("WriteAsync", exception);
                return FailAll(batch, error, static (request, e) => CloudWriteResult.Failed(request.Key, e));
            }
        }

        private async UniTask<CloudDeleteResult> InvokeDeleteAsync(string key, string expectedVersion, CancellationToken ct)
        {
            try
            {
                CloudDeleteResult result = await _provider.DeleteAsync(key, expectedVersion, ct);
                if (result != null)
                {
                    return result;
                }

                _logger.Error("[SaveSystem] ICloudSaveProvider.DeleteAsync returned null for '" + key + "'.");
                return CloudDeleteResult.Failed(key, NullResultError());
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                return CloudDeleteResult.Failed(key, ProviderExceptionError("DeleteAsync", exception));
            }
        }

        private CloudError CountMismatchError(string call, int returned, int expected)
        {
            string message = "ICloudSaveProvider." + call + " returned " + (returned < 0 ? "null" : returned.ToString()) +
                             " results for " + expected + " requests.";
            _logger.Error("[SaveSystem] " + message);
            return new CloudError(CloudErrorKind.Permanent, message);
        }

        // Contract says providers never throw; a stray OperationCanceledException is treated as a timeout
        private CloudError ProviderExceptionError(string call, Exception exception)
        {
            if (exception is OperationCanceledException)
            {
                return new CloudError(CloudErrorKind.Transient, "ICloudSaveProvider." + call + " was cancelled internally.", null, exception);
            }

            _logger.Error("[SaveSystem] ICloudSaveProvider." + call + " threw; providers must return classified errors.", exception);
            return new CloudError(CloudErrorKind.Permanent, "ICloudSaveProvider." + call + " threw: " + exception.Message, null, exception);
        }

        private static CloudError NullResultError()
        {
            return new CloudError(CloudErrorKind.Permanent, "The provider returned a null result.");
        }

        private static TResult[] FailAll<TRequest, TResult>(List<TRequest> batch, CloudError error, Func<TRequest, CloudError, TResult> fail)
        {
            var results = new TResult[batch.Count];
            for (int i = 0; i < batch.Count; i++)
            {
                results[i] = fail(batch[i], error);
            }

            return results;
        }

        private static void ValidateRequests<TRequest>(IReadOnlyList<TRequest> requests)
            where TRequest : class
        {
            if (requests == null)
            {
                throw new ArgumentNullException(nameof(requests));
            }

            for (int i = 0; i < requests.Count; i++)
            {
                if (requests[i] == null)
                {
                    throw new ArgumentException("Requests must not contain null entries.", nameof(requests));
                }
            }
        }

        private void LogRetry(string operation, int count, int retryNumber, TimeSpan delay)
        {
            if (_logger.IsVerboseEnabled)
            {
                _logger.Verbose("[SaveSystem] Cloud " + operation + " retry " + retryNumber + "/" + _retryPolicy.RetryCount + " for " + count +
                                " key(s) in " + delay.TotalMilliseconds + " ms.");
            }
        }

        private static TimeSpan Max(TimeSpan a, TimeSpan b)
        {
            return a >= b ? a : b;
        }

        private static void Swap(ref List<int> a, ref List<int> b)
        {
            List<int> temp = a;
            a = b;
            b = temp;
        }
    }
}
