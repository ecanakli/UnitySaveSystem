using System;
using System.Threading;

namespace Ecanakli.SaveSystem
{
    /// <summary>
    /// Shared collaborators and event raise delegates for internal operations. Built by SaveService, which owns every
    /// member (including the lifetime CTS behind LifetimeToken). Plain holder: no logic.
    /// </summary>
    internal sealed class SaveContext
    {
        public SaveContext(
            SaveServiceOptions options,
            ISaveStorage storage,
            ICloudSaveProvider provider,
            SaveSlotRegistry registry,
            SaveJson json,
            SlotStore slotStore,
            SyncStateStore syncStateStore,
            DeviceStateStore deviceStateStore,
            LocalWriteTracker localWriteTracker,
            OperationGate gate,
            SaveScheduler scheduler,
            CloudGateway cloudGateway,
            OrderedListenerDispatcher<IRestoreListener> restoreListeners,
            OrderedListenerDispatcher<IProfileDeactivatingListener> deactivationListeners,
            ListenerReentrancyGuard listenerGuard,
            MutationDetector mutationDetector,
            ReadinessSignal readiness,
            CancellationToken lifetimeToken,
            Func<LocalFlushResult> flushLocalNow,
            Action<ProfileActivationResult> raiseProfileActivated,
            Action<RestoreReport> raiseRestoreCompleted,
            Action<SlotLoadIssue> raiseSlotLoadIssueDetected,
            Action<UploadFailure> raiseUploadFailed,
            Action<LocalWriteHealth> raiseLocalWriteHealthChanged,
            Action<UpdateRequiredInfo> raiseUpdateRequired)
        {
            Options = options ?? throw new ArgumentNullException(nameof(options));
            Logger = options.Logger ?? throw new ArgumentException("Options.Logger must not be null.", nameof(options));
            Clock = options.Clock ?? throw new ArgumentException("Options.Clock must not be null.", nameof(options));
            Storage = storage ?? throw new ArgumentNullException(nameof(storage));
            Provider = provider ?? throw new ArgumentNullException(nameof(provider));
            Registry = registry ?? throw new ArgumentNullException(nameof(registry));
            Json = json ?? throw new ArgumentNullException(nameof(json));
            SlotStore = slotStore ?? throw new ArgumentNullException(nameof(slotStore));
            SyncStateStore = syncStateStore ?? throw new ArgumentNullException(nameof(syncStateStore));
            DeviceStateStore = deviceStateStore ?? throw new ArgumentNullException(nameof(deviceStateStore));
            LocalWriteTracker = localWriteTracker ?? throw new ArgumentNullException(nameof(localWriteTracker));
            Gate = gate ?? throw new ArgumentNullException(nameof(gate));
            Scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
            CloudGateway = cloudGateway ?? throw new ArgumentNullException(nameof(cloudGateway));
            RestoreListeners = restoreListeners ?? throw new ArgumentNullException(nameof(restoreListeners));
            DeactivationListeners = deactivationListeners ?? throw new ArgumentNullException(nameof(deactivationListeners));
            ListenerGuard = listenerGuard ?? throw new ArgumentNullException(nameof(listenerGuard));
            MutationDetector = mutationDetector ?? throw new ArgumentNullException(nameof(mutationDetector));
            Readiness = readiness ?? throw new ArgumentNullException(nameof(readiness));
            LifetimeToken = lifetimeToken;
            FlushLocalNow = flushLocalNow ?? throw new ArgumentNullException(nameof(flushLocalNow));
            RaiseProfileActivated = raiseProfileActivated ?? throw new ArgumentNullException(nameof(raiseProfileActivated));
            RaiseRestoreCompleted = raiseRestoreCompleted ?? throw new ArgumentNullException(nameof(raiseRestoreCompleted));
            RaiseSlotLoadIssueDetected = raiseSlotLoadIssueDetected ?? throw new ArgumentNullException(nameof(raiseSlotLoadIssueDetected));
            RaiseUploadFailed = raiseUploadFailed ?? throw new ArgumentNullException(nameof(raiseUploadFailed));
            RaiseLocalWriteHealthChanged = raiseLocalWriteHealthChanged ?? throw new ArgumentNullException(nameof(raiseLocalWriteHealthChanged));
            RaiseUpdateRequired = raiseUpdateRequired ?? throw new ArgumentNullException(nameof(raiseUpdateRequired));
        }

        public SaveServiceOptions Options { get; }

        public ISaveLogger Logger { get; }

        public ISaveClock Clock { get; }

        public ISaveStorage Storage { get; }

        public ICloudSaveProvider Provider { get; }

        public SaveSlotRegistry Registry { get; }

        public SaveJson Json { get; }

        public SlotStore SlotStore { get; }

        public SyncStateStore SyncStateStore { get; }

        public DeviceStateStore DeviceStateStore { get; }

        public LocalWriteTracker LocalWriteTracker { get; }

        public OperationGate Gate { get; }

        public SaveScheduler Scheduler { get; }

        public CloudGateway CloudGateway { get; }

        public OrderedListenerDispatcher<IRestoreListener> RestoreListeners { get; }

        public OrderedListenerDispatcher<IProfileDeactivatingListener> DeactivationListeners { get; }

        public ListenerReentrancyGuard ListenerGuard { get; }

        public MutationDetector MutationDetector { get; }

        public ReadinessSignal Readiness { get; }

        /// <summary>Cancelled by SaveService.Dispose; epoch CTSs link to it.</summary>
        public CancellationToken LifetimeToken { get; }

        /// <summary>SaveService.FlushLocalNow: synchronous local flush that ignores backoff.</summary>
        public Func<LocalFlushResult> FlushLocalNow { get; }

        public Action<ProfileActivationResult> RaiseProfileActivated { get; }

        public Action<RestoreReport> RaiseRestoreCompleted { get; }

        public Action<SlotLoadIssue> RaiseSlotLoadIssueDetected { get; }

        public Action<UploadFailure> RaiseUploadFailed { get; }

        public Action<LocalWriteHealth> RaiseLocalWriteHealthChanged { get; }

        public Action<UpdateRequiredInfo> RaiseUpdateRequired { get; }
    }
}
