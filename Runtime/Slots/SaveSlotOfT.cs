using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Ecanakli.SaveSystem
{
    /// <summary>
    /// Typed save slot. Subclass it, set Key (and optionally SyncMode, Scope, SchemaVersion) and override hooks as needed.
    /// Hooks (CreateDefault, Normalize, UpgradePayload, IsEmpty, ResolveConflict) must be synchronous and pure and must not
    /// call the save service; Mutate, SaveNowAsync and service calls made from a hook are refused and logged.
    /// </summary>
    public abstract class SaveSlot<TData> : SaveSlot where TData : class, new()
    {
        private TData _data;
        private bool _creatingDefault;
        private PayloadUpgradeHandler _upgradeHandler;
        private JToken _emptyReference;
        private SaveJson _emptyReferenceJson;

        // Set only during CaptureSnapshot so the default IsEmpty reuses the snapshot token
        private TData _snapshotSource;
        private JToken _snapshotToken;

        protected SaveSlot()
        {
        }

        /// <summary>Payload schema version written by this build; at least 1. Raise it together with an UpgradePayload step.</summary>
        protected virtual int SchemaVersion => 1;

        /// <summary>Live data for typed methods on the subclass. Change it only inside Mutate; never hand the reference out.</summary>
        protected TData Data
        {
            get
            {
                if (_data == null)
                {
                    _data = CreateFallbackDefaultTyped();
                }

                return _data;
            }
        }

        internal sealed override int SupportedSchema => SchemaVersion;

        internal sealed override Type DataType => typeof(TData);

        internal sealed override object DataBox => Data;

        private SaveJson Json => Host?.Json ?? SaveJson.Default;

        /// <summary>Reads a value from the data. Never keep a reference to the data or its sub-objects past the call.</summary>
        public TResult Read<TResult>(Func<TData, TResult> read)
        {
            if (read == null)
            {
                throw new ArgumentNullException(nameof(read));
            }

            WarnIfAccessedBeforeReady(nameof(Read));
            return read(Data);
        }

        /// <summary>Non-capturing Read; pass state through arg and use a static lambda.</summary>
        public TResult Read<TArg, TResult>(TArg arg, Func<TData, TArg, TResult> read)
        {
            if (read == null)
            {
                throw new ArgumentNullException(nameof(read));
            }

            WarnIfAccessedBeforeReady(nameof(Read));
            return read(Data, arg);
        }

        /// <summary>
        /// Changes the data and schedules persistence. Returns false (nothing runs) when the slot is not Ready, is CloudReadOnly,
        /// a profile switch is in progress, the service is disposed, or it is called from a hook.
        /// If the action throws, the partial change is still marked dirty and the exception propagates.
        /// </summary>
        public bool Mutate(Action<TData> mutate)
        {
            if (mutate == null)
            {
                throw new ArgumentNullException(nameof(mutate));
            }

            if (!CanMutate(nameof(Mutate)))
            {
                return false;
            }

            try
            {
                mutate(Data);
            }
            finally
            {
                CommitMutation();
            }

            return true;
        }

        /// <summary>Non-capturing Mutate; pass state through arg and use a static lambda.</summary>
        public bool Mutate<TArg>(TArg arg, Action<TData, TArg> mutate)
        {
            if (mutate == null)
            {
                throw new ArgumentNullException(nameof(mutate));
            }

            if (!CanMutate(nameof(Mutate)))
            {
                return false;
            }

            try
            {
                mutate(Data, arg);
            }
            finally
            {
                CommitMutation();
            }

            return true;
        }

        /// <summary>
        /// Completes after the current revision is fsynced and promoted on disk (purchases and rewards must await it).
        /// Requests an upload for CloudSync slots but does not await it. Caller cancellation is honored only before the write starts.
        /// </summary>
        public UniTask<SaveResult> SaveNowAsync(CancellationToken ct)
        {
            ISaveSlotHost host = Host;
            if (HookScope.RefuseIfActive(host?.Logger, nameof(SaveNowAsync) + " on slot '" + Key + "'"))
            {
                return UniTask.FromResult(SaveResult.Failure(SaveErrorCode.CalledFromHook, "SaveNowAsync was called from a slot hook."));
            }

            if (host == null)
            {
                return UniTask.FromResult(SaveResult.Failure(SaveErrorCode.SlotNotRegistered, "Slot '" + Key + "' is not registered with a SaveService."));
            }

            return host.SaveSlotNowAsync(this, ct);
        }

        /// <summary>Hook: a new default instance. Synchronous and pure.</summary>
        protected virtual TData CreateDefault()
        {
            return new TData();
        }

        /// <summary>Hook: repairs invariants (null lists, clamps). Runs exactly once whenever a data instance enters memory. Synchronous and pure.</summary>
        protected virtual void Normalize(TData data)
        {
        }

        /// <summary>
        /// Hook, ONE STEP per call: receives the payload at fromSchemaVersion and must return it at fromSchemaVersion + 1.
        /// The core calls it repeatedly (from, from+1, ... SchemaVersion-1); never jump straight to the current version.
        /// May modify and return the same object. Exceptions or null fail the load. Synchronous and pure.
        /// </summary>
        protected virtual JObject UpgradePayload(JObject payload, int fromSchemaVersion)
        {
            return payload;
        }

        /// <summary>Hook: true when data carries no player progress. Default compares the JSON of data with the normalized default. Sees only data, never envelope fields.</summary>
        protected virtual bool IsEmpty(TData data)
        {
            if (data == null)
            {
                return true;
            }

            JToken token = ReferenceEquals(data, _snapshotSource) && _snapshotToken != null ? _snapshotToken : Json.Snapshot(data);
            return JToken.DeepEquals(token, GetEmptyReference());
        }

        /// <summary>Hook: decides a local vs cloud conflict. Default: the empty side loses, otherwise cloud wins. Never mutate the context inputs.</summary>
        protected virtual ConflictResolution<TData> ResolveConflict(in ConflictContext<TData> context)
        {
            return DefaultConflictPolicy.Resolve(in context);
        }

        internal sealed override PayloadUpgradeResult RunUpgrade(JToken payload, int fromSchema)
        {
            if (_upgradeHandler == null)
            {
                _upgradeHandler = UpgradePayload;
            }

            using (HookScope.Enter(Key, nameof(UpgradePayload)))
            {
                return EnvelopeCodec.Upgrade(payload, fromSchema, SchemaVersion, _upgradeHandler);
            }
        }

        internal sealed override SlotMaterializeResult MaterializeNormalized(JToken payload)
        {
            if (payload == null || payload.Type == JTokenType.Null)
            {
                return SlotMaterializeResult.Failed(SlotMaterializeStage.Materialize, "Payload is null.", null);
            }

            TData data;
            try
            {
                data = Json.Materialize<TData>(payload);
            }
            catch (Exception exception)
            {
                return SlotMaterializeResult.Failed(
                    SlotMaterializeStage.Materialize, "Deserializing " + typeof(TData).Name + " failed: " + exception.Message, exception);
            }

            if (data == null)
            {
                return SlotMaterializeResult.Failed(SlotMaterializeStage.Materialize, "Payload materialized to null.", null);
            }

            return NormalizeInstance(data);
        }

        internal sealed override SlotMaterializeResult CreateNormalizedDefault()
        {
            if (!TryInvokeCreateDefault(out TData data, out Exception exception))
            {
                return SlotMaterializeResult.Failed(SlotMaterializeStage.CreateDefault, "CreateDefault threw or returned null.", exception);
            }

            return NormalizeInstance(data);
        }

        internal sealed override object CreateFallbackDefault()
        {
            return CreateFallbackDefaultTyped();
        }

        internal sealed override bool EvaluateIsEmpty(object data)
        {
            return EvaluateIsEmptyTyped(Cast(data));
        }

        internal sealed override SlotSnapshot CaptureSnapshot(bool evaluateIsEmpty)
        {
            TData data = Data;
            long revision = Revision;
            JToken token;
            try
            {
                token = Json.Snapshot(data);
            }
            catch (Exception exception)
            {
                return SlotSnapshot.Failed(revision, exception);
            }

            bool? isEmpty = null;
            if (evaluateIsEmpty)
            {
                _snapshotSource = data;
                _snapshotToken = token;
                try
                {
                    isEmpty = EvaluateIsEmptyTyped(data);
                }
                finally
                {
                    _snapshotSource = null;
                    _snapshotToken = null;
                }
            }

            return SlotSnapshot.Success(token, revision, isEmpty);
        }

        internal sealed override JToken SnapshotOf(object data)
        {
            return Json.Snapshot(Cast(data));
        }

        internal sealed override SlotConflictDecision ResolveConflictBoxed(object localData, object cloudData, in ConflictMetadata metadata)
        {
            TData local = Cast(localData);
            TData cloud = Cast(cloudData);
            bool localIsEmpty = EvaluateIsEmptyTyped(local);
            bool cloudIsEmpty = EvaluateIsEmptyTyped(cloud);
            var context = new ConflictContext<TData>(Key, local, cloud, localIsEmpty, cloudIsEmpty, metadata);

            ConflictResolution<TData> resolution;
            try
            {
                using (HookScope.Enter(Key, nameof(ResolveConflict)))
                {
                    resolution = ResolveConflict(in context);
                }
            }
            catch (Exception exception)
            {
                return SlotConflictDecision.Failed(localIsEmpty, cloudIsEmpty, "ResolveConflict threw.", exception);
            }

            if (resolution.Kind != ConflictResolutionKind.Merged)
            {
                return SlotConflictDecision.Success(resolution.Kind, null, localIsEmpty, cloudIsEmpty);
            }

            TData merged = resolution.MergedData;
            if (merged == null)
            {
                return SlotConflictDecision.Failed(localIsEmpty, cloudIsEmpty, "ResolveConflict returned Merged without data.", null);
            }

            SlotMaterializeResult normalized = NormalizeInstance(merged);
            if (!normalized.IsSuccess)
            {
                return SlotConflictDecision.Failed(localIsEmpty, cloudIsEmpty, "Normalize threw on merged data.", normalized.Exception);
            }

            return SlotConflictDecision.Success(ConflictResolutionKind.Merged, merged, localIsEmpty, cloudIsEmpty);
        }

        internal sealed override void ReplaceData(object normalizedData)
        {
            _data = Cast(normalizedData);
        }

        internal sealed override void ResetToDefault()
        {
            _data = CreateFallbackDefaultTyped();
        }

        private SlotMaterializeResult NormalizeInstance(TData data)
        {
            try
            {
                using (HookScope.Enter(Key, nameof(Normalize)))
                {
                    Normalize(data);
                }
            }
            catch (Exception exception)
            {
                return SlotMaterializeResult.Failed(SlotMaterializeStage.Normalize, "Normalize threw: " + exception.Message, exception);
            }

            return SlotMaterializeResult.Success(data);
        }

        private bool TryInvokeCreateDefault(out TData data, out Exception exception)
        {
            exception = null;
            try
            {
                using (HookScope.Enter(Key, nameof(CreateDefault)))
                {
                    data = CreateDefault();
                }
            }
            catch (Exception caught)
            {
                exception = caught;
                data = null;
                return false;
            }

            return data != null;
        }

        private TData CreateFallbackDefaultTyped()
        {
            if (_creatingDefault)
            {
                throw new InvalidOperationException("Data of slot '" + Key + "' was accessed while its default was being created.");
            }

            _creatingDefault = true;
            try
            {
                if (!TryInvokeCreateDefault(out TData data, out Exception createException))
                {
                    LogHookError(nameof(CreateDefault), createException);
                    data = new TData();
                }

                SlotMaterializeResult normalized = NormalizeInstance(data);
                if (normalized.IsSuccess)
                {
                    return data;
                }

                LogHookError(nameof(Normalize), normalized.Exception);

                // Normalize may have left the instance half-changed; use a fresh raw default
                return TryInvokeCreateDefault(out TData raw, out _) ? raw : new TData();
            }
            finally
            {
                _creatingDefault = false;
            }
        }

        private bool EvaluateIsEmptyTyped(TData data)
        {
            try
            {
                using (HookScope.Enter(Key, nameof(IsEmpty)))
                {
                    return IsEmpty(data);
                }
            }
            catch (Exception exception)
            {
                // Not empty is the safe answer: it never enables an empty upload over content
                LogHookError(nameof(IsEmpty), exception);
                return false;
            }
        }

        private JToken GetEmptyReference()
        {
            SaveJson json = Json;
            if (_emptyReference != null && ReferenceEquals(_emptyReferenceJson, json))
            {
                return _emptyReference;
            }

            SlotMaterializeResult normalized = CreateNormalizedDefault();
            TData reference;
            if (normalized.IsSuccess)
            {
                reference = (TData)normalized.Data;
            }
            else if (!TryInvokeCreateDefault(out reference, out _))
            {
                reference = new TData();
            }

            _emptyReference = json.Snapshot(reference);
            _emptyReferenceJson = json;
            return _emptyReference;
        }

        private TData Cast(object data)
        {
            if (data is TData typed)
            {
                return typed;
            }

            throw new InvalidOperationException(
                "Slot '" + Key + "' expected " + typeof(TData).FullName + " but got " + (data == null ? "null" : data.GetType().FullName) + ".");
        }

        private void LogHookError(string hookName, Exception exception)
        {
            Host?.Logger.Error(hookName + " of slot '" + Key + "' threw.", exception);
        }
    }
}
