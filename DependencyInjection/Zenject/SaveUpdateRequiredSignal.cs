using Ecanakli.SaveSystem;

namespace Ecanakli.SaveSystem.DependencyInjection
{
    /// <summary>Mirrors ISaveService.UpdateRequired; raised once per slot, source and epoch.</summary>
    public sealed class SaveUpdateRequiredSignal
    {
        public SaveUpdateRequiredSignal(UpdateRequiredInfo info)
        {
            Info = info;
        }

        public UpdateRequiredInfo Info { get; }
    }
}
