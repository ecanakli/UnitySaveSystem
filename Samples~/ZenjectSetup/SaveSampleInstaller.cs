using Ecanakli.SaveSystem;
using Ecanakli.SaveSystem.DependencyInjection;
using Zenject;

namespace Ecanakli.SaveSystem.Samples.DependencyInjection
{
    /// <summary>Wires the save service, its signals and the sample's settings slot. Install from your project/scene installer.</summary>
    public sealed class SaveSampleInstaller : MonoInstaller
    {
        public override void InstallBindings()
        {
            SignalBusInstaller.Install(Container);
            SaveServiceInstaller.Install(Container, new SaveServiceOptions());
            SaveSignalsInstaller.Install(Container); // optional: only needed for signal-based consumers

            Container.BindSaveSlot<SettingsSlot>();

            Container.BindInterfacesTo<SettingsRestoreLogger>().AsSingle().NonLazy();
            Container.BindInterfacesTo<SaveSampleBoot>().AsSingle().NonLazy();
        }
    }
}
