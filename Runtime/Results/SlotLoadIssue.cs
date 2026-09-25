using System;

namespace Ecanakli.SaveSystem
{
    /// <summary>Kind of problem found while loading a slot or its sync state.</summary>
    public enum SlotLoadIssueKind
    {
        /// <summary>Primary was corrupt; a valid .tmp was promoted.</summary>
        CorruptRecoveredFromTmp = 0,

        /// <summary>Primary was corrupt; .bak was loaded and rewritten as primary.</summary>
        CorruptRecoveredFromBackup = 1,

        /// <summary>No valid copy; defaults loaded and the slot needs cloud recovery.</summary>
        CorruptReset = 2,

        /// <summary>Read failed; slot is Failed and nothing was written.</summary>
        IoError = 3,

        /// <summary>UpgradePayload, ToObject or Normalize threw; slot is Failed and the file is untouched.</summary>
        NormalizeFailed = 4,

        /// <summary>Payload format or schema is newer than this build; file untouched.</summary>
        SchemaTooNew = 5,

        /// <summary>A cloud value was corrupt; its raw bytes were backed up locally.</summary>
        CloudPayloadCorrupt = 6,

        /// <summary>A NormalizeFailed local copy was quarantined and replaced by cloud data.</summary>
        LocalNormalizeFailedReplacedByCloud = 7,

        /// <summary>profile.json was corrupt or unreadable; safe sync-state fallbacks apply.</summary>
        SyncStateCorrupt = 8,
    }

    /// <summary>Why a payload was considered corrupt.</summary>
    public enum CorruptionCause
    {
        None = 0,
        ParseFailed = 1,
        ChecksumMismatch = 2,
        MaterializeFailed = 3,
    }

    /// <summary>Load problem reported through SlotLoadIssueDetected.</summary>
    public sealed class SlotLoadIssue
    {
        internal SlotLoadIssue(
            ProfileId? profile,
            string slotKey,
            SlotLoadIssueKind kind,
            CorruptionCause cause = CorruptionCause.None,
            string backupFileName = null,
            string message = null,
            Exception exception = null)
        {
            Profile = profile;
            SlotKey = slotKey;
            Kind = kind;
            Cause = cause;
            BackupFileName = backupFileName;
            Message = message;
            Exception = exception;
        }

        /// <summary>Owning profile; null for Device-scope slots.</summary>
        public ProfileId? Profile { get; }

        /// <summary>Null for SyncStateCorrupt.</summary>
        public string SlotKey { get; }

        public SlotLoadIssueKind Kind { get; }

        public CorruptionCause Cause { get; }

        /// <summary>Quarantine or cloud-corrupt file name, when one was written.</summary>
        public string BackupFileName { get; }

        public string Message { get; }

        public Exception Exception { get; }

        public override string ToString()
        {
            string text = (SlotKey ?? "<sync state>") + " " + Kind;
            if (Cause != CorruptionCause.None)
            {
                text += "(" + Cause + ")";
            }

            if (BackupFileName != null)
            {
                text += " backup=" + BackupFileName;
            }

            return Message == null ? text : text + ": " + Message;
        }
    }
}
