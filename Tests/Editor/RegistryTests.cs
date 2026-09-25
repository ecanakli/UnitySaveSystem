using System;
using NUnit.Framework;

namespace Ecanakli.SaveSystem.Tests
{
    /// <summary>SaveSlotRegistry validation: keys, duplicates, reserved names, scope rules and freezing.</summary>
    [TestFixture]
    public sealed class RegistryTests
    {
        [TestCase("a")]
        [TestCase("0")]
        [TestCase("Player_01")]
        [TestCase("inventory-v2")]
        [TestCase("A_b-C_9")]
        public void IsValidKey_AllowedCharacters_ReturnsTrue(string key)
        {
            Assert.That(SaveSlotRegistry.IsValidKey(key), Is.True);
        }

        [Test]
        public void IsValidKey_MaxLengthAccepted_OneOverRejected()
        {
            Assert.That(SaveSlotRegistry.IsValidKey(new string('a', SaveSlotRegistry.MaxKeyLength)), Is.True);
            Assert.That(SaveSlotRegistry.IsValidKey(new string('a', SaveSlotRegistry.MaxKeyLength + 1)), Is.False);
        }

        [TestCase((string)null)]
        [TestCase("")]
        [TestCase("with space")]
        [TestCase("dot.key")]
        [TestCase("slash/key")]
        [TestCase("back\\slash")]
        [TestCase("colon:key")]
        [TestCase("percent%key")]
        [TestCase("tab\tkey")]
        public void IsValidKey_InvalidKeys_ReturnsFalse(string key)
        {
            Assert.That(SaveSlotRegistry.IsValidKey(key), Is.False);
        }

        [Test]
        public void IsValidKey_NonAsciiLetter_ReturnsFalse()
        {
            // Built at runtime to keep the source ASCII-only
            string key = "key" + (char)0x00E9;

            Assert.That(SaveSlotRegistry.IsValidKey(key), Is.False);
        }

        [TestCase("")]
        [TestCase("bad key")]
        [TestCase("bad.key")]
        public void Register_InvalidKey_ThrowsArgumentException(string key)
        {
            var registry = new SaveSlotRegistry();

            Assert.Throws<ArgumentException>(() => registry.Register(new ProfileSlot(key)));
            Assert.That(registry.Count, Is.EqualTo(0));
        }

        [Test]
        public void Register_KeyLongerThanMax_ThrowsArgumentException()
        {
            var registry = new SaveSlotRegistry();

            Assert.Throws<ArgumentException>(() => registry.Register(new ProfileSlot(new string('k', SaveSlotRegistry.MaxKeyLength + 1))));
        }

        [Test]
        public void Register_Null_ThrowsArgumentNullException()
        {
            var registry = new SaveSlotRegistry();

            Assert.Throws<ArgumentNullException>(() => registry.Register(null));
        }

        [TestCase("player", "PLAYER")]
        [TestCase("Inventory", "inventory")]
        [TestCase("same", "same")]
        public void Register_DuplicateKey_CaseInsensitive_Throws(string first, string second)
        {
            var registry = new SaveSlotRegistry();
            registry.Register(new ProfileSlot(first));

            var exception = Assert.Throws<ArgumentException>(() => registry.Register(new ProfileSlot(second)));

            Assert.That(exception.Message, Does.Contain("case-insensitively"));
            Assert.That(registry.Count, Is.EqualTo(1));
        }

        [Test]
        public void Register_SameInstanceTwice_Throws()
        {
            var registry = new SaveSlotRegistry();
            var slot = new ProfileSlot();
            registry.Register(slot);

            var exception = Assert.Throws<ArgumentException>(() => registry.Register(slot));

            Assert.That(exception.Message, Does.Contain("registered twice"));
            Assert.That(registry.Count, Is.EqualTo(1));
        }

        [TestCase("device")]
        [TestCase("profile")]
        [TestCase("Device")]
        [TestCase("PROFILE")]
        public void Register_ReservedKey_Throws(string key)
        {
            var registry = new SaveSlotRegistry();

            var exception = Assert.Throws<ArgumentException>(() => registry.Register(new ProfileSlot(key)));

            Assert.That(exception.Message, Does.Contain("reserved"));
        }

        // S9: con.json resolves to a device on Win32, so the file silently reads back empty
        [TestCase("con")]
        [TestCase("PRN")]
        [TestCase("Aux")]
        [TestCase("nul")]
        [TestCase("com1")]
        [TestCase("COM9")]
        [TestCase("lpt1")]
        [TestCase("Lpt9")]
        public void Register_WindowsDeviceNameKey_Throws(string key)
        {
            var registry = new SaveSlotRegistry();

            var exception = Assert.Throws<ArgumentException>(() => registry.Register(new ProfileSlot(key)));

            Assert.That(exception.Message, Does.Contain("reserved"));
        }

        [TestCase("device", true)]
        [TestCase("profile", true)]
        [TestCase("con", true)]
        [TestCase("CON", true)]
        [TestCase("prn", true)]
        [TestCase("aux", true)]
        [TestCase("nul", true)]
        [TestCase("com1", true)]
        [TestCase("lpt9", true)]
        [TestCase("console", false)]
        [TestCase("com", false)]
        [TestCase("com0", false)]
        [TestCase("com10", false)]
        [TestCase("lpt", false)]
        [TestCase("lpt0", false)]
        [TestCase("nullable", false)]
        [TestCase("player", false)]
        public void IsReservedSlotKey_MatchesDeviceAndStateFileNames(string key, bool reserved)
        {
            Assert.That(SaveLayout.IsReservedSlotKey(key), Is.EqualTo(reserved));
        }

