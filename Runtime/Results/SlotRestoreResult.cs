namespace Ecanakli.SaveSystem
{
    /// <summary>Per-slot outcome in a RestoreReport.</summary>
    public enum SlotRestoreOutcome
    {
        Restored = 0,
        UpToDate = 1,
        LocalKept = 2,
        Merged = 3,
        NoCloudData = 4,
        SkippedToProtectLocalData = 5,
        SkippedSchemaTooNew = 6,
        Failed = 7,
        LoadedLocal = 8,
        RepairingCorruptCloud = 9,
        CorruptCloudIgnored = 10,
    }

    /// <summary>Failure detail for a Failed slot outcome.</summary>
    public enum SlotRestoreFailure
    {
        None = 0,
        ReadFailed = 1,
        AccountChanged = 2,
        LocalIoError = 3,
        DeletePending = 4,
        ApplyFailed = 5,
        LocalNormalizeFailed = 6,
        LocalSchemaTooNew = 7,
    }

    /// <summary>Restore or activation result for one slot.</summary>
    public sealed class SlotRestoreResult
    {
        internal SlotRestoreResult(
            string slotKey,
            SlotRestoreOutcome outcome,
            SlotRestoreFailure failure = SlotRestoreFailure.None,
            bool localPersistFailed = false,
            CloudError cloudError = null)
        {
            SlotKey = slotKey;
            Outcome = outcome;
            Failure = failure;
            LocalPersistFailed = localPersistFailed;
            CloudError = cloudError;
        }

        public string SlotKey { get; }

        public SlotRestoreOutcome Outcome { get; }

        public SlotRestoreFailure Failure { get; }

        /// <summary>Applied in memory but the disk write failed; the scheduler retries it.</summary>
        public bool LocalPersistFailed { get; }

        /// <summary>Provider error behind a ReadFailed or DeletePending failure.</summary>
        public CloudError CloudError { get; }

        /// <summary>False for Failed and SkippedSchemaTooNew.</summary>
        public bool IsSuccess => Outcome != SlotRestoreOutcome.Failed && Outcome != SlotRestoreOutcome.SkippedSchemaTooNew;

        public override string ToString()
        {
            string text = SlotKey + ": " + Outcome;
            if (Failure != SlotRestoreFailure.None)
            {
                text += "(" + Failure + ")";
            }

            return LocalPersistFailed ? text + " [local persist failed]" : text;
        }
    }
}
