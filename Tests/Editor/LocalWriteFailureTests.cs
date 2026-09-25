using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Ecanakli.SaveSystem.Tests
{
    /// <summary>Local write failures: dirty retention, health transitions, backoff, explicit writes that bypass it, profile.json and restore persist failures.</summary>
    [TestFixture]
    public sealed class LocalWriteFailureTests
    {
        private const int MaxFrames = 120;
        private const string AccountA = "account-a";

        private static readonly TimeSpan Margin = TimeSpan.FromMilliseconds(100);
        private static readonly string SlotPath = TestPaths.ProfileSlot(ProfileId.Guest, TestSlotKeys.Player);
        private static readonly ProfileId ProfileA = ProfileId.Account(AccountA);
        private static readonly string AccountSlotPath = TestPaths.ProfileSlot(ProfileA, TestSlotKeys.Player);

        [UnityTest]
        public IEnumerator ScheduledWriteDiskFull_KeepsDirty_RaisesHealthChangedOnce()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                var slot = new ProfileSlot();
                using (TestServiceContext context = CreateInitializedGuest(slot))
                {
                    var health = new List<LocalWriteHealth>();
                    Action<LocalWriteHealth> onHealthChanged = health.Add;
                    context.Service.LocalWriteHealthChanged += onHealthChanged;
                    try
                    {
                        context.Storage.FailWithKind(StorageOperation.Write, SlotPath, LocalWriteErrorKind.DiskFull);

                        await MutateAndRunScheduledWriteAsync(context, slot, 5, 1);

                        Assert.That(health.Count, Is.EqualTo(1), string.Join(", ", health));
                        Assert.That(health[0].IsHealthy, Is.False);
                        Assert.That(health[0].Kind, Is.EqualTo(LocalWriteErrorKind.DiskFull));
                        Assert.That(health[0].Failures.Single().SlotKey, Is.EqualTo(TestSlotKeys.Player));
                        Assert.That(health[0].Failures.Single().ConsecutiveFailures, Is.EqualTo(1));
                        Assert.That(context.Storage.HasFile(SlotPath), Is.False);
                        Assert.That(context.Storage.HasFile(TestPaths.Tmp(SlotPath)), Is.False, "The failed tmp is removed.");

                        // Only dirty slots are flushed, so a failure here proves the slot stayed dirty
                        LocalFlushResult flushed = context.Service.FlushLocalNow();
                        Assert.That(flushed.IsComplete, Is.False);
                        Assert.That(flushed.Failures.Single().SlotKey, Is.EqualTo(TestSlotKeys.Player));
                        Assert.That(flushed.Failures.Single().Kind, Is.EqualTo(LocalWriteErrorKind.DiskFull));
                        Assert.That(health.Count, Is.EqualTo(1), "Same kind again is not a transition.");
                        Assert.That(slot.PeekData().Coins, Is.EqualTo(5), "Memory keeps the unsaved change.");
                    }
                    finally
                    {
                        context.Service.LocalWriteHealthChanged -= onHealthChanged;
                    }
                }
            });
        }

        // S2: a slot whose snapshot throws was dropped with one log line and never became due again
        [UnityTest]
        public IEnumerator ScheduledWriteWithFailingSnapshot_ShowsInHealth_AndIsRetried()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                var slot = new SnapshotFailureSlot();
                string path = TestPaths.ProfileSlot(ProfileId.Guest, SnapshotFailureSlot.SlotKey);
                using (TestServiceContext context = CreateInitializedGuest(slot))
                {
                    var health = new List<LocalWriteHealth>();
                    Action<LocalWriteHealth> onHealthChanged = health.Add;
                    context.Service.LocalWriteHealthChanged += onHealthChanged;
                    try
                    {
                        Assert.That(slot.SetCoins(5), Is.True);
                        slot.FailSnapshot = true;

                        await AsyncTestUtility.WaitFramesAsync(2);
                        context.Clock.Advance(context.Options.LocalWriteDelay + Margin);
                        await AsyncTestUtility.WaitUntilAsync(() => health.Count >= 1, MaxFrames, "Snapshot failure recorded");

                        int firstAttempts = slot.SnapshotAttempts;
                        Assert.That(firstAttempts, Is.GreaterThanOrEqualTo(1), "Premise: the scheduled write tried to snapshot.");
                        Assert.That(health[0].IsHealthy, Is.False);
                        Assert.That(health[0].Failures.Single().SlotKey, Is.EqualTo(SnapshotFailureSlot.SlotKey));
                        Assert.That(health[0].Failures.Single().Message, Does.Contain("Snapshot"));
                        Assert.That(context.Storage.GetWriteCount(path), Is.EqualTo(0), "Nothing reached the file.");

                        // Backoff owns the retry: the slot is not parked until the next mutation
                        context.Clock.Advance(context.Options.LocalWriteBackoff[0] + Margin);
                        await AsyncTestUtility.WaitUntilAsync(() => slot.SnapshotAttempts > firstAttempts, MaxFrames, "Retry after the first backoff step");
                        Assert.That(health.Count, Is.EqualTo(1), "The same kind again is not a transition.");

                        // Recovery: the next scheduled attempt writes and health returns
                        slot.FailSnapshot = false;
                        context.Clock.Advance(context.Options.LocalWriteBackoff[1] + Margin);
                        await AsyncTestUtility.WaitUntilAsync(() => context.Storage.GetWriteCount(path) == 1, MaxFrames, "Write after recovery");
                        await AsyncTestUtility.WaitFramesAsync(2);

                        Assert.That(health.Count, Is.EqualTo(2), string.Join(", ", health));
                        Assert.That(health[1].IsHealthy, Is.True);
                        Assert.That(context.Service.FlushLocalNow().IsComplete, Is.True);
                        Assert.That(context.Storage.GetWriteCount(path), Is.EqualTo(1), "The slot is clean after the write.");
                    }
                    finally
                    {
                        context.Service.LocalWriteHealthChanged -= onHealthChanged;
                    }
                }
            });
        }

        [UnityTest]
        public IEnumerator RepeatedScheduledFailuresSameKind_NoAdditionalEvent_BackoffAdvances()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                var slot = new ProfileSlot();
                using (TestServiceContext context = CreateInitializedGuest(slot))
                {
                    var health = new List<LocalWriteHealth>();
                    Action<LocalWriteHealth> onHealthChanged = health.Add;
                    context.Service.LocalWriteHealthChanged += onHealthChanged;
                    try
                    {
                        IReadOnlyList<TimeSpan> backoff = context.Options.LocalWriteBackoff;
                        context.Storage.FailWithKind(StorageOperation.Write, SlotPath, LocalWriteErrorKind.DiskFull);
                        await MutateAndRunScheduledWriteAsync(context, slot, 5, 1);

                        context.Clock.Advance(backoff[0] + Margin);
                        await WaitForWriteAttemptsAsync(context, 2, "Retry after the first backoff step");

                        // The second step is longer; half of it retries nothing
                        context.Clock.Advance(TimeSpan.FromTicks(backoff[1].Ticks / 2));
                        await AsyncTestUtility.WaitFramesAsync(10);
                        Assert.That(WriteAttempts(context), Is.EqualTo(2), "No retry before the second backoff step passes.");

                        context.Clock.Advance(backoff[1] + Margin);
                        await WaitForWriteAttemptsAsync(context, 3, "Retry after the second backoff step");

                        Assert.That(health.Count, Is.EqualTo(1), string.Join(", ", health));
                        LocalFlushResult flushed = context.Service.FlushLocalNow();
                        Assert.That(flushed.Failures.Single().ConsecutiveFailures, Is.EqualTo(4));
                        Assert.That(health.Count, Is.EqualTo(1));
                    }
                    finally
                    {
                        context.Service.LocalWriteHealthChanged -= onHealthChanged;
                    }
                }
            });
        }

        [UnityTest]
        public IEnumerator Recovery_RaisesHealthyTransition_ResetsBackoff()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                var slot = new ProfileSlot();
                using (TestServiceContext context = CreateInitializedGuest(slot))
                {
                    var health = new List<LocalWriteHealth>();
                    Action<LocalWriteHealth> onHealthChanged = health.Add;
                    context.Service.LocalWriteHealthChanged += onHealthChanged;
                    try
                    {
                        StorageFault diskFull = context.Storage.FailWithKind(StorageOperation.Write, SlotPath, LocalWriteErrorKind.DiskFull);
                        await MutateAndRunScheduledWriteAsync(context, slot, 5, 1);
                        Assert.That(health.Count, Is.EqualTo(1));

                        context.Storage.RemoveFault(diskFull);
                        context.Clock.Advance(context.Options.LocalWriteBackoff[0] + Margin);
                        await AsyncTestUtility.WaitUntilAsync(() => context.Storage.GetWriteCount(SlotPath) == 1, MaxFrames, "Scheduled retry after recovery");
                        await AsyncTestUtility.WaitFramesAsync(2);

                        Assert.That(health.Count, Is.EqualTo(2), string.Join(", ", health));
                        Assert.That(health[1].IsHealthy, Is.True);
                        Assert.That(health[1].Kind, Is.Null);
                        Assert.That(health[1].Failures, Is.Empty);

                        // A reset backoff lets the next failure start from one consecutive failure after the normal write delay
                        context.Storage.FailWithKind(StorageOperation.Write, SlotPath, LocalWriteErrorKind.DiskFull);
                        await MutateAndRunScheduledWriteAsync(context, slot, 6, 3);

                        Assert.That(health.Count, Is.EqualTo(3), string.Join(", ", health));
                        Assert.That(health[2].IsHealthy, Is.False);
                        Assert.That(health[2].Failures.Single().ConsecutiveFailures, Is.EqualTo(1));
                    }
                    finally
                    {
                        context.Service.LocalWriteHealthChanged -= onHealthChanged;
                    }
                }
            });
        }

        [UnityTest]
        public IEnumerator MutationDuringBackoff_DoesNotRetryEarly()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                var slot = new ProfileSlot();
                using (TestServiceContext context = CreateInitializedGuest(slot))
                {
                    TimeSpan delay = context.Options.LocalWriteDelay;
                    TimeSpan firstBackoff = context.Options.LocalWriteBackoff[0];
                    Assert.That(delay + Margin + Margin, Is.LessThan(firstBackoff), "Premise: the write delay ends inside the first backoff step.");

                    StorageFault diskFull = context.Storage.FailWithKind(StorageOperation.Write, SlotPath, LocalWriteErrorKind.DiskFull);
                    await MutateAndRunScheduledWriteAsync(context, slot, 5, 1);

                    Assert.That(slot.Mutate(data => data.Coins = 6), Is.True);
                    context.Clock.Advance(delay + Margin);
                    await AsyncTestUtility.WaitFramesAsync(10);
                    Assert.That(WriteAttempts(context), Is.EqualTo(1), "A new mutation does not shorten the backoff.");

                    context.Storage.RemoveFault(diskFull);
                    context.Clock.Advance(firstBackoff);
                    await AsyncTestUtility.WaitUntilAsync(() => context.Storage.GetWriteCount(SlotPath) == 1, MaxFrames, "Write after the backoff");
                    await AsyncTestUtility.WaitFramesAsync(2);

                    Assert.That(WriteAttempts(context), Is.EqualTo(2));
                    Assert.That(context.Service.FlushLocalNow().IsComplete, Is.True);
                    Assert.That(context.Storage.GetWriteCount(SlotPath), Is.EqualTo(1), "The retry wrote the latest revision; nothing was left dirty.");
                }
            });
        }

        [Test]
        public void FlushLocalNow_IgnoresBackoff()
        {
            var slot = new ProfileSlot();
            using (TestServiceContext context = CreateInitializedGuest(slot))
            {
                var health = new List<LocalWriteHealth>();
                Action<LocalWriteHealth> onHealthChanged = health.Add;
                context.Service.LocalWriteHealthChanged += onHealthChanged;
                try
                {
                    StorageFault diskFull = context.Storage.FailWithKind(StorageOperation.Write, SlotPath, LocalWriteErrorKind.DiskFull);
                    Assert.That(slot.Mutate(data => data.Coins = 5), Is.True);

                    Assert.That(context.Service.FlushLocalNow().IsComplete, Is.False);
                    Assert.That(WriteAttempts(context), Is.EqualTo(1));
                    Assert.That(health.Count, Is.EqualTo(1));

                    // Same instant, still inside the backoff
                    Assert.That(context.Service.FlushLocalNow().IsComplete, Is.False);
                    Assert.That(WriteAttempts(context), Is.EqualTo(2), "An explicit flush always attempts the write.");

                    context.Storage.RemoveFault(diskFull);
                    LocalFlushResult recovered = context.Service.FlushLocalNow();

                    Assert.That(recovered.IsComplete, Is.True, recovered.ToString());
                    Assert.That(context.Storage.GetWriteCount(SlotPath), Is.EqualTo(1));
                    Assert.That(health.Count, Is.EqualTo(2));
                    Assert.That(health[1].IsHealthy, Is.True);
                }
                finally
                {
                    context.Service.LocalWriteHealthChanged -= onHealthChanged;
                }
            }
        }

        [Test]
        public void SaveNowAsync_DiskFull_ReturnsDiskFullCode_IgnoresBackoff()
        {
            var slot = new ProfileSlot();
            using (TestServiceContext context = CreateInitializedGuest(slot))
            {
                var health = new List<LocalWriteHealth>();
                Action<LocalWriteHealth> onHealthChanged = health.Add;
                context.Service.LocalWriteHealthChanged += onHealthChanged;
                try
                {
                    context.Storage.FailWithKind(StorageOperation.Write, SlotPath, LocalWriteErrorKind.DiskFull);
                    Assert.That(slot.Mutate(data => data.Coins = 5), Is.True);

                    SaveResult first = SaveNowSync(slot);

                    Assert.That(first.Status, Is.EqualTo(SaveStatus.Failed), first.ToString());
                    Assert.That(first.Error.Code, Is.EqualTo(SaveErrorCode.DiskFull));
                    Assert.That(first.DurableRevision, Is.EqualTo(0));
                    Assert.That(health.Count, Is.EqualTo(1));
                    Assert.That(health[0].Kind, Is.EqualTo(LocalWriteErrorKind.DiskFull));

                    SaveResult second = SaveNowSync(slot);

                    Assert.That(second.Error.Code, Is.EqualTo(SaveErrorCode.DiskFull), second.ToString());
                    Assert.That(WriteAttempts(context), Is.EqualTo(2), "SaveNowAsync ignores the backoff.");
                    Assert.That(health.Count, Is.EqualTo(1));
                    Assert.That(context.Storage.HasFile(SlotPath), Is.False);
                }
                finally
                {
                    context.Service.LocalWriteHealthChanged -= onHealthChanged;
                }
            }
        }

        [TestCase(LocalWriteErrorKind.AccessDenied, SaveErrorCode.AccessDenied)]
        [TestCase(LocalWriteErrorKind.IoError, SaveErrorCode.IoError)]
        public void CustomSaveStorageException_KindPreserved(LocalWriteErrorKind kind, SaveErrorCode expectedCode)
        {
            var slot = new ProfileSlot();
            using (TestServiceContext context = CreateInitializedGuest(slot))
            {
                var health = new List<LocalWriteHealth>();
                Action<LocalWriteHealth> onHealthChanged = health.Add;
                context.Service.LocalWriteHealthChanged += onHealthChanged;
                try
                {
                    // The inner exception alone would classify as DiskFull; the explicit kind wins
                    context.Storage.Fail(StorageOperation.Write, SlotPath, () => new CustomStorageException(kind));
                    Assert.That(slot.Mutate(data => data.Coins = 5), Is.True);

                    LocalFlushResult flushed = context.Service.FlushLocalNow();

                    Assert.That(flushed.Failures.Single().Kind, Is.EqualTo(kind), flushed.ToString());
                    Assert.That(health.Single().Kind, Is.EqualTo(kind));

                    SaveResult saved = SaveNowSync(slot);
                    Assert.That(saved.Error.Code, Is.EqualTo(expectedCode), saved.ToString());
                }
                finally
                {
                    context.Service.LocalWriteHealthChanged -= onHealthChanged;
                }
            }
        }

        // 04a item 6 + A3 5.3 guard: PendingWriteId cannot reach profile.json, so nothing is sent; the next flush after recovery uploads
        [Test]
        public void ProfileJsonPendingWriteIdPersistFails_UploadSkippedLocalWriteFailed()
        {
            using (TwoDeviceHarness harness = CreateSignedInHarness())
            {
                TestServiceContext context = harness.DeviceA;
                ProfileSlot slot = context.Slot<ProfileSlot>();
                ActivateReconciledAccount(context);
                UploadCoins(context, slot, 5);
                string cloudVersion = harness.CloudStore.GetVersion(AccountA, TestSlotKeys.Player);
                string profileStatePath = TestPaths.ProfileState(ProfileA);
                byte[] profileStateBefore = context.Storage.GetBytes(profileStatePath);
                Assert.That(profileStateBefore, Is.Not.Null, "Premise: profile.json exists.");
                context.Provider.ClearCalls();

                var health = new List<LocalWriteHealth>();
                Action<LocalWriteHealth> onHealthChanged = health.Add;
                context.Service.LocalWriteHealthChanged += onHealthChanged;
                try
                {
                    StorageFault stateWriteFails = context.Storage.FailWithKind(StorageOperation.Write, profileStatePath, LocalWriteErrorKind.DiskFull);
                    Assert.That(slot.Mutate(data => data.Coins = 6), Is.True);

                    FlushResult skipped = context.FlushSync();

                    CloudFlushSlotResult entry = CloudEntry(skipped, TestSlotKeys.Player);
                    Assert.That(entry.Status, Is.EqualTo(CloudFlushStatus.Skipped), skipped.ToString());
                    Assert.That(entry.Reason, Is.EqualTo(CloudFlushReason.LocalWriteFailed), skipped.ToString());
                    Assert.That(skipped.IsComplete, Is.False, skipped.ToString());
                    Assert.That(context.Provider.WriteCallCount, Is.EqualTo(0), "No provider write without a persisted PendingWriteId.");
                    Assert.That(harness.CloudStore.GetVersion(AccountA, TestSlotKeys.Player), Is.EqualTo(cloudVersion));
                    Assert.That(ReadCoins(context.Storage.GetBytes(AccountSlotPath)), Is.EqualTo(6), "Disk first: the slot file itself was written.");
                    Assert.That(context.Storage.GetBytes(profileStatePath), Is.EqualTo(profileStateBefore), "profile.json is unchanged.");
                    Assert.That(LoadAccountSlotSyncState(context).PendingWriteId, Is.Null);

                    Assert.That(health.Count, Is.EqualTo(1), string.Join(", ", health));
                    Assert.That(health[0].IsHealthy, Is.False);
                    Assert.That(health[0].Kind, Is.EqualTo(LocalWriteErrorKind.DiskFull));
                    Assert.That(health[0].Failures.Single().SlotKey, Is.Null, "The failing file is profile.json.");

                    context.Storage.RemoveFault(stateWriteFails);
                    FlushResult uploaded = context.FlushSync();

                    Assert.That(uploaded.IsComplete, Is.True, uploaded.ToString());
                    Assert.That(CloudEntry(uploaded, TestSlotKeys.Player).Status, Is.EqualTo(CloudFlushStatus.Uploaded), uploaded.ToString());
                    IReadOnlyList<CloudWriteRequest> writes = context.Provider.GetAllWriteRequests();
                    Assert.That(writes.Count, Is.EqualTo(1));
                    Assert.That(writes[0].ExpectedVersion, Is.EqualTo(cloudVersion));
                    Assert.That(ReadCoins(harness.CloudStore.GetValue(AccountA, TestSlotKeys.Player)), Is.EqualTo(6));

                    SlotSyncState sync = LoadAccountSlotSyncState(context);
                    Assert.That(sync.PendingWriteId, Is.Null);
                    Assert.That(sync.LastSyncedWriteId, Is.EqualTo(ReadWriteId(harness.CloudStore.GetValue(AccountA, TestSlotKeys.Player))));
                    Assert.That(health.Count, Is.EqualTo(2), string.Join(", ", health));
                    Assert.That(health[1].IsHealthy, Is.True);
                }
                finally
                {
                    context.Service.LocalWriteHealthChanged -= onHealthChanged;
                }
            }
        }

        // 04a A3 5.4 step 9: a failed slot write during restore keeps the applied data, sets LocalPersistFailed, keeps completeness and stays local-dirty
        [Test]
        public void RestorePersistFails_SetsLocalPersistFailed_SlotStaysLocalDirty()
        {
            using (TwoDeviceHarness harness = CreateSignedInHarness())
            {
                TestServiceContext context = harness.DeviceA;
                ProfileSlot slot = context.Slot<ProfileSlot>();
                ActivateReconciledAccount(context);
                UploadCoins(context, slot, 5);

                // The other device moves the cloud on while this device is clean
                TestServiceContext other = harness.DeviceB;
                ActivateReconciledAccount(other);
                UploadCoins(other, other.Slot<ProfileSlot>(), 9);

                byte[] fileBefore = context.Storage.GetBytes(AccountSlotPath);
                Assert.That(ReadCoins(fileBefore), Is.EqualTo(5), "Premise: the local file holds the old value.");
                context.Provider.ClearCalls();

                var health = new List<LocalWriteHealth>();
                Action<LocalWriteHealth> onHealthChanged = health.Add;
                context.Service.LocalWriteHealthChanged += onHealthChanged;
                try
                {
                    StorageFault diskFull = context.Storage.FailWithKind(StorageOperation.Write, AccountSlotPath, LocalWriteErrorKind.DiskFull);

                    RestoreReport report = RestoreSync(context);

                    Assert.That(report.Status, Is.EqualTo(SaveStatus.Success), report.ToString());
                    Assert.That(report.Completeness, Is.EqualTo(RestoreCompleteness.Full), "A persist failure does not reduce completeness. " + report);
                    SlotRestoreResult result = report.GetResult(slot);
                    Assert.That(result.Outcome, Is.EqualTo(SlotRestoreOutcome.Restored), report.ToString());
                    Assert.That(result.IsSuccess, Is.True);
                    Assert.That(result.LocalPersistFailed, Is.True, report.ToString());
                    Assert.That(slot.PeekData().Coins, Is.EqualTo(9), "The cloud value is applied in memory.");
                    Assert.That(context.Storage.GetBytes(AccountSlotPath), Is.EqualTo(fileBefore), "The failed write leaves the old file.");
                    Assert.That(health.Count, Is.EqualTo(1), string.Join(", ", health));
                    Assert.That(health[0].Kind, Is.EqualTo(LocalWriteErrorKind.DiskFull));
                    Assert.That(health[0].Failures.Single().SlotKey, Is.EqualTo(TestSlotKeys.Player));

                    // Only local-dirty slots are flushed, so another failed attempt proves the slot stayed dirty
                    LocalFlushResult stillFailing = context.Service.FlushLocalNow();
                    Assert.That(stillFailing.IsComplete, Is.False);
                    Assert.That(stillFailing.Failures.Single().SlotKey, Is.EqualTo(TestSlotKeys.Player));

                    context.Storage.RemoveFault(diskFull);
                    LocalFlushResult recovered = context.Service.FlushLocalNow();

                    Assert.That(recovered.IsComplete, Is.True, recovered.ToString());
                    Assert.That(ReadCoins(context.Storage.GetBytes(AccountSlotPath)), Is.EqualTo(9), "The restored value reaches the disk after recovery.");
                    Assert.That(health.Count, Is.EqualTo(2), string.Join(", ", health));
                    Assert.That(health[1].IsHealthy, Is.True);

                    // Sync state was persisted with the restore, so nothing is uploaded back
                    FlushResult flushed = context.FlushSync();
                    Assert.That(flushed.IsComplete, Is.True, flushed.ToString());
                    Assert.That(CloudEntry(flushed, TestSlotKeys.Player).Status, Is.EqualTo(CloudFlushStatus.AlreadyInSync), flushed.ToString());
                    Assert.That(context.Provider.WriteCallCount, Is.EqualTo(0));
                }
                finally
                {
                    context.Service.LocalWriteHealthChanged -= onHealthChanged;
                }
            }
        }

        // F2: a service-level revision guard survived the profile delete and silently dropped every write result of the recreated profile
        [Test]
        public void ProfileDeletedAndReactivated_LowerRevisionWrite_ClearsDirtyAndSetsHadContent()
        {
            using (TwoDeviceHarness harness = CreateSignedInHarness())
            {
                TestServiceContext context = harness.DeviceA;
                ProfileSlot slot = context.Slot<ProfileSlot>();
                ActivateReconciledAccount(context);

                // Drive the revision line past anything the recreated profile will reach
                for (int coins = 1; coins <= 5; coins++)
                {
                    Assert.That(slot.Mutate(data => data.Coins = coins), Is.True);
                    Assert.That(context.Service.FlushLocalNow().IsComplete, Is.True);
                }

                Assert.That(slot.Revision, Is.EqualTo(5), "Premise: the deleted profile reached revision 5.");
                Assert.That(context.ActivateSync(ProfileId.Guest).IsSuccess, Is.True);

                DeleteResult deleted = AsyncTestUtility.RunSync(
                    context.Service.DeleteProfileAsync(ProfileA, ProfileDeleteMode.DiscardUnsynced, CancellationToken.None),
                    nameof(SaveService.DeleteProfileAsync));
                Assert.That(deleted.Status, Is.EqualTo(SaveStatus.Success), deleted.ToString());

                Assert.That(context.ActivateSync(ProfileA).IsSuccess, Is.True);
                Assert.That(context.Storage.HasFile(AccountSlotPath), Is.False, "Premise: the recreated profile starts without a slot file.");
                Assert.That(slot.Revision, Is.EqualTo(0), "Premise: the revision line restarted.");
                context.Storage.ResetCounters();

                var health = new List<LocalWriteHealth>();
                Action<LocalWriteHealth> onHealthChanged = health.Add;
                context.Service.LocalWriteHealthChanged += onHealthChanged;
                try
                {
                    Assert.That(slot.Mutate(data => data.Coins = 7), Is.True);
                    LocalFlushResult flushed = context.Service.FlushLocalNow();
                    int writes = context.Storage.GetWriteCount(AccountSlotPath);

                    Assert.That(flushed.IsComplete, Is.True, flushed.ToString());
                    Assert.That(writes, Is.EqualTo(1), "Premise: revision 1 reached the disk.");
                    Assert.That(ReadCoins(context.Storage.GetBytes(AccountSlotPath)), Is.EqualTo(7));
                    Assert.That(health, Is.Empty, "A write that landed must not look like a local write failure.");
                    Assert.That(
                        LoadAccountSlotSyncState(context).HadContent, Is.True, "HadContent gates the empty-over-content upload guard.");

                    // The dirty flag was cleared, so the next flush has nothing to write
                    Assert.That(context.Service.FlushLocalNow().IsComplete, Is.True);
                    Assert.That(context.Storage.GetWriteCount(AccountSlotPath), Is.EqualTo(writes), "The slot is clean after the write.");
                }
                finally
                {
                    context.Service.LocalWriteHealthChanged -= onHealthChanged;
                }
            }
        }

        // F13: a dirty slot that is not Ready has nothing to snapshot; that is no disk failure and must stay out of write health
        [UnityTest]
        public IEnumerator ScheduledWriteOfANotReadySlot_StaysOutOfWriteHealth_AndIsRetried()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                var slot = new ProfileSlot();
                using (TestServiceContext context = CreateInitializedGuest(slot))
                {
                    var health = new List<LocalWriteHealth>();
                    Action<LocalWriteHealth> onHealthChanged = health.Add;
                    context.Service.LocalWriteHealthChanged += onHealthChanged;
                    try
                    {
                        Assert.That(slot.Mutate(data => data.Coins = 5), Is.True);

                        // What a failed reload leaves behind: local-dirty in the scheduler, not Ready for the write path
                        slot.MarkFailed(SlotFailure.IoError);

                        await AsyncTestUtility.WaitFramesAsync(2);
                        context.Clock.Advance(context.Options.LocalWriteDelay + Margin);

                        // Either outcome ends the wait: the skip log after the fix, the health event before it
                        await AsyncTestUtility.WaitUntilAsync(() => SkippedWrites(context) >= 1 || health.Count > 0, MaxFrames, "Skipped local write");
                        await AsyncTestUtility.WaitFramesAsync(2);

                        Assert.That(health, Is.Empty, "A slot that is not Ready must not surface as a disk failure. " + context.Logger.Describe());
                        Assert.That(SkippedWrites(context), Is.EqualTo(1), "The skip is logged once per dispatch.");
                        Assert.That(WriteAttempts(context), Is.EqualTo(0), "Nothing reached the file.");

                        // Backoff owns the retry: the slot is not parked until the next mutation
                        context.Clock.Advance(context.Options.LocalWriteBackoff[0] + Margin);
                        await AsyncTestUtility.WaitUntilAsync(() => SkippedWrites(context) >= 2, MaxFrames, "Retry after the first backoff step");

                        Assert.That(health, Is.Empty, context.Logger.Describe());
                        Assert.That(context.Service.FlushLocalNow().IsComplete, Is.True, "A slot that cannot be written does not make the flush incomplete.");
                    }
                    finally
                    {
                        context.Service.LocalWriteHealthChanged -= onHealthChanged;
                    }
                }
            });
        }

        // F15: a zero backoff step would make the refusal retry due again in the same frame, once per frame
        [Test]
        public void ZeroLocalWriteBackoffStep_IsRejectedAtConstruction()
        {
            SaveServiceOptions options = TestServiceFactory.CreateOptions(new ManualSaveClock(), new TestSaveLogger());
            options.LocalWriteBackoff = new[] { TimeSpan.Zero, TimeSpan.FromSeconds(5) };

            Assert.That(
                () => new SaveService(options, new InMemorySaveStorage(), NullCloudSaveProvider.Instance, new List<SaveSlot>()),
                Throws.ArgumentException.With.Message.Contains(nameof(SaveServiceOptions.LocalWriteBackoff)));
        }

        // Both devices signed in to the account; gateway retries off
        private static TwoDeviceHarness CreateSignedInHarness()
        {
            var harness = new TwoDeviceHarness(() => new SaveSlot[] { new ProfileSlot() }, null, options => options.CloudRetryCount = 0);
            harness.SignIn(AccountA);
            return harness;
        }

        // Account active and reconciled this epoch; counters reset afterwards
        private static void ActivateReconciledAccount(TestServiceContext context)
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
            context.Storage.ResetCounters();
        }

        private static void UploadCoins(TestServiceContext context, ProfileSlot slot, int coins)
        {
            Assert.That(slot.Mutate(data => data.Coins = coins), Is.True);
            FlushResult flushed = context.FlushSync();
            Assert.That(flushed.IsComplete, Is.True, flushed.ToString());
        }

        private static RestoreReport RestoreSync(TestServiceContext context)
        {
            return AsyncTestUtility.RunSync(context.Service.RestoreAsync(CancellationToken.None), nameof(SaveService.RestoreAsync));
        }

        private static CloudFlushSlotResult CloudEntry(FlushResult result, string key)
        {
            CloudFlushSlotResult entry = result.Cloud.FirstOrDefault(item => item.SlotKey == key);
            Assert.That(entry, Is.Not.Null, result.ToString());
            return entry;
        }

        // Reads profile.json directly without repairing anything
        private static SlotSyncState LoadAccountSlotSyncState(TestServiceContext context)
        {
            var store = new SyncStateStore(context.Storage, new TestSaveLogger(), context.Clock);
            SyncStateLoadResult loaded = store.Load(TestPaths.ProfileDirectory(ProfileA), SlotReadMode.ReadOnly);
            Assert.That(loaded.Status, Is.EqualTo(SyncStateLoadStatus.Loaded), loaded.Message);
            return loaded.State.PeekSlot(TestSlotKeys.Player);
        }

        // Coins of an envelope (local file or cloud value); property names matched case-insensitively
        private static int ReadCoins(byte[] envelopeBytes)
        {
            Assert.That(envelopeBytes, Is.Not.Null, "Missing envelope bytes.");
            var envelope = SaveJson.Parse(envelopeBytes) as JObject;
            Assert.That(envelope, Is.Not.Null, "Not an envelope object.");
            var data = envelope[SaveEnvelope.DataProperty] as JObject;
            Assert.That(data, Is.Not.Null, envelope.ToString());
            JToken coins = data.GetValue("Coins", StringComparison.OrdinalIgnoreCase);
            Assert.That(coins, Is.Not.Null, data.ToString());
            return coins.Value<int>();
        }

        private static string ReadWriteId(byte[] envelopeBytes)
        {
            var envelope = SaveJson.Parse(envelopeBytes) as JObject;
            Assert.That(envelope, Is.Not.Null, "Not an envelope object.");
            return envelope.Value<string>(SaveEnvelope.WriteIdProperty);
        }

        // Mutates, lets the local lane dispatch and waits until the storage saw the expected attempt count
        private static async UniTask MutateAndRunScheduledWriteAsync(TestServiceContext context, ProfileSlot slot, int coins, int expectedAttempts)
        {
            Assert.That(slot.Mutate(data => data.Coins = coins), Is.True);
            await AsyncTestUtility.WaitFramesAsync(2);
            context.Clock.Advance(context.Options.LocalWriteDelay + Margin);
            await WaitForWriteAttemptsAsync(context, expectedAttempts, "Scheduled local write");
        }

        private static async UniTask WaitForWriteAttemptsAsync(TestServiceContext context, int attempts, string description)
        {
            await AsyncTestUtility.WaitUntilAsync(() => WriteAttempts(context) >= attempts, MaxFrames, description);
            await AsyncTestUtility.WaitFramesAsync(5);
            Assert.That(WriteAttempts(context), Is.EqualTo(attempts), description);
        }

        private static int WriteAttempts(TestServiceContext context)
        {
            return context.Storage.CountCalls(StorageOperation.Write, SlotPath);
        }

        // Scheduled local writes the write path skipped because the slot was not Ready
        private static int SkippedWrites(TestServiceContext context)
        {
            return context.Logger.Count(TestLogLevel.Warning, "the scheduled local write was skipped");
        }

        private static SaveResult SaveNowSync(ProfileSlot slot)
        {
            return AsyncTestUtility.RunSync(slot.SaveNowAsync(CancellationToken.None), nameof(ProfileSlot.SaveNowAsync));
        }

        // Guest profile: no provider involvement; counters reset after init
        private static TestServiceContext CreateInitializedGuest(params SaveSlot[] slots)
        {
            TestServiceContext context = TestServiceFactory.Create(new TestServiceSetup
            {
                Slots = slots,
                ConfigureOptions = options => options.CloudRetryCount = 0,
            });

            Assert.That(context.InitializeSync().IsSuccess, Is.True);
            context.Storage.ResetCounters();
            return context;
        }

        private sealed class CustomStorageException : SaveStorageException
        {
            public CustomStorageException(LocalWriteErrorKind kind)
                : base(kind, "Custom storage failure (" + kind + ").", new IOException("disk full"))
            {
            }
        }

        /// <summary>Data whose only property throws while the owning slot is armed; that is what a snapshot failure looks like.</summary>
        private sealed class SnapshotFailureData
        {
            private int _coins;

            [JsonIgnore]
            public SnapshotFailureSlot Owner { get; set; }

            public int Coins
            {
                get
                {
                    Owner?.OnDataRead();
                    return _coins;
                }

                set => _coins = value;
            }
        }

        private sealed class SnapshotFailureSlot : SaveSlot<SnapshotFailureData>
        {
            public const string SlotKey = "snapshot_failure";

            /// <summary>Data reads that reached the serializer; one per write attempt.</summary>
            public int SnapshotAttempts { get; private set; }

            public bool FailSnapshot { get; set; }

            public override string Key => SlotKey;

            public override SyncMode SyncMode => SyncMode.LocalOnly;

            public override SlotScope Scope => SlotScope.Profile;

            public bool SetCoins(int coins)
            {
                return Mutate(coins, (data, value) => data.Coins = value);
            }

            internal void OnDataRead()
            {
                SnapshotAttempts++;
                if (FailSnapshot)
                {
                    throw new InvalidOperationException("Scripted snapshot failure.");
                }
            }

            protected override void Normalize(SnapshotFailureData data)
            {
                data.Owner = this;
            }
        }
    }
}
