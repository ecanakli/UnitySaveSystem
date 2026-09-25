using System.Collections.Generic;

namespace Ecanakli.SaveSystem
{
    /// <summary>Result of InitializeAsync.</summary>
    public sealed class InitializeResult
    {
        internal InitializeResult(SaveStatus status, SaveError error, ProfileId profile, IEnumerable<SlotLoadIssue> issues)
        {
            Status = status;
            Error = error;
            Profile = profile;
            Issues = ResultLists.Copy(issues);
        }

        public SaveStatus Status { get; }

        public SaveError Error { get; }

        /// <summary>Profile active after initialization.</summary>
        public ProfileId Profile { get; }

        /// <summary>Load issues raised for Device and initial Profile slots.</summary>
        public IReadOnlyList<SlotLoadIssue> Issues { get; }

        public bool IsSuccess => Status == SaveStatus.Success;

        public override string ToString()
        {
            return "Initialize " + Status + " profile=" + Profile + " issues=" + Issues.Count;
        }
    }
}
