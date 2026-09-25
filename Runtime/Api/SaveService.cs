using System;
using System.Collections.Generic;
using System.Threading;

namespace Ecanakli.SaveSystem
{
    /// <summary>
    /// Save service facade (01 section 3, 04a A2). Composes the core without DI and delegates to the internal operations.
    /// Main thread only. Partial files: core (this), Api, Host, LocalWrites, Cloud.
    /// </summary>
    public sealed partial class SaveService : ISaveService, ISaveSlotHost, ISaveSchedulerTarget
    {
        private static readonly Func<IRestoreListener, int> RestoreOrderOf = static listener => listener.Order;

        private static readonly Func<IProfileDeactivatingListener, int> DeactivationOrderOf = static listener => listener.Order;

#if UNITY_WEBGL && !UNITY_EDITOR
        private static bool s_webGlWarningLogged;
#endif

        private readonly SaveServiceOptions _options;
        private readonly ISaveLogger _logger;
        private readonly ISaveClock _clock;
        private readonly ISaveStorage _storage;
        private readonly SaveSlotRegistry _registry;
        private readonly SaveJson _json;
        private readonly SlotStore _slotStore;
        private readonly SyncStateStore _syncStateStore;
        private readonly DeviceStateStore _deviceStateStore;
        private readonly LocalWriteTracker _localWriteTracker;
        private readonly OperationGate _gate;
        private readonly SaveScheduler _scheduler;
        private readonly CloudGateway _cloudGateway;
        private readonly ListenerReentrancyGuard _listenerGuard;
        private readonly OrderedListenerDispatcher<IRestoreListener> _restoreListeners;
        private readonly OrderedListenerDispatcher<IProfileDeactivatingListener> _deactivationListeners;
        private readonly MutationDetector _mutationDetector;
        private readonly ReadinessSignal _readiness;
        private readonly CancellationTokenSource _lifetimeCts;
        private readonly SaveContext _context;
        private readonly ProfileSession _session;
        private readonly RestoreOperation _restore;
        private readonly CloudUploader _uploader;
        private readonly ProfileDeleteOperation _profileDelete;
        private readonly AccountDataDeleteOperation _accountDelete;
        private readonly string _liveRoot;

        private bool _disposed;

        // S4: set before the dispose flush so nothing raised by it reaches a handler
        private bool _disposing;
        private LocalFlushResult _disposeFlushResult;

        /// <summary>
        /// Composes the service. Throws on invalid options, invalid or duplicate slots, or a slot already registered with another service.
        /// </summary>
        public SaveService(SaveServiceOptions options, ISaveStorage storage, ICloudSaveProvider provider, IEnumerable<SaveSlot> slots)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            if (provider == null)
            {
                throw new ArgumentNullException(nameof(provider), "Pass NullCloudSaveProvider.Instance when no cloud provider is used.");
            }

            if (slots == null)
            {
                throw new ArgumentNullException(nameof(slots));
            }

            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
            options.Validate();
            _options = options;
            _logger = options.Logger;
            _clock = options.Clock;
            _isLocalDirty = IsSlotLocalDirty;

            _registry = new SaveSlotRegistry(slots);
            _registry.Freeze();
            _json = new SaveJson(options.JsonConverters);
            _slotStore = new SlotStore(storage, _logger, _clock);
            _syncStateStore = new SyncStateStore(storage, _logger, _clock);
            _deviceStateStore = new DeviceStateStore(storage, _logger, _clock);
            _localWriteTracker = new LocalWriteTracker(options.LocalWriteBackoff);
            _gate = new OperationGate();
            _scheduler = new SaveScheduler(this, _localWriteTracker, options);
            _cloudGateway = new CloudGateway(provider, RetryPolicy.FromOptions(options), _logger, options.CloudCallTimeout);
            _listenerGuard = new ListenerReentrancyGuard();
            _restoreListeners = new OrderedListenerDispatcher<IRestoreListener>("restore", RestoreOrderOf, _listenerGuard, _logger, _clock);
            _deactivationListeners = new OrderedListenerDispatcher<IProfileDeactivatingListener>(
                "deactivation", DeactivationOrderOf, _listenerGuard, _logger, _clock);
            _mutationDetector = new MutationDetector(options.DetectMutationsOutsideMutate, _logger);
            _readiness = new ReadinessSignal();
            _liveRoot = ResolveLiveRoot(storage, options);

