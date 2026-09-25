using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Ecanakli.SaveSystem.Tests
{
    /// <summary>Optional inputs for TestServiceFactory.Create; null members get fresh fakes.</summary>
    internal sealed class TestServiceSetup
    {
        public IEnumerable<SaveSlot> Slots { get; set; }

        public InMemorySaveStorage Storage { get; set; }

        public FakeCloudSaveProvider Provider { get; set; }

        public ManualSaveClock Clock { get; set; }

        public TestSaveLogger Logger { get; set; }

        /// <summary>Deterministic device id; null uses the persisted GUID.</summary>
        public string DeviceId { get; set; } = TestServiceFactory.DefaultDeviceId;

        /// <summary>Null creates a unique path under the temp directory (never written by the in-memory storage).</summary>
        public string RootDirectory { get; set; }

        /// <summary>Runs after the test defaults are applied.</summary>
        public Action<SaveServiceOptions> ConfigureOptions { get; set; }
    }

    /// <summary>A SaveService with its fakes. Dispose disposes the service only.</summary>
    internal sealed class TestServiceContext : IDisposable
    {
        internal TestServiceContext(
            SaveService service,
            SaveServiceOptions options,
            InMemorySaveStorage storage,
            FakeCloudSaveProvider provider,
            ManualSaveClock clock,
            TestSaveLogger logger,
            IReadOnlyList<SaveSlot> slots)
        {
            Service = service;
            Options = options;
            Storage = storage;
            Provider = provider;
            Clock = clock;
            Logger = logger;
            Slots = slots;
        }

        public SaveService Service { get; }

        public SaveServiceOptions Options { get; }

        public InMemorySaveStorage Storage { get; }

        public FakeCloudSaveProvider Provider { get; }

        public ManualSaveClock Clock { get; }

        public TestSaveLogger Logger { get; }

        public IReadOnlyList<SaveSlot> Slots { get; }

        public bool IsDisposed { get; private set; }

        /// <summary>First registered slot of type TSlot; throws when none.</summary>
        public TSlot Slot<TSlot>() where TSlot : SaveSlot
        {
            foreach (SaveSlot slot in Slots)
            {
                if (slot is TSlot typed)
                {
                    return typed;
                }
            }

            throw new InvalidOperationException("No slot of type " + typeof(TSlot).Name + " is registered.");
        }

        public TSlot Slot<TSlot>(string key) where TSlot : SaveSlot
        {
            foreach (SaveSlot slot in Slots)
            {
                if (slot is TSlot typed && string.Equals(slot.Key, key, StringComparison.Ordinal))
                {
                    return typed;
                }
            }

            throw new InvalidOperationException("No slot of type " + typeof(TSlot).Name + " with key '" + key + "' is registered.");
        }

        public InitializeResult InitializeSync()
        {
            return AsyncTestUtility.RunSync(Service.InitializeAsync(CancellationToken.None), nameof(SaveService.InitializeAsync));
        }

        /// <summary>Completes synchronously only without deactivation listeners and gate contention.</summary>
        public ProfileActivationResult ActivateSync(ProfileId profile)
        {
            return AsyncTestUtility.RunSync(Service.ActivateProfileAsync(profile, CancellationToken.None), nameof(SaveService.ActivateProfileAsync));
        }

        /// <summary>Completes synchronously only when no provider call is gated or retried.</summary>
        public FlushResult FlushSync()
        {
            return AsyncTestUtility.RunSync(Service.FlushAsync(CancellationToken.None), nameof(SaveService.FlushAsync));
        }

        public void Dispose()
        {
            if (IsDisposed)
            {
                return;
            }

            IsDisposed = true;
            Service.Dispose();
        }
    }

    /// <summary>Builds SaveService instances over fakes with OffloadIo = false.</summary>
    internal static class TestServiceFactory
    {
        public const string DefaultDeviceId = "device-test";

        public static string CreateRootDirectory()
        {
            return Path.Combine(Path.GetTempPath(), "EcanakliSaveSystemTests", "memory-" + Guid.NewGuid().ToString("N"));
        }

        public static SaveServiceOptions CreateOptions(ISaveClock clock, ISaveLogger logger, string deviceId = DefaultDeviceId, string rootDirectory = null)
        {
            var options = new SaveServiceOptions
            {
                RootDirectory = rootDirectory ?? CreateRootDirectory(),
                OffloadIo = false,
                Clock = clock,
                Logger = logger,
            };

            if (deviceId != null)
            {
                options.DeviceIdProvider = () => deviceId;
            }

            return options;
        }

        public static TestServiceContext Create(params SaveSlot[] slots)
        {
            return Create(new TestServiceSetup { Slots = slots });
        }

        public static TestServiceContext Create(TestServiceSetup setup)
        {
            if (setup == null)
            {
                throw new ArgumentNullException(nameof(setup));
            }

            InMemorySaveStorage storage = setup.Storage ?? new InMemorySaveStorage();
            FakeCloudSaveProvider provider = setup.Provider ?? new FakeCloudSaveProvider();
            ManualSaveClock clock = setup.Clock ?? new ManualSaveClock();
            TestSaveLogger logger = setup.Logger ?? new TestSaveLogger();

            SaveServiceOptions options = CreateOptions(clock, logger, setup.DeviceId, setup.RootDirectory);
            setup.ConfigureOptions?.Invoke(options);

            var slots = new List<SaveSlot>(setup.Slots ?? Array.Empty<SaveSlot>());
            var service = new SaveService(options, storage, provider, slots);
            return new TestServiceContext(service, options, storage, provider, clock, logger, slots);
        }

        /// <summary>Simulated app restart: disposes previous, then builds a service over the same storage, provider, clock, logger and options.</summary>
        public static TestServiceContext Restart(TestServiceContext previous, params SaveSlot[] slots)
        {
            if (previous == null)
            {
                throw new ArgumentNullException(nameof(previous));
            }

            previous.Dispose();
            var slotList = new List<SaveSlot>(slots ?? Array.Empty<SaveSlot>());
            var service = new SaveService(previous.Options, previous.Storage, previous.Provider, slotList);
            return new TestServiceContext(service, previous.Options, previous.Storage, previous.Provider, previous.Clock, previous.Logger, slotList);
        }
    }

    /// <summary>Relative storage paths used by the core.</summary>
    internal static class TestPaths
    {
        public static string DeviceState => SaveLayout.DeviceStatePath;

        public static string ProfileDirectory(ProfileId profile)
        {
            return SaveLayout.ProfileDirectory(profile);
        }

        public static string ProfileSlot(ProfileId profile, string key)
        {
            return SaveLayout.SlotPath(SaveLayout.ProfileDirectory(profile), key);
        }

        public static string DeviceSlot(string key)
        {
            return SaveLayout.SlotPath(SaveLayout.DeviceDirectory, key);
        }

        public static string ProfileState(ProfileId profile)
        {
            return SaveLayout.ProfileStatePath(SaveLayout.ProfileDirectory(profile));
        }

        public static string Tmp(string path)
        {
            return SaveLayout.TmpPath(path);
        }

        public static string Bak(string path)
        {
            return SaveLayout.BakPath(path);
        }
    }
}
