using Ecanakli.SaveSystem;

namespace Ecanakli.SaveSystem.DependencyInjection
{
    /// <summary>Mirrors ISaveService.RestoreCompleted.</summary>
    public sealed class SaveRestoreCompletedSignal
    {
        public SaveRestoreCompletedSignal(RestoreReport report)
        {
            Report = report;
        }

        public RestoreReport Report { get; }
    }
}
