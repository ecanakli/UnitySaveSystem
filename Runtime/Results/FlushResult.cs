using System.Collections.Generic;

namespace Ecanakli.SaveSystem
{
    /// <summary>Strict result of FlushAsync.</summary>
    public sealed class FlushResult
    {
        internal FlushResult(SaveStatus status, SaveError error, LocalFlushResult local, IEnumerable<CloudFlushSlotResult> cloud)
        {
            Status = status;
            Error = error;
            Local = local ?? LocalFlushResult.Complete;
            Cloud = ResultLists.Copy(cloud);
            IsComplete = ComputeIsComplete();
        }

        public SaveStatus Status { get; }

        /// <summary>Null unless the operation itself failed.</summary>
        public SaveError Error { get; }

        public LocalFlushResult Local { get; }

        /// <summary>One entry per Profile CloudSync slot.</summary>
        public IReadOnlyList<CloudFlushSlotResult> Cloud { get; }

        /// <summary>Local complete and every cloud entry Uploaded or AlreadyInSync.</summary>
        public bool IsComplete { get; }

        private bool ComputeIsComplete()
        {
            if (Status != SaveStatus.Success || !Local.IsComplete)
            {
                return false;
            }

            for (int i = 0; i < Cloud.Count; i++)
            {
                if (!Cloud[i].IsSynced)
                {
                    return false;
                }
            }

            return true;
        }

        public override string ToString()
        {
            return "Flush " + Status + " complete=" + IsComplete + " local=" + Local + " cloud=" + Cloud.Count;
        }
    }
}
