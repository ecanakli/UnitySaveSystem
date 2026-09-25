namespace Ecanakli.SaveSystem
{
    /// <summary>Permanent upload failure or refusal; never raised for transient retries.</summary>
    public sealed class UploadFailure
    {
        internal UploadFailure(ProfileId profile, string slotKey, CloudFlushReason reason, CloudError error = null, string message = null)
        {
            Profile = profile;
            SlotKey = slotKey;
            Reason = reason;
            Error = error;
            Message = message;
        }

        public ProfileId Profile { get; }

        public string SlotKey { get; }

        public CloudFlushReason Reason { get; }

        /// <summary>Provider error when Reason is CloudError.</summary>
        public CloudError Error { get; }

        public string Message { get; }

        public override string ToString()
        {
            return SlotKey + " " + Reason + (Error == null ? string.Empty : " " + Error) + (Message == null ? string.Empty : ": " + Message);
        }
    }
}
