using Ecanakli.SaveSystem;
using UnityEngine.Scripting;

namespace Ecanakli.SaveSystem.Samples.DependencyInjection
{
    /// <summary>Device-scoped, local-only settings slot; never leaves the device and never syncs to the cloud.</summary>
    public sealed class SettingsSlot : SaveSlot<SettingsData>
    {
        // Bound with Container.BindSaveSlot<SettingsSlot>(), which constructs it through Zenject reflection;
        // [Preserve] keeps the constructor from being stripped by IL2CPP.
        [Preserve]
        public SettingsSlot()
        {
        }

        public override string Key => "settings";

        public override SyncMode SyncMode => SyncMode.LocalOnly;

        public override SlotScope Scope => SlotScope.Device;

        // Runs once whenever a SettingsData instance enters memory (default, load or conflict merge).
        protected override void Normalize(SettingsData data)
        {
            data.MusicVolume = Clamp01(data.MusicVolume);
            data.SfxVolume = Clamp01(data.SfxVolume);
            if (string.IsNullOrEmpty(data.Language))
            {
                data.Language = "en";
            }
        }

        private static float Clamp01(float value)
        {
            if (value < 0f)
            {
                return 0f;
            }

            return value > 1f ? 1f : value;
        }
    }
}
