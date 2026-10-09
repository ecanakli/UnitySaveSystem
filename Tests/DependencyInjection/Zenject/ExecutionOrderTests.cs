using System.Threading;
using NUnit.Framework;
using Zenject;

namespace Ecanakli.SaveSystem.DependencyInjection.Tests
{
    [TestFixture]
    internal sealed class ExecutionOrderTests : ZenjectSaveTestBase
    {
        [Test]
        public void GameInitializable_BoundBeforeInstallers_InitializesSynchronously_ListenersAndSignalsAlreadyWired()
        {
            SignalBusInstaller.Install(Container);

            var restoreListener = new RecordingRestoreListener();
            Container.Bind<IRestoreListener>().FromInstance(restoreListener).AsSingle();

            var gameBoot = new GameBootInitializable();
            Container.Bind<IInitializable>().FromInstance(gameBoot).AsSingle();

            var options = new SaveServiceOptions { OffloadIo = false, Logger = new UnityDebugSaveLogger() };
            SaveServiceInstaller.Install(Container, options);
            SaveSignalsInstaller.Install(Container);

            var signalBus = Container.Resolve<SignalBus>();
            int signalCount = 0;
            signalBus.Subscribe<SaveProfileActivatedSignal>(_ => signalCount++);

            gameBoot.SaveService = Container.Resolve<ISaveService>();

            // Default (unspecified) priority is 0; the package's registrar/host/bridge use EarlyExecutionOrder
            // (-10000) and therefore Initialize() first in this ascending-priority pass.
            Container.Resolve<InitializableManager>().Initialize();

            Assert.IsTrue(gameBoot.Completed, "InitializeAsync should complete synchronously with OffloadIo = false.");
            Assert.AreEqual(1, restoreListener.CallCount, "The registrar must have added the listener before the game's Initialize ran.");
            Assert.AreEqual(1, signalCount, "The signal bridge must have subscribed before the game's Initialize ran.");
        }

        // Simulates a game-owned IInitializable that calls InitializeAsync itself, since the package never calls it
        // for the caller (04a A2: InitializeAsync is a caller-driven, awaited call).
        private sealed class GameBootInitializable : IInitializable
        {
            public ISaveService SaveService;

            public bool Completed { get; private set; }

            public void Initialize()
            {
                InitializeResult result = SaveService.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
                Completed = result.IsSuccess;
            }
        }
    }
}
