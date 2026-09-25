using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ecanakli.SaveSystem.Tests
{
    /// <summary>01 s13 item 13 and 04b ADR-003 principle 5: the query members only read; the probe answers before InitializeAsync.</summary>
    [TestFixture]
    public sealed class ProbeTests
    {
        private const string CorruptText = "{ \"fmt\": 1, \"data\": ";

        private static readonly ProfileId Probed = ProfileId.Local("probe");

        /// <summary>Local state the probe is pointed at.</summary>
        public enum ProbeState
        {
            ValidPrimary,
            NoDirectory,
            ReadFails,
            CorruptPrimary,
        }

        // A tmp file is a readable copy of its own generation; the probe reports it but never promotes it
        [Test]
        public void ProbeLocalPresence_TmpOnly_DoesNotPromote()
        {
            var storage = new InMemorySaveStorage();
            string primary = TestPaths.ProfileSlot(Probed, TestSlotKeys.Player);
            string tmp = TestPaths.Tmp(primary);
            storage.SetBytes(tmp, EncodePlayerEnvelope(7, 3));

            using (TestServiceContext context = CreateContext(storage, new ProfileSlot()))
            {
                Assert.That(context.InitializeSync().IsSuccess, Is.True);
                Assert.That(context.Service.ActiveProfile, Is.Not.EqualTo(Probed), "Premise: the probed profile is not the active one.");
                StorageSnapshot before = context.Storage.Snapshot();
                context.Storage.ResetCounters();

                LocalPresence presence = context.Service.ProbeLocalPresence(Probed);

                Assert.That(presence, Is.EqualTo(LocalPresence.Present), "A readable .tmp is content.");
                Assert.That(context.Storage.HasFile(tmp), Is.True, "The tmp file stays where it is.");
                Assert.That(context.Storage.HasFile(primary), Is.False, "No primary is created by a probe.");
                Assert.That(context.Storage.HasFile(TestPaths.Bak(primary)), Is.False);
                AssertNothingChanged(context.Storage, before);
            }
        }

        // An unusable tmp is not a generation either; still nothing is promoted, renamed or deleted
        [Test]
        public void ProbeLocalPresence_CorruptTmpOnly_IsAbsentAndPromotesNothing()
        {
            var storage = new InMemorySaveStorage();
            string primary = TestPaths.ProfileSlot(Probed, TestSlotKeys.Player);
            string tmp = TestPaths.Tmp(primary);
            storage.SetText(tmp, CorruptText);

            using (TestServiceContext context = CreateContext(storage, new ProfileSlot()))
            {
                Assert.That(context.InitializeSync().IsSuccess, Is.True);
                StorageSnapshot before = context.Storage.Snapshot();
                context.Storage.ResetCounters();

                LocalPresence presence = context.Service.ProbeLocalPresence(Probed);

                Assert.That(presence, Is.EqualTo(LocalPresence.Absent), "An unreadable tmp without a primary is no content.");
                Assert.That(context.Storage.HasFile(tmp), Is.True, "The tmp file is not quarantined or deleted.");
                Assert.That(context.Storage.HasFile(primary), Is.False);
                AssertNothingChanged(context.Storage, before);
            }
        }

        // ADR-003 principle 5 over every query member at once, with a stray tmp and a corrupt primary present
        [Test]
        public void QueryMembers_ZeroStorageWrites()
        {
            var player = new ProfileSlot();
            ProfileId offline = ProfileId.Local("offline");
            using (TestServiceContext context = CreateContext(null, player))
            {
                Assert.That(context.InitializeSync().IsSuccess, Is.True);
                Assert.That(context.ActivateSync(offline).IsSuccess, Is.True);
                Assert.That(player.Mutate(data => data.Coins = 4), Is.True);
                Assert.That(context.Service.FlushLocalNow().IsComplete, Is.True);

                // States a repairing read would touch
                string offlineSlot = TestPaths.ProfileSlot(offline, TestSlotKeys.Player);
                string probedSlot = TestPaths.ProfileSlot(Probed, TestSlotKeys.Player);
                context.Storage.SetText(TestPaths.Tmp(offlineSlot), CorruptText);
                context.Storage.SetText(probedSlot, CorruptText);

                StorageSnapshot before = context.Storage.Snapshot();
                context.Storage.ResetCounters();

                bool initialized = context.Service.IsInitialized;
                bool ready = context.Service.IsReady;
                LocalPresence activePresence = context.Service.ProbeLocalPresence(offline);
                LocalPresence corruptPresence = context.Service.ProbeLocalPresence(Probed);
                LocalPresence missingPresence = context.Service.ProbeLocalPresence(ProfileId.Local("missing"));
                IReadOnlyList<ProfileId> profiles = context.Service.GetLocalProfiles();
                int coins = player.Read(data => data.Coins);

                AssertNothingChanged(context.Storage, before);

                // Sanity only; the invariant above is the zero writes
                Assert.That(initialized, Is.True);
                Assert.That(ready, Is.True);
                Assert.That(activePresence, Is.EqualTo(LocalPresence.Present));
                Assert.That(corruptPresence, Is.EqualTo(LocalPresence.Undetermined));
                Assert.That(missingPresence, Is.EqualTo(LocalPresence.Absent));
                Assert.That(profiles, Does.Contain(offline));
                Assert.That(coins, Is.EqualTo(4));
            }
        }

        // 01 s13 item 13: Present, Absent and Undetermined (IO error, corrupt primary) without any InitializeAsync
        [TestCase(ProbeState.ValidPrimary, LocalPresence.Present)]
        [TestCase(ProbeState.NoDirectory, LocalPresence.Absent)]
        [TestCase(ProbeState.ReadFails, LocalPresence.Undetermined)]
        [TestCase(ProbeState.CorruptPrimary, LocalPresence.Undetermined)]
        public void ProbeLocalPresence_BeforeInitialize_ReportsPresence(ProbeState state, LocalPresence expected)
        {
            var storage = new InMemorySaveStorage();
            string primary = TestPaths.ProfileSlot(Probed, TestSlotKeys.Player);
            switch (state)
            {
                case ProbeState.ValidPrimary:
                    storage.SetBytes(primary, EncodePlayerEnvelope(5, 2));
                    break;
                case ProbeState.ReadFails:
                    storage.SetBytes(primary, EncodePlayerEnvelope(5, 2));
                    storage.FailWithIOException(StorageOperation.Read, primary);
                    break;
                case ProbeState.CorruptPrimary:
                    storage.SetText(primary, CorruptText);
                    break;
                default:
                    // NoDirectory: the profile has no files at all
                    break;
            }

            using (TestServiceContext context = CreateContext(storage, new ProfileSlot()))
            {
                StorageSnapshot before = context.Storage.Snapshot();
                context.Storage.ResetCounters();

                LocalPresence presence = context.Service.ProbeLocalPresence(Probed);

                Assert.That(context.Service.IsInitialized, Is.False, "Premise: the probe runs before InitializeAsync.");
                Assert.That(presence, Is.EqualTo(expected));
                AssertNothingChanged(context.Storage, before);
            }
        }

        private static void AssertNothingChanged(InMemorySaveStorage storage, StorageSnapshot before)
        {
            Assert.That(storage.MutationCallCount, Is.EqualTo(0), string.Join("\n", storage.Calls));
            Assert.That(storage.WriteAttemptCount, Is.EqualTo(0));
            IReadOnlyList<string> after = storage.AllFilePaths;
            Assert.That(after, Is.EqualTo(before.FilePaths));
            for (int i = 0; i < after.Count; i++)
            {
                Assert.That(storage.GetBytes(after[i]), Is.EqualTo(before.GetBytes(after[i])), after[i]);
            }
        }

        private static byte[] EncodePlayerEnvelope(int coins, long revision)
        {
            var envelope = new SaveEnvelope
            {
                Format = SaveEnvelope.CurrentFormat,
                Schema = 1,
                Revision = revision,
                DeviceId = TestServiceFactory.DefaultDeviceId,
                Data = new JObject { { "Coins", coins }, { "Level", 0 }, { "Items", new JArray() } },
            };

            return EnvelopeCodec.Encode(envelope).Bytes;
        }

        private static TestServiceContext CreateContext(InMemorySaveStorage storage, params SaveSlot[] slots)
        {
            return TestServiceFactory.Create(new TestServiceSetup
            {
                Slots = slots,
                Storage = storage,
                ConfigureOptions = options => options.CloudRetryCount = 0,
            });
        }
    }
}
