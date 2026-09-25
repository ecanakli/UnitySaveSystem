using System;
using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ecanakli.SaveSystem.Tests
{
    /// <summary>01 s13.7 default conflict rules: the empty side loses, otherwise cloud wins; the conflict file keeps both sides (04b B5).</summary>
    [TestFixture]
    public sealed class DefaultConflictPolicyTests
    {
        private const string AccountA = "account-a";

        private static readonly ProfileId ProfileA = ProfileId.Account(AccountA);

        [TestCase(false, false, ConflictResolutionKind.TakeCloud)]
        [TestCase(false, true, ConflictResolutionKind.KeepLocal)]
        [TestCase(true, false, ConflictResolutionKind.TakeCloud)]
        [TestCase(true, true, ConflictResolutionKind.TakeCloud)]
        public void Resolve_EmptySideLoses_OtherwiseCloudWins(bool localIsEmpty, bool cloudIsEmpty, ConflictResolutionKind expected)
        {
            var local = new ProfileData { Coins = 7 };
            var cloud = new ProfileData { Coins = 9 };
            ConflictContext<ProfileData> context = CreateContext(local, cloud, localIsEmpty, cloudIsEmpty);

            ConflictResolution<ProfileData> resolution = DefaultConflictPolicy.Resolve(in context);

            Assert.That(resolution.Kind, Is.EqualTo(expected));
            Assert.That(DefaultConflictPolicy.ResolveKind(localIsEmpty, cloudIsEmpty), Is.EqualTo(expected), "Resolve and ResolveKind agree.");
            Assert.That(resolution.MergedData, Is.Null, "The default policy never merges.");
            Assert.That(local.Coins, Is.EqualTo(7), "The context inputs are read-only.");
            Assert.That(cloud.Coins, Is.EqualTo(9));
        }

        // Revisions, timestamps and device ids are diagnostics only
        [Test]
        public void Resolve_ContentOnBothSides_IgnoresRevisionsAndTimestamps()
        {
            var local = new ProfileData { Coins = 7 };
            var cloud = new ProfileData { Coins = 9 };
            var metadata = new ConflictMetadata(99, 1, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc), "device-other", true);
            var context = new ConflictContext<ProfileData>(TestSlotKeys.Player, local, cloud, false, false, metadata);

            ConflictResolution<ProfileData> resolution = DefaultConflictPolicy.Resolve(in context);

            Assert.That(resolution.Kind, Is.EqualTo(ConflictResolutionKind.TakeCloud), "A newer local revision does not win.");
            Assert.That(context.SlotKey, Is.EqualTo(TestSlotKeys.Player));
            Assert.That(context.LocalRevision, Is.EqualTo(99));
            Assert.That(context.CloudRevision, Is.EqualTo(1));
            Assert.That(context.CloudDeviceId, Is.EqualTo("device-other"));
            Assert.That(context.HasSyncedBefore, Is.True);
        }

        // Row 11 with an empty local side: the claimed guest slot has a file but no progress
        [Test]
        public void EmptyLocalSlot_CloudHasContent_TakesCloud_ConflictFileHoldsBothSides()
        {
            using (TwoDeviceHarness harness = CreateHarness())
            {
                TestServiceContext other = harness.DeviceB;
                ActivateReconciled(other);
                Assert.That(other.Slot<ProfileSlot>().Mutate(data => data.Coins = 9), Is.True);
                Assert.That(other.FlushSync().IsComplete, Is.True);
                string cloudVersion = harness.CloudStore.GetVersion(AccountA, TestSlotKeys.Player);
                Assert.That(cloudVersion, Is.Not.Null, "Premise: the cloud holds content.");

                TestServiceContext device = harness.DeviceA;
                ProfileSlot slot = device.Slot<ProfileSlot>();
                Assert.That(device.InitializeSync().IsSuccess, Is.True);
                Assert.That(device.Service.ActiveProfile, Is.EqualTo(ProfileId.Guest), "Premise: playing as guest.");

                // An empty save still writes the file, so the claimed slot is Present without progress
                Assert.That(slot.Mutate(data => data.Coins = 0), Is.True);
                Assert.That(device.Service.FlushLocalNow().IsComplete, Is.True);
                Assert.That(device.Storage.HasFile(TestPaths.ProfileSlot(ProfileId.Guest, TestSlotKeys.Player)), Is.True, "Premise: the guest slot has a file.");

                ProfileActivationResult activated = device.ActivateSync(ProfileA);
                Assert.That(activated.IsSuccess, Is.True, activated.ToString());
                Assert.That(activated.ClaimedGuestData, Is.True, "Premise: the empty guest slot was claimed.");
                Assert.That(device.Storage.HasFile(TestPaths.ProfileSlot(ProfileA, TestSlotKeys.Player)), Is.True);
                Assert.That(slot.PeekData().Coins, Is.EqualTo(0));
                device.Provider.ClearCalls();

                var observed = new List<string>();
                slot.ConflictResolver = (in ConflictContext<ProfileData> conflict) =>
                {
                    observed.Add("localEmpty=" + conflict.LocalIsEmpty + " cloudEmpty=" + conflict.CloudIsEmpty + " hasSyncedBefore=" + conflict.HasSyncedBefore);
                    return DefaultConflictPolicy.Resolve(in conflict);
                };

                RestoreReport report = RestoreSync(device);

                Assert.That(report.Status, Is.EqualTo(SaveStatus.Success), report.ToString());
                Assert.That(slot.ResolveConflictCount, Is.EqualTo(1), report.ToString());
                Assert.That(observed, Is.EqualTo(new[] { "localEmpty=True cloudEmpty=False hasSyncedBefore=False" }));
                Assert.That(report.GetResult(slot).Outcome, Is.EqualTo(SlotRestoreOutcome.Restored), report.ToString());
                Assert.That(slot.PeekData().Coins, Is.EqualTo(9), "The empty local side loses.");

                JObject conflict = ReadConflictFile(device, TestSlotKeys.Player);
                Assert.That(conflict.Value<string>("resolution"), Is.EqualTo(ConflictResolutionKind.TakeCloud.ToString()));
                Assert.That(ReadInt(conflict["local"], "Coins"), Is.EqualTo(0), conflict.ToString());
                Assert.That(ReadInt(conflict["cloud"], "Coins"), Is.EqualTo(9), conflict.ToString());
                Assert.That(device.Provider.WriteCallCount, Is.EqualTo(0), "Nothing is uploaded during the reconcile.");
                Assert.That(harness.CloudStore.GetVersion(AccountA, TestSlotKeys.Player), Is.EqualTo(cloudVersion));
            }
        }

        private static ConflictContext<ProfileData> CreateContext(ProfileData local, ProfileData cloud, bool localIsEmpty, bool cloudIsEmpty)
        {
            var metadata = new ConflictMetadata(2, 3, null, TwoDeviceHarness.DeviceBId, false);
            return new ConflictContext<ProfileData>(TestSlotKeys.Player, local, cloud, localIsEmpty, cloudIsEmpty, metadata);
        }

        // Gateway retries off so provider errors surface in one call; both devices signed in
        private static TwoDeviceHarness CreateHarness()
        {
            var harness = new TwoDeviceHarness(() => new SaveSlot[] { new ProfileSlot() }, null, options => options.CloudRetryCount = 0);
            harness.SignIn(AccountA);
            return harness;
        }

        private static void ActivateReconciled(TestServiceContext context)
        {
            Assert.That(context.InitializeSync().IsSuccess, Is.True);
            if (context.Service.ActiveProfile != ProfileA)
            {
                ProfileActivationResult activated = context.ActivateSync(ProfileA);
                Assert.That(activated.IsSuccess, Is.True, activated.ToString());
            }

            RestoreReport report = RestoreSync(context);
            Assert.That(report.Status, Is.EqualTo(SaveStatus.Success), report.ToString());
            context.Provider.ClearCalls();
        }

        private static RestoreReport RestoreSync(TestServiceContext context)
        {
            return AsyncTestUtility.RunSync(context.Service.RestoreAsync(CancellationToken.None), nameof(SaveService.RestoreAsync));
        }

        private static JObject ReadConflictFile(TestServiceContext context, string key)
        {
            string path = SaveLayout.ConflictPath(TestPaths.ProfileDirectory(ProfileA), key);
            Assert.That(context.Storage.HasFile(path), Is.True, string.Join(", ", context.Storage.AllFilePaths));
            return (JObject)SaveJson.Parse(context.Storage.GetBytes(path));
        }

        // Property names depend on the serializer settings, so match case-insensitively
        private static int ReadInt(JToken token, string property)
        {
            Assert.That(token, Is.InstanceOf<JObject>(), token?.ToString());
            JToken value = ((JObject)token).GetValue(property, StringComparison.OrdinalIgnoreCase);
            Assert.That(value, Is.Not.Null, token.ToString());
            return value.Value<int>();
        }
    }
}
