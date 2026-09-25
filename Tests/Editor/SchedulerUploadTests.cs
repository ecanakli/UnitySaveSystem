using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Ecanakli.SaveSystem.Tests
{
    /// <summary>Scheduler-driven uploads: coalescing, disk-first order, lost responses, backoff, suspension and profile gating.</summary>
    [TestFixture]
    public sealed class SchedulerUploadTests
    {
        private const string AccountA = "account-a";
        private const string AccountB = "account-b";
        private const int MaxFrames = 120;

        private static readonly ProfileId ProfileA = ProfileId.Account(AccountA);

        [UnityTest]
        public IEnumerator ManyMutations_CoalesceIntoOneLocalWriteAndOneUpload()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                var slot = new ProfileSlot();
                using (TestServiceContext context = CreateContext(slot))
                {
                    ActivateReconciledAccount(context, AccountA);
                    string path = TestPaths.ProfileSlot(ProfileA, TestSlotKeys.Player);

                    for (int i = 1; i <= 25; i++)
                    {
                        int coins = i;
                        Assert.That(slot.Mutate(data => data.Coins = coins), Is.True);
                    }

                    await AsyncTestUtility.WaitFramesAsync(5);
                    Assert.That(context.Storage.GetWriteCount(path), Is.EqualTo(0), "Nothing is written inside the local write delay.");
                    Assert.That(context.Provider.WriteCallCount, Is.EqualTo(0), "Nothing is uploaded inside the debounce window.");

                    context.Clock.Advance(context.Options.CloudDebounce + TimeSpan.FromMilliseconds(100));
                    await AsyncTestUtility.WaitUntilAsync(() => context.Provider.WriteCallCount > 0, MaxFrames, "Scheduled upload");
                    await AsyncTestUtility.WaitFramesAsync(10);

                    Assert.That(context.Storage.GetWriteCount(path), Is.EqualTo(1));
                    Assert.That(context.Provider.WriteCallCount, Is.EqualTo(1));
                    Assert.That(context.Provider.GetAllWriteRequests().Count, Is.EqualTo(1));

                    SlotSyncState state = LoadSyncState(context, ProfileA).PeekSlot(TestSlotKeys.Player);
                    Assert.That(state.LastSyncedRevision, Is.EqualTo(slot.Revision), "The single upload carried the final revision.");
                    Assert.That(state.PendingWriteId, Is.Null);
                }
            });
        }

        [UnityTest]
        public IEnumerator ScheduledUpload_LocalFileWrittenBeforeCloudWrite()
        {
            return AsyncTestUtility.ToCoroutine(() => AssertLocalWriteBeforeCloudWriteAsync(null));
        }

        [UnityTest]
        public IEnumerator UploadDueBeforeLocalWriteDelay_WritesDiskFirst()
        {
            return AsyncTestUtility.ToCoroutine(() => AssertLocalWriteBeforeCloudWriteAsync(options => options.LocalWriteDelay = TimeSpan.FromSeconds(5)));
        }

        [UnityTest]
        public IEnumerator LostWriteResponse_NextRestore_RecognisesTheWriteByPendingWriteId()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                var slot = new ProfileSlot();
                using (TestServiceContext context = CreateContext(slot))
                {
                    ActivateReconciledAccount(context, AccountA);
                    context.Provider.EnqueueLostResponse(CloudOperation.Write, TestSlotKeys.Player);

                    Assert.That(slot.Mutate(data => data.Coins = 7), Is.True);
                    context.Clock.Advance(context.Options.CloudDebounce + TimeSpan.FromMilliseconds(100));
                    await AsyncTestUtility.WaitUntilAsync(() => context.Provider.WriteCallCount > 0, MaxFrames, "Scheduled upload");
                    await AsyncTestUtility.WaitFramesAsync(2);

                    string pendingWriteId = LoadSyncState(context, ProfileA).PeekSlot(TestSlotKeys.Player).PendingWriteId;
                    Assert.That(pendingWriteId, Is.Not.Null, "PendingWriteId is persisted before the provider call.");
                    Assert.That(context.Provider.Store.Contains(AccountA, TestSlotKeys.Player), Is.True, "The write landed even though its response was lost.");

                    RestoreReport report = await context.Service.RestoreAsync(CancellationToken.None);

                    SlotRestoreResult result = report.GetResult(slot);
                    Assert.That(result, Is.Not.Null, report.ToString());
                    Assert.That(result.Outcome, Is.EqualTo(SlotRestoreOutcome.UpToDate), report.ToString());
                    Assert.That(slot.ResolveConflictCount, Is.EqualTo(0));
                    Assert.That(context.Storage.HasFile(SaveLayout.ConflictPath(TestPaths.ProfileDirectory(ProfileA), TestSlotKeys.Player)), Is.False);
                    Assert.That(slot.PeekData().Coins, Is.EqualTo(7));

                    SlotSyncState state = LoadSyncState(context, ProfileA).PeekSlot(TestSlotKeys.Player);
                    Assert.That(state.PendingWriteId, Is.Null);
                    Assert.That(state.LastSyncedWriteId, Is.EqualTo(pendingWriteId));
                    Assert.That(state.LastSyncedRevision, Is.EqualTo(slot.Revision));
                }
            });
        }

        [Test]
        public void LostWriteResponse_NextRestore_ReportsUpToDate()
        {
            var slot = new ProfileSlot();
            using (TestServiceContext context = CreateContext(slot))
            {
                ActivateReconciledAccount(context, AccountA);
                context.Provider.EnqueueLostResponse(CloudOperation.Write, TestSlotKeys.Player);
                Assert.That(slot.Mutate(data => data.Coins = 7), Is.True);

                FlushResult flush = context.FlushSync();
                Assert.That(flush.Cloud[0].Reason, Is.EqualTo(CloudFlushReason.CloudError), flush.ToString());
                Assert.That(context.Provider.Store.Contains(AccountA, TestSlotKeys.Player), Is.True);

                RestoreReport report = AsyncTestUtility.RunSync(context.Service.RestoreAsync(CancellationToken.None), nameof(SaveService.RestoreAsync));

                // Row 8 compares slot.Revision with LastSyncedRevision before promoting the landed revision, so it reports LocalKept and queues a duplicate upload
                Assert.That(report.GetResult(slot).Outcome, Is.EqualTo(SlotRestoreOutcome.UpToDate), report.ToString());
            }
        }

        [UnityTest]
        public IEnumerator TransientFailure_ReschedulesWithBackoff_ThenSucceeds()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                var slot = new ProfileSlot();
                using (TestServiceContext context = CreateContext(slot))
                {
                    ActivateReconciledAccount(context, AccountA);
                    context.Provider.EnqueueError(CloudOperation.Write, TestSlotKeys.Player, CloudErrorKind.Transient);
                    var failures = new List<UploadFailure>();
                    Action<UploadFailure> onUploadFailed = failures.Add;
                    context.Service.UploadFailed += onUploadFailed;
                    try
                    {
                        Assert.That(slot.Mutate(data => data.Coins = 3), Is.True);
                        context.Clock.Advance(context.Options.CloudDebounce + TimeSpan.FromMilliseconds(100));
                        await AsyncTestUtility.WaitUntilAsync(() => context.Provider.WriteCallCount > 0, MaxFrames, "First upload attempt");
                        await AsyncTestUtility.WaitFramesAsync(2);

                        TimeSpan firstBackoff = context.Options.UploadRetryBackoff[0];
                        Assert.That(context.Provider.Store.Contains(AccountA, TestSlotKeys.Player), Is.False);
                        Assert.That(context.Clock.RequestedDelays, Has.Member(firstBackoff), "The cloud lane sleeps for the first backoff step.");

                        context.Clock.Advance(firstBackoff - TimeSpan.FromSeconds(1));
                        await AsyncTestUtility.WaitFramesAsync(10);
                        Assert.That(context.Provider.WriteCallCount, Is.EqualTo(1), "No retry before the backoff elapses.");

                        context.Clock.Advance(TimeSpan.FromSeconds(2));
                        await AsyncTestUtility.WaitUntilAsync(() => context.Provider.WriteCallCount > 1, MaxFrames, "Retry after backoff");
                        await AsyncTestUtility.WaitFramesAsync(2);

                        Assert.That(context.Provider.WriteCallCount, Is.EqualTo(2));
                        Assert.That(context.Provider.Store.Contains(AccountA, TestSlotKeys.Player), Is.True);
                        SlotSyncState state = LoadSyncState(context, ProfileA).PeekSlot(TestSlotKeys.Player);
                        Assert.That(state.PendingWriteId, Is.Null);
                        Assert.That(state.LastSyncedRevision, Is.EqualTo(slot.Revision));
                        Assert.That(failures, Is.Empty, "Transient failures never raise UploadFailed.");
                    }
                    finally
                    {
                        context.Service.UploadFailed -= onUploadFailed;
                    }
                }
            });
        }

        [UnityTest]
        public IEnumerator PermanentFailure_RaisesUploadFailed_SuspendsUntilReconcile_ThenUploads()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                var slot = new ProfileSlot();
                using (TestServiceContext context = CreateContext(slot))
                {
                    ActivateReconciledAccount(context, AccountA);
                    context.Provider.EnqueueError(CloudOperation.Write, TestSlotKeys.Player, CloudErrorKind.Permanent);
                    var failures = new List<UploadFailure>();
                    Action<UploadFailure> onUploadFailed = failures.Add;
                    context.Service.UploadFailed += onUploadFailed;
                    try
                    {
                        Assert.That(slot.Mutate(data => data.Coins = 4), Is.True);
                        context.Clock.Advance(context.Options.CloudDebounce + TimeSpan.FromMilliseconds(100));
                        await AsyncTestUtility.WaitUntilAsync(() => failures.Count > 0, MaxFrames, "UploadFailed");

                        Assert.That(failures.Count, Is.EqualTo(1));
                        Assert.That(failures[0].Profile, Is.EqualTo(ProfileA));
                        Assert.That(failures[0].SlotKey, Is.EqualTo(TestSlotKeys.Player));
                        Assert.That(failures[0].Reason, Is.EqualTo(CloudFlushReason.CloudError));
                        Assert.That(failures[0].Error.Kind, Is.EqualTo(CloudErrorKind.Permanent));

                        // Suspended: neither a new mutation nor the whole backoff ladder uploads
                        Assert.That(slot.Mutate(data => data.Coins = 5), Is.True);
                        context.Clock.Advance(TimeSpan.FromMinutes(10));
                        await AsyncTestUtility.WaitFramesAsync(10);
                        Assert.That(context.Provider.WriteCallCount, Is.EqualTo(1));

                        RestoreReport report = await context.Service.RestoreAsync(CancellationToken.None);
                        Assert.That(report.Status, Is.EqualTo(SaveStatus.Success), report.ToString());

                        context.Clock.Advance(context.Options.CloudMaxWait + TimeSpan.FromSeconds(1));
                        await AsyncTestUtility.WaitUntilAsync(() => context.Provider.WriteCallCount > 1, MaxFrames, "Upload after reconcile");
                        await AsyncTestUtility.WaitFramesAsync(2);

                        Assert.That(context.Provider.WriteCallCount, Is.EqualTo(2));
                        Assert.That(context.Provider.Store.Contains(AccountA, TestSlotKeys.Player), Is.True);
                        Assert.That(LoadSyncState(context, ProfileA).PeekSlot(TestSlotKeys.Player).LastSyncedRevision, Is.EqualTo(slot.Revision));
                        Assert.That(failures.Count, Is.EqualTo(1));
                    }
                    finally
                    {
                        context.Service.UploadFailed -= onUploadFailed;
                    }
                }
            });
        }

        [UnityTest]
        public IEnumerator ActivatingAnotherProfile_CancelsPendingDebounce_NoUploadForOldAccount()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                var slot = new ProfileSlot();
                using (TestServiceContext context = CreateContext(slot))
                {
                    ActivateReconciledAccount(context, AccountA);
                    Assert.That(slot.Mutate(data => data.Coins = 9), Is.True);
                    await AsyncTestUtility.WaitFramesAsync(2);

                    context.Provider.SignedInAccountId = AccountB;
                    Assert.That(context.ActivateSync(ProfileId.Account(AccountB)).IsSuccess, Is.True);
                    Assert.That(context.Storage.GetWriteCount(TestPaths.ProfileSlot(ProfileA, TestSlotKeys.Player)), Is.EqualTo(1), "The switch flushes the old profile locally.");

                    context.Clock.Advance(context.Options.CloudMaxWait + TimeSpan.FromMinutes(1));
                    await AsyncTestUtility.WaitFramesAsync(20);

                    Assert.That(CountWriteCalls(context.Provider, AccountA), Is.EqualTo(0));
                    Assert.That(context.Provider.Store.Contains(AccountA, TestSlotKeys.Player), Is.False);
                    Assert.That(context.Provider.WriteCallCount, Is.EqualTo(0));
                }
            });
        }

        [UnityTest]
        public IEnumerator GuestAndLocalProfiles_NeverCallTheProvider()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                var slot = new ProfileSlot();
                using (TestServiceContext context = CreateContext(slot))
                {
                    Assert.That(context.InitializeSync().IsSuccess, Is.True);
                    Assert.That(context.Service.ActiveProfile, Is.EqualTo(ProfileId.Guest));

                    // Being signed in does not make a guest profile cloud-backed
                    context.Provider.SignedInAccountId = AccountA;

                    await MutateAndRunSchedulerAsync(context, slot, 1);
                    Assert.That(context.Storage.GetWriteCount(TestPaths.ProfileSlot(ProfileId.Guest, TestSlotKeys.Player)), Is.GreaterThan(0), "The local lane ran.");

                    ProfileId local = ProfileId.Local("offline");
                    Assert.That(context.ActivateSync(local).IsSuccess, Is.True);
                    await MutateAndRunSchedulerAsync(context, slot, 2);
                    Assert.That(context.Storage.GetWriteCount(TestPaths.ProfileSlot(local, TestSlotKeys.Player)), Is.GreaterThan(0), "The local lane ran.");

                    Assert.That(context.Provider.TotalCallCount, Is.EqualTo(0));
                }
            });
        }

        [UnityTest]
        public IEnumerator UploadsBeforeFirstReconcile_AreSkipped_ThenRunAfterRestore()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                var slot = new ProfileSlot();
                using (TestServiceContext context = CreateContext(slot))
                {
                    InitializeAndActivate(context, AccountA);
                    string path = TestPaths.ProfileSlot(ProfileA, TestSlotKeys.Player);

                    Assert.That(slot.Mutate(data => data.Coins = 6), Is.True);
                    context.Clock.Advance(context.Options.CloudMaxWait + TimeSpan.FromSeconds(1));
                    await AsyncTestUtility.WaitUntilAsync(() => context.Storage.GetWriteCount(path) > 0, MaxFrames, "Scheduled local write");
                    await AsyncTestUtility.WaitFramesAsync(10);

                    Assert.That(context.Provider.WriteCallCount, Is.EqualTo(0), "No upload before the slot is reconciled this epoch.");
                    Assert.That(context.Provider.Store.Contains(AccountA, TestSlotKeys.Player), Is.False);

                    RestoreReport report = await context.Service.RestoreAsync(CancellationToken.None);
                    Assert.That(report.Status, Is.EqualTo(SaveStatus.Success), report.ToString());

                    context.Clock.Advance(context.Options.CloudMaxWait + TimeSpan.FromSeconds(1));
                    await AsyncTestUtility.WaitUntilAsync(() => context.Provider.WriteCallCount > 0, MaxFrames, "Upload after reconcile");
                    await AsyncTestUtility.WaitFramesAsync(2);

                    Assert.That(context.Provider.WriteCallCount, Is.EqualTo(1));
                    Assert.That(context.Provider.Store.Contains(AccountA, TestSlotKeys.Player), Is.True);
                }
            });
        }

        // 01 s13.9, 5.3: a mutation every half debounce never lets the quiet period end; CloudMaxWait still forces the disk-first write and the upload
        [UnityTest]
        public IEnumerator ContinuousMutation_MaxWaitForcesLocalWriteAndUpload()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                var slot = new ProfileSlot();

                // Local lane delay beyond max wait: only the upload's disk-first step can write the file in time
                using (TestServiceContext context = CreateContext(slot, options => options.LocalWriteDelay = options.CloudMaxWait + TimeSpan.FromMinutes(1)))
                {
                    ActivateReconciledAccount(context, AccountA);
                    string path = TestPaths.ProfileSlot(ProfileA, TestSlotKeys.Player);
                    TimeSpan maxWait = context.Options.CloudMaxWait;
                    TimeSpan step = TimeSpan.FromTicks(context.Options.CloudDebounce.Ticks / 2);
                    int steps = (int)(maxWait.Ticks / step.Ticks);
                    Assert.That(steps, Is.GreaterThan(2), "Premise: several mutations fit inside the max wait.");

                    DateTime? uploadedAt = null;
                    int localWritesAtUpload = -1;
                    long localRevisionAtUpload = -1;
                    long uploadedRevision = -1;
                    context.Provider.AfterWrite = (provider, requests) =>
                    {
                        uploadedAt = context.Clock.UtcNow;
                        localWritesAtUpload = context.Storage.GetWriteCount(path);
                        localRevisionAtUpload = EnvelopeCodec.Decode(context.Storage.GetBytes(path), 1).Envelope.Revision;
                        uploadedRevision = EnvelopeCodec.Decode(requests[0].Value, 1).Envelope.Revision;
                    };

                    DateTime firstMark = context.Clock.UtcNow;
                    Assert.That(slot.Mutate(data => data.Coins = 1), Is.True);
                    for (int i = 1; i < steps; i++)
                    {
                        context.Clock.Advance(step);
                        await AsyncTestUtility.WaitFramesAsync(3);
                        Assert.That(context.Provider.WriteCallCount, Is.EqualTo(0), "No upload while mutations keep the debounce open (step " + i + ").");
                        Assert.That(context.Storage.GetWriteCount(path), Is.EqualTo(0), "The local lane is still waiting (step " + i + ").");

                        int coins = i + 1;
                        Assert.That(slot.Mutate(data => data.Coins = coins), Is.True);
                    }

                    Assert.That(context.Clock.UtcNow + context.Options.CloudDebounce, Is.GreaterThan(firstMark + maxWait), "Premise: the quiet period ends after max wait.");
                    context.Clock.Advance(step);
                    await AsyncTestUtility.WaitUntilAsync(() => context.Provider.WriteCallCount > 0, MaxFrames, "Upload forced by CloudMaxWait");
                    await AsyncTestUtility.WaitFramesAsync(2);

                    Assert.That(uploadedAt, Is.EqualTo(firstMark + maxWait), "The upload ran at max wait, not after a quiet period.");
                    Assert.That(context.Provider.WriteCallCount, Is.EqualTo(1));
                    Assert.That(localWritesAtUpload, Is.EqualTo(1), "The forced upload wrote the local file first.");
                    Assert.That(uploadedRevision, Is.EqualTo(slot.Revision), "The forced upload carries the latest revision.");
                    Assert.That(localRevisionAtUpload, Is.GreaterThanOrEqualTo(uploadedRevision), "Disk first.");
                    Assert.That(context.Provider.Store.Contains(AccountA, TestSlotKeys.Player), Is.True);
                }
            });
        }

        [UnityTest]
        public IEnumerator ProviderSignsOutBeforeDebounce_UploadSkippedNotSignedIn_NoWrite_UploadsAfterSignIn()
        {
            return AsyncTestUtility.ToCoroutine(() => AssertAccountChangeBeforeDebounceAsync(null, CloudFlushReason.NotSignedIn));
        }

        [UnityTest]
        public IEnumerator ProviderSwitchesAccountBeforeDebounce_UploadSkippedAccountMismatch_NoWrite_UploadsAfterSignIn()
        {
            return AsyncTestUtility.ToCoroutine(() => AssertAccountChangeBeforeDebounceAsync(AccountB, CloudFlushReason.AccountMismatch));
        }

        // 01 s13.10: the provider session changes under the active account before the debounce fires
        private static async UniTask AssertAccountChangeBeforeDebounceAsync(string changedAccountId, CloudFlushReason expectedReason)
        {
            const string dispatchLog = "[SaveSystem] Scheduler upload for 1 slot(s).";
            var slot = new ProfileSlot();
            using (TestServiceContext context = CreateContext(slot))
            {
                ActivateReconciledAccount(context, AccountA);
                context.Logger.Clear();
                context.Clock.ClearRequestedDelays();

                Assert.That(slot.Mutate(data => data.Coins = 11), Is.True);
                await AsyncTestUtility.WaitFramesAsync(2);
                context.Provider.SignedInAccountId = changedAccountId;

                context.Clock.Advance(context.Options.CloudDebounce + TimeSpan.FromMilliseconds(100));
                await AsyncTestUtility.WaitUntilAsync(() => context.Logger.Contains(TestLogLevel.Verbose, dispatchLog), MaxFrames, "Scheduled upload dispatch");
                await AsyncTestUtility.WaitFramesAsync(5);

                Assert.That(context.Provider.TotalCallCount, Is.EqualTo(0), "Nothing reaches the provider for the wrong session.");
                Assert.That(context.Clock.RequestedDelays, Has.Member(context.Options.UploadRetryBackoff[0]), "The skipped upload is rescheduled; the slot stays dirty.");

                // The strict flush reports the refusal reason for the same state
                FlushResult flush = context.FlushSync();
                Assert.That(flush.Cloud.Count, Is.EqualTo(1), flush.ToString());
                Assert.That(flush.Cloud[0].Status, Is.EqualTo(CloudFlushStatus.Skipped), flush.ToString());
                Assert.That(flush.Cloud[0].Reason, Is.EqualTo(expectedReason), flush.ToString());
                Assert.That(context.Provider.TotalCallCount, Is.EqualTo(0));
                Assert.That(context.Provider.Store.Contains(AccountA, TestSlotKeys.Player), Is.False);
                if (changedAccountId != null)
                {
                    Assert.That(context.Provider.Store.Contains(changedAccountId, TestSlotKeys.Player), Is.False);
                }

                // Back on the right account: the pending retry uploads without a new mutation
                context.Provider.SignedInAccountId = AccountA;
                IReadOnlyList<TimeSpan> backoff = context.Options.UploadRetryBackoff;
                context.Clock.Advance(backoff[backoff.Count - 1] + TimeSpan.FromSeconds(1));
                await AsyncTestUtility.WaitUntilAsync(() => context.Provider.WriteCallCount > 0, MaxFrames, "Upload after signing back in");
                await AsyncTestUtility.WaitFramesAsync(2);

                Assert.That(context.Provider.WriteCallCount, Is.EqualTo(1));
                Assert.That(CountWriteCalls(context.Provider, AccountA), Is.EqualTo(1));
                Assert.That(context.Provider.Store.Contains(AccountA, TestSlotKeys.Player), Is.True);
                if (changedAccountId != null)
                {
                    Assert.That(CountWriteCalls(context.Provider, changedAccountId), Is.EqualTo(0));
                }

                SlotSyncState state = LoadSyncState(context, ProfileA).PeekSlot(TestSlotKeys.Player);
                Assert.That(state.LastSyncedRevision, Is.EqualTo(slot.Revision));
                Assert.That(state.PendingWriteId, Is.Null);
            }
        }

        private static async UniTask AssertLocalWriteBeforeCloudWriteAsync(Action<SaveServiceOptions> configure)
        {
            var slot = new ProfileSlot();
            using (TestServiceContext context = CreateContext(slot, configure))
            {
                ActivateReconciledAccount(context, AccountA);
                string path = TestPaths.ProfileSlot(ProfileA, TestSlotKeys.Player);
                int localWritesAtCloudWrite = -1;
                context.Provider.AfterWrite = (provider, requests) => localWritesAtCloudWrite = context.Storage.GetWriteCount(path);

                Assert.That(slot.Mutate(data => data.Coins = 12), Is.True);
                context.Clock.Advance(context.Options.CloudDebounce + TimeSpan.FromMilliseconds(100));
                await AsyncTestUtility.WaitUntilAsync(() => context.Provider.WriteCallCount > 0, MaxFrames, "Scheduled upload");

                Assert.That(localWritesAtCloudWrite, Is.EqualTo(1), "The local file holds the revision before the provider receives it.");

                // The later local lane dispatch finds the slot clean
                context.Clock.Advance(TimeSpan.FromSeconds(10));
                await AsyncTestUtility.WaitFramesAsync(10);
                Assert.That(context.Storage.GetWriteCount(path), Is.EqualTo(1));
                Assert.That(context.Provider.WriteCallCount, Is.EqualTo(1));
            }
        }

        private static async UniTask MutateAndRunSchedulerAsync(TestServiceContext context, ProfileSlot slot, int coins)
        {
            Assert.That(slot.Mutate(data => data.Coins = coins), Is.True);
            context.Clock.Advance(context.Options.CloudMaxWait + TimeSpan.FromMinutes(10));
            await AsyncTestUtility.WaitFramesAsync(20);
        }

        // Gateway retries off so a provider error surfaces in one call; scheduler backoff keeps its defaults
        private static TestServiceContext CreateContext(SaveSlot slot, Action<SaveServiceOptions> configure = null)
        {
            return TestServiceFactory.Create(new TestServiceSetup
            {
                Slots = new[] { slot },
                ConfigureOptions = options =>
                {
                    options.CloudRetryCount = 0;
                    configure?.Invoke(options);
                },
            });
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
            RestoreReport report = AsyncTestUtility.RunSync(context.Service.RestoreAsync(CancellationToken.None), nameof(SaveService.RestoreAsync));
            Assert.That(report.Status, Is.EqualTo(SaveStatus.Success), report.ToString());
            context.Provider.ClearCalls();
            context.Storage.ResetCounters();
        }

        private static int CountWriteCalls(FakeCloudSaveProvider provider, string accountId)
        {
            int count = 0;
            foreach (CloudCall call in provider.Calls)
            {
                if (call.Operation == CloudOperation.Write && string.Equals(call.AccountId, accountId, StringComparison.Ordinal))
                {
                    count++;
                }
            }

            return count;
        }

        // Reads profile.json directly without repairing anything
        private static ProfileSyncState LoadSyncState(TestServiceContext context, ProfileId profile)
        {
            var store = new SyncStateStore(context.Storage, new TestSaveLogger(), context.Clock);
            SyncStateLoadResult loaded = store.Load(TestPaths.ProfileDirectory(profile), SlotReadMode.ReadOnly);
            Assert.That(loaded.Status, Is.EqualTo(SyncStateLoadStatus.Loaded), loaded.Message);
            return loaded.State;
        }
    }
}
