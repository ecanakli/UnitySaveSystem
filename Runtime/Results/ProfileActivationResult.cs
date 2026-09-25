using System.Collections.Generic;

namespace Ecanakli.SaveSystem
{
    /// <summary>Result of profile activation, including the initial activation at InitializeAsync.</summary>
    public sealed class ProfileActivationResult
    {
        internal ProfileActivationResult(
            SaveStatus status,
            SaveError error,
            ProfileId profile,
            ProfileId? previous,
            bool alreadyActive,
            bool claimedGuestData,
            IEnumerable<SlotLoadIssue> issues,
            IEnumerable<ListenerFailure> deactivationFailures,
            bool deactivationTimedOut)
        {
            Status = status;
            Error = error;
            Profile = profile;
            Previous = previous;
            AlreadyActive = alreadyActive;
            ClaimedGuestData = claimedGuestData;
            Issues = ResultLists.Copy(issues);
            DeactivationFailures = ResultLists.Copy(deactivationFailures);
            DeactivationTimedOut = deactivationTimedOut;
        }

        public SaveStatus Status { get; }

        public SaveError Error { get; }

        public ProfileId Profile { get; }

        /// <summary>Null for the initial activation at InitializeAsync.</summary>
        public ProfileId? Previous { get; }

        /// <summary>The requested profile was already active; nothing was switched or dispatched.</summary>
        public bool AlreadyActive { get; }

        /// <summary>The guest directory was moved into this account.</summary>
        public bool ClaimedGuestData { get; }

        public IReadOnlyList<SlotLoadIssue> Issues { get; }

        public IReadOnlyList<ListenerFailure> DeactivationFailures { get; }

        public bool DeactivationTimedOut { get; }

        public bool IsSuccess => Status == SaveStatus.Success;

        public override string ToString()
        {
            return "Activation " + Status + " profile=" + Profile + " previous=" + (Previous.HasValue ? Previous.Value.ToString() : "none")
                   + " alreadyActive=" + AlreadyActive + " claimed=" + ClaimedGuestData + " issues=" + Issues.Count;
        }
    }
}
