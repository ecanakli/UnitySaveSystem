using System.Threading;
using Cysharp.Threading.Tasks;
using Ecanakli.SaveSystem;
using UnityEngine;

namespace Ecanakli.SaveSystem.Samples.PlainCSharp
{
    /// <summary>Logs every restore dispatch and shows how to branch on RestoreTrigger.</summary>
    public sealed class SettingsRestoreLogger : IRestoreListener
    {
        // Ties resolve by registration order; a single listener can use any constant.
        public int Order => 0;

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
    }
}
