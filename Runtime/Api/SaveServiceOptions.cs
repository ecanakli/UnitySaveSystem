using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Ecanakli.SaveSystem
{
    /// <summary>Configuration for SaveService. Set before constructing the service.</summary>
    public sealed class SaveServiceOptions
    {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
        private const bool DevelopmentChecksDefault = true;
#else
        private const bool DevelopmentChecksDefault = false;
#endif

        private static readonly TimeSpan[] DefaultLocalWriteBackoff =
        {
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60),
        };

        private static readonly TimeSpan[] DefaultUploadRetryBackoff =
        {
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5),
        };

        private string _rootDirectory;

        /// <summary>Save root; defaults to {persistentDataPath}/saves (resolved on first read, main thread).</summary>
        public string RootDirectory
        {
            get => _rootDirectory ?? SaveFolders.DefaultRootDirectory;
            set => _rootDirectory = value;
        }

        /// <summary>Coalescing delay before a dirty slot is written locally.</summary>
        public TimeSpan LocalWriteDelay { get; set; } = TimeSpan.FromMilliseconds(500);

        /// <summary>Quiet time after the last mutation before uploading.</summary>
        public TimeSpan CloudDebounce { get; set; } = TimeSpan.FromSeconds(2);

        /// <summary>Upper bound between the first dirty mark and the upload.</summary>
        public TimeSpan CloudMaxWait { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>Retries per provider call for Transient and RateLimited errors.</summary>
        public int CloudRetryCount { get; set; } = 3;

        /// <summary>First retry delay; doubles per attempt unless RetryAfter is given.</summary>
        public TimeSpan CloudRetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(500);

        /// <summary>Cap for a single retry delay.</summary>
        public TimeSpan CloudRetryMaxDelay { get; set; } = TimeSpan.FromSeconds(8);

        /// <summary>
        /// Budget for one ICloudSaveProvider call. A call that neither returns nor honours its token is abandoned and
        /// reported as a transient cloud error, so retries and rescheduling apply and the operation gate is released.
        /// Zero disables the timeout; the provider then decides how long an operation may run.
        /// </summary>
        public TimeSpan CloudCallTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>Reschedule delays after retries are exhausted; the last value repeats. Every value must be greater than zero.</summary>
        public IReadOnlyList<TimeSpan> UploadRetryBackoff { get; set; } = DefaultUploadRetryBackoff;

        /// <summary>Extra converters applied on every serialization path. TypeNameHandling stays None.</summary>
        public IReadOnlyList<JsonConverter> JsonConverters { get; set; } = Array.Empty<JsonConverter>();

        /// <summary>Encode and file IO on the thread pool. Tests set false.</summary>
        public bool OffloadIo { get; set; } = true;

        public ISaveLogger Logger { get; set; } = new UnityDebugSaveLogger();

        public ISaveClock Clock { get; set; } = UnitySaveClock.Instance;

        /// <summary>Optional device id source; null uses a GUID persisted in device.json. Diagnostics only.</summary>
        public Func<string> DeviceIdProvider { get; set; }

        /// <summary>Shared budget for all deactivation listeners of one switch.</summary>
        public TimeSpan DeactivationTimeout { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>Local write failure and refusal backoff; the last value repeats. Every value must be greater than zero.</summary>
        public IReadOnlyList<TimeSpan> LocalWriteBackoff { get; set; } = DefaultLocalWriteBackoff;

        /// <summary>Warn once per unloaded period when a slot is read or mutated before it is loaded.</summary>
        public bool WarnOnAccessBeforeReady { get; set; } = DevelopmentChecksDefault;

        /// <summary>Detect changes made outside Mutate on flush. Development aid; costs a snapshot per clean slot.</summary>
        public bool DetectMutationsOutsideMutate { get; set; } = DevelopmentChecksDefault;

        // Throws on invalid configuration; called at composition time
        internal void Validate()
        {
            if (string.IsNullOrEmpty(RootDirectory))
            {
                throw new ArgumentException("RootDirectory must not be null or empty.");
            }

            RequireNonNegative(LocalWriteDelay, nameof(LocalWriteDelay));
            RequireNonNegative(CloudDebounce, nameof(CloudDebounce));
            RequireNonNegative(CloudRetryBaseDelay, nameof(CloudRetryBaseDelay));
            RequireNonNegative(CloudRetryMaxDelay, nameof(CloudRetryMaxDelay));
            RequireNonNegative(CloudCallTimeout, nameof(CloudCallTimeout));
            RequireNonNegative(DeactivationTimeout, nameof(DeactivationTimeout));

            if (CloudMaxWait < CloudDebounce)
            {
                throw new ArgumentException("CloudMaxWait must be greater than or equal to CloudDebounce.");
            }

            if (CloudRetryCount < 0)
            {
                throw new ArgumentException("CloudRetryCount must not be negative.");
            }

            RequireBackoff(UploadRetryBackoff, nameof(UploadRetryBackoff));
            RequireBackoff(LocalWriteBackoff, nameof(LocalWriteBackoff));

            if (JsonConverters == null)
            {
                throw new ArgumentException("JsonConverters must not be null.");
            }

            for (int i = 0; i < JsonConverters.Count; i++)
            {
                if (JsonConverters[i] == null)
                {
                    throw new ArgumentException("JsonConverters must not contain null entries.");
                }
            }

            if (Logger == null)
            {
                throw new ArgumentException("Logger must not be null.");
            }

            if (Clock == null)
            {
                throw new ArgumentException("Clock must not be null.");
            }
        }

        private static void RequireNonNegative(TimeSpan value, string name)
        {
            if (value < TimeSpan.Zero)
            {
                throw new ArgumentException(name + " must not be negative.");
            }
        }

        private static void RequireBackoff(IReadOnlyList<TimeSpan> values, string name)
        {
            if (values == null || values.Count == 0)
            {
                throw new ArgumentException(name + " must contain at least one value.");
            }

            // A zero step would make a refused retry due again in the same frame, once per frame
            for (int i = 0; i < values.Count; i++)
            {
                if (values[i] <= TimeSpan.Zero)
                {
                    throw new ArgumentException(name + " values must be greater than zero.");
                }
            }
        }
    }
}