            _lifetimeCts = new CancellationTokenSource();
            _context = new SaveContext(
                options, _storage, provider, _registry, _json, _slotStore, _syncStateStore, _deviceStateStore, _localWriteTracker, _gate, _scheduler,
                _cloudGateway, _restoreListeners, _deactivationListeners, _listenerGuard, _mutationDetector, _readiness, _lifetimeCts.Token,
                FlushLocalCore, RaiseProfileActivated, RaiseRestoreCompleted, RaiseSlotLoadIssueDetected, RaiseUploadFailed,
                RaiseLocalWriteHealthChanged, RaiseUpdateRequired);
            _session = new ProfileSession(_context);
            _restore = new RestoreOperation(_context, _session);
            _uploader = new CloudUploader(_context, _session, DeferSlotReconcile);
            _profileDelete = new ProfileDeleteOperation(_context, _session);
            _accountDelete = new AccountDataDeleteOperation(_context, _session);

            try
            {
                AttachSlots();
            }
            catch
            {
                _lifetimeCts.Dispose();
                throw;
            }

            if (!SaveFolders.RegisterLiveRoot(_liveRoot))
            {
                _logger.Warning("[SaveSystem] Another live SaveService already uses " + _liveRoot + "; both write the same files.");
            }

            WarnIfStorageRootDiffers(storage, options);
            WarnIfWebGl();
        }

        public bool IsInitialized => !_disposed && _session.IsInitialized;

        public bool IsReady => _readiness.IsReady;

        public ProfileId ActiveProfile => _session.ActiveProfile;

        public event Action<ProfileActivationResult> ProfileActivated;

        public event Action<RestoreReport> RestoreCompleted;

        public event Action<SlotLoadIssue> SlotLoadIssueDetected;

        public event Action<UploadFailure> UploadFailed;

        public event Action<LocalWriteHealth> LocalWriteHealthChanged;

        public event Action<UpdateRequiredInfo> UpdateRequired;

        /// <summary>Flushes local writes, cancels every operation, disposes collaborators and releases the save root. Idempotent.</summary>
        /// <summary>Foreground resume: pending transient upload retries become due now (01 5.8).</summary>
        internal void ResumeTransientRetries()
        {
            if (_disposed)
            {
                return;
            }

            _scheduler.ResumeTransientRetries();
        }

        public void Dispose()
        {
            if (_disposed || _disposing || HookScope.RefuseIfActive(_logger, "Dispose"))
            {
                return;
            }

            // S4: the dispose flush must not reach a handler that could re-enter Dispose or FlushLocalNow
            _disposing = true;
            _disposeFlushResult = FlushBeforeDispose();
            _disposed = true;

            // Before cancelling: an activation unwinding on cancellation must not mark the service ready
            _readiness.Dispose();

            // Queued thread-pool writes are superseded; one in progress finishes first
            lock (_localWriteSync)
            {
                _localWriteGeneration++;
            }

            try
            {
                _lifetimeCts.Cancel();
            }
            catch (Exception exception)
            {
                _logger.Error("[SaveSystem] A callback registered on the lifetime token threw during Dispose.", exception);
            }

            _session.Dispose();
            _restore.Dispose();
            _scheduler.Dispose();
            _gate.Dispose();
            _restoreListeners.Dispose();
            _deactivationListeners.Dispose();
            _mutationDetector.Clear();
            _deferredReconciles.Clear();
            DetachSlots();
            SaveFolders.UnregisterLiveRoot(_liveRoot);
            _lifetimeCts.Dispose();

            ProfileActivated = null;
            RestoreCompleted = null;
            SlotLoadIssueDetected = null;
            UploadFailed = null;
            LocalWriteHealthChanged = null;
            UpdateRequired = null;
        }

