using System;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Ecanakli.SaveSystem
{
    /// <summary>Retries only Transient and RateLimited errors with capped exponential backoff; honours RetryAfter.</summary>
    internal sealed class RetryPolicy
    {
        public RetryPolicy(int retryCount, TimeSpan baseDelay, TimeSpan maxDelay, ISaveClock clock)
        {
            if (retryCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(retryCount), "Must not be negative.");
            }

            if (baseDelay < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(baseDelay), "Must not be negative.");
            }

            if (maxDelay < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(maxDelay), "Must not be negative.");
            }

            RetryCount = retryCount;
            BaseDelay = baseDelay;
            MaxDelay = maxDelay;
            Clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        /// <summary>Retries after the first attempt.</summary>
        public int RetryCount { get; }

        public TimeSpan BaseDelay { get; }

        /// <summary>Cap per delay; a RetryAfter above it is not waited for and ends the retries.</summary>
        public TimeSpan MaxDelay { get; }

        public ISaveClock Clock { get; }

        public static RetryPolicy FromOptions(SaveServiceOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            return new RetryPolicy(options.CloudRetryCount, options.CloudRetryBaseDelay, options.CloudRetryMaxDelay, options.Clock);
        }

        /// <summary>True when the error is retryable, retries remain and RetryAfter fits under MaxDelay.</summary>
        public bool ShouldRetry(CloudError error, int retriesDone)
        {
            if (error == null || !error.IsRetryable || retriesDone >= RetryCount)
            {
                return false;
            }

            return !HasUsableRetryAfter(error) || error.RetryAfter.GetValueOrDefault() <= MaxDelay;
        }

        /// <summary>BaseDelay doubled per retry (1-based) and capped at MaxDelay.</summary>
        public TimeSpan GetBackoffDelay(int retryNumber)
        {
            if (retryNumber < 1)
            {
                retryNumber = 1;
            }

            if (BaseDelay >= MaxDelay)
            {
                return MaxDelay;
            }

            double ticks = BaseDelay.Ticks * Math.Pow(2, retryNumber - 1);
            return ticks >= MaxDelay.Ticks ? MaxDelay : TimeSpan.FromTicks((long)ticks);
        }

        /// <summary>RetryAfter when the provider gave one, otherwise the backoff delay.</summary>
        public TimeSpan GetDelay(CloudError error, int retryNumber)
        {
            return error != null && HasUsableRetryAfter(error) ? error.RetryAfter.GetValueOrDefault() : GetBackoffDelay(retryNumber);
        }

        /// <summary>Waits through ISaveClock; throws OperationCanceledException on ct.</summary>
        public UniTask DelayAsync(TimeSpan delay, CancellationToken ct)
        {
            return Clock.Delay(delay < TimeSpan.Zero ? TimeSpan.Zero : delay, ct);
        }

        /// <summary>Runs a single-result call until success, a non-retryable error or retries run out.</summary>
        public async UniTask<TResult> ExecuteAsync<TState, TResult>(
            TState state,
            Func<TState, CancellationToken, UniTask<TResult>> operation,
            Func<TResult, CloudError> errorOf,
            CancellationToken ct)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (errorOf == null)
            {
                throw new ArgumentNullException(nameof(errorOf));
            }

            int retriesDone = 0;
            while (true)
            {
                TResult result = await operation(state, ct);
                CloudError error = errorOf(result);
                if (!ShouldRetry(error, retriesDone))
                {
                    return result;
                }

                retriesDone++;
                await DelayAsync(GetDelay(error, retriesDone), ct);
            }
        }

        private static bool HasUsableRetryAfter(CloudError error)
        {
            return error.RetryAfter.HasValue && error.RetryAfter.Value >= TimeSpan.Zero;
        }
    }
}
