using System.Threading;
using Cysharp.Threading.Tasks;

namespace Ecanakli.SaveSystem.DependencyInjection.Tests
{
    /// <summary>Records every dispatch call; used to prove SaveListenerRegistrar added/removed it.</summary>
    internal sealed class RecordingRestoreListener : IRestoreListener
    {
        public int Order => 0;

        public int CallCount { get; private set; }

        public RestoreReport LastReport { get; private set; }

        public UniTask OnRestoredAsync(RestoreReport report, CancellationToken ct)
        {
            CallCount++;
            LastReport = report;
            return UniTask.CompletedTask;
        }
    }

    /// <summary>Records every dispatch call; used to prove SaveListenerRegistrar added/removed it.</summary>
    internal sealed class RecordingDeactivationListener : IProfileDeactivatingListener
    {
        public int Order => 0;

        public int CallCount { get; private set; }

        public UniTask OnProfileDeactivatingAsync(ProfileDeactivation context, CancellationToken ct)
        {
            CallCount++;
            return UniTask.CompletedTask;
        }
    }
}
