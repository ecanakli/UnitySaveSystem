using System;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using Zenject;

namespace Ecanakli.SaveSystem.DependencyInjection.Tests
{
    [TestFixture]
    internal sealed class SaveSignalsInstallerTests : ZenjectSaveTestBase
    {
        [Test]
        public void Install_WithoutSignalBus_ThrowsDocumentedMessage()
        {
            var exception = Assert.Throws<InvalidOperationException>(() => SaveSignalsInstaller.Install(Container));
            Assert.AreEqual("Call SignalBusInstaller.Install before SaveSignalsInstaller.Install", exception.Message);
        }

        [Test]
        public void RealSaveService_InitTimeProfileActivated_FiresSignalExactlyOnce()
        {
            SignalBusInstaller.Install(Container);
            var options = new SaveServiceOptions { OffloadIo = false, Logger = new UnityDebugSaveLogger() };
            SaveServiceInstaller.Install(Container, options);
            SaveSignalsInstaller.Install(Container);

            var saveService = Container.Resolve<ISaveService>();
            var signalBus = Container.Resolve<SignalBus>();
            int callCount = 0;
            signalBus.Subscribe<SaveProfileActivatedSignal>(_ => callCount++);

            Container.Resolve<InitializableManager>().Initialize();
            RunSync(saveService.InitializeAsync(CancellationToken.None));

            Assert.AreEqual(1, callCount);
        }

        [Test]
        public void EachFakeCoreEvent_FiresItsSignalExactlyOnce()
        {
            var fakeService = BindFakeServiceStack();

            var signalBus = Container.Resolve<SignalBus>();
            int profileActivated = 0, restoreCompleted = 0, slotLoadIssue = 0, uploadFailed = 0, localWriteHealth = 0, updateRequired = 0;
            signalBus.Subscribe<SaveProfileActivatedSignal>(_ => profileActivated++);
            signalBus.Subscribe<SaveRestoreCompletedSignal>(_ => restoreCompleted++);
            signalBus.Subscribe<SaveSlotLoadIssueDetectedSignal>(_ => slotLoadIssue++);
            signalBus.Subscribe<SaveUploadFailedSignal>(_ => uploadFailed++);
            signalBus.Subscribe<SaveLocalWriteHealthChangedSignal>(_ => localWriteHealth++);
            signalBus.Subscribe<SaveUpdateRequiredSignal>(_ => updateRequired++);

            Container.Resolve<InitializableManager>().Initialize();

            fakeService.RaiseProfileActivated(null);
            fakeService.RaiseRestoreCompleted(null);
            fakeService.RaiseSlotLoadIssueDetected(null);
            fakeService.RaiseUploadFailed(null);
            fakeService.RaiseLocalWriteHealthChanged(null);
            fakeService.RaiseUpdateRequired(null);

            Assert.AreEqual(1, profileActivated);
            Assert.AreEqual(1, restoreCompleted);
            Assert.AreEqual(1, slotLoadIssue);
            Assert.AreEqual(1, uploadFailed);
            Assert.AreEqual(1, localWriteHealth);
            Assert.AreEqual(1, updateRequired);
        }

        [Test]
        public void FlushSavesRequest_CallsFlushAsyncOnce()
        {
            var fakeService = BindFakeServiceStack();
            var signalBus = Container.Resolve<SignalBus>();
            Container.Resolve<InitializableManager>().Initialize();

            signalBus.Fire(new FlushSavesRequest());

            Assert.AreEqual(1, fakeService.FlushAsyncCallCount);
        }

        [Test]
        public void AfterDispose_NoMoreSignalsAndNoMoreFlushHandling()
        {
            var fakeService = BindFakeServiceStack();
            var signalBus = Container.Resolve<SignalBus>();
            int profileActivated = 0;
            signalBus.Subscribe<SaveProfileActivatedSignal>(_ => profileActivated++);

            Container.Resolve<InitializableManager>().Initialize();

            var bridge = Container.ResolveAll<IDisposable>().OfType<SaveSignalBridge>().Single();
            bridge.Dispose();

            fakeService.RaiseProfileActivated(null);
            signalBus.Fire(new FlushSavesRequest());

            Assert.AreEqual(0, profileActivated, "No signal should fire once the bridge is disposed.");
            Assert.AreEqual(0, fakeService.FlushAsyncCallCount, "FlushSavesRequest must be a no-op once the bridge is disposed.");
        }

        // Binds a FakeSaveService instead of a real one so the wiring tests never depend on real save/restore
        // conditions (corrupt cloud data, schema mismatch, ...), which are already covered by the core test suite.
        private FakeSaveService BindFakeServiceStack()
        {
            SignalBusInstaller.Install(Container);
            var fakeService = new FakeSaveService();
            Container.Bind<ISaveService>().FromInstance(fakeService).AsSingle();
            Container.Bind<SaveServiceOptions>().FromInstance(new SaveServiceOptions { Logger = new UnityDebugSaveLogger() }).AsSingle();
            SaveSignalsInstaller.Install(Container);
            return fakeService;
        }
    }
}
