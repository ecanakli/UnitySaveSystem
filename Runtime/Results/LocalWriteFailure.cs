namespace Ecanakli.SaveSystem
{
    /// <summary>Classified cause of a local write failure.</summary>
    public enum LocalWriteErrorKind
    {
        IoError = 0,
        AccessDenied = 1,
        DiskFull = 2,
    }

    /// <summary>One failing local write target.</summary>
    public sealed class LocalWriteFailure
    {
        internal LocalWriteFailure(ProfileId? profile, string slotKey, LocalWriteErrorKind kind, int consecutiveFailures, string message)
        {
            Profile = profile;
            SlotKey = slotKey;
            Kind = kind;
            ConsecutiveFailures = consecutiveFailures;
            Message = message;
        }

        /// <summary>Owning profile; null for device-level files.</summary>
        public ProfileId? Profile { get; }

        /// <summary>Null when the failing file is profile.json or device.json.</summary>
        public string SlotKey { get; }

        public LocalWriteErrorKind Kind { get; }

        public int ConsecutiveFailures { get; }

        public string Message { get; }

        public override string ToString()
        {
            return (SlotKey ?? "<state file>") + " " + Kind + " x" + ConsecutiveFailures + ": " + Message;
        }
    }
}
