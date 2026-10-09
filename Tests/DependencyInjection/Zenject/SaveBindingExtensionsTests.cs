using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using Zenject;

namespace Ecanakli.SaveSystem.DependencyInjection.Tests
{
    [TestFixture]
    internal sealed class SaveBindingExtensionsTests : ZenjectSaveTestBase
    {
        // Regression for S6: with the old AsSingle body this combination threw
        // "Attempted to use AsSingle multiple times for type 'TestListenerSlot'" once bindings were flushed
        // (bindings finalize lazily, so the Resolve call below is what actually triggers the old assertion).
        [Test]
        public void BindSaveSlot_PlusConsumerOwnBindInterfacesToAsCached_DoesNotThrow()
        {
            Assert.DoesNotThrow(() =>
            {
                Container.BindSaveSlot<TestListenerSlot>();
                Container.BindInterfacesTo<TestListenerSlot>().AsCached();
                Container.Resolve<TestListenerSlot>();
            });
        }

        // BindInterfacesTo above is a separate creation binding, so it resolves its own TestListenerSlot instance,
        // not the one BindSaveSlot registered. additionalContracts is how one instance covers both contracts.
        [Test]
        public void BindSaveSlot_WithAdditionalContract_SharesOneInstanceWithTheSaveService()
        {
            Container.BindSaveSlot<TestListenerSlot>(typeof(IRestoreListener));
            SaveServiceInstaller.Install(Container, CreateIsolatedOptions());

            var slot = Container.Resolve<TestListenerSlot>();
            var listener = Container.Resolve<IRestoreListener>();
            Assert.AreSame(slot, listener, "SaveSlot and IRestoreListener must resolve to the same instance.");

            var saveService = Container.Resolve<ISaveService>();
            Container.Resolve<InitializableManager>().Initialize();
            RunSync(saveService.InitializeAsync(CancellationToken.None));

            Assert.AreEqual(SlotState.Ready, slot.State, "The save service must have loaded the exact instance IRestoreListener resolves to.");
        }

        // The case the AsCached bindings inside SaveServiceInstaller protect.
        [Test]
        public void UnrelatedGameService_BoundWithBindInterfacesAndSelfToAsSingle_CoexistsWithSaveServiceInstaller()
        {
            Container.BindInterfacesAndSelfTo<FakeGameService>().AsSingle();

            Assert.DoesNotThrow(() => SaveServiceInstaller.Install(Container, CreateIsolatedOptions()));

            Assert.IsNotNull(Container.Resolve<ISaveService>());
            Assert.IsNotNull(Container.Resolve<FakeGameService>());
        }

        [Test]
        public void BindSaveSlot_ResolvedThroughSaveSlotAndThroughTSlot_IsTheSameInstance()
        {
            Container.BindSaveSlot<TestSlotA>();

            var viaBase = Container.ResolveAll<SaveSlot>().Single();
            var viaConcrete = Container.Resolve<TestSlotA>();

            Assert.AreSame(viaBase, viaConcrete);
        }

        // F9 regression: additionalContracts repeating a contract already in the base pair (here, SaveSlot
        // itself) used to register the same provider twice under SaveSlot, so ResolveAll<SaveSlot>() returned
        // the instance twice.
        [Test]
        public void BindSaveSlot_DuplicateAdditionalContract_RegistersOnlyOnce()
        {
            Container.BindSaveSlot<TestSlotA>(typeof(SaveSlot));

            List<SaveSlot> resolved = Container.ResolveAll<SaveSlot>();

            Assert.That(resolved.Count, Is.EqualTo(1));
        }

        // F9 regression: a null additionalContracts entry used to reach Zenject's BindingUtil.AssertIsDerivedFromTypes
        // and throw a raw Zenject exception far from the actual cause.
        [Test]
        public void BindSaveSlot_NullAdditionalContractEntry_DoesNotThrow()
        {
            Assert.DoesNotThrow(() =>
            {
                Container.BindSaveSlot<TestSlotA>((Type)null);
                Container.Resolve<TestSlotA>();
            });
        }

        // Stand-in for a game's own singleton service, sharing IInitializable/IDisposable with the package's bindings.
        private sealed class FakeGameService : IInitializable, IDisposable
        {
            public void Initialize()
            {
            }

            public void Dispose()
            {
            }
        }
    }
}
