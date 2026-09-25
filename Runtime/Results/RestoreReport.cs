using System;
using System.Collections.Generic;
using System.Text;

namespace Ecanakli.SaveSystem
{
    /// <summary>Overall completeness of a restore or activation.</summary>
    public enum RestoreCompleteness
    {
        Full = 0,
        Partial = 1,
        NothingToRestore = 2,
        Failed = 3,
    }

    /// <summary>Report passed to restore listeners and raised with RestoreCompleted.</summary>
    public sealed class RestoreReport
    {
        internal RestoreReport(
            RestoreTrigger trigger,
            SaveStatus status,
            SaveError error,
            ProfileId profile,
            RestoreCompleteness completeness,
            IEnumerable<SlotRestoreResult> slots,
            IEnumerable<ListenerFailure> listenerFailures,
            bool requiresAppUpdate)
        {
            Trigger = trigger;
            Status = status;
            Error = error;
            Profile = profile;
            Completeness = completeness;
            Slots = ResultLists.Copy(slots);
            ListenerFailures = ResultLists.Copy(listenerFailures);
            RequiresAppUpdate = requiresAppUpdate;
        }

        public RestoreTrigger Trigger { get; }

        /// <summary>Canceled when listener dispatch was cancelled.</summary>
        public SaveStatus Status { get; }

        public SaveError Error { get; }

        public ProfileId Profile { get; }

        public RestoreCompleteness Completeness { get; }

        public IReadOnlyList<SlotRestoreResult> Slots { get; }

        public IReadOnlyList<ListenerFailure> ListenerFailures { get; }

        /// <summary>A slot payload used a newer schema or format than this build supports.</summary>
        public bool RequiresAppUpdate { get; }

        public bool IsCanceled => Status == SaveStatus.Canceled;

        /// <summary>Returns the slot's result, or null when the slot is not part of this report.</summary>
        public SlotRestoreResult GetResult(SaveSlot slot)
        {
            if (slot == null)
            {
                throw new ArgumentNullException(nameof(slot));
            }

            string key = slot.Key;
            for (int i = 0; i < Slots.Count; i++)
            {
                if (string.Equals(Slots[i].SlotKey, key, StringComparison.OrdinalIgnoreCase))
                {
                    return Slots[i];
                }
            }

            return null;
        }

        // Copy with listener outcome; the report itself stays immutable
        internal RestoreReport WithListenerOutcome(IEnumerable<ListenerFailure> listenerFailures, bool canceled)
        {
            return new RestoreReport(
                Trigger,
                canceled ? SaveStatus.Canceled : Status,
                Error,
                Profile,
                Completeness,
                Slots,
                listenerFailures,
                RequiresAppUpdate);
        }

        // Activation rule: Full when nothing failed, Partial when some failed, Failed when all did
        internal static RestoreCompleteness ComputeActivationCompleteness(IReadOnlyList<SlotRestoreResult> slots)
        {
            int failed = 0;
            for (int i = 0; i < slots.Count; i++)
            {
                if (!slots[i].IsSuccess)
                {
                    failed++;
                }
            }

            if (failed == 0)
            {
                return RestoreCompleteness.Full;
            }

            return failed == slots.Count ? RestoreCompleteness.Failed : RestoreCompleteness.Partial;
        }

        // Cloud restore rule from the design
        internal static RestoreCompleteness ComputeRestoreCompleteness(IReadOnlyList<SlotRestoreResult> slots)
        {
            int failed = 0;
            int succeeded = 0;
            int noCloudData = 0;
            for (int i = 0; i < slots.Count; i++)
            {
                switch (slots[i].Outcome)
                {
                    case SlotRestoreOutcome.Failed:
                    case SlotRestoreOutcome.SkippedSchemaTooNew:
                        failed++;
                        break;
                    case SlotRestoreOutcome.NoCloudData:
                        noCloudData++;
                        break;
                    default:
                        succeeded++;
                        break;
                }
            }

            if (failed == 0 && succeeded > 0)
            {
                return RestoreCompleteness.Full;
            }

            if (failed > 0 && succeeded > 0)
            {
                return RestoreCompleteness.Partial;
            }

            if (failed == 0 && noCloudData > 0)
            {
                return RestoreCompleteness.NothingToRestore;
            }

            return RestoreCompleteness.Failed;
        }

        public override string ToString()
        {
            var builder = new StringBuilder();
            builder.Append("RestoreReport trigger=").Append(Trigger)
                .Append(" status=").Append(Status)
                .Append(" profile=").Append(Profile)
                .Append(" completeness=").Append(Completeness);
            if (RequiresAppUpdate)
            {
                builder.Append(" requiresAppUpdate");
            }

            if (Error != null)
            {
                builder.Append(" error=").Append(Error);
            }

            for (int i = 0; i < Slots.Count; i++)
            {
                builder.Append('\n').Append("  ").Append(Slots[i]);
            }

            for (int i = 0; i < ListenerFailures.Count; i++)
            {
                builder.Append('\n').Append("  listener ").Append(ListenerFailures[i]);
            }

            return builder.ToString();
        }
    }
}
