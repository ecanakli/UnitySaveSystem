using System;
using UnityEngine.Scripting;
using Zenject;

namespace Ecanakli.SaveSystem.DependencyInjection
{
    /// <summary>
    /// Optional: declares the 7 save signals and binds the bridge that mirrors ISaveService onto them.
    /// Requires SignalBusInstaller.Install to have already run. Install after SaveServiceInstaller.
    /// </summary>
    public sealed class SaveSignalsInstaller : Installer<SaveSignalsInstaller>
    {
        [Preserve]
        public SaveSignalsInstaller()
        {
        }

        public override void InstallBindings()
        {
            if (!Container.HasBinding<SignalBus>())
            {
                throw new InvalidOperationException("Call SignalBusInstaller.Install before SaveSignalsInstaller.Install");
            }

            Container.DeclareSignal<SaveProfileActivatedSignal>().OptionalSubscriber();
            Container.DeclareSignal<SaveRestoreCompletedSignal>().OptionalSubscriber();
            Container.DeclareSignal<SaveSlotLoadIssueDetectedSignal>().OptionalSubscriber();
            Container.DeclareSignal<SaveUploadFailedSignal>().OptionalSubscriber();
            Container.DeclareSignal<SaveLocalWriteHealthChangedSignal>().OptionalSubscriber();
            Container.DeclareSignal<SaveUpdateRequiredSignal>().OptionalSubscriber();
            Container.DeclareSignal<FlushSavesRequest>().OptionalSubscriber();

            Container.Bind(typeof(IInitializable), typeof(IDisposable))
                .To<SaveSignalBridge>()
                .FromMethod(CreateBridge)
                .AsCached()
                .NonLazy();
            Container.BindExecutionOrder<SaveSignalBridge>(SaveServiceInstaller.EarlyExecutionOrder);
        }

        private static SaveSignalBridge CreateBridge(InjectContext ctx)
        {
            DiContainer container = ctx.Container;
            ISaveService saveService = container.Resolve<ISaveService>();
            SignalBus signalBus = container.Resolve<SignalBus>();
            SaveServiceOptions options = container.Resolve<SaveServiceOptions>();
            return new SaveSignalBridge(saveService, signalBus, options.Logger);
        }
    }
}
