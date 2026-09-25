using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using Zenject;

namespace Ecanakli.SaveSystem.DependencyInjection.Tests
{
    [TestFixture]
    internal sealed class SaveServiceInstallerTests : ZenjectSaveTestBase
    {
        [Test]
        public void Install_ServiceAndDisposableResolveToOneInstance()
        {
            SaveServiceInstaller.Install(Container, CreateIsolatedOptions());

            var saveService = Container.Resolve<ISaveService>();
            SaveService[] disposableServices = Container.ResolveAll<IDisposable>().OfType<SaveService>().ToArray();

            Assert.That(disposableServices.Length, Is.EqualTo(1), "Exactly one SaveService must be registered as IDisposable.");
            Assert.That(disposableServices[0], Is.SameAs(saveService), "The disposed service must be the one the game uses.");
        }

        // Zenject disposes highest execution order first, so the service takes the lowest and shuts down last.
        // The game disposable is bound BEFORE the installer on purpose: with equal orders it would be disposed
        // after the service, and its shutdown write would be dropped.
        [Test]
        public void Dispose_GameDisposableBoundFirst_StillSavesBecauseTheServiceShutsDownLast()
        {
            var shutdownWriter = new ShutdownWritingDisposable();
            Container.Bind<IDisposable>().FromInstance(shutdownWriter).AsCached();

            Container.BindSaveSlot<TestSlotA>();
            SaveServiceInstaller.Install(Container, CreateIsolatedOptions());

            var saveService = Container.Resolve<ISaveService>();
            Container.Resolve<InitializableManager>().Initialize();
            Assert.That(RunSync(saveService.InitializeAsync(CancellationToken.None)).IsSuccess, Is.True);
            shutdownWriter.Attach(saveService, Container.Resolve<TestSlotA>());

            DisposeContainerDisposables();

            Assert.That(shutdownWriter.Ran, Is.True, "Premise: the game disposable was disposed.");
            Assert.That(shutdownWriter.MutateSucceeded, Is.True, "A detached slot refuses Mutate, which means the service went first.");
            Assert.That(shutdownWriter.FlushWasComplete, Is.True, "The shutdown write must reach the disk.");
        }

        /// <summary>Game code that saves from its own Dispose; records whether the service was still usable.</summary>
        private sealed class ShutdownWritingDisposable : IDisposable
        {
            private ISaveService _service;
            private TestSlotA _slot;

            public bool Ran { get; private set; }

            public bool MutateSucceeded { get; private set; }

            public bool FlushWasComplete { get; private set; }

            public void Attach(ISaveService service, TestSlotA slot)
            {
                _service = service;
                _slot = slot;
            }

            public void Dispose()
            {
                Ran = true;
                MutateSucceeded = _slot.Mutate(data => data.Value = 42);
                FlushWasComplete = _service.FlushLocalNow().IsComplete;
            }
        }

        [Test]
        public void Install_BindsStorageOverTheOptionsRootDirectory()
        {
            string root = CreateTempRoot();
            SaveServiceOptions options = CreateIsolatedOptions();
            options.RootDirectory = root;

            SaveServiceInstaller.Install(Container, options);

            var storage = (AtomicFileStorage)Container.Resolve<ISaveStorage>();
            Assert.That(storage.RootDirectory, Is.EqualTo(System.IO.Path.GetFullPath(root)));
        }

        // F6/T8 regression: before IfNotBound(), a game-bound ISaveStorage collided with the installer's own
        // unconditional Bind<ISaveStorage>() ("Found multiple matches") instead of coexisting with it.
        [Test]
        public void Install_GameBindsOwnStorage_ServiceUsesTheGamesStorageInstead()
        {
            var gameStorage = new RecordingSaveStorage(CreateTempRoot());
            Container.Bind<ISaveStorage>().FromInstance(gameStorage).AsSingle();

            Assert.DoesNotThrow(() => SaveServiceInstaller.Install(Container, CreateIsolatedOptions()));
            Assert.That(Container.Resolve<ISaveStorage>(), Is.SameAs(gameStorage));

            var saveService = Container.Resolve<ISaveService>();
            Container.Resolve<InitializableManager>().Initialize();
            Assert.That(RunSync(saveService.InitializeAsync(CancellationToken.None)).IsSuccess, Is.True);

            Assert.That(gameStorage.WriteAtomicCalled, Is.True, "The service must write through the game's own storage, not a second one built by the installer.");
        }

