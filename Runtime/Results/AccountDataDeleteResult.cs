using System.Collections.Generic;

namespace Ecanakli.SaveSystem
{
    /// <summary>Cloud side of a delete.</summary>
    public enum CloudDeleteStatus
    {
        /// <summary>No cloud delete was part of the request.</summary>
        NotRequested = 0,
        Deleted = 1,
        AlreadyAbsent = 2,

        /// <summary>Failed; the tombstone is kept for a later retry.</summary>
        Failed = 3,
    }

    /// <summary>Cloud delete result for one slot key.</summary>
    public sealed class CloudDeleteSlotResult
    {
        internal CloudDeleteSlotResult(string slotKey, CloudDeleteStatus status, CloudError error = null)
        {
            SlotKey = slotKey;
            Status = status;
            Error = error;
        }

        public string SlotKey { get; }

        public CloudDeleteStatus Status { get; }

        public CloudError Error { get; }

        public override string ToString()
        {
            return SlotKey + ": " + Status;
        }
    }

    /// <summary>Result of DeleteAccountDataAsync.</summary>
    public sealed class AccountDataDeleteResult
    {
        internal AccountDataDeleteResult(SaveStatus status, SaveError error, string accountId, IEnumerable<CloudDeleteSlotResult> cloud, bool localDeleted)
        {
            Status = status;
            Error = error;
            AccountId = accountId;
            Cloud = ResultLists.Copy(cloud);
            LocalDeleted = localDeleted;
            IsComplete = ComputeIsComplete();
        }

        public SaveStatus Status { get; }

        /// <summary>ProfileActive, NotSignedIn, AccountMismatch, IoError, ...; null on success.</summary>
        public SaveError Error { get; }

        public string AccountId { get; }

        public IReadOnlyList<CloudDeleteSlotResult> Cloud { get; }

        public bool LocalDeleted { get; }

        /// <summary>Every cloud entry Deleted or AlreadyAbsent and local data deleted.</summary>
        public bool IsComplete { get; }

        private bool ComputeIsComplete()
        {
            if (Status != SaveStatus.Success || !LocalDeleted)
            {
                return false;
            }

            for (int i = 0; i < Cloud.Count; i++)
            {
                CloudDeleteStatus status = Cloud[i].Status;
                if (status != CloudDeleteStatus.Deleted && status != CloudDeleteStatus.AlreadyAbsent)
                {
                    return false;
                }
            }

            return true;
        }

        public override string ToString()
        {
            return "AccountDataDelete " + Status + " complete=" + IsComplete + " localDeleted=" + LocalDeleted;
        }
    }
}
