using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Ecanakli.SaveSystem;
using Ecanakli.SaveSystem.DependencyInjection;
using UnityEngine;
using UnityEngine.Scripting;
using Zenject;

namespace Ecanakli.SaveSystem.Samples.DependencyInjection
{
    /// <summary>
    /// Logs every restore dispatch (IRestoreListener) and every permanent upload failure (SaveUploadFailedSignal).
    /// Subscribes to the signal in Initialize and unsubscribes in Dispose, matching the package's own bridge.
    /// </summary>
    public sealed class SettingsRestoreLogger : IRestoreListener, IInitializable, IDisposable
    {
        private readonly SignalBus _signalBus;

        // Constructed through Zenject reflection; [Preserve] keeps the constructor from being stripped by IL2CPP.
        [Preserve]
        public SettingsRestoreLogger(SignalBus signalBus)
        {
            _signalBus = signalBus ?? throw new ArgumentNullException(nameof(signalBus));
        }

        // Ties resolve by registration order; a single listener can use any constant.
        public int Order => 0;

        public void Initialize()
        {
            _signalBus.Subscribe<SaveUploadFailedSignal>(OnUploadFailed);
        }

        public void Dispose()
        {
            _signalBus.Unsubscribe<SaveUploadFailedSignal>(OnUploadFailed);
        }

        public UniTask OnRestoredAsync(RestoreReport report, CancellationToken ct)
        {
            switch (report.Trigger)
            {
                case RestoreTrigger.ProfileActivated:
                    // Local slots just loaded for report.Profile; rebuild caches/views here, never replay earned effects.
                    Debug.Log("[SettingsRestoreLogger] Profile " + report.Profile + " loaded from local storage, completeness=" + report.Completeness);
                    break;
                case RestoreTrigger.CloudRestore:
                    // A cloud restore or single-slot conflict reconcile just ran.
                    Debug.Log("[SettingsRestoreLogger] Cloud restore for " + report.Profile + " completed, completeness=" + report.Completeness);
                    break;
            }

            return UniTask.CompletedTask;
        }

        private void OnUploadFailed(SaveUploadFailedSignal signal)
        {
            Debug.LogWarning("[SettingsRestoreLogger] Upload failed: " + signal.Failure);
        }
    }
}