        /// <summary>Delegates to a real AtomicFileStorage, but records whether WriteAtomic was actually reached.</summary>
        private sealed class RecordingSaveStorage : ISaveStorage
        {
            private readonly AtomicFileStorage _inner;

            public RecordingSaveStorage(string root)
            {
                _inner = new AtomicFileStorage(root);
            }

            public bool WriteAtomicCalled { get; private set; }

            public byte[] ReadAllBytes(string relativePath) => _inner.ReadAllBytes(relativePath);

            public void WriteAtomic(string relativePath, byte[] bytes)
            {
                WriteAtomicCalled = true;
                _inner.WriteAtomic(relativePath, bytes);
            }

            public bool FileExists(string relativePath) => _inner.FileExists(relativePath);

            public void DeleteFile(string relativePath) => _inner.DeleteFile(relativePath);

            public void MoveFile(string sourceRelativePath, string destinationRelativePath) => _inner.MoveFile(sourceRelativePath, destinationRelativePath);

            public bool DirectoryExists(string relativeDirectory) => _inner.DirectoryExists(relativeDirectory);

            public void CreateDirectory(string relativeDirectory) => _inner.CreateDirectory(relativeDirectory);

            public IReadOnlyList<string> ListFileNames(string relativeDirectory) => _inner.ListFileNames(relativeDirectory);

            public IReadOnlyList<string> ListDirectoryNames(string relativeDirectory) => _inner.ListDirectoryNames(relativeDirectory);

            public void MoveDirectory(string sourceRelativeDirectory, string destinationRelativeDirectory) => _inner.MoveDirectory(sourceRelativeDirectory, destinationRelativeDirectory);

            public void DeleteDirectory(string relativeDirectory) => _inner.DeleteDirectory(relativeDirectory);
        }

        // Options are not overridable; without the guard this hit Zenject's "multiple creation bindings" assert instead
        [Test]
        public void Install_OptionsAlreadyBound_ThrowsAndNamesTheInstallParameter()
        {
            Container.BindInstance(CreateIsolatedOptions());

            var error = Assert.Throws<InvalidOperationException>(() => SaveServiceInstaller.Install(Container, CreateIsolatedOptions()));
            StringAssert.Contains("SaveServiceInstaller.Install", error.Message);
        }

        // FromMethod receives the requesting container, so a child resolving first must not hand the service its own options
        [Test]
        public void Resolve_FirstFromAChildWithItsOwnOptions_ServiceStillUsesTheInstallOptions()
        {
            SaveServiceInstaller.Install(Container, CreateIsolatedOptions());
            DiContainer child = Container.CreateSubContainer();
            SaveServiceOptions invalidOptions = CreateIsolatedOptions();
            invalidOptions.CloudRetryCount = -1;
            child.BindInstance(invalidOptions);

            ISaveService fromChild = null;
            Assert.DoesNotThrow(() => fromChild = child.Resolve<ISaveService>(), "The child's options must never reach the service.");
            Assert.That(fromChild, Is.SameAs(Container.Resolve<ISaveService>()));
        }

        [Test]
        public void Resolve_FirstFromAChildWithItsOwnStorage_ServiceWritesThroughTheInstallStorage()
        {
            SaveServiceOptions options = CreateIsolatedOptions();
            SaveServiceInstaller.Install(Container, options);
            DiContainer child = Container.CreateSubContainer();
            var childStorage = new RecordingSaveStorage(CreateTempRoot());
            child.Bind<ISaveStorage>().FromInstance(childStorage).AsCached();

            var saveService = child.Resolve<ISaveService>();
            Container.Resolve<InitializableManager>().Initialize();
            Assert.That(RunSync(saveService.InitializeAsync(CancellationToken.None)).IsSuccess, Is.True);

            Assert.That(childStorage.WriteAtomicCalled, Is.False, "The child's storage must never reach the service.");
            Assert.That(System.IO.Directory.EnumerateFiles(options.RootDirectory, "*", System.IO.SearchOption.AllDirectories).Any(), Is.True,
                "Premise: the service wrote through the install storage.");
        }

        [Test]
        public void Resolve_FirstFromAChildWithItsOwnSlot_ServiceRegistersOnlyTheInstallContainersSlots()
        {
            Container.BindSaveSlot<TestSlotA>();
            SaveServiceInstaller.Install(Container, CreateIsolatedOptions());
            DiContainer child = Container.CreateSubContainer();
            child.BindSaveSlot<TestSlotB>();

            var saveService = child.Resolve<ISaveService>();
            Container.Resolve<InitializableManager>().Initialize();
            RunSync(saveService.InitializeAsync(CancellationToken.None));

            Assert.That(Container.Resolve<TestSlotA>().State, Is.EqualTo(SlotState.Ready), "Premise: the install container's slot loads.");
            Assert.That(child.Resolve<TestSlotB>().State, Is.EqualTo(SlotState.Unloaded), "A child's slot must not end up inside the app-wide service.");
        }

