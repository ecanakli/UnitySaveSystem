using System;
using Newtonsoft.Json.Linq;

namespace Ecanakli.SaveSystem
{
    /// <summary>
    /// Non-generic slot surface: public status plus internal members only the core calls.
    /// Derive from SaveSlot&lt;TData&gt;; Key, SyncMode and Scope must return constant values.
    /// </summary>
    public abstract class SaveSlot
    {
        private ISaveSlotHost _host;
        private bool _warnedWhileUnloaded;

        // Only SaveSlot<TData> derives directly
        private protected SaveSlot()
        {
        }

        /// <summary>Unique key matching ^[A-Za-z0-9_-]{1,64}$ (case-insensitive unique); also the file and cloud key.</summary>
        public abstract string Key { get; }

        /// <summary>Cloud relation; LocalOnly by default.</summary>
        public virtual SyncMode SyncMode => SyncMode.LocalOnly;

        /// <summary>Storage scope; Profile by default. Device requires LocalOnly.</summary>
        public virtual SlotScope Scope => SlotScope.Profile;

        public SlotState State { get; private set; }

        /// <summary>Local revision; incremented by every accepted Mutate.</summary>
        public long Revision { get; private set; }

        /// <summary>Reason for the Failed state; None otherwise.</summary>
        internal SlotFailure Failure { get; private set; }

        internal ISaveSlotHost Host => _host;

        /// <summary>Payload schema this build writes and reads (SchemaVersion hook).</summary>
        internal abstract int SupportedSchema { get; }

        internal abstract Type DataType { get; }

        /// <summary>Live data instance, boxed; creates the default when none exists yet.</summary>
        internal abstract object DataBox { get; }

        /// <summary>Binds the slot to a service; throws when it already belongs to another one.</summary>
        internal void AttachHost(ISaveSlotHost host)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (_host != null && !ReferenceEquals(_host, host))
            {
                throw new InvalidOperationException("Slot '" + Key + "' is already registered with another SaveService.");
            }

            _host = host;
        }

        /// <summary>Unbinds the slot when it belongs to host.</summary>
        internal void DetachHost(ISaveSlotHost host)
        {
            if (ReferenceEquals(_host, host))
            {
                _host = null;
            }
        }

        internal void MarkReady()
        {
            State = SlotState.Ready;
            Failure = SlotFailure.None;
        }

        internal void MarkFailed(SlotFailure failure)
        {
            if (failure == SlotFailure.None)
            {
                throw new ArgumentException("Failure must not be None.", nameof(failure));
            }

            State = SlotState.Failed;
            Failure = failure;
        }

        /// <summary>Memory gets defaults, State becomes Unloaded and the access-before-ready warning re-arms.</summary>
        internal void Unload()
        {
            ResetToDefault();
            State = SlotState.Unloaded;
            Failure = SlotFailure.None;
            _warnedWhileUnloaded = false;
        }

        internal void SetRevision(long revision)
        {
            if (revision < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(revision), "Must not be negative.");
            }