        private LocalFlushResult FlushBeforeDispose()
        {
            if (!_session.IsInitialized)
            {
                return LocalFlushResult.Complete;
            }

            try
            {
                return FlushLocalCore();
            }
            catch (Exception exception)
            {
                _logger.Error("[SaveSystem] Local flush during Dispose threw; unwritten changes are lost.", exception);
                return CreateFlushRefusal("Local flush during Dispose threw: " + exception.Message);
            }
        }

        private void AttachSlots()
        {
            IReadOnlyList<SaveSlot> registered = _registry.Slots;
            int attached = 0;
            try
            {
                for (; attached < registered.Count; attached++)
                {
                    registered[attached].AttachHost(this);
                }
            }
            catch
            {
                for (int i = 0; i < attached; i++)
                {
                    registered[i].DetachHost(this);
                }

                throw;
            }
        }

        // Lets the slots join another service later (tests, domain reload)
        private void DetachSlots()
        {
            IReadOnlyList<SaveSlot> registered = _registry.Slots;
            for (int i = 0; i < registered.Count; i++)
            {
                registered[i].DetachHost(this);
            }
        }

        // The storage root holds the files; options root is used for custom storages
        private static string ResolveLiveRoot(ISaveStorage storage, SaveServiceOptions options)
        {
            string root = storage is AtomicFileStorage atomic ? atomic.RootDirectory : options.RootDirectory;
            return SaveFolders.NormalizeRoot(root);
        }

        private void WarnIfStorageRootDiffers(ISaveStorage storage, SaveServiceOptions options)
        {
            if (!(storage is AtomicFileStorage atomic))
            {
                return;
            }

            try
            {
                string storageRoot = SaveFolders.NormalizeRoot(atomic.RootDirectory);
                string optionsRoot = SaveFolders.NormalizeRoot(options.RootDirectory);
                if (!string.Equals(storageRoot, optionsRoot, StringComparison.Ordinal))
                {
                    _logger.Warning("[SaveSystem] AtomicFileStorage root " + storageRoot + " differs from SaveServiceOptions.RootDirectory " + optionsRoot
                                    + "; files are written to the storage root.");
                }
            }
            catch (Exception exception)
            {
                _logger.Warning("[SaveSystem] Save roots could not be compared: " + exception.Message);
            }
        }

        private void WarnIfWebGl()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (s_webGlWarningLogged)
            {
                return;
            }

            s_webGlWarningLogged = true;
            _logger.Warning("[SaveSystem] WebGL is not supported in this version (no threads, browser-backed file system); saves may be lost.");
#endif
        }

        private void RaiseProfileActivated(ProfileActivationResult result)
        {
            RaiseEvent(ProfileActivated, result, nameof(ProfileActivated));
        }

        private void RaiseRestoreCompleted(RestoreReport report)
        {
            RaiseEvent(RestoreCompleted, report, nameof(RestoreCompleted));
        }

        private void RaiseSlotLoadIssueDetected(SlotLoadIssue issue)
        {
            RaiseEvent(SlotLoadIssueDetected, issue, nameof(SlotLoadIssueDetected));
        }

        private void RaiseUploadFailed(UploadFailure failure)
        {
            RaiseEvent(UploadFailed, failure, nameof(UploadFailed));
        }

        private void RaiseLocalWriteHealthChanged(LocalWriteHealth health)
        {
            RaiseEvent(LocalWriteHealthChanged, health, nameof(LocalWriteHealthChanged));
        }

        private void RaiseUpdateRequired(UpdateRequiredInfo info)
        {
            RaiseEvent(UpdateRequired, info, nameof(UpdateRequired));
        }

        // Each handler in its own try/catch; nothing is raised from inside Dispose or after it
        private void RaiseEvent<T>(Action<T> handlers, T arg, string eventName)
        {
            if (handlers == null || _disposed || _disposing)
            {
                return;
            }

            Delegate[] invocationList = handlers.GetInvocationList();
            for (int i = 0; i < invocationList.Length; i++)
            {
                try
                {
                    ((Action<T>)invocationList[i])(arg);
                }
                catch (Exception exception)
                {
                    _logger.Error("[SaveSystem] A " + eventName + " handler threw.", exception);
                }
            }
        }
    }
}
