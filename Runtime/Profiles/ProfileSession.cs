using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Ecanakli.SaveSystem
{
    /// <summary>
    /// Active profile, epoch number and epoch CTS, per-epoch reconcile flags, the active profile.json cache and device.json state.
    /// Runs init and activation (04a A3 5.6) including the guest claim. Main thread only.
    /// </summary>
    internal sealed class ProfileSession : IDisposable
    {
        /// <summary>Collision suffixes tried when an account directory belongs to another owner.</summary>
        public const int MaxAccountDirectoryProbes = 16;

        private const string ActivatedTrigger = "ProfileActivated";
        private const string DeactivatingTrigger = "ProfileDeactivating";

        private static readonly CancellationToken CanceledToken = new CancellationToken(true);

        private static readonly Predicate<SaveSlot> IsProfileScoped = static slot => slot.Scope == SlotScope.Profile;

        private static readonly Func<IProfileDeactivatingListener, ProfileDeactivation, CancellationToken, UniTask> InvokeDeactivating =
            static (listener, context, ct) => listener.OnProfileDeactivatingAsync(context, ct);

        private static readonly Func<IRestoreListener, RestoreReport, CancellationToken, UniTask> InvokeRestored =
            static (listener, report, ct) => listener.OnRestoredAsync(report, ct);

        private readonly SaveContext _context;
        private readonly HashSet<SaveSlot> _reconciledThisEpoch = new HashSet<SaveSlot>();
        private readonly HashSet<SaveSlot> _localUpdateRequired = new HashSet<SaveSlot>();
        private readonly HashSet<SaveSlot> _cloudUpdateRequired = new HashSet<SaveSlot>();

        private ProfileId _activeProfile = ProfileId.Guest;
        private string _activeDirectory;
        private ProfileSyncState _syncState = new ProfileSyncState();
        private bool _syncStateCanOverwrite;
        private DeviceState _deviceState = new DeviceState();
        private bool _deviceStateCanOverwrite;
        private bool _deviceStatePersistPending;
        private string _deviceId;
        private CancellationTokenSource _epochCts;
        private long _epoch;
        private int _pendingActivations;
        private bool _initialized;
        private bool _switching;
        private bool _disposed;

        public ProfileSession(SaveContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public bool IsInitialized => _initialized;

        public bool IsDisposed => _disposed;

        /// <summary>True from gate acquisition to the end of slot loading; Mutate is refused.</summary>
        public bool IsSwitchingProfile => _switching;

        public ProfileId ActiveProfile => _activeProfile;

        /// <summary>Resolved directory of the active profile; null before init.</summary>
        public string ActiveProfileDirectory => _activeDirectory;

        /// <summary>Incremented on every switch; uploads compare it before applying results.</summary>
        public long Epoch => _epoch;

        /// <summary>Cancelled on switch and Dispose; a cancelled token before init and after Dispose.</summary>
        public CancellationToken EpochToken => _epochCts != null ? _epochCts.Token : CanceledToken;

        /// <summary>Diagnostic device id (DeviceIdProvider or the GUID in device.json).</summary>
        public string DeviceId => _deviceId;

        /// <summary>profile.json of the active profile; mutate on the main thread, then PersistSyncState.</summary>
        public ProfileSyncState ActiveSyncState => _syncState;

        /// <summary>False when profile.json could not be read; writes are refused to protect unread metadata.</summary>
        public bool CanPersistSyncState => _syncStateCanOverwrite;

        /// <summary>
        /// Loads device.json, Device slots, then the last active profile (a missing local profile falls back to Guest).
        /// Readiness is set before restore listeners run (ProfileActivated, Previous null).
        /// </summary>
        public async UniTask<ProfileActivationResult> InitializeAsync(CancellationToken ct)
        {
            ProfileActivationResult refusal = CheckEntry("InitializeAsync", ProfileId.Guest, false);
            if (refusal != null)
            {
                return refusal;
            }

            ct.ThrowIfCancellationRequested();
            OperationGate.Releaser releaser = default;
            bool gateHeld = false;
            try
            {
                using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _context.LifetimeToken))
                {
                    try
                    {
                        releaser = await _context.Gate.AcquireAsync(linked.Token);
                        gateHeld = true;
                    }
                    catch (OperationCanceledException) when (linked.IsCancellationRequested)
                    {
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }

                if (!gateHeld)
                {
                    ct.ThrowIfCancellationRequested();
                    return CreateRefusal(ProfileId.Guest, SaveErrorCode.Disposed, "The save service was disposed during initialization.", null);
                }

                if (_disposed || _context.LifetimeToken.IsCancellationRequested)
                {
                    return CreateRefusal(ProfileId.Guest, SaveErrorCode.Disposed, "The save service was disposed during initialization.", null);
                }

                if (_initialized)
                {
                    return CreateRefusal(_activeProfile, SaveErrorCode.AlreadyInitialized, "The save service is already initialized.", null);
                }

                var slotResults = new List<SlotRestoreResult>();
                var issues = new List<SlotLoadIssue>();
                var updates = new List<UpdateRequiredInfo>();
                bool claimed = false;
                Exception initError = null;
                _switching = true;
                try
                {
                    claimed = InitializeCore(slotResults, issues, updates);
                }
                catch (Exception exception)
                {
                    initError = exception;
                    _context.Logger.Error("[SaveSystem] Initialization failed.", exception);
                }
                finally
                {
                    _switching = false;
                }

                ProfileId profile = _activeProfile;
                _initialized = initError == null;

                // S5: a failed init releases its waiters instead of stranding them, but IsReady is documented as
                // initialized, so a failure completes them with false rather than claiming readiness
                if (_pendingActivations == 0)
                {
                    if (_initialized)
                    {
                        _context.Readiness.SetReady();
                    }
                    else
                    {
                        _context.Readiness.Fail();
                    }
                }

                releaser.Dispose();
                gateHeld = false;

                RaiseLoadEvents(issues, updates);
                if (initError != null)
                {
                    return new ProfileActivationResult(
                        SaveStatus.Failed, new SaveError(SaveErrorCode.Unknown, "Initialization failed: " + initError.Message, initError), profile, null,
                        false, claimed, issues, null, false);
                }

                await DispatchActivatedAsync(profile, slotResults, ct);
                var result = new ProfileActivationResult(SaveStatus.Success, null, profile, null, false, claimed, issues, null, false);
                if (!_disposed)
                {
                    _context.RaiseProfileActivated(result);
                }

                return result;
            }
            finally
            {
                if (gateHeld)
                {
                    releaser.Dispose();
                }
            }
        }

        /// <summary>
        /// 04a A3 5.6: deactivation hooks before the gate (shared budget; caller cancel aborts with nothing switched), then under the gate
        /// flush, new epoch, unload, resolve, load, persist last-active; readiness is set before restore listeners run.
        /// One activation at a time: a second call made while one is in flight is refused with ReentrantCall.
        /// Throws OperationCanceledException only when ct is cancelled before the switch starts.
        /// </summary>
        public async UniTask<ProfileActivationResult> ActivateProfileAsync(ProfileId profile, CancellationToken ct)
        {
            ProfileActivationResult refusal = CheckEntry("ActivateProfileAsync", profile, true);
            if (refusal != null)
            {
                return refusal;
            }

            ct.ThrowIfCancellationRequested();

            // C2: single flight, checked before the hooks run; a second dispatch would write into the wrong profile
            if (_pendingActivations > 0)
            {
                return CreateRefusal(
                    profile, SaveErrorCode.ReentrantCall, "ActivateProfileAsync was called while another activation is in progress.", null);
            }

            if (profile == _activeProfile)
            {
                return CreateAlreadyActive(profile, ListenerDispatchResult.Empty);
            }

            ProfileId outgoing = _activeProfile;
            ListenerDispatchResult deactivation = ListenerDispatchResult.Empty;
            OperationGate.Releaser releaser = default;
            bool gateHeld = false;
            _pendingActivations++;
            try
            {
                _context.Readiness.Reset();
                using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _context.LifetimeToken))
                {
                    // N3: slots stay Ready so hooks may Mutate, SaveNowAsync and FlushAsync
                    deactivation = await _context.DeactivationListeners.DispatchAsync(
                        new ProfileDeactivation(outgoing, profile), DeactivatingTrigger, InvokeDeactivating, _context.Options.DeactivationTimeout,
                        linked.Token);

                    if (!deactivation.WasCanceled && !linked.IsCancellationRequested && !_disposed)
                    {
                        try
                        {
                            releaser = await _context.Gate.AcquireAsync(linked.Token);
                            gateHeld = true;
                        }
                        catch (OperationCanceledException) when (linked.IsCancellationRequested)
                        {
                        }
                        catch (ObjectDisposedException)
                        {
                        }
                    }
                }

                if (!gateHeld)
                {
                    ct.ThrowIfCancellationRequested();
                    return CreateRefusal(profile, SaveErrorCode.Disposed, "The save service was disposed during activation.", deactivation);
                }

                if (_disposed || _context.LifetimeToken.IsCancellationRequested)
                {
                    return CreateRefusal(profile, SaveErrorCode.Disposed, "The save service was disposed during activation.", deactivation);
                }

                if (profile == _activeProfile)
                {
                    return CreateAlreadyActive(profile, deactivation);
                }

                var slotResults = new List<SlotRestoreResult>();
                var issues = new List<SlotLoadIssue>();
                var updates = new List<UpdateRequiredInfo>();
                bool claimed = false;
                Exception switchError = null;
                _switching = true;
                try
                {
                    claimed = SwitchCore(outgoing, profile, slotResults, issues, updates);
                }
                catch (Exception exception)
                {
                    switchError = exception;
                    _context.Logger.Error("[SaveSystem] Activation of " + profile + " failed.", exception);
                }
                finally
                {
                    _switching = false;
                }

                // F5: ready before the gate is released and before listeners run, but the single flight stays held:
                // the tail raises load events, dispatches restore listeners and raises ProfileActivated
                SetReadyIfLastActivation();
                releaser.Dispose();
                gateHeld = false;

                RaiseLoadEvents(issues, updates);
                if (switchError != null)
                {
                    return new ProfileActivationResult(
                        SaveStatus.Failed, new SaveError(SaveErrorCode.Unknown, "Activation failed: " + switchError.Message, switchError), _activeProfile,
                        outgoing, false, claimed, issues, deactivation.Failures, deactivation.TimedOut);
                }

                await DispatchActivatedAsync(profile, slotResults, ct);
                var result = new ProfileActivationResult(
                    SaveStatus.Success, null, profile, outgoing, false, claimed, issues, deactivation.Failures, deactivation.TimedOut);
                if (!_disposed)
                {
                    _context.RaiseProfileActivated(result);
                }

                return result;
            }
            finally
            {
                if (gateHeld)
                {
                    releaser.Dispose();
                }

                CompletePendingActivation();
            }
        }

        /// <summary>Local profiles on disk, sorted by name. Query only; never persists. Empty on storage errors (logged).</summary>
        public IReadOnlyList<ProfileId> GetLocalProfiles()
        {
            try
            {
                return SaveLayout.EnumerateLocalProfiles(_context.Storage);
            }
            catch (Exception exception)
            {
                _context.Logger.Error("[SaveSystem] Local profiles could not be enumerated.", exception);
                return Array.Empty<ProfileId>();
            }
        }

        /// <summary>Existing directory of a profile or null when it has none. Read-only; throws storage exceptions.</summary>
        public string FindProfileDirectory(ProfileId profile)
        {
            if (_initialized && profile == _activeProfile && _activeDirectory != null)
            {
                return _activeDirectory;
            }

            ISaveStorage storage = _context.Storage;
            if (profile.Kind != ProfileKind.Account)
            {
                string directory = SaveLayout.ProfileDirectory(profile);
                return storage.DirectoryExists(directory) ? directory : null;
            }

            for (int index = 0; index < MaxAccountDirectoryProbes; index++)
            {
                string candidate = SaveLayout.AccountDirectory(profile.AccountId, index);
                if (!storage.DirectoryExists(candidate))
                {
                    return null;
                }

                if (IsDirectoryEmpty(candidate) || OwnerMatches(candidate, profile.AccountId))
                {
                    return candidate;
                }
            }

            return null;
        }

        /// <summary>device for Device slots, otherwise the active profile directory.</summary>
        public string GetSlotDirectory(SaveSlot slot)
        {
            if (slot == null)
            {
                throw new ArgumentNullException(nameof(slot));
            }

            if (slot.Scope == SlotScope.Device)
            {
                return SaveLayout.DeviceDirectory;
            }

            return _activeDirectory ?? throw new InvalidOperationException("No profile is active.");
        }

        /// <summary>LocalWriteTracker key of the slot file.</summary>
        public LocalWriteTarget GetLocalWriteTarget(SaveSlot slot)
        {
            if (slot == null)
            {
                throw new ArgumentNullException(nameof(slot));
            }

            return LocalWriteTarget.Slot(slot.Scope == SlotScope.Device ? (ProfileId?)null : _activeProfile, slot.Key);
        }

        /// <summary>Sync entry of a Profile slot, created when missing (mutations are persisted by PersistSyncState).</summary>
        public SlotSyncState GetSyncState(SaveSlot slot)
        {
            if (slot == null)
            {
                throw new ArgumentNullException(nameof(slot));
            }

            return _syncState.GetOrCreateSlot(slot.Key);
        }

        /// <summary>Sync entry or a detached default; never adds an entry.</summary>
        public SlotSyncState PeekSyncState(SaveSlot slot)
        {
            if (slot == null)
            {
                throw new ArgumentNullException(nameof(slot));
            }

            return _syncState.PeekSlot(slot.Key);
        }

        /// <summary>Writes the active profile.json; updates write health. Refused (IoError) when the file could not be read at load.</summary>
        public StateWriteResult PersistSyncState()
        {
            if (_activeDirectory == null)
            {
                throw new InvalidOperationException("No profile is active.");
            }

            if (!_syncStateCanOverwrite)
            {
                string message = "profile.json in " + _activeDirectory + " could not be read at load; it is not overwritten.";
                _context.Logger.Warning("[SaveSystem] " + message);
                return StateWriteResult.Failed(new IOException(message));
            }

            StateWriteResult written = _context.SyncStateStore.Save(_activeDirectory, _syncState);
            ReportLocalWrite(LocalWriteTarget.ProfileState(_activeProfile), written.IsSuccess, written.ErrorKind, written.Message);
            return written;
        }

        /// <summary>Records a local write attempt in LocalWriteTracker and raises LocalWriteHealthChanged on a transition.</summary>
        public void ReportLocalWrite(LocalWriteTarget target, bool success, LocalWriteErrorKind errorKind, string message)
        {
            LocalWriteTracker tracker = _context.LocalWriteTracker;
            bool changed;
            if (success)
            {
                changed = tracker.RecordSuccess(target);
            }
            else
            {
                _context.Logger.Error("[SaveSystem] Local write to " + target + " failed (" + errorKind + "): " + message);
                changed = tracker.RecordFailure(target, errorKind, message, _context.Clock.UtcNow);
            }

            if (changed)
            {
                RaiseHealthChanged();
            }
        }

        public bool IsReconciledThisEpoch(SaveSlot slot)
        {
            return slot != null && _reconciledThisEpoch.Contains(slot);
        }

        /// <summary>Sets ReconciledThisEpoch and lifts upload suspension for the slot.</summary>
        public void MarkReconciledThisEpoch(SaveSlot slot)
        {
            if (slot == null || _disposed)
            {
                return;
            }

            _reconciledThisEpoch.Add(slot);
            _context.Scheduler.OnSlotReconciled(slot);
        }

        /// <summary>Write conflict: clears ReconciledThisEpoch so uploads wait for the single-slot reconcile.</summary>
        public void MarkNeedsReconcile(SaveSlot slot)
        {
            if (slot != null)
            {
                _reconciledThisEpoch.Remove(slot);
            }
        }

        /// <summary>True the first time per slot, source and epoch; the caller then raises UpdateRequired.</summary>
        public bool TryMarkUpdateRequired(SaveSlot slot, PayloadSource source)
        {
            if (slot == null)
            {
                return false;
            }

            return source == PayloadSource.Local ? _localUpdateRequired.Add(slot) : _cloudUpdateRequired.Add(slot);
        }

        /// <summary>Clears the last-active pointer when it names profile; true when the pointer is clear on disk.</summary>
        public bool ClearLastActiveProfileIf(ProfileId profile)
        {
            ProfileId? stored = _deviceState.LastActiveProfile;
            if (!stored.HasValue || stored.Value != profile)
            {
                // Memory is clear, but an earlier failed write can leave the pointer on disk
                return !_deviceStatePersistPending || PersistDeviceState();
            }

            _deviceState.LastActiveProfile = null;
            return PersistDeviceState();
        }

        /// <summary>Cancels and disposes the epoch CTS. Collaborators in the context are owned by SaveService.</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CancelEpoch();
            _reconciledThisEpoch.Clear();
            _localUpdateRequired.Clear();
            _cloudUpdateRequired.Clear();
        }

        private bool InitializeCore(List<SlotRestoreResult> slotResults, List<SlotLoadIssue> issues, List<UpdateRequiredInfo> updates)
        {
            LoadDeviceState();
            BeginNewEpoch();
            LoadSlots(_context.Registry.Select(SlotScope.Device), null, SaveLayout.DeviceDirectory, null, slotResults, issues, updates);

            ProfileId profile = ResolveInitialProfile();
            _activeProfile = profile;
            bool claimed = LoadProfile(profile, slotResults, issues, updates);
            PersistLastActive(profile);
            _context.Scheduler.BeginEpoch(_epochCts.Token);
            LogVerbose("Initialized with " + profile + " (epoch " + _epoch + ", directory " + _activeDirectory + ").");
            return claimed;
        }

        private bool SwitchCore(
            ProfileId outgoing, ProfileId incoming, List<SlotRestoreResult> slotResults, List<SlotLoadIssue> issues, List<UpdateRequiredInfo> updates)
        {
            FlushLocal();
            BeginNewEpoch();

            IReadOnlyList<SaveSlot> profileSlots = _context.Registry.Select(SlotScope.Profile);
            _context.Scheduler.RemoveWhere(IsProfileScoped);
            if (_context.LocalWriteTracker.RemoveProfile(outgoing))
            {
                RaiseHealthChanged();
            }

            for (int i = 0; i < profileSlots.Count; i++)
            {
                _context.MutationDetector.Forget(profileSlots[i]);
                profileSlots[i].Unload();
            }

            _activeProfile = incoming;
            _activeDirectory = null;
            _syncState = new ProfileSyncState();
            _syncStateCanOverwrite = false;

            bool claimed = LoadProfile(incoming, slotResults, issues, updates);
            PersistLastActive(incoming);
            _context.Scheduler.BeginEpoch(_epochCts.Token);
            LogVerbose("Activated " + incoming + " from " + outgoing + " (epoch " + _epoch + ", directory " + _activeDirectory + ").");
            return claimed;
        }

        private void FlushLocal()
        {
            try
            {
                LocalFlushResult flushed = _context.FlushLocalNow();
                if (flushed != null && !flushed.IsComplete)
                {
                    _context.Logger.Warning("[SaveSystem] Local flush before the profile switch was incomplete: " + flushed + ". Unwritten changes are lost.");
                }
            }
            catch (Exception exception)
            {
                _context.Logger.Error("[SaveSystem] Local flush before the profile switch threw.", exception);
            }
        }

        // Stops scheduler lanes, cancels the old epoch and starts a new one linked to the lifetime token
        private void BeginNewEpoch()
        {
            _context.Scheduler.StopEpoch();
            CancelEpoch();
            _epoch++;
            _epochCts = CancellationTokenSource.CreateLinkedTokenSource(_context.LifetimeToken);
            _reconciledThisEpoch.Clear();
            _localUpdateRequired.Clear();
            _cloudUpdateRequired.Clear();
            _context.MutationDetector.BeginEpoch();
        }

        private void CancelEpoch()
        {
            CancellationTokenSource previous = _epochCts;
            _epochCts = null;
            if (previous == null)
            {
                return;
            }

            try
            {
                previous.Cancel();
            }
            catch (Exception exception)
            {
                _context.Logger.Error("[SaveSystem] A callback registered on the epoch token threw during cancellation.", exception);
            }

            previous.Dispose();
        }

        private void LoadDeviceState()
        {
            DeviceStateLoadResult loaded = _context.DeviceStateStore.Load();
            _deviceState = loaded.State;
            _deviceStateCanOverwrite = loaded.CanOverwrite;

            string provided = null;
            Func<string> provider = _context.Options.DeviceIdProvider;
            if (provider != null)
            {
                try
                {
                    provided = provider();
                }
                catch (Exception exception)
                {
                    _context.Logger.Error("[SaveSystem] DeviceIdProvider threw; using the persisted device id.", exception);
                }
            }

            if (!string.IsNullOrEmpty(provided))
            {
                _deviceId = provided;
                return;
            }

            if (string.IsNullOrEmpty(_deviceState.DeviceId))
            {
                _deviceState.DeviceId = Guid.NewGuid().ToString("N");
                PersistDeviceState();
            }

            _deviceId = _deviceState.DeviceId;
        }

        private ProfileId ResolveInitialProfile()
        {
            ProfileId? last = _deviceState.LastActiveProfile;
            if (!last.HasValue)
            {
                return ProfileId.Guest;
            }

            ProfileId profile = last.Value;
            if (profile.Kind == ProfileKind.Guest)
            {
                return profile;
            }

            try
            {
                // Init never claims guest data: a stale Account pointer without its data falls back like a Local one
                bool exists = profile.Kind == ProfileKind.Account
                    ? HasAccountDirectoryWithContent(profile.AccountId)
                    : _context.Storage.DirectoryExists(SaveLayout.ProfileDirectory(profile));
                if (exists)
                {
                    return profile;
                }
            }
            catch (Exception exception)
            {
                // Unknown existence keeps the pointer; the slot loads report the IO error
                _context.Logger.Error("[SaveSystem] Could not check the directory of " + profile + ".", exception);
                return profile;
            }

            _context.Logger.Warning("[SaveSystem] Last active profile " + profile + " has no saved data directory; falling back to guest.");
            return ProfileId.Guest;
        }

        private bool LoadProfile(ProfileId profile, List<SlotRestoreResult> slotResults, List<SlotLoadIssue> issues, List<UpdateRequiredInfo> updates)
        {
            string directory = ResolveDirectory(profile, out bool claimed);
            _activeDirectory = directory;

            SyncStateLoadResult loaded = _context.SyncStateStore.Load(directory);
            _syncState = loaded.State;
            _syncStateCanOverwrite = loaded.CanOverwrite;
            SlotLoadIssue stateIssue = loaded.CreateIssue(profile);
            if (stateIssue != null)
            {
                issues.Add(stateIssue);
            }

            // Persist safe fallbacks so they survive restarts; a newer-version file is only overwritten when needed
            bool syncDirty = loaded.UsedSafeFallback && loaded.Status != SyncStateLoadStatus.NewerVersion && loaded.CanOverwrite;
            if (profile.Kind == ProfileKind.Account && _syncState.OwnerAccountId == null)
            {
                _syncState.OwnerAccountId = profile.AccountId;
                syncDirty = true;
            }

            syncDirty |= LoadSlots(_context.Registry.Select(SlotScope.Profile), profile, directory, _syncState, slotResults, issues, updates);
            if (syncDirty && _syncStateCanOverwrite)
            {
                PersistSyncState();
            }

            if (claimed)
            {
                _context.Logger.Info("[SaveSystem] Guest data was claimed by " + profile + ".");
            }

            return claimed;
        }

        // Loads each slot, applies the result and collects outcomes; returns true when the sync state changed
        private bool LoadSlots(
            IReadOnlyList<SaveSlot> slots,
            ProfileId? profile,
            string directory,
            ProfileSyncState syncState,
            List<SlotRestoreResult> slotResults,
            List<SlotLoadIssue> issues,
            List<UpdateRequiredInfo> updates)
        {
            bool syncDirty = false;
            for (int i = 0; i < slots.Count; i++)
            {
                SaveSlot slot = slots[i];
                string key = slot.Key;
                SlotLoadResult result;
                try
                {
                    result = _context.SlotStore.Load(slot, directory);
                }
                catch (Exception exception)
                {
                    _context.Logger.Error("[SaveSystem] Loading slot '" + key + "' threw; it stays Failed.", exception);
                    slot.ResetToDefault();
                    slot.MarkFailed(SlotFailure.IoError);
                    issues.Add(new SlotLoadIssue(profile, key, SlotLoadIssueKind.IoError, CorruptionCause.None, null, exception.Message, exception));
                    slotResults.Add(new SlotRestoreResult(key, SlotRestoreOutcome.Failed, SlotRestoreFailure.LocalIoError));
                    continue;
                }

                slot.ApplyLoadResult(in result);
                IReadOnlyList<SlotLoadIssue> slotIssues = result.CreateIssues(profile, key);
                for (int j = 0; j < slotIssues.Count; j++)
                {
                    issues.Add(slotIssues[j]);
                }

                if (result.Failure == SlotFailure.SchemaTooNew && TryMarkUpdateRequired(slot, PayloadSource.Local))
                {
                    updates.Add(new UpdateRequiredInfo(
                        profile, key, PayloadSource.Local, result.File.FoundFormat, result.File.FoundSchema, SaveEnvelope.CurrentFormat, slot.SupportedSchema));
                }

                slotResults.Add(new SlotRestoreResult(
                    key, result.IsReady ? SlotRestoreOutcome.LoadedLocal : SlotRestoreOutcome.Failed, ToRestoreFailure(result.Failure)));

                if (result.IsReady)
                {
                    _context.MutationDetector.CaptureBaseline(slot);
                }

                if (syncState != null && slot.SyncMode != SyncMode.LocalOnly)
                {
                    syncDirty |= ApplyLoadToSyncState(slot, in result, syncState);
                }
            }

            return syncDirty;
        }

        private static bool ApplyLoadToSyncState(SaveSlot slot, in SlotLoadResult result, ProfileSyncState syncState)
        {
            bool dirty = false;
            string key = slot.Key;
            if (result.NeedsCloudRecovery)
            {
                SlotSyncState entry = syncState.GetOrCreateSlot(key);
                if (!entry.NeedsCloudRecovery)
                {
                    entry.NeedsCloudRecovery = true;
                    dirty = true;
                }
            }

            // Content already on disk counts as HadContent
            if (result.IsReady && result.Presence == LocalPresence.Present && !syncState.PeekSlot(key).HadContent && !slot.EvaluateIsEmpty(slot.DataBox))
            {
                syncState.GetOrCreateSlot(key).HadContent = true;
                dirty = true;
            }

            return dirty;
        }

        private string ResolveDirectory(ProfileId profile, out bool claimed)
        {
            claimed = false;
            if (profile.Kind == ProfileKind.Account)
            {
                return ResolveAccountDirectory(profile.AccountId, out claimed);
            }

            // Guest and Local are never claimed; Local is created when missing
            string directory = SaveLayout.ProfileDirectory(profile);
            EnsureDirectory(directory);
            return directory;
        }

        private string ResolveAccountDirectory(string accountId, out bool claimed)
        {
            claimed = false;
            ISaveStorage storage = _context.Storage;
            for (int index = 0; index < MaxAccountDirectoryProbes; index++)
            {
                string candidate = SaveLayout.AccountDirectory(accountId, index);
                bool exists;
                bool empty;
                try
                {
                    exists = storage.DirectoryExists(candidate);
                    empty = exists && IsDirectoryEmpty(candidate);
                }
                catch (Exception exception)
                {
                    _context.Logger.Error("[SaveSystem] Could not probe " + candidate + "; using it as is.", exception);
                    return candidate;
                }

                if (!exists || empty)
                {
                    claimed = TryClaimGuest(candidate, exists);
                    if (!claimed)
                    {
                        EnsureDirectory(candidate);
                    }

                    return candidate;
                }

                if (OwnerMatches(candidate, accountId))
                {
                    return candidate;
                }

                _context.Logger.Warning("[SaveSystem] " + candidate + " belongs to another account; trying the next suffix.");
            }

            string fallback = SaveLayout.AccountDirectory(accountId, MaxAccountDirectoryProbes);
            _context.Logger.Error("[SaveSystem] No free directory for account after " + MaxAccountDirectoryProbes + " probes; using " + fallback + ".");
            EnsureDirectory(fallback);
            return fallback;
        }

        // Same probe order as ResolveAccountDirectory, without side effects; absent or empty counts as missing
        private bool HasAccountDirectoryWithContent(string accountId)
        {
            ISaveStorage storage = _context.Storage;
            for (int index = 0; index <= MaxAccountDirectoryProbes; index++)
            {
                string candidate = SaveLayout.AccountDirectory(accountId, index);
                if (!storage.DirectoryExists(candidate) || IsDirectoryEmpty(candidate))
                {
                    return false;
                }

                if (OwnerMatches(candidate, accountId))
                {
                    return true;
                }
            }

            return false;
        }

        // Atomic move guest -> account; only when the target is absent or empty. Conflicts with cloud data are left to reconcile.
        private bool TryClaimGuest(string targetDirectory, bool targetExists)
        {
            ISaveStorage storage = _context.Storage;
            string guestDirectory = SaveLayout.ProfileDirectory(ProfileId.Guest);
            try
            {
                if (!storage.DirectoryExists(guestDirectory) || !HasProfileSlotFiles(guestDirectory))
                {
                    return false;
                }

                if (targetExists)
                {
                    storage.DeleteDirectory(targetDirectory);
                }

                storage.MoveDirectory(guestDirectory, targetDirectory);
                return true;
            }
            catch (Exception exception)
            {
                _context.Logger.Error("[SaveSystem] Claiming guest data into " + targetDirectory + " failed; the account starts without it.", exception);
                return false;
            }
        }

        // Any primary, tmp or bak file of a registered Profile slot
        private bool HasProfileSlotFiles(string directory)
        {
            IReadOnlyList<string> names = _context.Storage.ListFileNames(directory);
            if (names.Count == 0)
            {
                return false;
            }

            IReadOnlyList<SaveSlot> slots = _context.Registry.Select(SlotScope.Profile);
            for (int i = 0; i < names.Count; i++)
            {
                string name = names[i];
                for (int j = 0; j < slots.Count; j++)
                {
                    string primary = slots[j].Key + SaveLayout.JsonExtension;
                    if (string.Equals(name, primary, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(name, primary + SaveLayout.TmpSuffix, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(name, primary + SaveLayout.BakSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private bool IsDirectoryEmpty(string directory)
        {
            ISaveStorage storage = _context.Storage;
            return storage.ListFileNames(directory).Count == 0 && storage.ListDirectoryNames(directory).Count == 0;
        }

        // Unknown owner (missing, corrupt or unreadable profile.json) counts as a match
        private bool OwnerMatches(string directory, string accountId)
        {
            string owner = _context.SyncStateStore.Load(directory, SlotReadMode.ReadOnly).State.OwnerAccountId;
            return owner == null || string.Equals(owner, accountId, StringComparison.Ordinal);
        }

        private void EnsureDirectory(string directory)
        {
            try
            {
                if (!_context.Storage.DirectoryExists(directory))
                {
                    _context.Storage.CreateDirectory(directory);
                }
            }
            catch (Exception exception)
            {
                _context.Logger.Error("[SaveSystem] Could not create " + directory + ".", exception);
            }
        }

        private void PersistLastActive(ProfileId profile)
        {
            ProfileId? stored = _deviceState.LastActiveProfile;
            bool unchanged = stored.HasValue ? stored.Value == profile : profile.Kind == ProfileKind.Guest;
            if (unchanged && !_deviceStatePersistPending)
            {
                return;
            }

            _deviceState.LastActiveProfile = profile;
            PersistDeviceState();
        }

        private bool PersistDeviceState()
        {
            if (!_deviceStateCanOverwrite)
            {
                _context.Logger.Warning("[SaveSystem] device.json could not be read at init; it is not overwritten this session.");
                return false;
            }

            StateWriteResult written = _context.DeviceStateStore.Save(_deviceState);
            ReportLocalWrite(LocalWriteTarget.DeviceState, written.IsSuccess, written.ErrorKind, written.Message);
            _deviceStatePersistPending = !written.IsSuccess;
            return written.IsSuccess;
        }

        private async UniTask DispatchActivatedAsync(ProfileId profile, List<SlotRestoreResult> slotResults, CancellationToken ct)
        {
            if (_disposed || _context.RestoreListeners.Count == 0)
            {
                return;
            }

            bool requiresAppUpdate = false;
            for (int i = 0; i < slotResults.Count; i++)
            {
                if (slotResults[i].Failure == SlotRestoreFailure.LocalSchemaTooNew)
                {
                    requiresAppUpdate = true;
                    break;
                }
            }

            var report = new RestoreReport(
                RestoreTrigger.ProfileActivated, SaveStatus.Success, null, profile, RestoreReport.ComputeActivationCompleteness(slotResults), slotResults, null,
                requiresAppUpdate);

            // Switch already happened: listener cancellation marks the dispatch, it does not throw
            using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _context.LifetimeToken))
            {
                ListenerDispatchResult dispatched = await _context.RestoreListeners.DispatchAsync(report, ActivatedTrigger, InvokeRestored, null, linked.Token);
                if (dispatched.WasCanceled)
                {
                    _context.Logger.Warning("[SaveSystem] Restore listener dispatch for " + profile + " was cancelled; remaining listeners were skipped.");
                }
            }
        }

        private void RaiseLoadEvents(List<SlotLoadIssue> issues, List<UpdateRequiredInfo> updates)
        {
            if (_disposed)
            {
                return;
            }

            for (int i = 0; i < issues.Count; i++)
            {
                _context.RaiseSlotLoadIssueDetected(issues[i]);
            }

            for (int i = 0; i < updates.Count; i++)
            {
                _context.RaiseUpdateRequired(updates[i]);
            }
        }

        private void RaiseHealthChanged()
        {
            if (!_disposed)
            {
                _context.RaiseLocalWriteHealthChanged(_context.LocalWriteTracker.Health);
            }
        }

        // Readiness without giving up the single flight: this activation is still counted
        private void SetReadyIfLastActivation()
        {
            if (_pendingActivations <= 1 && _initialized && !_disposed)
            {
                _context.Readiness.SetReady();
            }
        }

        private void CompletePendingActivation()
        {
            if (_pendingActivations > 0)
            {
                _pendingActivations--;
            }

            if (_pendingActivations == 0 && _initialized && !_disposed)
            {
                _context.Readiness.SetReady();
            }
        }

        private ProfileActivationResult CheckEntry(string operation, ProfileId profile, bool requireInitialized)
        {
            if (_disposed || _context.LifetimeToken.IsCancellationRequested)
            {
                return CreateRefusal(profile, SaveErrorCode.Disposed, operation + " was called after Dispose.", null);
            }

            if (HookScope.RefuseIfActive(_context.Logger, operation))
            {
                return CreateRefusal(profile, SaveErrorCode.CalledFromHook, operation + " was called from a slot hook.", null);
            }

            if (_context.ListenerGuard.RefuseIfDispatching(_context.Logger, operation))
            {
                return CreateRefusal(profile, SaveErrorCode.ReentrantCall, operation + " was called during listener dispatch.", null);
            }

            if (requireInitialized && !_initialized)
            {
                return CreateRefusal(profile, SaveErrorCode.NotInitialized, operation + " requires InitializeAsync first.", null);
            }

            if (!requireInitialized && _initialized)
            {
                return CreateRefusal(_activeProfile, SaveErrorCode.AlreadyInitialized, "The save service is already initialized.", null);
            }

            return null;
        }

        private ProfileActivationResult CreateRefusal(ProfileId profile, SaveErrorCode code, string message, ListenerDispatchResult deactivation)
        {
            return new ProfileActivationResult(
                SaveStatus.Failed,
                new SaveError(code, message),
                profile,
                _initialized ? _activeProfile : (ProfileId?)null,
                false,
                false,
                null,
                deactivation?.Failures,
                deactivation != null && deactivation.TimedOut);
        }

        private ProfileActivationResult CreateAlreadyActive(ProfileId profile, ListenerDispatchResult deactivation)
        {
            LogVerbose(profile + " is already active.");
            return new ProfileActivationResult(
                SaveStatus.Success, null, profile, profile, true, false, null, deactivation.Failures, deactivation.TimedOut);
        }

        private static SlotRestoreFailure ToRestoreFailure(SlotFailure failure)
        {
            switch (failure)
            {
                case SlotFailure.IoError:
                    return SlotRestoreFailure.LocalIoError;
                case SlotFailure.NormalizeFailed:
                    return SlotRestoreFailure.LocalNormalizeFailed;
                case SlotFailure.SchemaTooNew:
                    return SlotRestoreFailure.LocalSchemaTooNew;
                default:
                    return SlotRestoreFailure.None;
            }
        }

        private void LogVerbose(string message)
        {
            if (_context.Logger.IsVerboseEnabled)
            {
                _context.Logger.Verbose("[SaveSystem] " + message);
            }
        }
    }
}