            Revision = revision;
        }

        /// <summary>Applies a completed local load: data (or defaults), revision and Ready/Failed state.</summary>
        internal void ApplyLoadResult(in SlotLoadResult result)
        {
            if (result.Data != null)
            {
                ReplaceData(result.Data);
            }
            else
            {
                ResetToDefault();
            }

            Revision = result.Revision < 0 ? 0 : result.Revision;
            if (result.Failure == SlotFailure.None)
            {
                MarkReady();
            }
            else
            {
                MarkFailed(result.Failure);
            }
        }

        /// <summary>Runs UpgradePayload once per step up to SupportedSchema inside the hook scope.</summary>
        internal abstract PayloadUpgradeResult RunUpgrade(JToken payload, int fromSchema);

        /// <summary>ToObject then Normalize exactly once, inside try; never throws.</summary>
        internal abstract SlotMaterializeResult MaterializeNormalized(JToken payload);

        /// <summary>CreateDefault then Normalize exactly once, inside try; never throws.</summary>
        internal abstract SlotMaterializeResult CreateNormalizedDefault();

        /// <summary>Default normalized if possible, otherwise raw CreateDefault or new TData(); never throws.</summary>
        internal abstract object CreateFallbackDefault();

        /// <summary>IsEmpty hook on a materialized instance; an exception is logged and counts as not empty.</summary>
        internal abstract bool EvaluateIsEmpty(object data);

        /// <summary>Snapshot of the live data at the current revision, optionally with IsEmpty evaluated on the same instance.</summary>
        internal abstract SlotSnapshot CaptureSnapshot(bool evaluateIsEmpty);

        /// <summary>Snapshot of a materialized instance; throws on serializer errors.</summary>
        internal abstract JToken SnapshotOf(object data);

        /// <summary>Runs ResolveConflict (and Normalize on merged data) inside the hook scope; never throws.</summary>
        internal abstract SlotConflictDecision ResolveConflictBoxed(object localData, object cloudData, in ConflictMetadata metadata);

        /// <summary>Swaps in an already normalized instance; revision and state are untouched.</summary>
        internal abstract void ReplaceData(object normalizedData);

        /// <summary>Memory gets the fallback default; revision and state are untouched.</summary>
        internal abstract void ResetToDefault();

        // Shared refusal rules for Mutate
        private protected bool CanMutate(string operation)
        {
            ISaveSlotHost host = _host;
            if (HookScope.RefuseIfActive(host?.Logger, operation + " on slot '" + Key + "'"))
            {
                return false;
            }

            if (host == null)
            {
                return false;
            }

            if (State != SlotState.Ready)
            {
                if (State == SlotState.Unloaded)
                {
                    WarnIfAccessedBeforeReady(operation);
                }

                return false;
            }

            if (SyncMode == SyncMode.CloudReadOnly)
            {
                host.Logger.Warning(operation + " refused: slot '" + Key + "' is CloudReadOnly.");
                return false;
            }

            return !host.IsSwitchingProfile && !host.IsDisposed;
        }

        private protected void CommitMutation()
        {
            Revision++;
            _host?.OnSlotMutated(this);
        }

        private protected void WarnIfAccessedBeforeReady(string operation)
        {
            ISaveSlotHost host = _host;
            if (State != SlotState.Unloaded || _warnedWhileUnloaded || host == null || !host.Options.WarnOnAccessBeforeReady)
            {
                return;
            }

            _warnedWhileUnloaded = true;
            host.Logger.Warning(
                operation + " on slot '" + Key + "' before it was loaded; it sees defaults. Await WhenReadyAsync before read-modify-write.");
        }
    }

    /// <summary>Stage at which materialization failed.</summary>
    internal enum SlotMaterializeStage
    {
        None = 0,
        CreateDefault = 1,
        Upgrade = 2,
        Materialize = 3,
        Normalize = 4,
    }

    /// <summary>Normalized boxed TData or the failure that prevented it.</summary>
    internal readonly struct SlotMaterializeResult
    {
        private SlotMaterializeResult(object data, SlotMaterializeStage failedStage, string message, Exception exception)
        {
            Data = data;
            FailedStage = failedStage;
            Message = message;
            Exception = exception;
        }

        public bool IsSuccess => FailedStage == SlotMaterializeStage.None;

        /// <summary>Normalized instance; null on failure.</summary>
        public object Data { get; }

        public SlotMaterializeStage FailedStage { get; }

        public string Message { get; }

        public Exception Exception { get; }

        internal static SlotMaterializeResult Success(object data)
        {
            return new SlotMaterializeResult(data, SlotMaterializeStage.None, null, null);
        }

        internal static SlotMaterializeResult Failed(SlotMaterializeStage stage, string message, Exception exception)
        {
            return new SlotMaterializeResult(null, stage, message, exception);
        }
    }

    /// <summary>Snapshot token of the live data at a revision.</summary>
    internal readonly struct SlotSnapshot
    {
        private SlotSnapshot(JToken data, long revision, bool? isEmpty, Exception exception)
        {
            Data = data;
            Revision = revision;
            IsEmpty = isEmpty;
            Exception = exception;
        }

        public bool IsSuccess => Data != null;

        /// <summary>Detached token; null when the serializer threw.</summary>
        public JToken Data { get; }

        public long Revision { get; }

        /// <summary>Null when not evaluated.</summary>
        public bool? IsEmpty { get; }

        public Exception Exception { get; }

        internal static SlotSnapshot Success(JToken data, long revision, bool? isEmpty)
        {
            return new SlotSnapshot(data, revision, isEmpty, null);
        }

        internal static SlotSnapshot Failed(long revision, Exception exception)
        {
            return new SlotSnapshot(null, revision, null, exception);
        }
    }

    /// <summary>Outcome of ResolveConflict with the emptiness flags it was given.</summary>
    internal readonly struct SlotConflictDecision
    {
        private SlotConflictDecision(
            bool isSuccess,
            ConflictResolutionKind kind,
            object mergedData,
            bool localIsEmpty,
            bool cloudIsEmpty,
            string message,
            Exception exception)
        {
            IsSuccess = isSuccess;
            Kind = kind;
            MergedData = mergedData;
            LocalIsEmpty = localIsEmpty;
            CloudIsEmpty = cloudIsEmpty;
            Message = message;
            Exception = exception;
        }

        /// <summary>False when the hook or Normalize on merged data threw.</summary>
        public bool IsSuccess { get; }

        public ConflictResolutionKind Kind { get; }

        /// <summary>Normalized merged instance when Kind is Merged.</summary>
        public object MergedData { get; }

        public bool LocalIsEmpty { get; }

        public bool CloudIsEmpty { get; }

        public string Message { get; }

        public Exception Exception { get; }

        internal static SlotConflictDecision Success(ConflictResolutionKind kind, object mergedData, bool localIsEmpty, bool cloudIsEmpty)
        {
            return new SlotConflictDecision(true, kind, mergedData, localIsEmpty, cloudIsEmpty, null, null);
        }

        internal static SlotConflictDecision Failed(bool localIsEmpty, bool cloudIsEmpty, string message, Exception exception)
        {
            return new SlotConflictDecision(false, ConflictResolutionKind.KeepLocal, null, localIsEmpty, cloudIsEmpty, message, exception);
        }
    }
}
