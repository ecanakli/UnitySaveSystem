using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Ecanakli.SaveSystem.Tests
{
    /// <summary>Strict FlushAsync: IsComplete only when local is complete and every Profile CloudSync slot is Uploaded or AlreadyInSync.</summary>
    [TestFixture]
    public sealed class FlushStrictnessTests
    {
        private const string AccountA = "account-a";
        private const int MaxFrames = 120;

        private static readonly ProfileId ProfileA = ProfileId.Account(AccountA);

        [Test]
        public void AllDirtyCloudSyncSlotsUploaded_IsComplete()
        {
            var player = new ProfileSlot();
            var stats = new ProfileSlot("stats");
            var notes = new ProfileSlot("notes", SyncMode.LocalOnly);
            using (TestServiceContext context = CreateContext(player, stats, notes))
            {
                ActivateReconciledAccount(context, AccountA);
                Assert.That(player.Mutate(data => data.Coins = 5), Is.True);
                Assert.That(stats.Mutate(data => data.Level = 2), Is.True);
                Assert.That(notes.Mutate(data => data.Coins = 1), Is.True);

                FlushResult result = context.FlushSync();

                Assert.That(result.Status, Is.EqualTo(SaveStatus.Success), result.ToString());
                Assert.That(result.Error, Is.Null);
                Assert.That(result.Local.IsComplete, Is.True);
                Assert.That(result.Cloud.Select(entry => entry.SlotKey), Is.EquivalentTo(new[] { TestSlotKeys.Player, "stats" }), "LocalOnly slots have no cloud entry.");
                Assert.That(result.Cloud.All(entry => entry.Status == CloudFlushStatus.Uploaded), Is.True, string.Join(", ", result.Cloud));
                Assert.That(result.IsComplete, Is.True);

                Assert.That(context.Provider.WriteCallCount, Is.EqualTo(1), "One batch carries every dirty slot.");
                Assert.That(context.Provider.Store.Contains(AccountA, TestSlotKeys.Player), Is.True);
                Assert.That(context.Provider.Store.Contains(AccountA, "stats"), Is.True);
                Assert.That(context.Provider.Store.Contains(AccountA, "notes"), Is.False);
            }
        }

        [Test]
        public void DirtySlotNotReconciled_SkippedNotReconciled_NotComplete()
        {
            var slot = new ProfileSlot();
            using (TestServiceContext context = CreateContext(slot))
            {
                InitializeAndActivate(context, AccountA);
                Assert.That(slot.Mutate(data => data.Coins = 5), Is.True);

                FlushResult result = context.FlushSync();

                Assert.That(result.Status, Is.EqualTo(SaveStatus.Success), result.ToString());
                Assert.That(result.Local.IsComplete, Is.True);
                AssertSingleCloud(result, CloudFlushStatus.Skipped, CloudFlushReason.NotReconciled);
                Assert.That(result.IsComplete, Is.False);
                Assert.That(context.Provider.WriteCallCount, Is.EqualTo(0));
            }
        }

        [Test]
        public void EmptyPayloadOverKnownContent_SkippedEmptyOverContent_NotComplete()
        {
            var slot = new ProfileSlot();
            using (TestServiceContext context = CreateContext(slot))
            {
                ActivateReconciledAccount(context, AccountA);
                Assert.That(slot.Mutate(data => data.Coins = 5), Is.True);
                AssertSingleCloud(context.FlushSync(), CloudFlushStatus.Uploaded, CloudFlushReason.None);
                string versionWithContent = context.Provider.Store.GetVersion(AccountA, TestSlotKeys.Player);

                var failures = new List<UploadFailure>();
                Action<UploadFailure> onUploadFailed = failures.Add;
                context.Service.UploadFailed += onUploadFailed;
                try
                {
                    Assert.That(slot.Mutate(data => data.Coins = 0), Is.True);

                    FlushResult result = context.FlushSync();

                    Assert.That(result.Status, Is.EqualTo(SaveStatus.Success), result.ToString());
                    Assert.That(result.Local.IsComplete, Is.True, "The empty data is still saved locally.");
                    AssertSingleCloud(result, CloudFlushStatus.Skipped, CloudFlushReason.EmptyOverContent);
                    Assert.That(result.IsComplete, Is.False);

                    Assert.That(context.Provider.WriteCallCount, Is.EqualTo(1));
                    Assert.That(context.Provider.Store.GetVersion(AccountA, TestSlotKeys.Player), Is.EqualTo(versionWithContent));
                    Assert.That(failures.Count, Is.EqualTo(1));
                    Assert.That(failures[0].Reason, Is.EqualTo(CloudFlushReason.EmptyOverContent));
                    Assert.That(failures[0].SlotKey, Is.EqualTo(TestSlotKeys.Player));
                }
                finally
                {
                    context.Service.UploadFailed -= onUploadFailed;
                }
            }
        }

        [Test]
        public void ReconciledCleanSlotWithoutPendingWriteId_AlreadyInSync_DirtyAgainUploads()
        {
            var slot = new ProfileSlot();
            using (TestServiceContext context = CreateContext(slot))
            {
                ActivateReconciledAccount(context, AccountA);
                Assert.That(slot.Mutate(data => data.Coins = 5), Is.True);
                AssertSingleCloud(context.FlushSync(), CloudFlushStatus.Uploaded, CloudFlushReason.None);

                FlushResult clean = context.FlushSync();
                AssertSingleCloud(clean, CloudFlushStatus.AlreadyInSync, CloudFlushReason.None);
                Assert.That(clean.IsComplete, Is.True);
                Assert.That(context.Provider.WriteCallCount, Is.EqualTo(1), "AlreadyInSync never calls the provider.");

                Assert.That(slot.Mutate(data => data.Coins = 6), Is.True);
                FlushResult dirty = context.FlushSync();
                AssertSingleCloud(dirty, CloudFlushStatus.Uploaded, CloudFlushReason.None);
                Assert.That(dirty.IsComplete, Is.True);
                Assert.That(context.Provider.WriteCallCount, Is.EqualTo(2));
            }
        }

        [Test]
        public void CleanSlotNotReconciled_IsNotAlreadyInSync()
        {
            var slot = new ProfileSlot();
            using (TestServiceContext context = CreateContext(slot))
            {
                InitializeAndActivate(context, AccountA);

                FlushResult result = context.FlushSync();

                AssertSingleCloud(result, CloudFlushStatus.Skipped, CloudFlushReason.NotReconciled);
                Assert.That(result.IsComplete, Is.False);
                Assert.That(context.Provider.TotalCallCount, Is.EqualTo(0));
            }
        }

        [Test]
        public void ReconciledCleanSlotWithPendingWriteId_UploadsInsteadOfAlreadyInSync()
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
                InjectPendingWriteId(restarted, ProfileA, TestSlotKeys.Player, "stale-write");

                Assert.That(restarted.InitializeSync().IsSuccess, Is.True);
                if (restarted.Service.ActiveProfile != ProfileA)
                {
                    Assert.That(restarted.ActivateSync(ProfileA).IsSuccess, Is.True);
                }

                RestoreReport report = AsyncTestUtility.RunSync(restarted.Service.RestoreAsync(CancellationToken.None), nameof(SaveService.RestoreAsync));
                Assert.That(report.GetResult(restartedSlot).IsSuccess, Is.True, report.ToString());

                // Premise: reconciled and clean, only the PendingWriteId is left
                SlotSyncState before = LoadSyncState(restarted, ProfileA).PeekSlot(TestSlotKeys.Player);
                Assert.That(before.PendingWriteId, Is.EqualTo("stale-write"), report.ToString());
                Assert.That(restartedSlot.Revision, Is.EqualTo(before.LastSyncedRevision), report.ToString());
                restarted.Provider.ClearCalls();

                FlushResult result = restarted.FlushSync();

                AssertSingleCloud(result, CloudFlushStatus.Uploaded, CloudFlushReason.None);
                Assert.That(result.IsComplete, Is.True);
                Assert.That(restarted.Provider.WriteCallCount, Is.EqualTo(1));
                Assert.That(LoadSyncState(restarted, ProfileA).PeekSlot(TestSlotKeys.Player).PendingWriteId, Is.Null);
            }
            finally
            {
                context.Dispose();
                restarted?.Dispose();
            }
        }

        // B2: the first upload lands with a lost response and the retry is refused; the reconcile must still see its own write
        [Test]
        public void LostUploadResponse_ThenRefusedRetry_ReconcileKeepsTheLocalDelta()
        {
            var slot = new ProfileSlot();
            using (TestServiceContext context = CreateContext(slot))
            {
                ActivateReconciledAccount(context, AccountA);

                // Synced once, so the next upload is conditional on that provider version
                Assert.That(slot.Mutate(data => data.Coins = 5), Is.True);
                AssertSingleCloud(context.FlushSync(), CloudFlushStatus.Uploaded, CloudFlushReason.None);

                // The write lands but its response is lost
                Assert.That(slot.Mutate(data => data.Coins = 6), Is.True);
                context.Provider.EnqueueLostResponse(CloudOperation.Write, TestSlotKeys.Player);
                Assert.That(context.FlushSync().IsComplete, Is.False);
                string landedWriteId = CloudWriteId(context, TestSlotKeys.Player);
                Assert.That(
                    LoadSyncState(context, ProfileA).PeekSlot(TestSlotKeys.Player).PendingWriteId,
                    Is.EqualTo(landedWriteId),
                    "Premise: the landed write is journaled.");

                // The retry carries the stale expected version and is refused
                Assert.That(slot.Mutate(data => data.Coins = 7), Is.True);
                FlushResult refused = context.FlushSync();

                AssertSingleCloud(refused, CloudFlushStatus.Failed, CloudFlushReason.CloudError);
                Assert.That(refused.Cloud[0].Error.Kind, Is.EqualTo(CloudErrorKind.Conflict), refused.ToString());
                Assert.That(CloudWriteId(context, TestSlotKeys.Player), Is.EqualTo(landedWriteId), "Premise: the refused write never reached the cloud.");
                SlotSyncState journal = LoadSyncState(context, ProfileA).PeekSlot(TestSlotKeys.Player);
                Assert.That(journal.IsPendingWriteId(landedWriteId), Is.True, "The refused id must not displace the landed one.");

                RestoreReport report = AsyncTestUtility.RunSync(context.Service.RestoreAsync(CancellationToken.None), nameof(SaveService.RestoreAsync));

                Assert.That(report.GetResult(slot).Outcome, Is.EqualTo(SlotRestoreOutcome.LocalKept), report.ToString());
                Assert.That(slot.PeekData().Coins, Is.EqualTo(7), "The unsynced local delta survives.");
                Assert.That(slot.ResolveConflictCount, Is.EqualTo(0), report.ToString());
                Assert.That(context.Storage.HasFile(PlayerConflictPath), Is.False, string.Join(", ", context.Storage.AllFilePaths));

                // The queued upload now supersedes exactly the write that landed
                Assert.That(context.FlushSync().IsComplete, Is.True);
                Assert.That(ReadCloudCoins(context), Is.EqualTo(7));
                Assert.That(LoadSyncState(context, ProfileA).PeekSlot(TestSlotKeys.Player).PendingWriteIds, Is.Empty);
            }
        }

        // S3: the gate must come back even when the provider neither returns nor honours its token
        [UnityTest]
        public IEnumerator ProviderWriteNeverReturns_TimesOutTransient_ReleasesTheGate()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                TimeSpan timeout = TimeSpan.FromSeconds(5);
                var slot = new ProfileSlot();
                TestServiceContext context = TestServiceFactory.Create(new TestServiceSetup
                {
                    Slots = new SaveSlot[] { slot },
                    ConfigureOptions = options =>
                    {
                        options.CloudRetryCount = 0;
                        options.CloudCallTimeout = timeout;

                        // Only the call timeout may fire while the clock moves
                        options.CloudDebounce = TimeSpan.FromMinutes(5);
                        options.CloudMaxWait = TimeSpan.FromMinutes(5);
                    },
                });

                try
                {
                    ActivateReconciledAccount(context, AccountA);
                    Assert.That(slot.Mutate(data => data.Coins = 5), Is.True);
                    context.Provider.IgnoresCancellationWhileHeld = true;
                    context.Provider.HoldWrites();

                    UniTask<FlushResult> flush = context.Service.FlushAsync(CancellationToken.None).Preserve();
                    await AsyncTestUtility.WaitUntilAsync(() => context.Provider.HeldCallCount == 1, MaxFrames, "Held provider write");
                    Assert.That(flush.Status.IsCompleted(), Is.False, "Premise: the flush waits for the wedged call.");

                    context.Clock.Advance(timeout);
                    await AsyncTestUtility.WaitUntilAsync(() => flush.Status.IsCompleted(), MaxFrames, "Flush after the provider call timed out");

                    FlushResult result = await flush;
                    AssertSingleCloud(result, CloudFlushStatus.Failed, CloudFlushReason.CloudError);
                    Assert.That(result.Cloud[0].Error.Kind, Is.EqualTo(CloudErrorKind.Transient), result.Cloud[0].ToString());
                    Assert.That(result.IsComplete, Is.False);
                    Assert.That(context.Provider.HeldCallCount, Is.EqualTo(1), "Premise: the abandoned call is still wedged.");

                    // The gate is free: an operation that needs it completes while the provider call is still stuck
                    UniTask<ProfileActivationResult> activation = context.Service.ActivateProfileAsync(ProfileId.Guest, CancellationToken.None).Preserve();
                    await AsyncTestUtility.WaitUntilAsync(() => activation.Status.IsCompleted(), MaxFrames, "Activation after the timeout");
                    ProfileActivationResult activated = await activation;
                    Assert.That(activated.IsSuccess, Is.True, activated.ToString());
                }
                finally
                {
                    context.Provider.ReleaseWrites();
                    context.Dispose();
                }
            });
        }

        [Test]
        public void LocalWriteFailure_NotComplete_RaisesHealthChangedOnce()
        {
            var slot = new ProfileSlot();
            using (TestServiceContext context = CreateContext(slot))
            {
                ActivateReconciledAccount(context, AccountA);
                Assert.That(slot.Mutate(data => data.Coins = 5), Is.True);
                context.Storage.FailWithKind(StorageOperation.Write, TestPaths.ProfileSlot(ProfileA, TestSlotKeys.Player), LocalWriteErrorKind.DiskFull);

                var health = new List<LocalWriteHealth>();
                Action<LocalWriteHealth> onHealthChanged = health.Add;
                context.Service.LocalWriteHealthChanged += onHealthChanged;
                try
                {
                    FlushResult result = context.FlushSync();

                    Assert.That(result.IsComplete, Is.False, result.ToString());
                    Assert.That(result.Local.IsComplete, Is.False);
                    Assert.That(result.Local.Failures.Count, Is.EqualTo(1));
                    Assert.That(result.Local.Failures[0].Kind, Is.EqualTo(LocalWriteErrorKind.DiskFull));
                    AssertSingleCloud(result, CloudFlushStatus.Skipped, CloudFlushReason.LocalWriteFailed);
                    Assert.That(context.Provider.WriteCallCount, Is.EqualTo(0), "Disk first: nothing is uploaded while the local write fails.");

                    Assert.That(health.Count, Is.EqualTo(1));
                    Assert.That(health[0].IsHealthy, Is.False);
                    Assert.That(health[0].Kind, Is.EqualTo(LocalWriteErrorKind.DiskFull));
                }
                finally
                {
                    context.Service.LocalWriteHealthChanged -= onHealthChanged;
                }
            }
        }

        [Test]
        public void GuestProfile_SkippedProfileNotCloudBacked_NotComplete()
        {
            var slot = new ProfileSlot();
            var notes = new ProfileSlot("notes", SyncMode.LocalOnly);
            using (TestServiceContext context = CreateContext(slot, notes))
            {
                Assert.That(context.InitializeSync().IsSuccess, Is.True);
                Assert.That(context.Service.ActiveProfile, Is.EqualTo(ProfileId.Guest));
                context.Provider.SignedInAccountId = AccountA;
                Assert.That(slot.Mutate(data => data.Coins = 5), Is.True);

                FlushResult result = context.FlushSync();

                Assert.That(result.Status, Is.EqualTo(SaveStatus.Success), result.ToString());
                Assert.That(result.Local.IsComplete, Is.True);
                AssertSingleCloud(result, CloudFlushStatus.Skipped, CloudFlushReason.ProfileNotCloudBacked);
                Assert.That(result.IsComplete, Is.False);
                Assert.That(context.Provider.TotalCallCount, Is.EqualTo(0));
            }
        }

        [Test]
        public void LocalProfile_SkippedProfileNotCloudBacked_NotComplete()
        {
            var slot = new ProfileSlot();
            using (TestServiceContext context = CreateContext(slot))
            {
                Assert.That(context.InitializeSync().IsSuccess, Is.True);
                context.Provider.SignedInAccountId = AccountA;
                Assert.That(context.ActivateSync(ProfileId.Local("offline")).IsSuccess, Is.True);
                Assert.That(slot.Mutate(data => data.Coins = 5), Is.True);

                FlushResult result = context.FlushSync();

                Assert.That(result.Status, Is.EqualTo(SaveStatus.Success), result.ToString());
                AssertSingleCloud(result, CloudFlushStatus.Skipped, CloudFlushReason.ProfileNotCloudBacked);
                Assert.That(result.IsComplete, Is.False);
                Assert.That(context.Provider.TotalCallCount, Is.EqualTo(0));
            }
        }

        [Test]
        public void ProviderPermanentError_FailedCloudError_ThenSuspendedUntilReconcile()
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
                    Assert.That(slot.Mutate(data => data.Coins = 5), Is.True);

                    FlushResult result = context.FlushSync();

                    Assert.That(result.Status, Is.EqualTo(SaveStatus.Success), result.ToString());
                    Assert.That(result.Local.IsComplete, Is.True);
                    AssertSingleCloud(result, CloudFlushStatus.Failed, CloudFlushReason.CloudError);
                    Assert.That(result.Cloud[0].Error, Is.Not.Null);
                    Assert.That(result.Cloud[0].Error.Kind, Is.EqualTo(CloudErrorKind.Permanent));
                    Assert.That(result.IsComplete, Is.False);
                    Assert.That(failures.Count, Is.EqualTo(1));

                    FlushResult suspended = context.FlushSync();
                    AssertSingleCloud(suspended, CloudFlushStatus.Skipped, CloudFlushReason.SuspendedUntilReconcile);
                    Assert.That(suspended.IsComplete, Is.False);
                    Assert.That(context.Provider.WriteCallCount, Is.EqualTo(1));
                    Assert.That(failures.Count, Is.EqualTo(1));
                }
                finally
                {
                    context.Service.UploadFailed -= onUploadFailed;
                }
            }
        }

        // The gate serializes activation behind the in-flight batch, so the switch supersedes the flush queued after it
        [UnityTest]
        public IEnumerator ProfileActivatedDuringHeldBatch_InFlightFlushCompletes_QueuedFlushSuperseded()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                var slot = new ProfileSlot();
                using (TestServiceContext context = CreateContext(slot))
                {
                    ActivateReconciledAccount(context, AccountA);
                    Assert.That(slot.Mutate(data => data.Coins = 3), Is.True);
                    context.Provider.HoldWrites();
                    try
                    {
                        UniTask<FlushResult> inFlight = context.Service.FlushAsync(CancellationToken.None).Preserve();
                        await AsyncTestUtility.WaitUntilAsync(() => context.Provider.HeldCallCount == 1, MaxFrames, "Held provider write");

                        UniTask<ProfileActivationResult> activation = context.Service.ActivateProfileAsync(ProfileId.Guest, CancellationToken.None).Preserve();
                        await AsyncTestUtility.WaitFramesAsync(2);
                        UniTask<FlushResult> queued = context.Service.FlushAsync(CancellationToken.None).Preserve();
                        await AsyncTestUtility.WaitFramesAsync(2);

                        Assert.That(inFlight.Status.IsCompleted(), Is.False);
                        Assert.That(activation.Status.IsCompleted(), Is.False, "Activation waits for the gate held by the batch.");
                        Assert.That(queued.Status.IsCompleted(), Is.False);

                        context.Provider.ReleaseWrites();
                        await AsyncTestUtility.WaitUntilAsync(
                            () => inFlight.Status.IsCompleted() && activation.Status.IsCompleted() && queued.Status.IsCompleted(),
                            MaxFrames,
                            "Flushes and activation");

                        FlushResult first = await inFlight;
                        Assert.That(first.Status, Is.EqualTo(SaveStatus.Success), first.ToString());
                        AssertSingleCloud(first, CloudFlushStatus.Uploaded, CloudFlushReason.None);
                        Assert.That(first.IsComplete, Is.True);
                        Assert.That(context.Provider.Store.Contains(AccountA, TestSlotKeys.Player), Is.True);

                        ProfileActivationResult activated = await activation;
                        Assert.That(activated.IsSuccess, Is.True);
                        Assert.That(context.Service.ActiveProfile, Is.EqualTo(ProfileId.Guest));

                        FlushResult superseded = await queued;
                        Assert.That(superseded.Status, Is.EqualTo(SaveStatus.Failed), superseded.ToString());
                        Assert.That(superseded.Error, Is.Not.Null);
                        Assert.That(superseded.Error.Code, Is.EqualTo(SaveErrorCode.Superseded));
                        AssertSingleCloud(superseded, CloudFlushStatus.Failed, CloudFlushReason.Aborted);
                        Assert.That(superseded.IsComplete, Is.False);
                        Assert.That(context.Provider.WriteCallCount, Is.EqualTo(1));
                    }
                    finally
                    {
                        context.Provider.ReleaseWrites();
                    }
                }
            });
        }

        [UnityTest]
        public IEnumerator DisposedDuringHeldBatch_ReportsFailedAborted()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                var slot = new ProfileSlot();
                TestServiceContext context = CreateContext(slot);
                try
                {
                    ActivateReconciledAccount(context, AccountA);
                    Assert.That(slot.Mutate(data => data.Coins = 3), Is.True);
                    context.Provider.HoldWrites();

                    UniTask<FlushResult> inFlight = context.Service.FlushAsync(CancellationToken.None).Preserve();
                    await AsyncTestUtility.WaitUntilAsync(() => context.Provider.HeldCallCount == 1, MaxFrames, "Held provider write");

                    context.Dispose();
                    context.Provider.ReleaseWrites();
                    await AsyncTestUtility.WaitUntilAsync(() => inFlight.Status.IsCompleted(), MaxFrames, "In-flight flush");

                    FlushResult result = await inFlight;
                    Assert.That(result.Status, Is.EqualTo(SaveStatus.Failed), result.ToString());
                    Assert.That(result.Error, Is.Not.Null);
                    Assert.That(result.Error.Code, Is.EqualTo(SaveErrorCode.Disposed));
                    AssertSingleCloud(result, CloudFlushStatus.Failed, CloudFlushReason.Aborted);
                    Assert.That(result.IsComplete, Is.False);
                }
                finally
                {
                    context.Provider.ReleaseWrites();
                    context.Dispose();
                }
            });
        }

        private static string PlayerConflictPath => SaveLayout.ConflictPath(TestPaths.ProfileDirectory(ProfileA), TestSlotKeys.Player);

        private static JObject ReadCloudEnvelope(TestServiceContext context, string key)
        {
            byte[] value = context.Provider.Store.GetValue(AccountA, key);
            Assert.That(value, Is.Not.Null, "No cloud value for " + key + ".");
            var envelope = SaveJson.Parse(value) as JObject;
            Assert.That(envelope, Is.Not.Null, "The cloud value is an envelope object.");
            return envelope;
        }

        private static string CloudWriteId(TestServiceContext context, string key)
        {
            return ReadCloudEnvelope(context, key).Value<string>(SaveEnvelope.WriteIdProperty);
        }

        // Property names depend on the serializer settings, so match case-insensitively
        private static int ReadCloudCoins(TestServiceContext context)
        {
            JToken data = ReadCloudEnvelope(context, TestSlotKeys.Player)[SaveEnvelope.DataProperty];
            Assert.That(data, Is.InstanceOf<JObject>(), data?.ToString());
            JToken coins = ((JObject)data).GetValue("Coins", StringComparison.OrdinalIgnoreCase);
            Assert.That(coins, Is.Not.Null, data.ToString());
            return coins.Value<int>();
        }

        private static void AssertSingleCloud(FlushResult result, CloudFlushStatus status, CloudFlushReason reason)
        {
            Assert.That(result.Cloud.Count, Is.EqualTo(1), result.ToString());
            Assert.That(result.Cloud[0].Status, Is.EqualTo(status), result.Cloud[0].ToString());
            Assert.That(result.Cloud[0].Reason, Is.EqualTo(reason), result.Cloud[0].ToString());
        }

        // Gateway retries off so a provider error surfaces in one call
        private static TestServiceContext CreateContext(params SaveSlot[] slots)
        {
            return TestServiceFactory.Create(new TestServiceSetup
            {
                Slots = slots,
                ConfigureOptions = options => options.CloudRetryCount = 0,
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

        // Rewrites profile.json while no service owns it
        private static void InjectPendingWriteId(TestServiceContext context, ProfileId profile, string key, string writeId)
        {
            var store = new SyncStateStore(context.Storage, new TestSaveLogger(), context.Clock);
            string directory = TestPaths.ProfileDirectory(profile);
            SyncStateLoadResult loaded = store.Load(directory);
            Assert.That(loaded.Status, Is.EqualTo(SyncStateLoadStatus.Loaded), loaded.Message);
            loaded.State.GetOrCreateSlot(key).PendingWriteId = writeId;
            StateWriteResult saved = store.Save(directory, loaded.State);
            Assert.That(saved.IsSuccess, Is.True, saved.Message);
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
