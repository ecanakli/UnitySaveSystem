using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Ecanakli.SaveSystem;
using UnityEngine;
using UnityEngine.Scripting;
using Zenject;

namespace Ecanakli.SaveSystem.Samples.DependencyInjection
{
    /// <summary>
    /// Boots the save service. Runs at Zenject's default execution order, after SaveServiceInstaller's
    /// registrar and lifecycle host (bound at EarlyExecutionOrder), so listeners are already registered
    /// and the pause/quit driver is already attached before InitializeAsync is awaited.
    /// Owns a CancellationTokenSource for its own async work; cancelled and disposed in Dispose.
    /// </summary>
    public sealed class SaveSampleBoot : IInitializable, IDisposable
    {
        private readonly ISaveService _saveService;
        private readonly CancellationTokenSource _lifetimeCts = new CancellationTokenSource();

        // Constructed through Zenject reflection; [Preserve] keeps the constructor from being stripped by IL2CPP.
        [Preserve]
        public SaveSampleBoot(ISaveService saveService)
        {
            _saveService = saveService ?? throw new ArgumentNullException(nameof(saveService));
        }

        public void Initialize()
        {
            InitializeSaveServiceAsync(_lifetimeCts.Token).Forget(LogException);
        }

        public void Dispose()
        {
            _lifetimeCts.Cancel();
            _lifetimeCts.Dispose();
        }

        private async UniTask InitializeSaveServiceAsync(CancellationToken ct)
        {
            InitializeResult result = await _saveService.InitializeAsync(ct);
            if (!result.IsSuccess)
            {
                Debug.LogError("[SaveSampleBoot] InitializeAsync failed: " + result.Error);
            }
        }

        // Passed to Forget as the exception handler; cancellation from Dispose is expected and not an error.
        private static void LogException(Exception exception)
        {
            if (exception is OperationCanceledException)
            {
                return;
            }

            Debug.LogException(exception);
        }
    }
}
