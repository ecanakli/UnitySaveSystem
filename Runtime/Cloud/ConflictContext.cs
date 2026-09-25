using System;

namespace Ecanakli.SaveSystem
{
    /// <summary>Input to SaveSlot.ResolveConflict. Treat Local and Cloud as read-only.</summary>
    public readonly struct ConflictContext<TData> where TData : class
    {
        private readonly ConflictMetadata _metadata;

        internal ConflictContext(string slotKey, TData local, TData cloud, bool localIsEmpty, bool cloudIsEmpty, ConflictMetadata metadata)
        {
            SlotKey = slotKey;
            Local = local;
            Cloud = cloud;
            LocalIsEmpty = localIsEmpty;
            CloudIsEmpty = cloudIsEmpty;
            _metadata = metadata;
        }

        public string SlotKey { get; }

        /// <summary>Normalized local data.</summary>
        public TData Local { get; }

        /// <summary>Normalized cloud data.</summary>
        public TData Cloud { get; }

        public bool LocalIsEmpty { get; }

        public bool CloudIsEmpty { get; }

        public long LocalRevision => _metadata.LocalRevision;

        public long CloudRevision => _metadata.CloudRevision;

        /// <summary>Diagnostics only; clocks drift.</summary>
        public DateTime? CloudSavedAtUtc => _metadata.CloudSavedAtUtc;

        /// <summary>Diagnostics only; device backups clone ids.</summary>
        public string CloudDeviceId => _metadata.CloudDeviceId;

        /// <summary>False when this device never synced the slot (for example a guest claim).</summary>
        public bool HasSyncedBefore => _metadata.HasSyncedBefore;
    }

    // Untyped conflict metadata passed from the core into the typed slot
    internal readonly struct ConflictMetadata
    {
        public ConflictMetadata(long localRevision, long cloudRevision, DateTime? cloudSavedAtUtc, string cloudDeviceId, bool hasSyncedBefore)
        {
            LocalRevision = localRevision;
            CloudRevision = cloudRevision;
            CloudSavedAtUtc = cloudSavedAtUtc;
            CloudDeviceId = cloudDeviceId;
            HasSyncedBefore = hasSyncedBefore;
        }

        public long LocalRevision { get; }

        public long CloudRevision { get; }

        public DateTime? CloudSavedAtUtc { get; }

        public string CloudDeviceId { get; }

        public bool HasSyncedBefore { get; }
    }
}