        [Test]
        public void Register_KeysThatOnlyLookLikeDevices_Succeeds()
        {
            var registry = new SaveSlotRegistry();

            registry.Register(new ProfileSlot("console"));
            registry.Register(new ProfileSlot("com0"));
            registry.Register(new ProfileSlot("com10"));

            Assert.That(registry.Count, Is.EqualTo(3));
        }

        [TestCase(SyncMode.CloudSync)]
        [TestCase(SyncMode.CloudReadOnly)]
        public void Register_DeviceScopeWithCloudMode_Throws(SyncMode mode)
        {
            var registry = new SaveSlotRegistry();

            Assert.Throws<ArgumentException>(() => registry.Register(new ConfigurableSlot("device_cloud", mode, SlotScope.Device, 1)));
        }

        [Test]
        public void Register_DeviceScopeLocalOnly_Succeeds()
        {
            var registry = new SaveSlotRegistry();

            registry.Register(new DeviceSettingsSlot());

            Assert.That(registry.Count, Is.EqualTo(1));
        }

        [Test]
        public void Register_UnknownSyncModeOrScope_Throws()
        {
            var registry = new SaveSlotRegistry();

            Assert.Throws<ArgumentException>(() => registry.Register(new ConfigurableSlot("bad_mode", (SyncMode)99, SlotScope.Profile, 1)));
            Assert.Throws<ArgumentException>(() => registry.Register(new ConfigurableSlot("bad_scope", SyncMode.LocalOnly, (SlotScope)99, 1)));
            Assert.That(registry.Count, Is.EqualTo(0));
        }

        [TestCase(0)]
        [TestCase(-1)]
        public void Register_SchemaVersionBelowOne_Throws(int schema)
        {
            var registry = new SaveSlotRegistry();

            Assert.Throws<ArgumentException>(() => registry.Register(new ConfigurableSlot("schema_zero", SyncMode.LocalOnly, SlotScope.Profile, schema)));
        }

        [Test]
        public void Freeze_RegisterAfterFreeze_ThrowsInvalidOperation()
        {
            var registry = new SaveSlotRegistry(new SaveSlot[] { new ProfileSlot() });
            registry.Freeze();

            Assert.That(registry.IsFrozen, Is.True);
            Assert.Throws<InvalidOperationException>(() => registry.Register(new ProfileSlot("late")));
            Assert.That(registry.Count, Is.EqualTo(1));
        }

        [Test]
        public void Constructor_InvalidSlotInSequence_Throws()
        {
            Assert.Throws<ArgumentException>(() => new SaveSlotRegistry(new SaveSlot[] { new ProfileSlot("ok"), new ProfileSlot("OK") }));
            Assert.Throws<ArgumentNullException>(() => new SaveSlotRegistry(null));
        }

        [Test]
        public void TryGet_IsCaseInsensitive_ContainsRequiresSameInstance()
        {
            var slot = new ProfileSlot("Player");
            var registry = new SaveSlotRegistry(new SaveSlot[] { slot });

            Assert.That(registry.TryGet("pLaYeR", out SaveSlot found), Is.True);
            Assert.That(found, Is.SameAs(slot));
            Assert.That(registry.TryGet(null, out _), Is.False);
            Assert.That(registry.Contains(slot), Is.True);
            Assert.That(registry.Contains(new ProfileSlot("Player")), Is.False);
        }

        [Test]
        public void Select_FiltersByScopeAndMode_KeepsRegistrationOrder()
        {
            var cloudA = new ProfileSlot("cloud_a");
            var local = new ProfileSlot("local", SyncMode.LocalOnly);
            var device = new DeviceSettingsSlot();
            var readOnly = new CloudReadOnlySlot();
            var cloudB = new ProfileSlot("cloud_b");
            var registry = new SaveSlotRegistry(new SaveSlot[] { cloudA, local, device, readOnly, cloudB });

            Assert.That(registry.Select(SlotScope.Device), Is.EqualTo(new SaveSlot[] { device }));
            Assert.That(registry.Select(SyncMode.CloudSync), Is.EqualTo(new SaveSlot[] { cloudA, cloudB }));
            Assert.That(registry.Select(SlotScope.Profile, SyncMode.LocalOnly), Is.EqualTo(new SaveSlot[] { local }));
            Assert.That(registry.SelectProfileCloudSlots(), Is.EqualTo(new SaveSlot[] { cloudA, readOnly, cloudB }));
            Assert.That(registry.Slots, Is.EqualTo(new SaveSlot[] { cloudA, local, device, readOnly, cloudB }));
        }

        [Test]
        public void ServiceConstructor_DuplicateKeys_Throws()
        {
            Assert.Throws<ArgumentException>(() => TestServiceFactory.Create(new ProfileSlot("player"), new ProfileSlot("PLAYER")));
        }

        [Test]
        public void ServiceConstructor_DeviceScopeCloudSync_Throws()
        {
            Assert.Throws<ArgumentException>(() => TestServiceFactory.Create(new ConfigurableSlot("device_cloud", SyncMode.CloudSync, SlotScope.Device, 1)));
        }

        [Test]
        public void ServiceConstructor_SlotOwnedByAnotherLiveService_Throws_AndOwnerKeepsIt()
        {
            var slot = new ProfileSlot();
            using (TestServiceContext owner = TestServiceFactory.Create(slot))
            {
                Assert.That(() => TestServiceFactory.Create(slot), Throws.InstanceOf<Exception>());
                Assert.That(slot.Host, Is.SameAs(owner.Service));
            }
        }

        private sealed class ConfigurableSlot : TestSlot<SettingsData>
        {
            public ConfigurableSlot(string key, SyncMode syncMode, SlotScope scope, int schemaVersion)
                : base(key, syncMode, scope, schemaVersion)
            {
            }
        }
    }
}
