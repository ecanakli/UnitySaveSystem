using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;

namespace Ecanakli.SaveSystem.Tests
{
    /// <summary>Proves the fakes and the service factory work; behavior coverage lives in the phase 3b-3d suites.</summary>
    [TestFixture]
    public sealed class SmokeTests
    {
        [Test]
        public void InitializeAsync_EmptyStorage_ServiceBecomesReady()
        {
            using (TestServiceContext context = TestServiceFactory.Create(new ProfileSlot(), new DeviceSettingsSlot()))
            {
                InitializeResult result = context.InitializeSync();

                Assert.That(result.IsSuccess, Is.True, result.ToString());
                Assert.That(context.Service.IsReady, Is.True);
                Assert.That(context.Service.ActiveProfile, Is.EqualTo(ProfileId.Guest));
                Assert.That(context.Slot<ProfileSlot>().State, Is.EqualTo(SlotState.Ready));
                Assert.That(context.Slot<DeviceSettingsSlot>().State, Is.EqualTo(SlotState.Ready));
                Assert.That(context.Logger.Count(TestLogLevel.Error), Is.EqualTo(0), context.Logger.Describe());
            }
        }

        [Test]
        public void MutateThenFlushLocalNow_WritesSlotFile_AndRestartLoadsIt()
        {
            TestServiceContext context = TestServiceFactory.Create(new ProfileSlot());
            TestServiceContext restarted = null;
            try
            {
                Assert.That(context.InitializeSync().IsSuccess, Is.True);
                string path = TestPaths.ProfileSlot(ProfileId.Guest, TestSlotKeys.Player);

                Assert.That(context.Slot<ProfileSlot>().Mutate(data => data.Coins = 42), Is.True);
                LocalFlushResult flush = context.Service.FlushLocalNow();

                Assert.That(flush.IsComplete, Is.True, flush.ToString());
                Assert.That(context.Storage.GetWriteCount(path), Is.GreaterThan(0));
                EnvelopeDecodeResult decoded = EnvelopeCodec.Decode(context.Storage.GetBytes(path), 1);
                Assert.That(decoded.IsOk, Is.True, decoded.Message);
                Assert.That((int)decoded.Envelope.Data["Coins"], Is.EqualTo(42));

                restarted = TestServiceFactory.Restart(context, new ProfileSlot());
                Assert.That(restarted.InitializeSync().IsSuccess, Is.True);
                Assert.That(restarted.Slot<ProfileSlot>().Read(data => data.Coins), Is.EqualTo(42));
            }
            finally
            {
                context.Dispose();
                restarted?.Dispose();
            }
        }

        [Test]
        public void InMemoryStorage_SnapshotAndRestore_RoundTripsFilesAndDirectories()
        {
            var storage = new InMemorySaveStorage();
            const string path = "profiles/guest/player.json";
            byte[] first = Encoding.UTF8.GetBytes("first");
            byte[] second = Encoding.UTF8.GetBytes("second");

            storage.WriteAtomic(path, first);
            StorageSnapshot snapshot = storage.Snapshot();
            storage.WriteAtomic(path, second);
            storage.CreateDirectory("profiles/acc-extra");

            Assert.That(storage.GetBytes(TestPaths.Bak(path)), Is.EqualTo(first));
            Assert.That(storage.WriteCount, Is.EqualTo(2));

            storage.RestoreSnapshot(snapshot);

            Assert.That(storage.GetBytes(path), Is.EqualTo(first));
            Assert.That(storage.HasFile(TestPaths.Bak(path)), Is.False);
            Assert.That(storage.HasFile(TestPaths.Tmp(path)), Is.False);
            Assert.That(storage.ListDirectoryNames("profiles"), Is.EqualTo(new[] { "guest" }));
            Assert.That(storage.WriteCount, Is.EqualTo(2), "Counters are not part of a snapshot.");
        }

        [Test]
        public void ManualClock_Advance_CompletesOnlyDueDelays_AndHonoursCancellation()
        {
            var clock = new ManualSaveClock();
            DateTime start = clock.UtcNow;
            using (var cts = new CancellationTokenSource())
            {
                UniTask shortDelay = clock.Delay(TimeSpan.FromSeconds(1), CancellationToken.None);
                UniTask longDelay = clock.Delay(TimeSpan.FromSeconds(5), CancellationToken.None);
                UniTask canceledDelay = clock.Delay(TimeSpan.FromSeconds(3), cts.Token);

                clock.Advance(TimeSpan.FromSeconds(2));

                Assert.That(shortDelay.Status, Is.EqualTo(UniTaskStatus.Succeeded));
                Assert.That(longDelay.Status, Is.EqualTo(UniTaskStatus.Pending));
                Assert.That(clock.UtcNow, Is.EqualTo(start + TimeSpan.FromSeconds(2)));

                cts.Cancel();

                Assert.That(canceledDelay.Status, Is.EqualTo(UniTaskStatus.Canceled));
                Assert.Throws<OperationCanceledException>(() => canceledDelay.GetAwaiter().GetResult());
                Assert.That(clock.PendingDelayCount, Is.EqualTo(1));

                clock.Advance(TimeSpan.FromSeconds(3));

                Assert.That(longDelay.Status, Is.EqualTo(UniTaskStatus.Succeeded));
                Assert.That(clock.PendingDelayCount, Is.EqualTo(0));
            }
        }

        [Test]
        public void FakeCloudProvider_ConditionalWrite_RejectsStaleVersion_AndReadReturnsCurrentVersion()
        {
            var provider = new FakeCloudSaveProvider(signedInAccountId: "account-1");
            byte[] value = Encoding.UTF8.GetBytes("{\"coins\":1}");

            IReadOnlyList<CloudWriteResult> written = AsyncTestUtility.RunSync(
                provider.WriteAsync(new[] { new CloudWriteRequest("player", value, null, CloudAccess.ClientOwned) }, CancellationToken.None));
            Assert.That(written[0].IsSuccess, Is.True);
            Assert.That(written[0].NewVersion, Is.Not.Null);

            IReadOnlyList<CloudWriteResult> stale = AsyncTestUtility.RunSync(
                provider.WriteAsync(new[] { new CloudWriteRequest("player", value, "stale", CloudAccess.ClientOwned) }, CancellationToken.None));
            Assert.That(stale[0].IsSuccess, Is.False);
            Assert.That(stale[0].Error.Kind, Is.EqualTo(CloudErrorKind.Conflict));

            IReadOnlyList<CloudReadResult> read = AsyncTestUtility.RunSync(
                provider.ReadAsync(new[] { new CloudReadRequest("player", CloudAccess.ClientOwned) }, CancellationToken.None));
            Assert.That(read[0].Status, Is.EqualTo(CloudReadStatus.Found));
            Assert.That(read[0].Version, Is.EqualTo(written[0].NewVersion));
            Assert.That(read[0].Value, Is.EqualTo(value));
            Assert.That(provider.WriteCallCount, Is.EqualTo(2));
            Assert.That(provider.ReadCallCount, Is.EqualTo(1));
        }
    }
}
