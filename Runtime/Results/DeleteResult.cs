using System.Collections.Generic;

namespace Ecanakli.SaveSystem
{
    /// <summary>What DeleteSlotAsync removes.</summary>
    public enum DeleteTarget
    {
        LocalOnly = 0,
        LocalAndCloud = 1,
    }

    /// <summary>Result of DeleteSlotAsync and DeleteProfileAsync.</summary>
    public sealed class DeleteResult
    {
        internal DeleteResult(
            SaveStatus status,
            SaveError error,
            bool localDeleted,
            CloudDeleteStatus cloud,
            CloudError cloudError = null,
            IEnumerable<string> unsyncedSlotKeys = null)
        {
            Status = status;
            Error = error;
            LocalDeleted = localDeleted;
            Cloud = cloud;
            CloudError = cloudError;
            UnsyncedSlotKeys = ResultLists.Copy(unsyncedSlotKeys);
        }

        public SaveStatus Status { get; }

        public SaveError Error { get; }

        /// <summary>True when the local files are gone (including nothing to delete).</summary>
        public bool LocalDeleted { get; }

        public CloudDeleteStatus Cloud { get; }

        public CloudError CloudError { get; }

        /// <summary>Filled when the error code is UnsyncedChanges.</summary>
        public IReadOnlyList<string> UnsyncedSlotKeys { get; }

        public bool IsSuccess => Status == SaveStatus.Success;

        public override string ToString()
        {
            return "Delete " + Status + " local=" + LocalDeleted + " cloud=" + Cloud + (Error == null ? string.Empty : " " + Error);
        }
    }
}
