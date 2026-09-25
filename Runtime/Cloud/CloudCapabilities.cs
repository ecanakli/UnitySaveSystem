using System;

namespace Ecanakli.SaveSystem
{
    /// <summary>Limits and features declared by a cloud provider.</summary>
    public sealed class CloudCapabilities
    {
        public CloudCapabilities(
            int maxValueBytes,
            int maxKeysPerWrite,
            int maxKeysPerRead,
            long maxTotalBytes,
            bool supportsConditionalWrite,
            bool preservesValueText = false)
        {
            if (maxValueBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxValueBytes), "Must be positive.");
            }

            if (maxKeysPerWrite <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxKeysPerWrite), "Must be positive.");
            }

            if (maxKeysPerRead <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxKeysPerRead), "Must be positive.");
            }

            if (maxTotalBytes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxTotalBytes), "Must be zero (unlimited) or positive.");
            }

            MaxValueBytes = maxValueBytes;
            MaxKeysPerWrite = maxKeysPerWrite;
            MaxKeysPerRead = maxKeysPerRead;
            MaxTotalBytes = maxTotalBytes;
            SupportsConditionalWrite = supportsConditionalWrite;
            PreservesValueText = preservesValueText;
        }

        public int MaxValueBytes { get; }

        public int MaxKeysPerWrite { get; }

        public int MaxKeysPerRead { get; }

        /// <summary>Total storage quota in bytes; 0 means unknown or unlimited.</summary>
        public long MaxTotalBytes { get; }

        /// <summary>Writes honor CloudWriteRequest.ExpectedVersion.</summary>
        public bool SupportsConditionalWrite { get; }

        /// <summary>Stored values come back byte-identical; enables cloud checksum verification.</summary>
        public bool PreservesValueText { get; }

        public bool SupportsBatchWrite => MaxKeysPerWrite > 1;
    }
}
