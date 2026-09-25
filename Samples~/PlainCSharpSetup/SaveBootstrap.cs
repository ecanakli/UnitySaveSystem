using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Ecanakli.SaveSystem;
using UnityEngine;

namespace Ecanakli.SaveSystem.Samples.PlainCSharp
{
    /// <summary>
    /// Composes the save service without any DI framework. Attach to one persistent GameObject that
    /// survives for the lifetime of the app (or make its GameObject DontDestroyOnLoad yourself).
    /// Owns the service, the lifecycle driver handle and the CancellationTokenSource used for its own async work.
    /// </summary>
    public sealed class SaveBootstrap : MonoBehaviour
    {
        private SettingsSlot _settingsSlot;
        private SettingsRestoreLogger _restoreLogger;
        private ISaveService _saveService;
        private IDisposable _lifecycleHandle;
        private CancellationTokenSource _lifetimeCts;

        private void Awake()
        {
            _lifetimeCts = new CancellationTokenSource();

            _settingsSlot = new SettingsSlot();
            _restoreLogger = new SettingsRestoreLogger();

            var options = new SaveServiceOptions();
            var storage = new AtomicFileStorage(options.RootDirectory);
            _saveService = new SaveService(options, storage, NullCloudSaveProvider.Instance, new SaveSlot[] { _settingsSlot });

            // Must be added before InitializeAsync: a listener added afterward gets no catch-up call.
            _saveService.AddRestoreListener(_restoreLogger);

            // Forwards pause/focus/quit to the service; dispose the handle to flush and detach.
            _lifecycleHandle = SaveLifecycleDriver.Attach(_saveService);
        }

        private void Start()
        {
            // Start cannot be async itself; Forget's exception handler owns unhandled-error logging below.
            InitializeSaveServiceAsync(_lifetimeCts.Token).Forget(LogUnhandledException);
        }

        private async UniTask InitializeSaveServiceAsync(CancellationToken ct)
        {
            InitializeResult result = await _saveService.InitializeAsync(ct);
            if (!result.IsSuccess)
            {
                Debug.LogError("[SaveBootstrap] InitializeAsync failed: " + result.Error);
            }
        }

        /// <summary>Wire this to a UI slider's OnValueChanged; shows Read, Mutate and SaveNowAsync together.</summary>
        public void OnMusicVolumeChanged(float newVolume)
        {
            float previous = _settingsSlot.Read(static data => data.MusicVolume);

            bool applied = _settingsSlot.Mutate(newVolume, static (data, value) => data.MusicVolume = value);
            if (!applied)
            {
                // Refused: slot not Ready yet, a profile switch is in progress, or the service is disposed.
                Debug.LogWarning("[SaveBootstrap] Mutate refused; music volume change was not applied.");
                return;
            }

            Debug.Log("[SaveBootstrap] Music volume " + previous + " -> " + newVolume);
            SaveSettingsNowAsync(_lifetimeCts.Token).Forget(LogUnhandledException);
        }

        // Purchases and rewards must await SaveNowAsync; this sample just logs the durable result.
        private async UniTask SaveSettingsNowAsync(CancellationToken ct)
        {
            SaveResult result = await _settingsSlot.SaveNowAsync(ct);
            if (!result.IsSuccess)
            {
                Debug.LogWarning("[SaveBootstrap] SaveNowAsync failed: " + result.Error);
            }
        }

        private void OnDestroy()
        {
            _lifetimeCts.Cancel();
            _lifetimeCts.Dispose();

            _saveService.RemoveRestoreListener(_restoreLogger);

            // Flushes local writes synchronously and detaches the pause/quit driver.
            _lifecycleHandle?.Dispose();
            _lifecycleHandle = null;

            _saveService.Dispose();
        }

        // Shared exception handler for every Forget call in this class; cancellation from OnDestroy is expected.
        private static void LogUnhandledException(Exception exception)
        {
            if (exception is OperationCanceledException)
            {
                return;
            }

            Debug.LogException(exception);
        }
    }
}
