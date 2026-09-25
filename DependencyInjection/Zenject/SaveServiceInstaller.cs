using System;
using System.Collections.Generic;
using UnityEngine.Scripting;
using Zenject;

namespace Ecanakli.SaveSystem.DependencyInjection
{
    /// <summary>
    /// Core Zenject bindings for the save system. Works without SignalBus; add SaveSignalsInstaller separately for signals.
    /// </summary>
    public sealed class SaveServiceInstaller : Installer<SaveServiceOptions, SaveServiceInstaller>
    {
        /// <summary>Execution order shared by the registrar, the lifecycle host and (via SaveSignalsInstaller) the signal bridge.</summary>
        internal const int EarlyExecutionOrder = -10000;

        private readonly SaveServiceOptions _options;

        [Preserve]
        public SaveServiceInstaller(SaveServiceOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public override void InstallBindings()
        {
            // Options arrive only through Install; a game binding of its own would otherwise surface as an opaque Zenject assert
            if (Container.HasBindingId(typeof(SaveServiceOptions), null, InjectSources.Local))
            {
                throw new InvalidOperationException(
                    "SaveServiceOptions is already bound in this container: either SaveServiceInstaller.Install ran here twice, or the options were bound directly. "
                    + "Install once per container and pass the options only through SaveServiceInstaller.Install.");
            }

            Container.Bind<SaveServiceOptions>().FromInstance(_options).AsSingle();

            // IfNotBound: a game that binds its own ISaveStorage before installing keeps it (F6);
            // TryResolve<ICloudSaveProvider>() below already treats the cloud provider as overridable the same way.
            // AsCached, not AsSingle: Zenject 6+ refuses AsSingle twice for one contract, and it asserts while
            // finalizing before IfNotBound can skip this binding, so a game binding its own storage would crash
            Container.Bind<ISaveStorage>().FromMethod(CreateStorage).AsCached().IfNotBound();

            // To<SaveService>() is load-bearing: without a concrete type Zenject caches one provider per contract,
            // so ISaveService and IDisposable would resolve to two separate services over the same save folder.
            Container.Bind(typeof(ISaveService), typeof(IDisposable))
                .To<SaveService>()
                .FromMethod(CreateSaveService)
                .AsCached();

            // Disposal runs highest order first, so the lowest order shuts down last: after the lifecycle host's final flush
            Container.BindExecutionOrder<SaveService>(EarlyExecutionOrder - 1);

            Container.Bind(typeof(IInitializable), typeof(IDisposable))
                .To<SaveListenerRegistrar>()
                .FromMethod(CreateListenerRegistrar)
                .AsCached()
                .NonLazy();
            Container.BindExecutionOrder<SaveListenerRegistrar>(EarlyExecutionOrder);

            Container.Bind(typeof(IInitializable), typeof(IDisposable))
                .To<SaveLifecycleHost>()
                .FromMethod(CreateLifecycleHost)
                .AsCached()
                .NonLazy();
            Container.BindExecutionOrder<SaveLifecycleHost>(EarlyExecutionOrder);
        }

        // Uses the installer's own _options field directly (F7): a container resolve here could pick a different
        // SaveServiceOptions instance in a subcontainer, and was needless indirection now that IfNotBound (F6)
        // means ISaveStorage is not always built by this installer either.
        private AtomicFileStorage CreateStorage(InjectContext ctx)
        {
            return new AtomicFileStorage(_options.RootDirectory);
        }

        // The installer's own options and container: ctx.Container is whichever container asked first, possibly a child
        private SaveService CreateSaveService(InjectContext ctx)
        {
            ISaveStorage storage = Container.Resolve<ISaveStorage>();
            ICloudSaveProvider provider = Container.TryResolve<ICloudSaveProvider>() ?? NullCloudSaveProvider.Instance;
            List<SaveSlot> slots = Container.ResolveAll<SaveSlot>();
            return new SaveService(_options, storage, provider, slots);
        }

        private static SaveListenerRegistrar CreateListenerRegistrar(InjectContext ctx)
        {
            DiContainer container = ctx.Container;
            ISaveService saveService = container.Resolve<ISaveService>();
            List<IRestoreListener> restoreListeners = container.ResolveAll<IRestoreListener>();
            List<IProfileDeactivatingListener> deactivationListeners = container.ResolveAll<IProfileDeactivatingListener>();
            return new SaveListenerRegistrar(saveService, restoreListeners, deactivationListeners);
        }

        private static SaveLifecycleHost CreateLifecycleHost(InjectContext ctx)
        {
            return new SaveLifecycleHost(ctx.Container.Resolve<ISaveService>());
        }
    }
}
