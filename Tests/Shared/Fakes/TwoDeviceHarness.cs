using System;

namespace Ecanakli.SaveSystem.Tests
{
    /// <summary>
    /// Two SaveService instances ("devices") with separate InMemorySaveStorage, loggers and providers over one shared
    /// FakeCloudStore, one shared ManualSaveClock, OffloadIo = false. Both providers start signed out.
    /// </summary>
    internal sealed class TwoDeviceHarness : IDisposable
    {
        public const string DeviceAId = "device-a";
        public const string DeviceBId = "device-b";

        private Func<SaveSlot[]> _deviceASlotFactory;
        private Func<SaveSlot[]> _deviceBSlotFactory;

        /// <summary>slotFactory is called once per service instance (slots cannot be shared between services).</summary>
        public TwoDeviceHarness(Func<SaveSlot[]> slotFactory, CloudCapabilities capabilities = null, Action<SaveServiceOptions> configureOptions = null)
            : this(slotFactory, slotFactory, capabilities, configureOptions)
        {
        }

        private TwoDeviceHarness(
            Func<SaveSlot[]> deviceASlotFactory, Func<SaveSlot[]> deviceBSlotFactory, CloudCapabilities capabilities, Action<SaveServiceOptions> configureOptions)
        {
            _deviceASlotFactory = deviceASlotFactory ?? throw new ArgumentNullException(nameof(deviceASlotFactory));
            _deviceBSlotFactory = deviceBSlotFactory ?? throw new ArgumentNullException(nameof(deviceBSlotFactory));
            CloudStore = new FakeCloudStore();
            Clock = new ManualSaveClock();
            DeviceA = CreateDevice(DeviceAId, _deviceASlotFactory, capabilities, configureOptions);
            DeviceB = CreateDevice(DeviceBId, _deviceBSlotFactory, capabilities, configureOptions);
        }

        public FakeCloudStore CloudStore { get; }

        public ManualSaveClock Clock { get; }

        public TestServiceContext DeviceA { get; private set; }

        public TestServiceContext DeviceB { get; private set; }

        /// <summary>Devices with different slot sets, for example two package or game versions sharing one key.</summary>
        public static TwoDeviceHarness WithSeparateSlots(
            Func<SaveSlot[]> deviceASlotFactory,
            Func<SaveSlot[]> deviceBSlotFactory,
            CloudCapabilities capabilities = null,
            Action<SaveServiceOptions> configureOptions = null)
        {
            return new TwoDeviceHarness(deviceASlotFactory, deviceBSlotFactory, capabilities, configureOptions);
        }

        public void SignIn(string accountId)
        {
            DeviceA.Provider.SignedInAccountId = accountId;
            DeviceB.Provider.SignedInAccountId = accountId;
        }

        public void SignOut()
        {
            SignIn(null);
        }

        public void InitializeBothSync()
        {
            DeviceA.InitializeSync();
            DeviceB.InitializeSync();
        }

        /// <summary>Disposes device A and rebuilds it with fresh slots over the same storage and provider.</summary>
        public TestServiceContext RestartDeviceA()
        {
            DeviceA = TestServiceFactory.Restart(DeviceA, _deviceASlotFactory());
            return DeviceA;
        }

        public TestServiceContext RestartDeviceB()
        {
            DeviceB = TestServiceFactory.Restart(DeviceB, _deviceBSlotFactory());
            return DeviceB;
        }

        /// <summary>Restart with a new slot factory kept for later restarts (simulated app update).</summary>
        public TestServiceContext RestartDeviceA(Func<SaveSlot[]> slotFactory)
        {
            _deviceASlotFactory = slotFactory ?? throw new ArgumentNullException(nameof(slotFactory));
            return RestartDeviceA();
        }

        public TestServiceContext RestartDeviceB(Func<SaveSlot[]> slotFactory)
        {
            _deviceBSlotFactory = slotFactory ?? throw new ArgumentNullException(nameof(slotFactory));
            return RestartDeviceB();
        }

        public void Dispose()
        {
            DeviceA.Dispose();
            DeviceB.Dispose();
        }

        private TestServiceContext CreateDevice(string deviceId, Func<SaveSlot[]> slotFactory, CloudCapabilities capabilities, Action<SaveServiceOptions> configureOptions)
        {
            return TestServiceFactory.Create(new TestServiceSetup
            {
                Slots = slotFactory(),
                Storage = new InMemorySaveStorage(),
                Provider = new FakeCloudSaveProvider(CloudStore, capabilities),
                Clock = Clock,
                Logger = new TestSaveLogger(),
                DeviceId = deviceId,
                ConfigureOptions = configureOptions,
            });
        }
    }
}
