using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Ecanakli.SaveSystem.Tests
{
    /// <summary>Full cloud restore: completeness, gate, single flight, listeners, N1, conflict file, corrupt cloud, too-new schema, R8.</summary>
    [TestFixture]
    public sealed class RestoreOperationTests
    {
        private const string AccountA = "account-a";
        private const string AccountB = "account-b";
        private const string OtherDeviceId = "device-other";
        private const string StatsKey = "stats";
        private const string DerivedKey = "derived_cache";
        private const int MaxFrames = 120;

        private static readonly ProfileId ProfileA = ProfileId.Account(AccountA);

        [Test]
        public void Completeness_EveryCloudValueRestored_Full()
        {
            var player = new ProfileSlot();
            var stats = new ProfileSlot(StatsKey);
            using (TestServiceContext context = CreateContext(player, stats))
            {
                var otherPlayer = new ProfileSlot();
                var otherStats = new ProfileSlot(StatsKey);
                UploadFromOtherDevice(context.Provider.Store, () =>
                {
                    Assert.That(otherPlayer.Mutate(data => data.Coins = 9), Is.True);
                    Assert.That(otherStats.Mutate(data => data.Level = 4), Is.True);
                }, otherPlayer, otherStats);
                InitializeAndActivate(context, AccountA);

                RestoreReport report = RestoreSync(context);

                Assert.That(report.Status, Is.EqualTo(SaveStatus.Success), report.ToString());
                Assert.That(report.Trigger, Is.EqualTo(RestoreTrigger.CloudRestore));
                Assert.That(report.Completeness, Is.EqualTo(RestoreCompleteness.Full), report.ToString());
                Assert.That(report.GetResult(player).Outcome, Is.EqualTo(SlotRestoreOutcome.Restored), report.ToString());
                Assert.That(report.GetResult(stats).Outcome, Is.EqualTo(SlotRestoreOutcome.Restored), report.ToString());
                Assert.That(player.PeekData().Coins, Is.EqualTo(9));
                Assert.That(stats.PeekData().Level, Is.EqualTo(4));
            }
        }

        [Test]
        public void Completeness_OneCloudReadFails_Partial()
        {
            var player = new ProfileSlot();
            var stats = new ProfileSlot(StatsKey);
            using (TestServiceContext context = CreateContext(player, stats))
            {
                var otherPlayer = new ProfileSlot();
                var otherStats = new ProfileSlot(StatsKey);
                UploadFromOtherDevice(context.Provider.Store, () =>
                {
                    Assert.That(otherPlayer.Mutate(data => data.Coins = 9), Is.True);
                    Assert.That(otherStats.Mutate(data => data.Level = 4), Is.True);
                }, otherPlayer, otherStats);
                InitializeAndActivate(context, AccountA);
                context.Provider.EnqueueError(CloudOperation.Read, StatsKey, CloudErrorKind.Permanent);

                RestoreReport report = RestoreSync(context);

                Assert.That(report.Status, Is.EqualTo(SaveStatus.Success), report.ToString());
                Assert.That(report.Completeness, Is.EqualTo(RestoreCompleteness.Partial), report.ToString());
                Assert.That(report.GetResult(player).Outcome, Is.EqualTo(SlotRestoreOutcome.Restored), report.ToString());

                SlotRestoreResult failed = report.GetResult(stats);
                Assert.That(failed.Outcome, Is.EqualTo(SlotRestoreOutcome.Failed), report.ToString());
                Assert.That(failed.Failure, Is.EqualTo(SlotRestoreFailure.ReadFailed));
                Assert.That(failed.CloudError, Is.Not.Null);
                Assert.That(failed.CloudError.Kind, Is.EqualTo(CloudErrorKind.Permanent));
                Assert.That(player.PeekData().Coins, Is.EqualTo(9));
                Assert.That(stats.PeekData().Level, Is.EqualTo(0), "A failed read never changes local data.");
            }
        }

        [Test]
        public void Completeness_NoCloudValues_NothingToRestore()
        {
            var player = new ProfileSlot();
            var stats = new ProfileSlot(StatsKey);
            using (TestServiceContext context = CreateContext(player, stats))
            {
                InitializeAndActivate(context, AccountA);

                RestoreReport report = RestoreSync(context);

                Assert.That(report.Status, Is.EqualTo(SaveStatus.Success), report.ToString());
                Assert.That(report.Completeness, Is.EqualTo(RestoreCompleteness.NothingToRestore), report.ToString());
                Assert.That(report.Slots.All(slot => slot.Outcome == SlotRestoreOutcome.NoCloudData), Is.True, report.ToString());
                Assert.That(context.Provider.ReadCallCount, Is.EqualTo(1), "One batched read for every slot.");
            }
        }

        [Test]
        public void Completeness_EveryCloudReadFails_Failed()
        {
            var player = new ProfileSlot();
            var stats = new ProfileSlot(StatsKey);
            using (TestServiceContext context = CreateContext(player, stats))
            {
                InitializeAndActivate(context, AccountA);
                context.Provider.EnqueueError(CloudOperation.Read, null, CloudErrorKind.Permanent, -1);

                RestoreReport report = RestoreSync(context);

                Assert.That(report.Completeness, Is.EqualTo(RestoreCompleteness.Failed), report.ToString());
                Assert.That(report.Slots.Count, Is.EqualTo(2), report.ToString());
                foreach (SlotRestoreResult slot in report.Slots)
                {
                    Assert.That(slot.Outcome, Is.EqualTo(SlotRestoreOutcome.Failed), slot.ToString());
                    Assert.That(slot.Failure, Is.EqualTo(SlotRestoreFailure.ReadFailed), slot.ToString());
                    Assert.That(slot.IsSuccess, Is.False);
                }
            }
        }

        [UnityTest]
        public IEnumerator FlushDuringHeldRestore_NoUploadUntilTheRestoreReleasesTheGate()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                var slot = new ProfileSlot();
                using (TestServiceContext context = CreateContext(slot))
                {
                    ActivateReconciledAccount(context, AccountA);
                    Assert.That(slot.Mutate(data => data.Coins = 5), Is.True);
                    context.Provider.HoldReads();
                    try
                    {
                        UniTask<RestoreReport> restore = context.Service.RestoreAsync(CancellationToken.None).Preserve();
                        await AsyncTestUtility.WaitUntilAsync(() => context.Provider.HeldCallCount == 1, MaxFrames, "Held restore read");

                        UniTask<FlushResult> flush = context.Service.FlushAsync(CancellationToken.None).Preserve();
                        await AsyncTestUtility.WaitFramesAsync(3);

                        Assert.That(flush.Status.IsCompleted(), Is.False, "The flush waits for the gate held by the restore.");
                        Assert.That(context.Provider.WriteCallCount, Is.EqualTo(0), "No upload while the restore holds the gate.");

                        context.Provider.ReleaseReads();
                        // The contended gate hands over through SemaphoreSlim and the sync context, which the unfocused editor pumps slowly
                        await AsyncTestUtility.WaitUntilAsync(() => restore.Status.IsCompleted() && flush.Status.IsCompleted(), MaxFrames * 10, "Restore and flush");

                        RestoreReport report = await restore;
                        Assert.That(report.Status, Is.EqualTo(SaveStatus.Success), report.ToString());

                        FlushResult flushed = await flush;
                        Assert.That(flushed.IsComplete, Is.True, flushed.ToString());

                        IReadOnlyList<CloudCall> calls = context.Provider.Calls;
                        int readIndex = IndexOf(calls, CloudOperation.Read);
                        int writeIndex = IndexOf(calls, CloudOperation.Write);
                        Assert.That(readIndex, Is.GreaterThanOrEqualTo(0), string.Join(", ", calls));
                        Assert.That(writeIndex, Is.GreaterThan(readIndex), string.Join(", ", calls));
                    }
                    finally
                    {
                        context.Provider.ReleaseReads();
                    }
                }
            });
        }

        [UnityTest]
        public IEnumerator ConcurrentRestoreCalls_ShareOneProviderRead()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                var slot = new ProfileSlot();
                using (TestServiceContext context = CreateContext(slot))
                {
                    InitializeAndActivate(context, AccountA);
                    var completed = new List<RestoreReport>();
                    Action<RestoreReport> onCompleted = completed.Add;
                    context.Service.RestoreCompleted += onCompleted;
                    context.Provider.HoldReads();
                    try
                    {
                        UniTask<RestoreReport> first = context.Service.RestoreAsync(CancellationToken.None).Preserve();
                        await AsyncTestUtility.WaitUntilAsync(() => context.Provider.HeldCallCount == 1, MaxFrames, "Held restore read");
                        UniTask<RestoreReport> second = context.Service.RestoreAsync(CancellationToken.None).Preserve();
                        await AsyncTestUtility.WaitFramesAsync(2);

                        Assert.That(context.Provider.ReadCallCount, Is.EqualTo(1), "The second caller joins the restore in flight.");
                        Assert.That(second.Status.IsCompleted(), Is.False);

                        context.Provider.ReleaseReads();
                        await AsyncTestUtility.WaitUntilAsync(() => first.Status.IsCompleted() && second.Status.IsCompleted(), MaxFrames, "Both restores");

                        RestoreReport firstReport = await first;
                        RestoreReport secondReport = await second;
                        Assert.That(firstReport.Status, Is.EqualTo(SaveStatus.Success), firstReport.ToString());
                        Assert.That(secondReport, Is.SameAs(firstReport), "Both callers get the shared run's report.");
                        Assert.That(context.Provider.ReadCallCount, Is.EqualTo(1));
                        Assert.That(completed.Count, Is.EqualTo(1), "One run raises RestoreCompleted once.");
                    }
                    finally
                    {
                        context.Provider.ReleaseReads();
                        context.Service.RestoreCompleted -= onCompleted;
                    }
                }
            });
        }

        [UnityTest]
        public IEnumerator RestoreListeners_RunInOrder_ThrowingListenerDoesNotStopTheOthers()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                var slot = new ProfileSlot();
                using (TestServiceContext context = CreateContext(slot))
                {
                    ActivateReconciledAccount(context, AccountA);
                    var calls = new List<int>();
                    var late = new RecordingRestoreListener(30, calls);
                    var failing = new RecordingRestoreListener(10, calls, true);
                    var middle = new RecordingRestoreListener(20, calls);
                    var completed = new List<RestoreReport>();
                    Action<RestoreReport> onCompleted = completed.Add;

                    // Registration order differs from Order on purpose
                    context.Service.AddRestoreListener(late);
                    context.Service.AddRestoreListener(failing);
                    context.Service.AddRestoreListener(middle);
                    context.Service.RestoreCompleted += onCompleted;
                    try
                    {
                        RestoreReport report = await context.Service.RestoreAsync(CancellationToken.None);

                        Assert.That(calls, Is.EqualTo(new[] { 10, 20, 30 }), "Ascending Order; the throwing listener does not stop dispatch.");
                        Assert.That(report.Status, Is.EqualTo(SaveStatus.Success), report.ToString());
                        Assert.That(report.ListenerFailures.Count, Is.EqualTo(1), report.ToString());

                        ListenerFailure failure = report.ListenerFailures[0];
                        Assert.That(failure.Kind, Is.EqualTo(ListenerFailureKind.Threw));
                        Assert.That(failure.Order, Is.EqualTo(10));
                        Assert.That(failure.ListenerType, Is.EqualTo(typeof(RecordingRestoreListener)));
                        Assert.That(failure.Exception, Is.InstanceOf<InvalidOperationException>());

                        Assert.That(completed.Count, Is.EqualTo(1));
                        Assert.That(completed[0].ListenerFailures.Count, Is.EqualTo(1), "RestoreCompleted carries the listener outcome.");
                    }
                    finally
                    {
                        context.Service.RestoreCompleted -= onCompleted;
                        context.Service.RemoveRestoreListener(late);
                        context.Service.RemoveRestoreListener(failing);
                        context.Service.RemoveRestoreListener(middle);
                    }
                }
            });
        }

        // N1: the account check right before apply sees the switch made while the read was held
        [UnityTest]
        public IEnumerator SignedInAccountChangesDuringHeldRead_AccountMismatch_NothingApplied()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                var slot = new ProfileSlot();
                using (TestServiceContext context = CreateContext(slot))
                {
                    var other = new ProfileSlot();
                    UploadFromOtherDevice(context.Provider.Store, () => Assert.That(other.Mutate(data => data.Coins = 9), Is.True), other);
                    InitializeAndActivate(context, AccountA);

                    string slotPath = TestPaths.ProfileSlot(ProfileA, TestSlotKeys.Player);
                    byte[] fileBefore = context.Storage.HasFile(slotPath) ? context.Storage.GetBytes(slotPath) : null;
                    var listener = new RecordingRestoreListener(0, new List<int>());
                    var completed = new List<RestoreReport>();
                    Action<RestoreReport> onCompleted = completed.Add;
                    context.Service.AddRestoreListener(listener);
                    context.Service.RestoreCompleted += onCompleted;
                    context.Provider.HoldReads();
                    try
                    {
                        UniTask<RestoreReport> restore = context.Service.RestoreAsync(CancellationToken.None).Preserve();
                        await AsyncTestUtility.WaitUntilAsync(() => context.Provider.HeldCallCount == 1, MaxFrames, "Held restore read");

                        context.Provider.SignedInAccountId = AccountB;
                        context.Provider.ReleaseReads();
                        await AsyncTestUtility.WaitUntilAsync(() => restore.Status.IsCompleted(), MaxFrames, "Restore");

                        RestoreReport report = await restore;
                        Assert.That(report.Status, Is.EqualTo(SaveStatus.Failed), report.ToString());
                        Assert.That(report.Error, Is.Not.Null, report.ToString());
                        Assert.That(report.Error.Code, Is.EqualTo(SaveErrorCode.AccountMismatch), report.ToString());
                        Assert.That(report.Completeness, Is.EqualTo(RestoreCompleteness.Failed));
                        Assert.That(report.Slots.Count, Is.EqualTo(1), report.ToString());
                        Assert.That(report.Slots[0].Outcome, Is.EqualTo(SlotRestoreOutcome.Failed));
                        Assert.That(report.Slots[0].Failure, Is.EqualTo(SlotRestoreFailure.AccountChanged));

                        Assert.That(slot.PeekData().Coins, Is.EqualTo(0), "The fetched cloud value is not applied.");
                        byte[] fileAfter = context.Storage.HasFile(slotPath) ? context.Storage.GetBytes(slotPath) : null;
                        Assert.That(fileAfter, Is.EqualTo(fileBefore), "The slot file is not rewritten.");
                        Assert.That(listener.CallCount, Is.EqualTo(0), "No listener dispatch after an account change.");
                        Assert.That(context.Provider.WriteCallCount, Is.EqualTo(0));
                        Assert.That(context.Provider.DeleteCallCount, Is.EqualTo(0));

                        Assert.That(completed.Count, Is.EqualTo(1), "RestoreCompleted is still raised.");
                        Assert.That(completed[0].Error.Code, Is.EqualTo(SaveErrorCode.AccountMismatch));
                    }
                    finally
                    {
                        context.Provider.ReleaseReads();
                        context.Service.RestoreCompleted -= onCompleted;
                        context.Service.RemoveRestoreListener(listener);
                    }
                }
            });
        }

        // A5: the epoch ends under the held read (Dispose is the reachable path); step 7 applies and persists nothing
        [UnityTest]
        public IEnumerator DisposeDuringFetch_AppliesNothing_ReportsDisposed()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                var slot = new ProfileSlot();
                using (TestServiceContext context = CreateContext(slot))
                {
                    ActivateReconciledAccount(context, AccountA);
                    Assert.That(slot.Mutate(data => data.Coins = 5), Is.True);
                    Assert.That(context.Service.FlushLocalNow().IsComplete, Is.True);

                    var other = new ProfileSlot();
                    UploadFromOtherDevice(context.Provider.Store, () => Assert.That(other.Mutate(data => data.Coins = 9), Is.True), other);

                    string slotPath = TestPaths.ProfileSlot(ProfileA, TestSlotKeys.Player);
                    byte[] fileBefore = context.Storage.GetBytes(slotPath);
                    Assert.That(fileBefore, Is.Not.Null, "Premise: the local slot file holds the unsynced value.");

                    var listener = new RecordingRestoreListener(0, new List<int>());
                    var completed = new List<RestoreReport>();
                    Action<RestoreReport> onCompleted = completed.Add;
                    context.Service.AddRestoreListener(listener);
                    context.Service.RestoreCompleted += onCompleted;
                    context.Provider.HoldReads();
                    try
                    {
                        UniTask<RestoreReport> restore = context.Service.RestoreAsync(CancellationToken.None).Preserve();
                        await AsyncTestUtility.WaitUntilAsync(() => context.Provider.HeldCallCount == 1, MaxFrames, "Held restore read");

                        // Dispose cancels the epoch while the fetch is still in flight
                        context.Dispose();
                        context.Provider.ReleaseReads();
                        await AsyncTestUtility.WaitUntilAsync(() => restore.Status.IsCompleted(), MaxFrames, "Restore");

                        RestoreReport report = await restore;
                        Assert.That(report.Status, Is.EqualTo(SaveStatus.Failed), report.ToString());
                        Assert.That(report.Error, Is.Not.Null, report.ToString());
                        Assert.That(report.Error.Code, Is.EqualTo(SaveErrorCode.Disposed), "Dispose cancels the epoch token too; the cause decides the code. " + report);
                        Assert.That(report.Completeness, Is.EqualTo(RestoreCompleteness.Failed), report.ToString());
                        Assert.That(report.Slots.Count, Is.EqualTo(1), report.ToString());
                        Assert.That(report.Slots[0].Outcome, Is.EqualTo(SlotRestoreOutcome.Failed), report.ToString());
                        Assert.That(report.Slots[0].Failure, Is.EqualTo(SlotRestoreFailure.AccountChanged), report.ToString());

                        Assert.That(slot.PeekData().Coins, Is.EqualTo(5), "The fetched cloud value is not applied.");
                        Assert.That(context.Storage.GetBytes(slotPath), Is.EqualTo(fileBefore), "The slot file is not rewritten.");
                        Assert.That(listener.CallCount, Is.EqualTo(0), "A superseded epoch dispatches no listeners.");
                        Assert.That(completed.Count, Is.EqualTo(0), "A disposed service raises nothing.");
                        Assert.That(context.Provider.WriteCallCount, Is.EqualTo(0));
                        Assert.That(context.Provider.DeleteCallCount, Is.EqualTo(0));
                    }
                    finally
                    {
                        context.Provider.ReleaseReads();
                    }
                }
            });
        }

        // S10: the read returns, then Dispose lands before the apply checkpoint; the code is Disposed, not a profile change
        [Test]
        public void DisposeBetweenFetchAndCheckpoint_ReportsDisposed_RaisesNothing()
        {
            var slot = new ProfileSlot();
            TestServiceContext context = CreateContext(slot);
            try
            {
                var other = new ProfileSlot();
                UploadFromOtherDevice(context.Provider.Store, () => Assert.That(other.Mutate(data => data.Coins = 9), Is.True), other);
                InitializeAndActivate(context, AccountA);

                var completed = new List<RestoreReport>();
                Action<RestoreReport> onCompleted = completed.Add;
                context.Service.RestoreCompleted += onCompleted;
                context.Provider.AfterRead = (provider, requests) => context.Dispose();
                try
                {
                    RestoreReport report = RestoreSync(context);

                    Assert.That(report.Status, Is.EqualTo(SaveStatus.Failed), report.ToString());
                    Assert.That(report.Error, Is.Not.Null, report.ToString());
                    Assert.That(report.Error.Code, Is.EqualTo(SaveErrorCode.Disposed), report.ToString());
                    Assert.That(report.Completeness, Is.EqualTo(RestoreCompleteness.Failed), report.ToString());
                    Assert.That(slot.PeekData().Coins, Is.EqualTo(0), "The fetched cloud value is not applied.");
                    Assert.That(completed, Is.Empty, "A disposed service raises nothing.");
                }
                finally
                {
                    context.Provider.AfterRead = null;
                    context.Service.RestoreCompleted -= onCompleted;
                }
            }
            finally
            {
                context.Dispose();
            }
        }

        // Row 3: a wrong dataSha256 is corruption only when the provider returns the stored text unchanged
        [TestCase(true)]
        [TestCase(false)]
        public void Row3_ChecksumMismatchOnlyWhenPreservesValueText(bool preservesValueText)
        {
            var store = new FakeCloudStore();
            var other = new ProfileSlot();
            UploadFromOtherDevice(store, () => Assert.That(other.Mutate(data => data.Coins = 9), Is.True), other);
            string cloudText = store.GetText(AccountA, TestSlotKeys.Player);
            Assert.That(cloudText, Is.Not.Null, "Premise: the other device uploaded a verbatim envelope.");
            store.PutText(AccountA, TestSlotKeys.Player, BreakChecksum(cloudText));
            string corruptVersion = store.GetVersion(AccountA, TestSlotKeys.Player);

            var slot = new ProfileSlot();
            var provider = new FakeCloudSaveProvider(store, FakeCloudSaveProvider.CreateCapabilities(preservesValueText: preservesValueText));
            using (TestServiceContext context = CreateContext(provider, slot))
            {
                InitializeAndActivate(context, AccountA);
                Assert.That(slot.Mutate(data => data.Coins = 5), Is.True);
                Assert.That(context.Service.FlushLocalNow().IsComplete, Is.True);
                context.Provider.ClearCalls();

                var issues = new List<SlotLoadIssue>();
                Action<SlotLoadIssue> onIssue = issues.Add;
                context.Service.SlotLoadIssueDetected += onIssue;
                try
                {
                    RestoreReport report = RestoreSync(context);

                    Assert.That(report.Status, Is.EqualTo(SaveStatus.Success), report.ToString());
                    string directory = TestPaths.ProfileDirectory(ProfileA);
                    string[] backups = context.Storage.ListFileNames(directory)
                        .Where(name => SaveLayout.IsBackupFileName(BackupFileFamily.CloudCorrupt, TestSlotKeys.Player, name))
                        .ToArray();
                    SlotLoadIssue corrupt = issues.SingleOrDefault(entry => entry.Kind == SlotLoadIssueKind.CloudPayloadCorrupt);

                    if (preservesValueText)
                    {
                        Assert.That(report.GetResult(slot).Outcome, Is.EqualTo(SlotRestoreOutcome.RepairingCorruptCloud), report.ToString());
                        Assert.That(slot.PeekData().Coins, Is.EqualTo(5), "The unverifiable cloud value never replaces local data.");
                        Assert.That(slot.ResolveConflictCount, Is.EqualTo(0), report.ToString());
                        Assert.That(corrupt, Is.Not.Null, string.Join(", ", issues.Select(entry => entry.Kind)));
                        Assert.That(corrupt.Cause, Is.EqualTo(CorruptionCause.ChecksumMismatch), corrupt.ToString());
                        Assert.That(backups.Length, Is.EqualTo(1), string.Join(", ", context.Storage.AllFilePaths));
                    }
                    else
                    {
                        // The provider may re-indent values, so the checksum cannot be trusted and the payload is accepted
                        Assert.That(report.GetResult(slot).Outcome, Is.EqualTo(SlotRestoreOutcome.Restored), report.ToString());
                        Assert.That(slot.PeekData().Coins, Is.EqualTo(9));
                        Assert.That(slot.ResolveConflictCount, Is.EqualTo(1), report.ToString());
                        Assert.That(corrupt, Is.Null, string.Join(", ", issues.Select(entry => entry.Kind)));
                        Assert.That(backups.Length, Is.EqualTo(0), string.Join(", ", context.Storage.AllFilePaths));
                    }

                    Assert.That(context.Provider.WriteCallCount, Is.EqualTo(0), "Nothing is uploaded during the restore.");
                    Assert.That(store.GetVersion(AccountA, TestSlotKeys.Player), Is.EqualTo(corruptVersion));
                }
                finally
                {
                    context.Service.SlotLoadIssueDetected -= onIssue;
                }
            }
        }

        [TestCase(ConflictResolutionKind.KeepLocal)]
        [TestCase(ConflictResolutionKind.TakeCloud)]
        public void Conflict_BothSidesChanged_WritesConflictFileWithResolutionLocalAndCloud(ConflictResolutionKind resolution)
        {
            var slot = new ProfileSlot();
            using (TestServiceContext context = CreateContext(slot))
            {
                ActivateReconciledAccount(context, AccountA);
                Assert.That(slot.Mutate(data => data.Coins = 1), Is.True);
                Assert.That(context.FlushSync().IsComplete, Is.True);

                // Another device moves the cloud on from our synced write
                var other = new ProfileSlot();
                UploadFromOtherDevice(context.Provider.Store, () => Assert.That(other.Mutate(data => data.Coins = 9), Is.True), other);
                string cloudVersion = context.Provider.Store.GetVersion(AccountA, TestSlotKeys.Player);

                slot.ConflictResolver = (in ConflictContext<ProfileData> conflict) =>
                    resolution == ConflictResolutionKind.KeepLocal ? ConflictResolution<ProfileData>.KeepLocal : ConflictResolution<ProfileData>.TakeCloud;
                Assert.That(slot.Mutate(data => data.Coins = 5), Is.True);
                Assert.That(context.Service.FlushLocalNow().IsComplete, Is.True);
                context.Provider.ClearCalls();

                RestoreReport report = RestoreSync(context);

                Assert.That(report.Status, Is.EqualTo(SaveStatus.Success), report.ToString());
                Assert.That(slot.ResolveConflictCount, Is.EqualTo(1), report.ToString());

                string conflictPath = SaveLayout.ConflictPath(TestPaths.ProfileDirectory(ProfileA), TestSlotKeys.Player);
                Assert.That(context.Storage.HasFile(conflictPath), Is.True, "The conflict file is written for every resolution.");
                var document = (JObject)SaveJson.Parse(context.Storage.GetBytes(conflictPath));
                Assert.That(document.Value<string>("resolution"), Is.EqualTo(resolution.ToString()));
                Assert.That(ReadInt(document["local"], "Coins"), Is.EqualTo(5), document.ToString());
                Assert.That(ReadInt(document["cloud"], "Coins"), Is.EqualTo(9), document.ToString());

                SlotRestoreResult result = report.GetResult(slot);
                if (resolution == ConflictResolutionKind.KeepLocal)
                {
                    Assert.That(result.Outcome, Is.EqualTo(SlotRestoreOutcome.LocalKept), report.ToString());
                    Assert.That(slot.PeekData().Coins, Is.EqualTo(5));

                    // Keeping local uploads over exactly the cloud value that lost
                    FlushResult flushed = context.FlushSync();
                    Assert.That(flushed.IsComplete, Is.True, flushed.ToString());
                    IReadOnlyList<CloudWriteRequest> writes = context.Provider.GetAllWriteRequests();
                    Assert.That(writes.Count, Is.EqualTo(1));
                    Assert.That(writes[0].ExpectedVersion, Is.EqualTo(cloudVersion));
                }
                else
                {
                    Assert.That(result.Outcome, Is.EqualTo(SlotRestoreOutcome.Restored), report.ToString());
                    Assert.That(slot.PeekData().Coins, Is.EqualTo(9));
                    Assert.That(context.Provider.WriteCallCount, Is.EqualTo(0));
                }
            }
        }

        // S11: a slot mutated between the decision and the apply is re-decided with fresh presence, so row 9 becomes a conflict
        [Test]
        public void MutationBeforeApply_ReDecidesWithFreshPresence_ConflictInsteadOfSilentTakeCloud()
        {
            var player = new ProfileSlot();
            var stats = new ProfileSlot(StatsKey);
            using (TestServiceContext context = CreateContext(player, stats))
            {
                var other = new ProfileSlot();
                UploadFromOtherDevice(context.Provider.Store, () => Assert.That(other.Mutate(data => data.Coins = 9), Is.True), other);
                InitializeAndActivate(context, AccountA);

                // Row 9 premise: the player slot has no local file, so presence is Absent at the decision
                string playerPath = TestPaths.ProfileSlot(ProfileA, TestSlotKeys.Player);
                context.Storage.RemoveFile(playerPath);
                context.Storage.RemoveFile(TestPaths.Bak(playerPath));
                context.Storage.RemoveFile(TestPaths.Tmp(playerPath));
                Assert.That(context.Storage.HasFile(playerPath), Is.False, "Premise: the local copy is absent.");

                // The flush before the apply fails for the other slot; its health transition mutates the player slot
                Assert.That(stats.Mutate(data => data.Level = 1), Is.True);
                context.Storage.FailWithKind(StorageOperation.Write, TestPaths.ProfileSlot(ProfileA, StatsKey), LocalWriteErrorKind.DiskFull);

                bool handled = false;
                bool mutationApplied = false;
                Action<LocalWriteHealth> onHealthChanged = health =>
                {
                    if (handled)
                    {
                        return;
                    }

                    handled = true;
                    mutationApplied = player.Mutate(data => data.Coins = 7);
                };

                context.Service.LocalWriteHealthChanged += onHealthChanged;
                try
                {
                    RestoreReport report = RestoreSync(context);

                    Assert.That(handled, Is.True, "Premise: the flush before the apply raised a health transition.");
                    Assert.That(mutationApplied, Is.True, "Premise: the mutation between the decision and the apply was accepted.");
                    Assert.That(report.Status, Is.EqualTo(SaveStatus.Success), report.ToString());
                    Assert.That(player.ResolveConflictCount, Is.EqualTo(1), "The fresh mutation turns row 9 into row 11. " + report);

                    string conflictPath = SaveLayout.ConflictPath(TestPaths.ProfileDirectory(ProfileA), TestSlotKeys.Player);
                    Assert.That(context.Storage.HasFile(conflictPath), Is.True, "Both sides survive in the conflict file.");
                    var document = (JObject)SaveJson.Parse(context.Storage.GetBytes(conflictPath));
                    Assert.That(ReadInt(document["local"], "Coins"), Is.EqualTo(7), document.ToString());
                    Assert.That(ReadInt(document["cloud"], "Coins"), Is.EqualTo(9), document.ToString());
                }
                finally
                {
                    context.Service.LocalWriteHealthChanged -= onHealthChanged;
                }
            }
        }

        [Test]
        public void CorruptCloudValue_BackedUpAsCloudCorruptFile_ThenConditionalRepairUpload()
        {
            var slot = new ProfileSlot();
            using (TestServiceContext context = CreateContext(slot))
            {
                ActivateReconciledAccount(context, AccountA);
                Assert.That(slot.Mutate(data => data.Coins = 5), Is.True);
                Assert.That(context.FlushSync().IsComplete, Is.True);

                byte[] corrupt = Encoding.UTF8.GetBytes("{ \"fmt\": 1, \"data\": ");
                string corruptVersion = context.Provider.SetRawValue(AccountA, TestSlotKeys.Player, corrupt);
                context.Provider.ClearCalls();

                var issues = new List<SlotLoadIssue>();
                Action<SlotLoadIssue> onIssue = issues.Add;
                context.Service.SlotLoadIssueDetected += onIssue;
                try
                {
                    RestoreReport report = RestoreSync(context);

                    Assert.That(report.Status, Is.EqualTo(SaveStatus.Success), report.ToString());
                    Assert.That(report.GetResult(slot).Outcome, Is.EqualTo(SlotRestoreOutcome.RepairingCorruptCloud), report.ToString());
                    Assert.That(slot.PeekData().Coins, Is.EqualTo(5), "Local data is kept.");

                    string directory = TestPaths.ProfileDirectory(ProfileA);
                    string[] backups = context.Storage.ListFileNames(directory)
                        .Where(name => SaveLayout.IsBackupFileName(BackupFileFamily.CloudCorrupt, TestSlotKeys.Player, name))
                        .ToArray();
                    Assert.That(backups.Length, Is.EqualTo(1), string.Join(", ", context.Storage.AllFilePaths));
                    Assert.That(context.Storage.GetBytes(SaveLayout.Combine(directory, backups[0])), Is.EqualTo(corrupt), "Raw cloud bytes are backed up verbatim.");

                    SlotLoadIssue issue = issues.SingleOrDefault(entry => entry.Kind == SlotLoadIssueKind.CloudPayloadCorrupt);
                    Assert.That(issue, Is.Not.Null, string.Join(", ", issues.Select(entry => entry.Kind)));
                    Assert.That(issue.SlotKey, Is.EqualTo(TestSlotKeys.Player));
                    Assert.That(issue.BackupFileName, Is.EqualTo(backups[0]));

                    FlushResult flushed = context.FlushSync();

                    Assert.That(flushed.IsComplete, Is.True, flushed.ToString());
                    Assert.That(flushed.Cloud[0].Status, Is.EqualTo(CloudFlushStatus.Uploaded), flushed.ToString());
                    IReadOnlyList<CloudWriteRequest> writes = context.Provider.GetAllWriteRequests();
                    Assert.That(writes.Count, Is.EqualTo(1));
                    Assert.That(writes[0].ExpectedVersion, Is.EqualTo(corruptVersion), "The repair upload is conditional on the corrupt value.");

                    var repaired = (JObject)SaveJson.Parse(context.Provider.Store.GetValue(AccountA, TestSlotKeys.Player));
                    Assert.That(ReadInt(repaired[SaveEnvelope.DataProperty], "Coins"), Is.EqualTo(5));
                }
                finally
                {
                    context.Service.SlotLoadIssueDetected -= onIssue;
                }
            }
        }

        [Test]
        public void CloudSchemaTooNew_RaisesUpdateRequiredOnce_RequiresAppUpdate()
        {
            var slot = new SchemaV1Slot();
            using (TestServiceContext context = CreateContext(slot))
            {
                var newer = new SchemaV2Slot();
                UploadFromOtherDevice(context.Provider.Store, () => Assert.That(newer.Mutate(data =>
                {
                    data.DisplayName = "hero";
                    data.Coins = 3;
                }), Is.True), newer);
                string cloudVersion = context.Provider.Store.GetVersion(AccountA, TestSlotKeys.Schema);
                InitializeAndActivate(context, AccountA);

                var updates = new List<UpdateRequiredInfo>();
                Action<UpdateRequiredInfo> onUpdate = updates.Add;
                context.Service.UpdateRequired += onUpdate;
                try
                {
                    RestoreReport first = RestoreSync(context);

                    Assert.That(first.RequiresAppUpdate, Is.True, first.ToString());
                    Assert.That(first.GetResult(slot).Outcome, Is.EqualTo(SlotRestoreOutcome.SkippedSchemaTooNew), first.ToString());
                    Assert.That(updates.Count, Is.EqualTo(1));
                    Assert.That(updates[0].SlotKey, Is.EqualTo(TestSlotKeys.Schema));
                    Assert.That(updates[0].Source, Is.EqualTo(PayloadSource.Cloud));
                    Assert.That(updates[0].FoundSchema, Is.EqualTo(2));
                    Assert.That(updates[0].SupportedSchema, Is.EqualTo(1));
                    Assert.That(slot.PeekData().Name, Is.Null, "The newer payload is never applied.");

                    RestoreReport second = RestoreSync(context);

                    Assert.That(second.RequiresAppUpdate, Is.True, second.ToString());
                    Assert.That(updates.Count, Is.EqualTo(1), "UpdateRequired is raised once per slot, source and epoch.");
                    Assert.That(context.Provider.WriteCallCount, Is.EqualTo(0));
                    Assert.That(context.Provider.Store.GetVersion(AccountA, TestSlotKeys.Schema), Is.EqualTo(cloudVersion));
                }
                finally
                {
                    context.Service.UpdateRequired -= onUpdate;
                }
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NonCloudBackedProfile_FailsProfileNotCloudBacked(bool localProfile)
        {
            var slot = new ProfileSlot();
            using (TestServiceContext context = CreateContext(slot))
            {
                Assert.That(context.InitializeSync().IsSuccess, Is.True);
                context.Provider.SignedInAccountId = AccountA;
                if (localProfile)
                {
                    Assert.That(context.ActivateSync(ProfileId.Local("offline")).IsSuccess, Is.True);
                }

                Assert.That(context.Service.ActiveProfile.IsCloudBacked, Is.False);

                RestoreReport report = RestoreSync(context);

                Assert.That(report.Status, Is.EqualTo(SaveStatus.Failed), report.ToString());
                Assert.That(report.Error, Is.Not.Null);
                Assert.That(report.Error.Code, Is.EqualTo(SaveErrorCode.ProfileNotCloudBacked));
                Assert.That(report.Completeness, Is.EqualTo(RestoreCompleteness.Failed));
                Assert.That(context.Provider.TotalCallCount, Is.EqualTo(0));
            }
        }

        // R8: row 9 before row 8, so our own last write restores data lost to a local corruption reset
        [Test]
        public void RestoreAfterLocalCorruptionReset_RestoresTheCloudData()
        {
            var slot = new ProfileSlot();
            TestServiceContext context = CreateContext(slot);
            TestServiceContext restarted = null;
            try
            {
                ActivateReconciledAccount(context, AccountA);
                Assert.That(slot.Mutate(data => data.Coins = 5), Is.True);
                Assert.That(context.FlushSync().IsComplete, Is.True);

                var restartedSlot = new ProfileSlot();
                restarted = TestServiceFactory.Restart(context, restartedSlot);
                string slotPath = TestPaths.ProfileSlot(ProfileA, TestSlotKeys.Player);
                restarted.Storage.SetText(slotPath, "{ \"fmt\": 1, \"data\": ");
                restarted.Storage.RemoveFile(TestPaths.Bak(slotPath));

                Assert.That(restarted.InitializeSync().IsSuccess, Is.True);
                if (restarted.Service.ActiveProfile != ProfileA)
                {
                    Assert.That(restarted.ActivateSync(ProfileA).IsSuccess, Is.True);
                }

                Assert.That(restartedSlot.PeekData() == null || restartedSlot.PeekData().Coins == 0, Is.True, "Premise: the corrupt local copy was reset.");

                RestoreReport report = RestoreSync(restarted);

                Assert.That(report.Status, Is.EqualTo(SaveStatus.Success), report.ToString());
                Assert.That(report.GetResult(restartedSlot).Outcome, Is.EqualTo(SlotRestoreOutcome.Restored), report.ToString());
                Assert.That(report.Completeness, Is.EqualTo(RestoreCompleteness.Full));
                Assert.That(restartedSlot.PeekData().Coins, Is.EqualTo(5));
                Assert.That(LoadSyncState(restarted, ProfileA).PeekSlot(TestSlotKeys.Player).NeedsCloudRecovery, Is.False);
            }
            finally
            {
                context.Dispose();
                restarted?.Dispose();
            }
        }

        // D1: the cloud holds an older unconfirmed write, so row 8 matches only when the whole ring reaches the decider
        [Test]
        public void CloudHoldsOlderUnconfirmedWrite_Row8KeepsLocal_WithoutConflictResolution()
        {
            var slot = new ProfileSlot();
            using (TestServiceContext context = CreateContext(slot))
            {
                (string landed, string newer) = ArrangeLandedWriteBehindNewerPending(context, slot);
                slot.ResetCounters();

                RestoreReport report = RestoreSync(context);

                Assert.That(report.Status, Is.EqualTo(SaveStatus.Success), report.ToString());
                Assert.That(report.GetResult(slot).Outcome, Is.EqualTo(SlotRestoreOutcome.LocalKept), report.ToString());
                Assert.That(slot.ResolveConflictCount, Is.EqualTo(0), "Ownership is proven by the unconfirmed ids, so no conflict is raised.");
                Assert.That(context.Storage.HasFile(PlayerConflictPath), Is.False, string.Join(", ", context.Storage.AllFilePaths));
                Assert.That(slot.PeekData().Coins, Is.EqualTo(7), "The unsynced local delta survives.");

                SlotSyncState sync = LoadSyncState(context, ProfileA).PeekSlot(TestSlotKeys.Player);
                Assert.That(sync.LastSyncedWriteId, Is.EqualTo(landed), "The landed write is promoted.");
                Assert.That(sync.IsPendingWriteId(newer), Is.True);
            }
        }

        // D3: promoting the landed id confirms it and older ones only; a newer write may still be in flight
        [Test]
        public void Row8PromotesTheLandedWriteId_KeepsTheNewerUnconfirmedId()
        {
            var slot = new ProfileSlot();
            using (TestServiceContext context = CreateContext(slot))
            {
                (string landed, string newer) = ArrangeLandedWriteBehindNewerPending(context, slot);

                RestoreReport report = RestoreSync(context);

                Assert.That(report.GetResult(slot).Outcome, Is.EqualTo(SlotRestoreOutcome.LocalKept), report.ToString());
                SlotSyncState sync = LoadSyncState(context, ProfileA).PeekSlot(TestSlotKeys.Player);
                Assert.That(sync.PendingWriteIds, Is.EqualTo(new[] { newer }), "Only the promoted id and older ones are confirmed.");
                Assert.That(sync.IsPendingWriteId(landed), Is.False);
            }
        }

        // D3: TakeCloud of our own landed write must not confirm a newer write that is still in flight
        [Test]
        public void Row9TakesOurOwnLandedWrite_KeepsTheNewerUnconfirmedId()
        {
            var slot = new ProfileSlot();
            TestServiceContext context = CreateContext(slot);
            TestServiceContext restarted = null;
            try
            {
                (string landed, string newer) = ArrangeLandedWriteBehindNewerPending(context, slot);

                var restartedSlot = new ProfileSlot();
                restarted = TestServiceFactory.Restart(context, restartedSlot);
                string slotPath = TestPaths.ProfileSlot(ProfileA, TestSlotKeys.Player);
                restarted.Storage.SetText(slotPath, "{ \"fmt\": 1, \"data\": ");
                restarted.Storage.RemoveFile(TestPaths.Bak(slotPath));

                Assert.That(restarted.InitializeSync().IsSuccess, Is.True);
                if (restarted.Service.ActiveProfile != ProfileA)
                {
                    Assert.That(restarted.ActivateSync(ProfileA).IsSuccess, Is.True);
                }

                RestoreReport report = RestoreSync(restarted);

                Assert.That(report.GetResult(restartedSlot).Outcome, Is.EqualTo(SlotRestoreOutcome.Restored), report.ToString());
                Assert.That(restartedSlot.PeekData().Coins, Is.EqualTo(6), "The landed write is the cloud value.");

                SlotSyncState sync = LoadSyncState(restarted, ProfileA).PeekSlot(TestSlotKeys.Player);
                Assert.That(sync.LastSyncedWriteId, Is.EqualTo(landed));
                Assert.That(sync.PendingWriteIds, Is.EqualTo(new[] { newer }), "A newer write may still be in flight.");
            }
            finally
            {
                context.Dispose();
                restarted?.Dispose();
            }
        }

        // D2: the gateway retries a lost response with the same write id; the Conflict that answers it must not drop that id
        [Test]
        public void GatewayRetryOfALandedWriteIsRefused_KeepsTheWriteIdSoTheReconcileProvesOwnership()
        {
            var slot = new ProfileSlot();
            using (TestServiceContext context = CreateRetryingContext(slot))
            {
                ActivateReconciledAccount(context, AccountA);
                Assert.That(slot.Mutate(data => data.Coins = 5), Is.True);
                Assert.That(context.FlushSync().IsComplete, Is.True, "Premise: the next upload is conditional on that version.");
                context.Provider.ClearCalls();

                // The write lands, the response is lost, and the gateway's own retry carries the now stale expected version
                Assert.That(slot.Mutate(data => data.Coins = 6), Is.True);
                context.Provider.EnqueueLostResponse(CloudOperation.Write, TestSlotKeys.Player);

                UniTask<FlushResult> pending = context.Service.FlushAsync(CancellationToken.None);
                Assert.That(pending.Status, Is.EqualTo(UniTaskStatus.Pending), "Premise: the flush waits for the retry backoff.");

                context.Clock.Advance(RetryDelay);

                FlushResult flushed = AsyncTestUtility.RunSync(pending, nameof(SaveService.FlushAsync));
                Assert.That(flushed.IsComplete, Is.False, flushed.ToString());
                Assert.That(flushed.Cloud[0].Error.Kind, Is.EqualTo(CloudErrorKind.Conflict), flushed.ToString());
                Assert.That(context.Provider.WriteCallCount, Is.EqualTo(2), "Premise: the gateway retried the same write id.");

                string landed = CloudWriteId(context, TestSlotKeys.Player);
                Assert.That(
                    LoadSyncState(context, ProfileA).PeekSlot(TestSlotKeys.Player).IsPendingWriteId(landed),
                    Is.True,
                    "A refused retry does not prove the write never landed.");

                slot.ResetCounters();
                RestoreReport report = RestoreSync(context);

                Assert.That(report.GetResult(slot).Outcome, Is.EqualTo(SlotRestoreOutcome.UpToDate), report.ToString());
                Assert.That(slot.ResolveConflictCount, Is.EqualTo(0), "The write id proves we own the cloud value.");
                Assert.That(context.Storage.HasFile(PlayerConflictPath), Is.False, string.Join(", ", context.Storage.AllFilePaths));
                Assert.That(slot.PeekData().Coins, Is.EqualTo(6));
                Assert.That(LoadSyncState(context, ProfileA).PeekSlot(TestSlotKeys.Player).LastSyncedWriteId, Is.EqualTo(landed));
            }
        }

        // Row 9 at service level: an unreadable local copy is quarantined and replaced by the cloud value
        [Test]
        public void Row9_LocalNormalizeFailed_QuarantinesAndTakesCloud()
        {
            var writer = new NormalizeThrowsSlot(TestSlotKeys.NormalizeThrows, SyncMode.CloudSync) { ThrowOnNormalize = false };
            TestServiceContext context = CreateContext(writer);
            TestServiceContext restarted = null;
            try
            {
                ActivateReconciledAccount(context, AccountA);
                Assert.That(writer.Mutate(data => data.Coins = 5), Is.True);
                Assert.That(context.Service.FlushLocalNow().IsComplete, Is.True);

                var other = new NormalizeThrowsSlot(TestSlotKeys.NormalizeThrows, SyncMode.CloudSync) { ThrowOnNormalize = false };
                UploadFromOtherDevice(context.Provider.Store, () => Assert.That(other.Mutate(data => data.Coins = 9), Is.True), other);
                string cloudVersion = context.Provider.Store.GetVersion(AccountA, TestSlotKeys.NormalizeThrows);

                // Next launch: this build cannot normalize the local copy
                var broken = new NormalizeThrowsSlot(TestSlotKeys.NormalizeThrows, SyncMode.CloudSync) { ThrowOnNormalize = true };
                restarted = TestServiceFactory.Restart(context, broken);
                restarted.InitializeSync();
                if (restarted.Service.ActiveProfile != ProfileA)
                {
                    restarted.ActivateSync(ProfileA);
                }

                Assert.That(restarted.Service.ActiveProfile, Is.EqualTo(ProfileA), "Premise: the account profile is active.");
                Assert.That(broken.State, Is.EqualTo(SlotState.Failed), "Premise: the local copy failed to normalize.");
                Assert.That(broken.Failure, Is.EqualTo(SlotFailure.NormalizeFailed));

                // Only the local bytes were unusable; the cloud payload normalizes
                broken.ThrowOnNormalize = false;
                restarted.Provider.ClearCalls();

                var issues = new List<SlotLoadIssue>();
                Action<SlotLoadIssue> onIssue = issues.Add;
                restarted.Service.SlotLoadIssueDetected += onIssue;
                try
                {
                    RestoreReport report = RestoreSync(restarted);

                    Assert.That(report.Status, Is.EqualTo(SaveStatus.Success), report.ToString());
                    Assert.That(report.GetResult(broken).Outcome, Is.EqualTo(SlotRestoreOutcome.Restored), report.ToString());
                    Assert.That(report.Completeness, Is.EqualTo(RestoreCompleteness.Full), report.ToString());
                    Assert.That(broken.State, Is.EqualTo(SlotState.Ready));
                    Assert.That(broken.PeekData().Coins, Is.EqualTo(9));

                    SlotLoadIssue issue = issues.SingleOrDefault(entry => entry.Kind == SlotLoadIssueKind.LocalNormalizeFailedReplacedByCloud);
                    Assert.That(issue, Is.Not.Null, string.Join(", ", issues.Select(entry => entry.Kind)));
                    Assert.That(issue.SlotKey, Is.EqualTo(TestSlotKeys.NormalizeThrows));
                    Assert.That(issue.BackupFileName, Is.Not.Null, issue.ToString());

                    string directory = TestPaths.ProfileDirectory(ProfileA);
                    string[] quarantined = restarted.Storage.ListFileNames(directory)
                        .Where(name => SaveLayout.IsBackupFileName(BackupFileFamily.LocalCorrupt, TestSlotKeys.NormalizeThrows, name))
                        .ToArray();
                    Assert.That(quarantined, Is.EqualTo(new[] { issue.BackupFileName }), string.Join(", ", restarted.Storage.AllFilePaths));
                    Assert.That(restarted.Provider.WriteCallCount, Is.EqualTo(0), "Row 9 downloads; it never uploads.");
                    Assert.That(restarted.Provider.Store.GetVersion(AccountA, TestSlotKeys.NormalizeThrows), Is.EqualTo(cloudVersion));
                    Assert.That(LoadSyncState(restarted, ProfileA).PeekSlot(TestSlotKeys.NormalizeThrows).NeedsCloudRecovery, Is.False);
                }
                finally
                {
                    restarted.Service.SlotLoadIssueDetected -= onIssue;
                }
            }
            finally
            {
                context.Dispose();
                restarted?.Dispose();
            }
        }

        // F10 recipe: a listener clears derived LocalOnly data once its source slot was restored
        [UnityTest]
        public IEnumerator DerivedLocalOnlyResetRecipe_ListenerDeletesAfterRestored()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                var source = new ProfileSlot();
                var derived = new ProfileSlot(DerivedKey, SyncMode.LocalOnly);
                using (TestServiceContext context = CreateContext(source, derived))
                {
                    ActivateReconciledAccount(context, AccountA);
                    Assert.That(derived.Mutate(data => data.Level = 4), Is.True);
                    Assert.That(context.Service.FlushLocalNow().IsComplete, Is.True);

                    string derivedPath = TestPaths.ProfileSlot(ProfileA, DerivedKey);
                    Assert.That(context.Storage.HasFile(derivedPath), Is.True, "Premise: the derived cache is on disk.");

                    var other = new ProfileSlot();
                    UploadFromOtherDevice(context.Provider.Store, () => Assert.That(other.Mutate(data => data.Coins = 9), Is.True), other);

                    var listener = new DerivedResetListener(context.Service, source, derived);
                    context.Service.AddRestoreListener(listener);
                    try
                    {
                        RestoreReport report = await context.Service.RestoreAsync(CancellationToken.None);

                        Assert.That(report.Status, Is.EqualTo(SaveStatus.Success), report.ToString());
                        Assert.That(report.GetResult(source).Outcome, Is.EqualTo(SlotRestoreOutcome.Restored), report.ToString());
                        Assert.That(report.GetResult(derived), Is.Null, "A LocalOnly slot is never part of a cloud restore.");
                        Assert.That(report.ListenerFailures.Count, Is.EqualTo(0), report.ToString());

                        Assert.That(listener.DeleteCount, Is.EqualTo(1));
                        Assert.That(listener.LastResult, Is.Not.Null);
                        Assert.That(listener.LastResult.Status, Is.EqualTo(SaveStatus.Success), listener.LastResult.ToString());
                        Assert.That(derived.PeekData().Level, Is.EqualTo(0), "The derived cache is reset to defaults.");
                        Assert.That(context.Storage.HasFile(derivedPath), Is.False);
                        Assert.That(source.PeekData().Coins, Is.EqualTo(9), "The restored source slot is untouched by the delete.");
                        Assert.That(context.Provider.DeleteCallCount, Is.EqualTo(0), "A LocalOnly delete never reaches the cloud.");
                    }
                    finally
                    {
                        context.Service.RemoveRestoreListener(listener);
                    }
                }
            });
        }

        // 01 s13.2: ToObject and Normalize run once per applied cloud payload
        [Test]
        public void CloudApply_NormalizesThePayloadExactlyOnce()
        {
            var slot = new ProfileSlot();
            using (TestServiceContext context = CreateContext(slot))
            {
                var other = new ProfileSlot();
                UploadFromOtherDevice(context.Provider.Store, () => Assert.That(other.Mutate(data => data.Coins = 9), Is.True), other);
                InitializeAndActivate(context, AccountA);

                // Warms the cached empty reference so the counters see only the apply path
                slot.EvaluateIsEmpty(slot.PeekData());
                slot.ResetCounters();

                RestoreReport report = RestoreSync(context);

                Assert.That(report.GetResult(slot).Outcome, Is.EqualTo(SlotRestoreOutcome.Restored), report.ToString());
                Assert.That(slot.PeekData().Coins, Is.EqualTo(9));
                Assert.That(slot.NormalizeCount, Is.EqualTo(1), "The cloud payload is normalized exactly once.");
                Assert.That(slot.CreateDefaultCount, Is.EqualTo(0), "Taking cloud data creates no default instance.");
            }
        }

        // 01 s13.8: a server-owned mirror is applied from raw bytes, never uploaded, and reset when the value disappears
        [Test]
        public void CloudReadOnly_AppliesRawPayload_NeverUploads_NotFoundResetsMirror()
        {
            var slot = new CloudReadOnlySlot();
            using (TestServiceContext context = CreateContext(slot))
            {
                context.Provider.SetRawText(
                    AccountA, TestSlotKeys.ServerRewards, "{\"Granted\":[\"daily_1\"],\"ServerVersion\":3}", CloudAccess.ServerOwned);
                InitializeAndActivate(context, AccountA);

                RestoreReport restored = RestoreSync(context);

                Assert.That(restored.Status, Is.EqualTo(SaveStatus.Success), restored.ToString());
                Assert.That(restored.Completeness, Is.EqualTo(RestoreCompleteness.Full), restored.ToString());
                Assert.That(restored.GetResult(slot).Outcome, Is.EqualTo(SlotRestoreOutcome.Restored), restored.ToString());
                Assert.That(slot.PeekData().Granted, Is.EqualTo(new[] { "daily_1" }));
                Assert.That(slot.PeekData().ServerVersion, Is.EqualTo(3));

                string mirrorPath = TestPaths.ProfileSlot(ProfileA, TestSlotKeys.ServerRewards);
                Assert.That(context.Storage.HasFile(mirrorPath), Is.True, "The applied payload is mirrored locally.");
                Assert.That(context.Provider.WriteCallCount, Is.EqualTo(0));

                FlushResult flushed = context.FlushSync();
                Assert.That(context.Provider.WriteCallCount, Is.EqualTo(0), "A server-owned slot is never uploaded. " + flushed);

                // The server drops the value
                Assert.That(context.Provider.Store.Remove(AccountA, TestSlotKeys.ServerRewards), Is.True);
                RestoreReport reset = RestoreSync(context);

                Assert.That(reset.Status, Is.EqualTo(SaveStatus.Success), reset.ToString());
                Assert.That(reset.GetResult(slot).Outcome, Is.EqualTo(SlotRestoreOutcome.NoCloudData), reset.ToString());
                Assert.That(slot.PeekData().Granted, Is.Empty, "The mirror falls back to defaults.");
                Assert.That(slot.PeekData().ServerVersion, Is.EqualTo(0));
                Assert.That(context.Storage.HasFile(mirrorPath), Is.False, "The mirror files are deleted with the server value.");
                Assert.That(context.Provider.WriteCallCount, Is.EqualTo(0));
                Assert.That(context.Provider.DeleteCallCount, Is.EqualTo(0), "Resetting a mirror never deletes the server value.");
            }
        }

        // 5.4 steps 7-9: once past the checkpoint the apply and persist finish even though the caller's token is cancelled
        [UnityTest]
        public IEnumerator CallerCancelAfterCheckpoint_KeepsAppliedDataPersisted()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                var slot = new ProfileSlot();
                using (TestServiceContext context = CreateContext(slot))
                {
                    var other = new ProfileSlot();
                    UploadFromOtherDevice(context.Provider.Store, () => Assert.That(other.Mutate(data => data.Coins = 9), Is.True), other);
                    InitializeAndActivate(context, AccountA);

                    using (var source = new CancellationTokenSource())
                    {
                        var calls = new List<int>();
                        var blocking = new BlockingRestoreListener(10);
                        var late = new RecordingRestoreListener(20, calls);
                        var completed = new List<RestoreReport>();
                        Action<RestoreReport> onCompleted = completed.Add;
                        context.Service.AddRestoreListener(blocking);
                        context.Service.AddRestoreListener(late);
                        context.Service.RestoreCompleted += onCompleted;
                        string slotPath = TestPaths.ProfileSlot(ProfileA, TestSlotKeys.Player);
                        try
                        {
                            UniTask<RestoreReport> restore = context.Service.RestoreAsync(source.Token).Preserve();

                            // Listener dispatch is step 11, so apply and persist are already done here
                            await AsyncTestUtility.WaitUntilAsync(() => blocking.CallCount == 1, MaxFrames, "Restore listener dispatch");

                            Assert.That(slot.PeekData().Coins, Is.EqualTo(9));
                            var applied = (JObject)SaveJson.Parse(context.Storage.GetBytes(slotPath));
                            Assert.That(ReadInt(applied[SaveEnvelope.DataProperty], "Coins"), Is.EqualTo(9), "Premise: the cloud value is already persisted.");

                            source.Cancel();
                            await AsyncTestUtility.WaitUntilAsync(() => completed.Count > 0, MaxFrames, "RestoreCompleted");

                            // The caller's await surfaces either the cancel or the finished report; neither rolls anything back
                            try
                            {
                                await restore;
                            }
                            catch (OperationCanceledException)
                            {
                            }

                            Assert.That(late.CallCount, Is.EqualTo(0), "Listeners after the cancel are skipped.");

                            RestoreReport report = completed[0];
                            Assert.That(report.Status, Is.EqualTo(SaveStatus.Canceled), report.ToString());
                            Assert.That(report.GetResult(slot).Outcome, Is.EqualTo(SlotRestoreOutcome.Restored), report.ToString());
                            Assert.That(
                                report.ListenerFailures.Any(failure => failure.Order == 20 && failure.Kind == ListenerFailureKind.Skipped), Is.True, report.ToString());

                            Assert.That(slot.PeekData().Coins, Is.EqualTo(9), "The applied value stays in memory.");
                            var after = (JObject)SaveJson.Parse(context.Storage.GetBytes(slotPath));
                            Assert.That(ReadInt(after[SaveEnvelope.DataProperty], "Coins"), Is.EqualTo(9), "The cancel rolls nothing back.");
                            Assert.That(
                                LoadSyncState(context, ProfileA).PeekSlot(TestSlotKeys.Player).LastSyncedRevision, Is.GreaterThan(0), "profile.json was persisted too.");
                        }
                        finally
                        {
                            blocking.Release();
                            context.Service.RestoreCompleted -= onCompleted;
                            context.Service.RemoveRestoreListener(blocking);
                            context.Service.RemoveRestoreListener(late);
                        }
                    }
                }
            });
        }

        // 02 H: the scheduled single-slot reconcile dispatches restore listeners for that slot only
        [UnityTest]
        public IEnumerator SingleSlotConflictReconcile_DispatchesCloudRestoreForThatSlotOnly()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                var player = new ProfileSlot();
                var stats = new ProfileSlot(StatsKey);
                using (TestServiceContext context = CreateContext(player, stats))
                {
                    ActivateReconciledAccount(context, AccountA);
                    Assert.That(player.Mutate(data => data.Coins = 1), Is.True);
                    Assert.That(stats.Mutate(data => data.Level = 1), Is.True);
                    FlushResult synced = context.FlushSync();
                    Assert.That(synced.IsComplete, Is.True, synced.ToString());

                    // Another device moves only the player value on
                    var otherPlayer = new ProfileSlot();
                    var otherStats = new ProfileSlot(StatsKey);
                    UploadFromOtherDevice(
                        context.Provider.Store, () => Assert.That(otherPlayer.Mutate(data => data.Coins = 9), Is.True), otherPlayer, otherStats);

                    var listener = new CapturingRestoreListener(0);
                    var completed = new List<RestoreReport>();
                    Action<RestoreReport> onCompleted = completed.Add;
                    context.Service.AddRestoreListener(listener);
                    context.Service.RestoreCompleted += onCompleted;
                    try
                    {
                        Assert.That(player.Mutate(data => data.Coins = 5), Is.True);
                        FlushResult conflicted = await context.Service.FlushAsync(CancellationToken.None);
                        Assert.That(conflicted.IsComplete, Is.False, "Premise: the conditional upload lost. " + conflicted);

                        await AsyncTestUtility.WaitUntilAsync(() => listener.Reports.Count > 0, MaxFrames, "Single-slot reconcile dispatch");

                        RestoreReport report = listener.Reports[0];
                        Assert.That(report.Trigger, Is.EqualTo(RestoreTrigger.CloudRestore), report.ToString());
                        Assert.That(report.Status, Is.EqualTo(SaveStatus.Success), report.ToString());
                        Assert.That(report.Slots.Count, Is.EqualTo(1), report.ToString());
                        Assert.That(report.Slots[0].SlotKey, Is.EqualTo(TestSlotKeys.Player), report.ToString());
                        Assert.That(report.GetResult(stats), Is.Null, "The reconcile covers only the conflicting slot.");
                        Assert.That(listener.Reports.Count, Is.EqualTo(1), "One conflict dispatches once.");
                        Assert.That(completed.Count, Is.EqualTo(1), "RestoreCompleted carries the same single-slot run.");
                        Assert.That(player.ResolveConflictCount, Is.EqualTo(1), report.ToString());
                    }
                    finally
                    {
                        context.Service.RestoreCompleted -= onCompleted;
                        context.Service.RemoveRestoreListener(listener);
                    }
                }
            });
        }

        private static int IndexOf(IReadOnlyList<CloudCall> calls, CloudOperation operation)
        {
            for (int i = 0; i < calls.Count; i++)
            {
                if (calls[i].Operation == operation)
                {
                    return i;
                }
            }

            return -1;
        }

        // Property names depend on the serializer settings, so match case-insensitively
        private static int ReadInt(JToken token, string property)
        {
            Assert.That(token, Is.InstanceOf<JObject>(), token?.ToString());
            JToken value = ((JObject)token).GetValue(property, StringComparison.OrdinalIgnoreCase);
            Assert.That(value, Is.Not.Null, token.ToString());
            return value.Value<int>();
        }

        private static RestoreReport RestoreSync(TestServiceContext context)
        {
            return AsyncTestUtility.RunSync(context.Service.RestoreAsync(CancellationToken.None), nameof(SaveService.RestoreAsync));
        }

        // Gateway retries off so a provider error surfaces in one call
        private static TestServiceContext CreateContext(params SaveSlot[] slots)
        {
            return CreateContext(null, slots);
        }

        private static TestServiceContext CreateContext(FakeCloudSaveProvider provider, params SaveSlot[] slots)
        {
            return TestServiceFactory.Create(new TestServiceSetup
            {
                Slots = slots,
                Provider = provider,
                ConfigureOptions = options => options.CloudRetryCount = 0,
            });
        }

        // Replaces dataSha256 with a wrong hex of the same length; the compact data marker stays intact
        private static string BreakChecksum(string envelopeText)
        {
            const string marker = "\"" + SaveEnvelope.DataSha256Property + "\":\"";
            int start = envelopeText.IndexOf(marker, StringComparison.Ordinal);
            Assert.That(start, Is.GreaterThanOrEqualTo(0), envelopeText);
            start += marker.Length;
            int end = envelopeText.IndexOf('"', start);
            Assert.That(end, Is.GreaterThan(start), envelopeText);
            return envelopeText.Substring(0, start) + new string('0', end - start) + envelopeText.Substring(end);
        }

        // A second device on the same cloud store: reconciles, mutates and uploads, then shuts down
        private static void UploadFromOtherDevice(FakeCloudStore store, Action mutate, params SaveSlot[] slots)
        {
            using (TestServiceContext other = TestServiceFactory.Create(new TestServiceSetup
                   {
                       Slots = slots,
                       Provider = new FakeCloudSaveProvider(store),
                       DeviceId = OtherDeviceId,
                       ConfigureOptions = options => options.CloudRetryCount = 0,
                   }))
            {
                ActivateReconciledAccount(other, AccountA);
                mutate();
                FlushResult flushed = other.FlushSync();
                Assert.That(flushed.IsComplete, Is.True, flushed.ToString());
            }
        }

        private static void InitializeAndActivate(TestServiceContext context, string accountId)
        {
            context.Provider.SignedInAccountId = accountId;
            Assert.That(context.InitializeSync().IsSuccess, Is.True);
            Assert.That(context.ActivateSync(ProfileId.Account(accountId)).IsSuccess, Is.True);
        }

        // Signed in, active and reconciled this epoch; counters reset afterwards
        private static void ActivateReconciledAccount(TestServiceContext context, string accountId)
        {
            InitializeAndActivate(context, accountId);
            RestoreReport report = RestoreSync(context);
            Assert.That(report.Status, Is.EqualTo(SaveStatus.Success), report.ToString());
            context.Provider.ClearCalls();
            context.Storage.ResetCounters();
        }

        // Reads profile.json directly without repairing anything
        private static ProfileSyncState LoadSyncState(TestServiceContext context, ProfileId profile)
        {
            var store = new SyncStateStore(context.Storage, new TestSaveLogger(), context.Clock);
            SyncStateLoadResult loaded = store.Load(TestPaths.ProfileDirectory(profile), SlotReadMode.ReadOnly);
            Assert.That(loaded.Status, Is.EqualTo(SyncStateLoadStatus.Loaded), loaded.Message);
            return loaded.State;
        }

        // GetBackoffDelay(1) is BaseDelay with no jitter, and BaseDelay >= MaxDelay returns MaxDelay, so one value covers both
        private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);

        private static readonly string PlayerConflictPath =
            SaveLayout.ConflictPath(TestPaths.ProfileDirectory(ProfileA), TestSlotKeys.Player);

        // One gateway retry with a deterministic backoff, so a lost response is resent with the same write id
        private static TestServiceContext CreateRetryingContext(params SaveSlot[] slots)
        {
            return TestServiceFactory.Create(new TestServiceSetup
            {
                Slots = slots,
                ConfigureOptions = options =>
                {
                    options.CloudRetryCount = 1;
                    options.CloudRetryBaseDelay = RetryDelay;
                    options.CloudRetryMaxDelay = RetryDelay;
                },
            });
        }

        // writeId of the envelope the cloud currently holds for that key; null when the key is absent
        private static string CloudWriteId(TestServiceContext context, string key)
        {
            string text = context.Provider.Store.GetText(AccountA, key);
            return text == null ? null : (string)JObject.Parse(text)[SaveEnvelope.WriteIdProperty];
        }

        // Leaves the cloud holding an older unconfirmed write while a newer one is still unconfirmed locally:
        // the ring must carry both, because only the older id can prove we own the cloud value.
        private static (string Landed, string Newer) ArrangeLandedWriteBehindNewerPending(
            TestServiceContext context,
            ProfileSlot slot)
        {
            ActivateReconciledAccount(context, AccountA);

            // The write is applied, then a Transient error is returned, so its id stays unconfirmed
            Assert.That(slot.Mutate(data => data.Coins = 6), Is.True);
            context.Provider.EnqueueLostResponse(CloudOperation.Write, TestSlotKeys.Player);
            Assert.That(context.FlushSync().IsComplete, Is.False, "Premise: the response to the landed write is lost.");

            string landed = CloudWriteId(context, TestSlotKeys.Player);
            Assert.That(landed, Is.Not.Null, "Premise: a lost response still stored the value.");

            // A newer write never reaches the cloud, so two ids are unconfirmed at once
            Assert.That(slot.Mutate(data => data.Coins = 7), Is.True);
            context.Provider.EnqueueError(CloudOperation.Write, TestSlotKeys.Player, CloudErrorKind.Transient);
            Assert.That(context.FlushSync().IsComplete, Is.False, "Premise: the newer write does not reach the cloud.");
            Assert.That(CloudWriteId(context, TestSlotKeys.Player), Is.EqualTo(landed), "Premise: the cloud still holds the older write.");

            SlotSyncState sync = LoadSyncState(context, ProfileA).PeekSlot(TestSlotKeys.Player);
            string newer = sync.PendingWriteId;
            Assert.That(newer, Is.Not.Null.And.Not.EqualTo(landed));
            Assert.That(sync.IsPendingWriteId(landed), Is.True, "Premise: both unconfirmed ids are on disk.");

            context.Provider.ClearCalls();
            context.Storage.ResetCounters();
            return (landed, newer);
        }

        /// <summary>Records CloudRestore dispatches by Order; optionally fails with a faulted task.</summary>
        private sealed class RecordingRestoreListener : IRestoreListener
        {
            private readonly List<int> _calls;
            private readonly bool _throws;

            public RecordingRestoreListener(int order, List<int> calls, bool throws = false)
            {
                Order = order;
                _calls = calls;
                _throws = throws;
            }

            public int Order { get; }

            public int CallCount { get; private set; }

            public UniTask OnRestoredAsync(RestoreReport report, CancellationToken ct)
            {
                if (report.Trigger != RestoreTrigger.CloudRestore)
                {
                    return UniTask.CompletedTask;
                }

                CallCount++;
                _calls.Add(Order);
                return _throws ? UniTask.FromException(new InvalidOperationException("Scripted restore listener failure.")) : UniTask.CompletedTask;
            }
        }

        /// <summary>Holds the dispatch open until the test releases it, so the caller can cancel after apply and persist.</summary>
        private sealed class BlockingRestoreListener : IRestoreListener
        {
            private readonly UniTaskCompletionSource _gate = new UniTaskCompletionSource();

            public BlockingRestoreListener(int order)
            {
                Order = order;
            }

            public int Order { get; }

            public int CallCount { get; private set; }

            public UniTask OnRestoredAsync(RestoreReport report, CancellationToken ct)
            {
                CallCount++;
                return _gate.Task;
            }

            public void Release()
            {
                _gate.TrySetResult();
            }
        }

        /// <summary>Keeps every dispatched report for assertions.</summary>
        private sealed class CapturingRestoreListener : IRestoreListener
        {
            public CapturingRestoreListener(int order)
            {
                Order = order;
            }

            public int Order { get; }

            public List<RestoreReport> Reports { get; } = new List<RestoreReport>();

            public UniTask OnRestoredAsync(RestoreReport report, CancellationToken ct)
            {
                Reports.Add(report);
                return UniTask.CompletedTask;
            }
        }

        /// <summary>F10 docs recipe: clears a derived LocalOnly slot when its source slot was restored or merged.</summary>
        private sealed class DerivedResetListener : IRestoreListener
        {
            private readonly ISaveService _service;
            private readonly SaveSlot _source;
            private readonly SaveSlot _derived;

            public DerivedResetListener(ISaveService service, SaveSlot source, SaveSlot derived)
            {
                _service = service;
                _source = source;
                _derived = derived;
            }

            public int Order => 100;

            public int DeleteCount { get; private set; }

            public DeleteResult LastResult { get; private set; }

            public async UniTask OnRestoredAsync(RestoreReport report, CancellationToken ct)
            {
                if (report.Trigger != RestoreTrigger.CloudRestore)
                {
                    return;
                }

                SlotRestoreResult result = report.GetResult(_source);
                if (result == null || (result.Outcome != SlotRestoreOutcome.Restored && result.Outcome != SlotRestoreOutcome.Merged))
                {
                    return;
                }

                DeleteCount++;
                LastResult = await _service.DeleteSlotAsync(_derived, DeleteTarget.LocalOnly, ct);
            }
        }
    }
}
