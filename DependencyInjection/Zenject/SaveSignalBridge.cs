using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Zenject;

namespace Ecanakli.SaveSystem.DependencyInjection
{
    /// <summary>
    /// Mirrors every ISaveService fact event onto its signal and forwards FlushSavesRequest to FlushAsync.
    /// Subscribes in Initialize, unsubscribes in Dispose. Owns a CTS for its own flush calls, cancelled and
    /// disposed in Dispose. Never uses BindSignal().ToMethod().
    /// </summary>
    internal sealed class SaveSignalBridge : IInitializable, IDisposable
    {
        private readonly ISaveService _saveService;
        private readonly SignalBus _signalBus;
        private readonly ISaveLogger _logger;
        private readonly CancellationTokenSource _flushCts = new CancellationTokenSource();
        private bool _disposed;

        internal SaveSignalBridge(ISaveService saveService, SignalBus signalBus, ISaveLogger logger)
        {
            _saveService = saveService ?? throw new ArgumentNullException(nameof(saveService));
            _signalBus = signalBus ?? throw new ArgumentNullException(nameof(signalBus));
            _logger = logger;
        }

        public void Initialize()
        {
            _saveService.ProfileActivated += OnProfileActivated;
            _saveService.RestoreCompleted += OnRestoreCompleted;
            _saveService.SlotLoadIssueDetected += OnSlotLoadIssueDetected;
            _saveService.UploadFailed += OnUploadFailed;
            _saveService.LocalWriteHealthChanged += OnLocalWriteHealthChanged;
            _saveService.UpdateRequired += OnUpdateRequired;
            _signalBus.Subscribe<FlushSavesRequest>(OnFlushSavesRequest);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            _saveService.ProfileActivated -= OnProfileActivated;
            _saveService.RestoreCompleted -= OnRestoreCompleted;
            _saveService.SlotLoadIssueDetected -= OnSlotLoadIssueDetected;
            _saveService.UploadFailed -= OnUploadFailed;
            _saveService.LocalWriteHealthChanged -= OnLocalWriteHealthChanged;
            _saveService.UpdateRequired -= OnUpdateRequired;
            _signalBus.Unsubscribe<FlushSavesRequest>(OnFlushSavesRequest);

            _flushCts.Cancel();
            _flushCts.Dispose();
        }

        private void OnProfileActivated(ProfileActivationResult result)
        {
            _signalBus.Fire(new SaveProfileActivatedSignal(result));
        }

        private void OnRestoreCompleted(RestoreReport report)
        {
            _signalBus.Fire(new SaveRestoreCompletedSignal(report));
        }

        private void OnSlotLoadIssueDetected(SlotLoadIssue issue)
        {
            _signalBus.Fire(new SaveSlotLoadIssueDetectedSignal(issue));
        }

        private void OnUploadFailed(UploadFailure failure)
        {
            _signalBus.Fire(new SaveUploadFailedSignal(failure));
        }

        private void OnLocalWriteHealthChanged(LocalWriteHealth health)
        {
            _signalBus.Fire(new SaveLocalWriteHealthChangedSignal(health));
        }

        private void OnUpdateRequired(UpdateRequiredInfo info)
        {
            _signalBus.Fire(new SaveUpdateRequiredSignal(info));
        }

        // Fire-and-forget by design: FlushSavesRequest is a request signal with no result to return to the caller.
        // Every exception is caught inside RunFlushAsync so nothing escapes the void signal handler.
        private void OnFlushSavesRequest()
        {
            if (_disposed)
            {
                return;
            }

            RunFlushAsync(_flushCts.Token).Forget();
        }

        private async UniTaskVoid RunFlushAsync(CancellationToken ct)
        {
            try
            {
                await _saveService.FlushAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // Expected when Dispose cancels the bridge's own CTS while a flush is in flight
            }
            catch (Exception exception)
            {
                _logger?.Error("[SaveSystem] FlushSavesRequest handling threw.", exception);
            }
        }
    }
}
