using Ecanakli.SaveSystem;

namespace Ecanakli.SaveSystem.DependencyInjection
{
    /// <summary>Mirrors ISaveService.LocalWriteHealthChanged (healthy to failing, failing to healthy, or kind change).</summary>
    public sealed class SaveLocalWriteHealthChangedSignal
    {
        public SaveLocalWriteHealthChangedSignal(LocalWriteHealth health)
        {
            Health = health;
        }

        public LocalWriteHealth Health { get; }
    }
}
