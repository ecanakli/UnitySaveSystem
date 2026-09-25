using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Ecanakli.SaveSystem
{
    /// <summary>
    /// Cloud restore (01 5.4 with 04a A3 deltas, N1, N4, 04 R1) and the scheduled single-slot conflict reconcile (02 H).
    /// Main thread only. Only OperationCanceledException for the caller's own token escapes; everything else is in the report.
    /// </summary>
    internal sealed partial class RestoreOperation : IDisposable
    {
        private const string DispatchTrigger = "CloudRestore";

        private static readonly Func<IRestoreListener, RestoreReport, CancellationToken, UniTask> InvokeRestored =
            static (listener, report, ct) => listener.OnRestoredAsync(report, ct);

        private readonly SaveContext _context;
        private readonly ProfileSession _session;
        private readonly List<SaveSlot> _pendingSlotReconciles = new List<SaveSlot>();
        private UniTask<RestoreReport> _inFlight;
        private bool _running;
        private long _pendingEpoch = -1;
        private bool _slotLoopRunning;
        private bool _disposed;

        public RestoreOperation(SaveContext context, ProfileSession session)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _session = session ?? throw new ArgumentNullException(nameof(session));
        }

        /// <summary>True while a full restore is in flight; later callers join it.</summary>
        public bool IsRunning => _running;

        /// <summary>
        /// Full restore of the active account's non-LocalOnly Profile slots. Joins an in-flight restore; each caller waits with its own
        /// token (AttachExternalCancellation) while the shared run continues. Refused from hooks and listener dispatch.
        /// </summary>
        public UniTask<RestoreReport> RestoreAsync(CancellationToken ct)
        {
            RestoreReport refusal = CheckEntry();
            if (refusal != null)
            {
                return UniTask.FromResult(refusal);
            }

            if (ct.IsCancellationRequested)
            {
                return UniTask.FromCanceled<RestoreReport>(ct);
            }

            if (_running)
            {
                LogVerbose("RestoreAsync joined the restore in flight.");
            }
            else
            {
                _inFlight = RunFullAsync(ct).Preserve();
            }

            return _inFlight.AttachExternalCancellation(ct);
        }

        /// <summary>
        /// Schedules a single-slot reconcile after a write Conflict. Runs after a player-loop yield, never inline, so the
        /// uploader's gate is released first. Requests are dropped when the epoch changes.
        /// </summary>
        public void RequestSlotReconcile(SaveSlot slot)
        {
            if (slot == null || _disposed || _session.IsDisposed)
            {
                return;
            }

            long epoch = _session.Epoch;
            if (_pendingEpoch != epoch)
            {
                _pendingSlotReconciles.Clear();
                _pendingEpoch = epoch;
            }

            if (!_pendingSlotReconciles.Contains(slot))
            {
                _pendingSlotReconciles.Add(slot);
            }

            if (_slotLoopRunning)
            {
                return;
            }

            _slotLoopRunning = true;
            RunSlotReconcilesAsync(epoch, _session.EpochToken).Forget();
        }

        /// <summary>Stops scheduling; an in-flight run finishes against disposed collaborators and raises nothing.</summary>
        public void Dispose()
        {
            _disposed = true;
            _pendingSlotReconciles.Clear();
        }

        private async UniTask<RestoreReport> RunFullAsync(CancellationToken ct)
        {
            _running = true;
            try
            {
                return await RunSafeAsync(null, ct);
            }
            finally
            {
                _running = false;
            }
        }

        private async UniTaskVoid RunSlotReconcilesAsync(long epoch, CancellationToken epochToken)
        {
            try
            {
                bool canceled = await UniTask.Yield(PlayerLoopTiming.Update, epochToken).SuppressCancellationThrow();
                while (!canceled && !_disposed && !_session.IsDisposed && _session.Epoch == epoch && _pendingEpoch == epoch
                       && _pendingSlotReconciles.Count > 0)
                {
                    var slots = new List<SaveSlot>(_pendingSlotReconciles);
                    _pendingSlotReconciles.Clear();
                    LogVerbose("Single-slot reconcile for " + slots.Count + " slot(s).");
                    await RunSafeAsync(slots, CancellationToken.None);
                }
            }
            catch (Exception exception)
            {
                _context.Logger.Error("[SaveSystem] Scheduled single-slot reconcile failed.", exception);
            }
            finally
            {
                _slotLoopRunning = false;
            }

            // Requests for a newer epoch that arrived while this loop was ending
            if (!_disposed && !_session.IsDisposed && _pendingSlotReconciles.Count > 0 && _pendingEpoch == _session.Epoch && _pendingEpoch != epoch)
            {
                _slotLoopRunning = true;
                RunSlotReconcilesAsync(_session.Epoch, _session.EpochToken).Forget();
            }
        }

        private async UniTask<RestoreReport> RunSafeAsync(IReadOnlyList<SaveSlot> onlySlots, CancellationToken callerCt)
        {
            try
            {
                return await RunCoreAsync(onlySlots, callerCt);
            }
            catch (Exception exception)
            {
                _context.Logger.Error("[SaveSystem] Restore failed unexpectedly.", exception);
                var report = new RestoreReport(
                    RestoreTrigger.CloudRestore, SaveStatus.Failed, new SaveError(SaveErrorCode.Unknown, "Restore failed: " + exception.Message, exception),
                    _session.ActiveProfile, RestoreCompleteness.Failed, null, null, false);
                if (!_disposed && !_session.IsDisposed)
                {
                    _context.RaiseRestoreCompleted(report);
                }

                return report;
            }
        }

        private async UniTask<RestoreReport> RunCoreAsync(IReadOnlyList<SaveSlot> onlySlots, CancellationToken callerCt)
        {
            var run = new RestoreRun(_session.ActiveProfile, _session.Epoch, _session.EpochToken, onlySlots);
            if (!_session.IsInitialized)
            {
                run.Fail(SaveErrorCode.NotInitialized, "RestoreAsync requires InitializeAsync first.");
                return await FinishAsync(run, callerCt);
            }

            OperationGate.Releaser releaser = default;
            bool gateHeld = false;
            try
            {
                using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(callerCt, run.EpochToken))
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

                if (gateHeld)
                {
                    await ExecuteUnderGateAsync(run, callerCt);
                }
                else
                {
                    SetAborted(run, callerCt);
                }
            }
            finally
            {
                // Step 10: the gate is released before uploads are queued and listeners run
                if (gateHeld)
                {
                    releaser.Dispose();
                }
            }

            return await FinishAsync(run, callerCt);
        }

        private async UniTask ExecuteUnderGateAsync(RestoreRun run, CancellationToken callerCt)
        {
            if (IsDisposed())
            {
                run.Fail(SaveErrorCode.Disposed, "The save service was disposed during the restore.");
                run.Raise = false;
                return;
            }

            if (_session.Epoch != run.Epoch || _session.ActiveProfile != run.Profile)
            {
                run.Fail(SaveErrorCode.Superseded, "The active profile changed before the restore started.");
                return;
            }

            if (!CheckPreconditions(run))
            {
                return;
            }

            string directory = _session.ActiveProfileDirectory;
            ReloadIoErrorSlots(run, directory);
            SelectSlots(run);
            if (run.IsSlotRun && run.Work.Count == 0)
            {
                // Already reconciled by a full restore in between
                run.Raise = false;
                return;
            }

            run.Dispatch = true;
            if (!await FetchAsync(run, callerCt))
            {
                return;
            }

            ClassifyAndDecide(run, directory);
            FlushBeforeApply(run);
            if (!PassCheckpoint(run, callerCt))
            {
                return;
            }

            ApplyAll(run, directory);
            PersistLocal(run, directory);
            await DeleteTombstonedCloudValuesAsync(run);
        }

        // Step 3: Guest/Local -> ProfileNotCloudBacked; signed out -> NotSignedIn; other account -> AccountMismatch
        private bool CheckPreconditions(RestoreRun run)
        {
            if (!run.Profile.IsCloudBacked)
            {
                run.Fail(SaveErrorCode.ProfileNotCloudBacked, run.Profile + " is not cloud backed.");
                return false;
            }

            string signedIn = ReadSignedInAccountId();
            if (string.IsNullOrEmpty(signedIn))
            {
                run.Fail(SaveErrorCode.NotSignedIn, "The cloud provider is not signed in.");
                return false;
            }

            if (!string.Equals(signedIn, run.Profile.AccountId, StringComparison.Ordinal))
            {
                run.Fail(SaveErrorCode.AccountMismatch, "The signed-in account does not match " + run.Profile + ".");
                return false;
            }

            return true;
        }

        // Step 7 plus N1: no await between this check and the synchronous apply
        private bool PassCheckpoint(RestoreRun run, CancellationToken callerCt)
        {
            if (callerCt.IsCancellationRequested)
            {
                run.Cancel();
                return false;
            }

            if (IsDisposed() || _session.Epoch != run.Epoch || _session.ActiveProfile != run.Profile)
            {
                FailAfterFetch(run);
                return false;
            }

            if (!string.Equals(ReadSignedInAccountId(), run.Profile.AccountId, StringComparison.Ordinal))
            {
                run.FailAllAccountChanged(SaveErrorCode.AccountMismatch, "The signed-in account changed during the cloud fetch; nothing was applied.");
                return false;
            }

            return true;
        }

        // The epoch token is linked to the lifetime token, so Dispose and a profile change arrive the same way; Dispose raises nothing
        private void FailAfterFetch(RestoreRun run)
        {
            if (IsDisposed())
            {
                run.FailAllAccountChanged(SaveErrorCode.Disposed, "The save service was disposed during the cloud fetch; nothing was applied.");
                run.Raise = false;
                return;
            }

            run.FailAllAccountChanged(SaveErrorCode.Superseded, "The active profile changed during the cloud fetch; nothing was applied.");
        }

        private void SetAborted(RestoreRun run, CancellationToken callerCt)
        {
            if (IsDisposed())
            {
                run.Fail(SaveErrorCode.Disposed, "The save service was disposed during the restore.");
                run.Raise = false;
            }
            else if (callerCt.IsCancellationRequested)
            {
                run.Cancel();
            }
            else
            {
                run.Fail(SaveErrorCode.Superseded, "The active profile changed while the restore waited for the gate.");
            }
        }

        private async UniTask<RestoreReport> FinishAsync(RestoreRun run, CancellationToken callerCt)
        {
            if (run.Status == SaveStatus.Success && !IsDisposed() && _session.Epoch == run.Epoch)
            {
                QueueUploads(run);
            }

            RaiseCollectedEvents(run);
            RestoreReport report = BuildReport(run);
            if (run.Status == SaveStatus.Success && run.Dispatch)
            {
                report = await DispatchAsync(report, run, callerCt);
            }

            if (_context.Logger.IsVerboseEnabled)
            {
                _context.Logger.Verbose("[SaveSystem] " + report);
            }

            if (run.Raise && !IsDisposed())
            {
                _context.RaiseRestoreCompleted(report);
            }

            return report;
        }

        private void QueueUploads(RestoreRun run)
        {
            for (int i = 0; i < run.Work.Count; i++)
            {
                SlotWork work = run.Work[i];
                if (work.QueueUpload && work.IsSuccess)
                {
                    _context.Scheduler.MarkLocalDirty(work.Slot);
                    _context.Scheduler.MarkCloudDirty(work.Slot);
                }
            }
        }

        private void RaiseCollectedEvents(RestoreRun run)
        {
            if (IsDisposed())
            {
                return;
            }

            for (int i = 0; i < run.Issues.Count; i++)
            {
                _context.RaiseSlotLoadIssueDetected(run.Issues[i]);
            }

            for (int i = 0; i < run.Updates.Count; i++)
            {
                _context.RaiseUpdateRequired(run.Updates[i]);
            }
        }

        private static RestoreReport BuildReport(RestoreRun run)
        {
            var slots = new List<SlotRestoreResult>(run.Work.Count);
            for (int i = 0; i < run.Work.Count; i++)
            {
                slots.Add(run.Work[i].ToResult());
            }

            RestoreCompleteness completeness;
            if (run.Status != SaveStatus.Success)
            {
                completeness = RestoreCompleteness.Failed;
            }
            else if (slots.Count == 0)
            {
                completeness = RestoreCompleteness.NothingToRestore;
            }
            else
            {
                completeness = RestoreReport.ComputeRestoreCompleteness(slots);
            }

            return new RestoreReport(RestoreTrigger.CloudRestore, run.Status, run.Error, run.Profile, completeness, slots, null, run.RequiresAppUpdate);
        }

        // Step 11: outside the gate, caller token linked with the epoch token
        private async UniTask<RestoreReport> DispatchAsync(RestoreReport report, RestoreRun run, CancellationToken callerCt)
        {
            if (IsDisposed() || _context.RestoreListeners.Count == 0)
            {
                return report;
            }

            using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(callerCt, run.EpochToken))
            {
                ListenerDispatchResult dispatched = await _context.RestoreListeners.DispatchAsync(report, DispatchTrigger, InvokeRestored, null, linked.Token);
                if (dispatched.WasCanceled || dispatched.Failures.Count > 0)
                {
                    return report.WithListenerOutcome(dispatched.Failures, dispatched.WasCanceled);
                }
            }

            return report;
        }

        private RestoreReport CheckEntry()
        {
            const string operation = "RestoreAsync";
            if (IsDisposed())
            {
                return CreateRefusal(SaveErrorCode.Disposed, operation + " was called after Dispose.");
            }

            if (HookScope.RefuseIfActive(_context.Logger, operation))
            {
                return CreateRefusal(SaveErrorCode.CalledFromHook, operation + " was called from a slot hook.");
            }

            if (_context.ListenerGuard.RefuseIfDispatching(_context.Logger, operation))
            {
                return CreateRefusal(SaveErrorCode.ReentrantCall, operation + " was called during listener dispatch.");
            }

            return null;
        }

        private RestoreReport CreateRefusal(SaveErrorCode code, string message)
        {
            return new RestoreReport(
                RestoreTrigger.CloudRestore, SaveStatus.Failed, new SaveError(code, message), _session.ActiveProfile, RestoreCompleteness.Failed, null, null,
                false);
        }

        private string ReadSignedInAccountId()
        {
            try
            {
                return _context.CloudGateway.SignedInAccountId;
            }
            catch (Exception exception)
            {
                _context.Logger.Error("[SaveSystem] ICloudSaveProvider.SignedInAccountId threw; treated as signed out.", exception);
                return null;
            }
        }

        private bool IsDisposed()
        {
            return _disposed || _session.IsDisposed || _context.LifetimeToken.IsCancellationRequested;
        }

        private void LogVerbose(string message)
        {
            if (_context.Logger.IsVerboseEnabled)
            {
                _context.Logger.Verbose("[SaveSystem] " + message);
            }
        }

        /// <summary>State of one restore or single-slot reconcile run.</summary>
        private sealed class RestoreRun
        {
            public RestoreRun(ProfileId profile, long epoch, CancellationToken epochToken, IReadOnlyList<SaveSlot> onlySlots)
            {
                Profile = profile;
                Epoch = epoch;
                EpochToken = epochToken;
                OnlySlots = onlySlots;
            }

            public readonly List<SlotWork> Work = new List<SlotWork>();
            public readonly List<SlotLoadIssue> Issues = new List<SlotLoadIssue>();
            public readonly List<UpdateRequiredInfo> Updates = new List<UpdateRequiredInfo>();

            public ProfileId Profile { get; }

            public long Epoch { get; }

            public CancellationToken EpochToken { get; }

            /// <summary>Null for a full restore.</summary>
            public IReadOnlyList<SaveSlot> OnlySlots { get; }

            public bool IsSlotRun => OnlySlots != null;

            public SaveStatus Status { get; private set; } = SaveStatus.Success;

            public SaveError Error { get; private set; }

            public bool Dispatch { get; set; }

            public bool Raise { get; set; } = true;

            public bool RequiresAppUpdate { get; set; }

            // Precondition-style failure: no slots, no dispatch
            public void Fail(SaveErrorCode code, string message)
            {
                Status = SaveStatus.Failed;
                Error = new SaveError(code, message);
                Dispatch = false;
                Work.Clear();
            }

            // Caller cancelled before the checkpoint: nothing applied
            public void Cancel()
            {
                Status = SaveStatus.Canceled;
                Error = null;
                Dispatch = false;
                Work.Clear();
            }

            // N1: every slot Failed(AccountChanged), nothing applied, no dispatch
            public void FailAllAccountChanged(SaveErrorCode code, string message)
            {
                Status = SaveStatus.Failed;
                Error = new SaveError(code, message);
                Dispatch = false;
                RequiresAppUpdate = false;
                for (int i = 0; i < Work.Count; i++)
                {
                    Work[i].Fail(SlotRestoreFailure.AccountChanged);
                }
            }
        }
    }
}
