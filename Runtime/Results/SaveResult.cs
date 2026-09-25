namespace Ecanakli.SaveSystem
{
    /// <summary>Result of SaveNowAsync.</summary>
    public readonly struct SaveResult
    {
        internal SaveResult(SaveStatus status, SaveError error, long durableRevision)
        {
            Status = status;
            Error = error;
            DurableRevision = durableRevision;
        }

        public SaveStatus Status { get; }

        /// <summary>Null on success.</summary>
        public SaveError Error { get; }

        /// <summary>Revision known to be fsynced and promoted on disk; meaningful on success.</summary>
        public long DurableRevision { get; }

        public bool IsSuccess => Status == SaveStatus.Success;

        internal static SaveResult Success(long durableRevision)
        {
            return new SaveResult(SaveStatus.Success, null, durableRevision);
        }

        internal static SaveResult Failure(SaveErrorCode code, string message = null)
        {
            return new SaveResult(SaveStatus.Failed, new SaveError(code, message), 0);
        }

        internal static SaveResult Failure(SaveError error)
        {
            return new SaveResult(SaveStatus.Failed, error, 0);
        }

        public override string ToString()
        {
            return IsSuccess ? "Success(rev " + DurableRevision + ")" : Status + " " + Error;
        }
    }
}
