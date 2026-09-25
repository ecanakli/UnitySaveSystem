using System;
using System.Collections.Generic;
using Ecanakli.SaveSystem;

namespace Ecanakli.SaveSystem.UnityCloudSave
{
    /// <summary>Capability values and per-key access-class overrides for UnityCloudSaveProvider.</summary>
    public sealed class UnityCloudSaveOptions
    {
        // Unity.Services.CloudSave.Internal.Models.Item docs: "Any JSON serializable structure with a maximum
        // size of 5 MB." Uses the smaller decimal-MB reading as a conservative client-side precheck.
        public const int DocumentedMaxValueBytes = 5_000_000;

        // Not documented publicly; PlayerDataService.SaveWithErrorHandlingAsync (source-verified) auto-chunks
        // at 20 items per HTTP call, so 20 is used as the write batch ceiling.
        public const int DocumentedMaxKeysPerWrite = 20;

        // No discoverable per-call key-count limit for LoadAsync; mirrors the write ceiling until a live check.
        public const int DefaultMaxKeysPerRead = 20;

        private readonly HashSet<string> _publicKeys;

        public UnityCloudSaveOptions(
            int maxValueBytes = DocumentedMaxValueBytes,
            int maxKeysPerWrite = DocumentedMaxKeysPerWrite,
            int maxKeysPerRead = DefaultMaxKeysPerRead,
            long maxTotalBytes = 0,
            bool preservesValueText = false,
            IEnumerable<string> publicKeys = null)
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
                throw new ArgumentOutOfRangeException(nameof(maxTotalBytes), "Must be zero (unknown) or positive.");
            }

            MaxValueBytes = maxValueBytes;
            MaxKeysPerWrite = maxKeysPerWrite;
            MaxKeysPerRead = maxKeysPerRead;
            MaxTotalBytes = maxTotalBytes;
            PreservesValueText = preservesValueText;
            _publicKeys = publicKeys == null ? null : new HashSet<string>(publicKeys, StringComparer.Ordinal);
        }

        public int MaxValueBytes { get; }

        public int MaxKeysPerWrite { get; }

        public int MaxKeysPerRead { get; }

        /// <summary>0 means unknown/unlimited; no per-account byte quota is documented in the installed package.</summary>
        public long MaxTotalBytes { get; }

        /// <summary>Keep false until a live-service round trip proves stored values come back byte-identical.</summary>
        public bool PreservesValueText { get; }

        /// <summary>UGS Cloud Save write locks always support conditional writes.</summary>
        public bool SupportsConditionalWrite => true;

        /// <summary>True when this key should use AccessClass.Public instead of the CloudAccess-derived default.</summary>
        public bool IsPublicKey(string key)
        {
            return key != null && _publicKeys != null && _publicKeys.Contains(key);
        }

        public CloudCapabilities ToCapabilities()
        {
            return new CloudCapabilities(MaxValueBytes, MaxKeysPerWrite, MaxKeysPerRead, MaxTotalBytes, SupportsConditionalWrite, PreservesValueText);
        }
    }
}
