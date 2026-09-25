namespace Ecanakli.SaveSystem
{
    /// <summary>Per-slot cloud outcome of a flush.</summary>
    public enum CloudFlushStatus
    {
        Uploaded = 0,
        AlreadyInSync = 1,
        Skipped = 2,
        Failed = 3,
    }

    /// <summary>Reason attached to a Skipped or Failed cloud flush entry.</summary>
    public enum CloudFlushReason
    {
        None = 0,

        // Skipped
        ProfileNotCloudBacked = 10,
        NotSignedIn = 11,
        AccountMismatch = 12,
        NotReconciled = 13,
        SlotNotReady = 14,
        SchemaTooNew = 15,
        EmptyOverContent = 16,
        LocalWriteFailed = 17,
        SuspendedUntilReconcile = 18,

        // Failed
        CloudError = 30,
        Aborted = 31,
    }

    /// <summary>Cloud result for one Profile CloudSync slot.</summary>
    public sealed class CloudFlushSlotResult
    {
        internal CloudFlushSlotResult(string slotKey, CloudFlushStatus status, CloudFlushReason reason, CloudError error = null)
        {
            SlotKey = slotKey;
            Status = status;
            Reason = reason;
            Error = error;
        }

        public string SlotKey { get; }

        public CloudFlushStatus Status { get; }

        public CloudFlushReason Reason { get; }

        /// <summary>Set when Reason is CloudError.</summary>
        public CloudError Error { get; }

        /// <summary>Uploaded or AlreadyInSync.</summary>
        public bool IsSynced => Status == CloudFlushStatus.Uploaded || Status == CloudFlushStatus.AlreadyInSync;

        public override string ToString()
        {
            return SlotKey + ": " + Status + (Reason == CloudFlushReason.None ? string.Empty : "(" + Reason + ")");
        }
    }
}