        [Test]
        public void Install_TwiceInOneContainer_ThrowsAndNamesTheDoubleInstall()
        {
            SaveServiceInstaller.Install(Container, CreateIsolatedOptions());

            var error = Assert.Throws<InvalidOperationException>(() => SaveServiceInstaller.Install(Container, CreateIsolatedOptions()));
            StringAssert.Contains("ran here twice", error.Message);
        }

        [Test]
        public void Install_ZeroSlots_ResolvesReadySaveService_WithoutSignalBus()
        {
            SaveServiceInstaller.Install(Container, CreateIsolatedOptions());

            var saveService = Container.Resolve<ISaveService>();
            Container.Resolve<InitializableManager>().Initialize();

            InitializeResult result = RunSync(saveService.InitializeAsync(CancellationToken.None));

            Assert.IsTrue(result.IsSuccess);
            Assert.IsTrue(saveService.IsInitialized);
        }

        [Test]
        public void Install_MultipleSlots_ResolvesAllSlotsThroughSaveService()
        {
            Container.BindSaveSlot<TestSlotA>();
            Container.BindSaveSlot<TestSlotB>();
            SaveServiceInstaller.Install(Container, CreateIsolatedOptions());

            var saveService = Container.Resolve<ISaveService>();
            var slotA = Container.Resolve<TestSlotA>();
            var slotB = Container.Resolve<TestSlotB>();
            Container.Resolve<InitializableManager>().Initialize();

            RunSync(saveService.InitializeAsync(CancellationToken.None));

            Assert.AreEqual(SlotState.Ready, slotA.State);
            Assert.AreEqual(SlotState.Ready, slotB.State);
        }

        [Test]
        public void NoCloudProviderBound_RestoreAsync_FallsBackToNullCloudSaveProvider()
        {
            Container.BindSaveSlot<TestCloudSlot>();
            SaveServiceInstaller.Install(Container, CreateIsolatedOptions());

            var saveService = Container.Resolve<ISaveService>();
            Container.Resolve<InitializableManager>().Initialize();

            RunSync(saveService.InitializeAsync(CancellationToken.None));
            RunSync(saveService.ActivateProfileAsync(ProfileId.Account("acc-fallback"), CancellationToken.None));

            RestoreReport report = RunSync(saveService.RestoreAsync(CancellationToken.None));

            // NullCloudSaveProvider.SignedInAccountId is always null, so an Account profile restore is refused as
            // NotSignedIn. Any real provider signed into "acc-fallback" would not hit this path.
            Assert.AreEqual(SaveStatus.Failed, report.Status);
            Assert.AreEqual(SaveErrorCode.NotSignedIn, report.Error.Code);
        }

        [Test]
        public void Registrar_AddsListenersOnInitialize_RemovesOnDisposeIndependentlyOfSaveService()
        {
            var restoreListener = new RecordingRestoreListener();
            var deactivationListener = new RecordingDeactivationListener();
            Container.Bind<IRestoreListener>().FromInstance(restoreListener).AsSingle();
            Container.Bind<IProfileDeactivatingListener>().FromInstance(deactivationListener).AsSingle();
            SaveServiceInstaller.Install(Container, CreateIsolatedOptions());

            var saveService = Container.Resolve<ISaveService>();
            Container.Resolve<InitializableManager>().Initialize();

            RunSync(saveService.InitializeAsync(CancellationToken.None));
            Assert.AreEqual(1, restoreListener.CallCount, "InitializeAsync should dispatch the added restore listener once.");

            RunSync(saveService.ActivateProfileAsync(ProfileId.Local("slot-1"), CancellationToken.None));
            Assert.AreEqual(1, deactivationListener.CallCount, "ActivateProfileAsync should dispatch the added deactivation listener once.");

            // Dispose only the registrar, in isolation, without tearing down the SaveService itself.
            var registrar = Container.ResolveAll<IDisposable>().OfType<SaveListenerRegistrar>().Single();
            registrar.Dispose();

            RunSync(saveService.ActivateProfileAsync(ProfileId.Local("slot-2"), CancellationToken.None));
            Assert.AreEqual(1, deactivationListener.CallCount, "After the registrar disposes, the listener must no longer be called.");
        }
    }
}
