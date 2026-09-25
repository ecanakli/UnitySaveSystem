using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Ecanakli.SaveSystem.Tests
{
    /// <summary>DeleteSlotAsync targets and tombstones; DeleteProfileAsync RequireSynced and DiscardUnsynced modes.</summary>
    [TestFixture]
    public sealed class DeleteTests
    {
        private const string AccountA = "account-a";
        private const int MaxFrames = 120;

        private static readonly ProfileId ProfileA = ProfileId.Account(AccountA);

        [Test]
        public void DeleteSlot_LocalOnly_RemovesLocalFiles_KeepsCloudValue_NoProviderCall()
        {
            var slot = new ProfileSlot();
            using (TestServiceContext context = CreateContext(slot))
            {
                ActivateReconciledAccount(context, AccountA);
                UploadCoins(context, slot, 5);
                string version = context.Provider.Store.GetVersion(AccountA, TestSlotKeys.Player);
                context.Provider.ClearCalls();

                DeleteResult result = DeleteSlotSync(context, slot, DeleteTarget.LocalOnly);

                Assert.That(result.Status, Is.EqualTo(SaveStatus.Success), result.ToString());
                Assert.That(result.LocalDeleted, Is.True);
                Assert.That(result.Cloud, Is.EqualTo(CloudDeleteStatus.NotRequested));
                AssertNoSlotFiles(context, ProfileA, TestSlotKeys.Player);
                Assert.That(slot.State, Is.EqualTo(SlotState.Ready));
                Assert.That(slot.PeekData().Coins, Is.EqualTo(0), "Memory is reset to the normalized default.");
                Assert.That(context.Provider.TotalCallCount, Is.EqualTo(0));
                Assert.That(context.Provider.Store.GetVersion(AccountA, TestSlotKeys.Player), Is.EqualTo(version), "The cloud copy stays.");
                Assert.That(LoadSyncState(context, ProfileA).PeekSlot(TestSlotKeys.Player).PendingDelete, Is.False);

                // The default never overwrites the kept cloud copy before the next reconcile
                FlushResult flushed = context.FlushSync();
                Assert.That(flushed.Cloud.Single().Status, Is.EqualTo(CloudFlushStatus.Skipped), flushed.ToString());
                Assert.That(flushed.Cloud.Single().Reason, Is.EqualTo(CloudFlushReason.NotReconciled), flushed.ToString());
                Assert.That(context.Provider.WriteCallCount, Is.EqualTo(0));
            }
        }

        [Test]
        public void DeleteSlot_LocalAndCloud_DeletesCloudConditionally_ClearsTombstone_NoEmptyUploadFollows()
        {
            var slot = new ProfileSlot();
            using (TestServiceContext context = CreateContext(slot))
            {
                ActivateReconciledAccount(context, AccountA);
                UploadCoins(context, slot, 5);
                string version = context.Provider.Store.GetVersion(AccountA, TestSlotKeys.Player);
                context.Provider.ClearCalls();

                DeleteResult result = DeleteSlotSync(context, slot, DeleteTarget.LocalAndCloud);

                Assert.That(result.Status, Is.EqualTo(SaveStatus.Success), result.ToString());
                Assert.That(result.LocalDeleted, Is.True);
                Assert.That(result.Cloud, Is.EqualTo(CloudDeleteStatus.Deleted));
                Assert.That(result.CloudError, Is.Null);
                AssertNoSlotFiles(context, ProfileA, TestSlotKeys.Player);
                Assert.That(context.Provider.Store.Contains(AccountA, TestSlotKeys.Player), Is.False);
                Assert.That(context.Provider.DeleteCallCount, Is.EqualTo(1));
                Assert.That(context.Provider.Calls.Single(call => call.Operation == CloudOperation.Delete).DeleteExpectedVersion, Is.EqualTo(version));

                SlotSyncState sync = LoadSyncState(context, ProfileA).PeekSlot(TestSlotKeys.Player);
                Assert.That(sync.PendingDelete, Is.False);
                Assert.That(sync.DeletedWriteId, Is.Null);

                context.FlushSync();
                Assert.That(context.Provider.WriteCallCount, Is.EqualTo(0), "The default counts as synced with the deleted value.");
                Assert.That(context.Provider.Store.Contains(AccountA, TestSlotKeys.Player), Is.False);
            }
        }

        [Test]
        public void DeleteSlot_CloudDeleteFails_KeepsTombstoneWithDeletedWriteId_NextRestoreFinishesIt()
        {
            var slot = new ProfileSlot();
            using (TestServiceContext context = CreateContext(slot))
            {
                ActivateReconciledAccount(context, AccountA);
                UploadCoins(context, slot, 5);
                string version = context.Provider.Store.GetVersion(AccountA, TestSlotKeys.Player);
                string writeId = LoadSyncState(context, ProfileA).PeekSlot(TestSlotKeys.Player).LastSyncedWriteId;
                Assert.That(writeId, Is.Not.Null, "Premise: the upload recorded its write id.");
                context.Provider.EnqueueError(CloudOperation.Delete, TestSlotKeys.Player, CloudErrorKind.Transient);

                DeleteResult result = DeleteSlotSync(context, slot, DeleteTarget.LocalAndCloud);

                Assert.That(result.Status, Is.EqualTo(SaveStatus.Failed), result.ToString());
                Assert.That(result.Error.Code, Is.EqualTo(SaveErrorCode.CloudError));
                Assert.That(result.LocalDeleted, Is.True, "Local data is gone even though the cloud side failed.");
                Assert.That(result.Cloud, Is.EqualTo(CloudDeleteStatus.Failed));
                Assert.That(result.CloudError, Is.Not.Null);
                AssertNoSlotFiles(context, ProfileA, TestSlotKeys.Player);
                Assert.That(context.Provider.Store.Contains(AccountA, TestSlotKeys.Player), Is.True);

                SlotSyncState tombstone = LoadSyncState(context, ProfileA).PeekSlot(TestSlotKeys.Player);
                Assert.That(tombstone.PendingDelete, Is.True);
                Assert.That(tombstone.DeletedWriteId, Is.EqualTo(writeId));
                Assert.That(tombstone.DeletedProviderVersion, Is.EqualTo(version));

                context.Provider.ClearCalls();
                RestoreReport report = AsyncTestUtility.RunSync(context.Service.RestoreAsync(CancellationToken.None), nameof(SaveService.RestoreAsync));

                Assert.That(report.GetResult(slot).Outcome, Is.EqualTo(SlotRestoreOutcome.NoCloudData), report.ToString());
                Assert.That(context.Provider.DeleteCallCount, Is.EqualTo(1), "Restore retries the delete instead of downloading.");
                Assert.That(context.Provider.Store.Contains(AccountA, TestSlotKeys.Player), Is.False);
                Assert.That(LoadSyncState(context, ProfileA).PeekSlot(TestSlotKeys.Player).PendingDelete, Is.False);
                Assert.That(slot.PeekData().Coins, Is.EqualTo(0), "The deleted value is never restored.");
            }
        }

        [UnityTest]
        public IEnumerator DeleteSlot_LocalAndCloud_CompletesOnlyAfterCloudDelete()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                var slot = new ProfileSlot();
                using (TestServiceContext context = CreateContext(slot))
                {
                    ActivateReconciledAccount(context, AccountA);
                    UploadCoins(context, slot, 5);
                    context.Provider.HoldDeletes();
                    try
                    {
                        UniTask<DeleteResult> delete = context.Service.DeleteSlotAsync(slot, DeleteTarget.LocalAndCloud, CancellationToken.None).Preserve();
                        await AsyncTestUtility.WaitUntilAsync(() => context.Provider.HeldCallCount == 1, MaxFrames, "Held provider delete");
                        await AsyncTestUtility.WaitFramesAsync(2);

                        Assert.That(delete.Status.IsCompleted(), Is.False, "The delete awaits the provider.");
                        Assert.That(context.Storage.HasFile(TestPaths.ProfileSlot(ProfileA, TestSlotKeys.Player)), Is.False, "Local files go first.");
                        Assert.That(context.Provider.Store.Contains(AccountA, TestSlotKeys.Player), Is.True);

                        context.Provider.ReleaseDeletes();
                        await AsyncTestUtility.WaitUntilAsync(() => delete.Status.IsCompleted(), MaxFrames, "Slot delete");

                        DeleteResult result = await delete;
                        Assert.That(result.Status, Is.EqualTo(SaveStatus.Success), result.ToString());
                        Assert.That(result.Cloud, Is.EqualTo(CloudDeleteStatus.Deleted));
                        Assert.That(context.Provider.Store.Contains(AccountA, TestSlotKeys.Player), Is.False);
                    }
                    finally
                    {
                        context.Provider.ReleaseDeletes();
                    }
                }
            });
        }

        [UnityTest]
        public IEnumerator DeleteSlot_LocalAndCloud_CancelsPendingDebounce()
        {
            return AsyncTestUtility.ToCoroutine(() => AssertDeleteCancelsPendingDebounceAsync(DeleteTarget.LocalAndCloud));
        }

        [UnityTest]
        public IEnumerator DeleteSlot_LocalOnly_CancelsPendingDebounce()
        {
            return AsyncTestUtility.ToCoroutine(() => AssertDeleteCancelsPendingDebounceAsync(DeleteTarget.LocalOnly));
        }

        [Test]
        public void DeleteProfile_ActiveProfile_RefusedProfileActive()
        {
            var slot = new ProfileSlot();
            using (TestServiceContext context = CreateContext(slot))
            {
                Assert.That(context.InitializeSync().IsSuccess, Is.True);
                Assert.That(slot.Mutate(data => data.Coins = 3), Is.True);
                context.Service.FlushLocalNow();

                DeleteResult result = DeleteProfileSync(context, ProfileId.Guest, ProfileDeleteMode.DiscardUnsynced);

                Assert.That(result.Status, Is.EqualTo(SaveStatus.Failed), result.ToString());
                Assert.That(result.Error.Code, Is.EqualTo(SaveErrorCode.ProfileActive));
                Assert.That(result.LocalDeleted, Is.False);
                Assert.That(context.Storage.HasFile(TestPaths.ProfileSlot(ProfileId.Guest, TestSlotKeys.Player)), Is.True);
                Assert.That(slot.PeekData().Coins, Is.EqualTo(3));
            }
        }

        [Test]
        public void DeleteProfile_RequireSynced_SyncedAccount_DeletesDirectory_CloudUntouched()
        {
            var player = new ProfileSlot();
            var notes = new ProfileSlot("notes", SyncMode.LocalOnly);
            using (TestServiceContext context = CreateContext(player, notes))
            {
                ActivateReconciledAccount(context, AccountA);
                UploadCoins(context, player, 5);

                // LocalOnly slots never block RequireSynced
                Assert.That(notes.Mutate(data => data.Coins = 1), Is.True);
                Assert.That(context.ActivateSync(ProfileId.Guest).IsSuccess, Is.True);
                context.Provider.ClearCalls();

                DeleteResult result = DeleteProfileSync(context, ProfileA, ProfileDeleteMode.RequireSynced);

                Assert.That(result.Status, Is.EqualTo(SaveStatus.Success), result.ToString());
                Assert.That(result.LocalDeleted, Is.True);
                Assert.That(result.Cloud, Is.EqualTo(CloudDeleteStatus.NotRequested));
                Assert.That(result.UnsyncedSlotKeys, Is.Empty);
                AssertProfileDirectoryGone(context, ProfileA);
                Assert.That(context.Provider.TotalCallCount, Is.EqualTo(0));
                Assert.That(context.Provider.Store.Contains(AccountA, TestSlotKeys.Player), Is.True);
            }
        }

        [Test]
        public void DeleteProfile_RequireSynced_DirtyCloudSyncSlot_RefusedWithKeys()
        {
            var player = new ProfileSlot();
            var stats = new ProfileSlot("stats");
            using (TestServiceContext context = CreateContext(player, stats))
            {
                ActivateReconciledAccount(context, AccountA);
                Assert.That(player.Mutate(data => data.Coins = 5), Is.True);
                Assert.That(stats.Mutate(data => data.Level = 2), Is.True);
                Assert.That(context.FlushSync().IsComplete, Is.True);

                // Written locally by the switch, never uploaded
                Assert.That(player.Mutate(data => data.Coins = 6), Is.True);
                Assert.That(context.ActivateSync(ProfileId.Guest).IsSuccess, Is.True);

                DeleteResult result = DeleteProfileSync(context, ProfileA, ProfileDeleteMode.RequireSynced);

                AssertUnsyncedRefusal(result, TestSlotKeys.Player);
                Assert.That(context.Storage.HasDirectory(TestPaths.ProfileDirectory(ProfileA)), Is.True);
                Assert.That(context.Storage.HasFile(TestPaths.ProfileSlot(ProfileA, TestSlotKeys.Player)), Is.True);
                Assert.That(context.Storage.HasFile(TestPaths.ProfileSlot(ProfileA, "stats")), Is.True);
            }
        }

        [Test]
        public void DeleteProfile_RequireSynced_PendingWriteId_Refused()
        {
            var player = new ProfileSlot();
            using (TestServiceContext context = CreateContext(player))
            {
                ActivateReconciledAccount(context, AccountA);
                UploadCoins(context, player, 5);
                Assert.That(context.ActivateSync(ProfileId.Guest).IsSuccess, Is.True);
                InjectPendingWriteId(context, ProfileA, TestSlotKeys.Player, "unconfirmed-write");

                DeleteResult result = DeleteProfileSync(context, ProfileA, ProfileDeleteMode.RequireSynced);

                AssertUnsyncedRefusal(result, TestSlotKeys.Player);
                Assert.That(context.Storage.HasDirectory(TestPaths.ProfileDirectory(ProfileA)), Is.True);
            }
        }

        [Test]
        public void DeleteProfile_RequireSynced_UndeterminedPresence_Refused()
        {
            var player = new ProfileSlot();
            using (TestServiceContext context = CreateContext(player))
            {
                ActivateReconciledAccount(context, AccountA);
                UploadCoins(context, player, 5);
                Assert.That(context.ActivateSync(ProfileId.Guest).IsSuccess, Is.True);
                StorageFault unreadable = context.Storage.FailWithIOException(StorageOperation.Read, TestPaths.ProfileSlot(ProfileA, TestSlotKeys.Player));

                DeleteResult result = DeleteProfileSync(context, ProfileA, ProfileDeleteMode.RequireSynced);

                context.Storage.RemoveFault(unreadable);
                AssertUnsyncedRefusal(result, TestSlotKeys.Player);
                Assert.That(context.Storage.HasFile(TestPaths.ProfileSlot(ProfileA, TestSlotKeys.Player)), Is.True);
            }
        }

        [Test]
        public void DeleteProfile_RequireSynced_GuestWithCloudSyncContent_Refused()
        {
            var player = new ProfileSlot();
            using (TestServiceContext context = CreateContext(player))
            {
                Assert.That(context.InitializeSync().IsSuccess, Is.True);
                Assert.That(player.Mutate(data => data.Coins = 4), Is.True);

                // Local profiles never claim the guest directory
                Assert.That(context.ActivateSync(ProfileId.Local("offline")).IsSuccess, Is.True);
                Assert.That(context.Storage.HasFile(TestPaths.ProfileSlot(ProfileId.Guest, TestSlotKeys.Player)), Is.True, "Premise: guest content is on disk.");

                DeleteResult result = DeleteProfileSync(context, ProfileId.Guest, ProfileDeleteMode.RequireSynced);

                AssertUnsyncedRefusal(result, TestSlotKeys.Player);
                Assert.That(context.Storage.HasFile(TestPaths.ProfileSlot(ProfileId.Guest, TestSlotKeys.Player)), Is.True);
            }
        }

        [Test]
        public void DeleteProfile_DiscardUnsynced_DirtyAccount_DeletesDirectory_LastActiveDoesNotPointAtIt()
        {
            var player = new ProfileSlot();
            using (TestServiceContext context = CreateContext(player))
            {
                ActivateReconciledAccount(context, AccountA);
                UploadCoins(context, player, 5);
                Assert.That(player.Mutate(data => data.Coins = 9), Is.True);
                Assert.That(context.ActivateSync(ProfileId.Guest).IsSuccess, Is.True);
                context.Provider.ClearCalls();

                DeleteResult result = DeleteProfileSync(context, ProfileA, ProfileDeleteMode.DiscardUnsynced);

                Assert.That(result.Status, Is.EqualTo(SaveStatus.Success), result.ToString());
                Assert.That(result.LocalDeleted, Is.True);
                Assert.That(result.UnsyncedSlotKeys, Is.Empty);
                AssertProfileDirectoryGone(context, ProfileA);
                Assert.That(context.Provider.TotalCallCount, Is.EqualTo(0), "Cloud data is untouched.");
                Assert.That(context.Provider.Store.Contains(AccountA, TestSlotKeys.Player), Is.True);

                DeviceStateLoadResult device = new DeviceStateStore(context.Storage, new TestSaveLogger(), context.Clock).Load(SlotReadMode.ReadOnly);
                Assert.That(device.State.LastActiveProfile, Is.Not.EqualTo((ProfileId?)ProfileA));
            }
        }

        [Test]
        public void DeleteProfile_DiscardUnsynced_GuestWithContent_Deletes()
        {
            var player = new ProfileSlot();
            using (TestServiceContext context = CreateContext(player))
            {
                Assert.That(context.InitializeSync().IsSuccess, Is.True);
                Assert.That(player.Mutate(data => data.Coins = 4), Is.True);
                Assert.That(context.ActivateSync(ProfileId.Local("offline")).IsSuccess, Is.True);

                DeleteResult result = DeleteProfileSync(context, ProfileId.Guest, ProfileDeleteMode.DiscardUnsynced);

                Assert.That(result.Status, Is.EqualTo(SaveStatus.Success), result.ToString());
                AssertProfileDirectoryGone(context, ProfileId.Guest);
                Assert.That(context.Service.ActiveProfile, Is.EqualTo(ProfileId.Local("offline")));
            }
        }

        // Edge: the pointer write failed on the switch away, so device.json still names the deleted local profile; init falls back to Guest (04a A3 5.6 step 7)
        [Test]
        public void DeleteProfile_StaleLocalLastActivePointer_RestartFallsBackToGuest_NothingDeleted()
        {
            ProfileId saveOne = ProfileId.Local("save1");
            TestServiceContext context = CreateContext(new ProfileSlot());
            TestServiceContext restarted = null;
            try
            {
                ProfileSlot player = context.Slot<ProfileSlot>();
                Assert.That(context.InitializeSync().IsSuccess, Is.True);
                Assert.That(context.ActivateSync(saveOne).IsSuccess, Is.True);
                Assert.That(player.Mutate(data => data.Coins = 4), Is.True);
                Assert.That(context.Service.FlushLocalNow().IsComplete, Is.True);

                StorageFault pointerWriteFails = context.Storage.FailWithKind(StorageOperation.Write, TestPaths.DeviceState, LocalWriteErrorKind.IoError);
                ProfileActivationResult guest = context.ActivateSync(ProfileId.Guest);
                Assert.That(guest.IsSuccess, Is.True, guest.ToString());
                Assert.That(player.Mutate(data => data.Coins = 7), Is.True);
                Assert.That(context.Service.FlushLocalNow().IsComplete, Is.True);

                DeleteResult deleted = DeleteProfileSync(context, saveOne, ProfileDeleteMode.DiscardUnsynced);

                Assert.That(deleted.Status, Is.EqualTo(SaveStatus.Success), deleted.ToString());
                AssertProfileDirectoryGone(context, saveOne);
                Assert.That(LoadLastActiveProfile(context), Is.EqualTo((ProfileId?)saveOne), "Premise: device.json still names the deleted profile.");

                context.Storage.RemoveFault(pointerWriteFails);
                string guestSlotPath = TestPaths.ProfileSlot(ProfileId.Guest, TestSlotKeys.Player);
                byte[] guestBytes = context.Storage.GetBytes(guestSlotPath);
                Assert.That(guestBytes, Is.Not.Null, "Premise: guest progress is on disk.");

                var restartedPlayer = new ProfileSlot();
                restarted = TestServiceFactory.Restart(context, restartedPlayer);
                IReadOnlyList<string> filesBefore = restarted.Storage.AllFilePaths;

                InitializeResult initialized = restarted.InitializeSync();

                Assert.That(initialized.IsSuccess, Is.True, initialized.ToString());
                Assert.That(restarted.Service.ActiveProfile, Is.EqualTo(ProfileId.Guest));
                Assert.That(restartedPlayer.State, Is.EqualTo(SlotState.Ready));
                Assert.That(restartedPlayer.PeekData().Coins, Is.EqualTo(7), "Guest data loads; nothing of the deleted profile comes back.");
                Assert.That(restarted.Storage.HasDirectory(TestPaths.ProfileDirectory(saveOne)), Is.False, "The deleted profile is not recreated.");
                Assert.That(restarted.Storage.GetBytes(guestSlotPath), Is.EqualTo(guestBytes));
                Assert.That(filesBefore.Where(path => !restarted.Storage.HasFile(path)), Is.Empty, "Init deletes nothing.");
                Assert.That(LoadLastActiveProfile(restarted), Is.Not.EqualTo((ProfileId?)saveOne), "The stale pointer is replaced.");
                Assert.That(restarted.Provider.TotalCallCount, Is.EqualTo(0));
            }
            finally
            {
                context.Dispose();
                restarted?.Dispose();
            }
        }

        // A pointer write that failed earlier is retried when the delete clears the pointer after the disk recovers
        [Test]
        public void DeleteProfile_EarlierPointerWriteFailed_DiskRecovered_ClearsStalePointer()
        {
            ProfileId saveOne = ProfileId.Local("save1");
            TestServiceContext context = CreateContext(new ProfileSlot());
            try
            {
                ProfileSlot player = context.Slot<ProfileSlot>();
                Assert.That(context.InitializeSync().IsSuccess, Is.True);
                Assert.That(context.ActivateSync(saveOne).IsSuccess, Is.True);
                Assert.That(player.Mutate(data => data.Coins = 4), Is.True);
                Assert.That(context.Service.FlushLocalNow().IsComplete, Is.True);

                StorageFault pointerWriteFails = context.Storage.FailWithKind(StorageOperation.Write, TestPaths.DeviceState, LocalWriteErrorKind.IoError);
                Assert.That(context.ActivateSync(ProfileId.Guest).IsSuccess, Is.True);
                Assert.That(LoadLastActiveProfile(context), Is.EqualTo((ProfileId?)saveOne), "Premise: the pointer write failed.");
                context.Storage.RemoveFault(pointerWriteFails);

                DeleteResult deleted = DeleteProfileSync(context, saveOne, ProfileDeleteMode.DiscardUnsynced);

                Assert.That(deleted.Status, Is.EqualTo(SaveStatus.Success), deleted.ToString());
                AssertProfileDirectoryGone(context, saveOne);
                Assert.That(LoadLastActiveProfile(context), Is.Not.EqualTo((ProfileId?)saveOne), "The stale pointer on disk is cleared.");
            }
            finally
            {
                context.Dispose();
            }
        }

        // Edge: device.json names an account whose directory is gone; init falls back to Guest and never claims guest data
        [Test]
        public void Initialize_StaleAccountLastActivePointer_DirectoryMissing_FallsBackToGuest_NoClaim()
        {
            ProfileId profileB = ProfileId.Account("account-b");
            TestServiceContext context = CreateContext(new ProfileSlot());
            TestServiceContext restarted = null;
            try
            {
                // Account B keeps local progress, then the player continues as guest
                ProfileSlot player = context.Slot<ProfileSlot>();
                Assert.That(context.InitializeSync().IsSuccess, Is.True);
                Assert.That(context.ActivateSync(profileB).IsSuccess, Is.True);
                Assert.That(player.Mutate(data => data.Coins = 9), Is.True);
                Assert.That(context.Service.FlushLocalNow().IsComplete, Is.True);
                Assert.That(context.ActivateSync(ProfileId.Guest).IsSuccess, Is.True);
                Assert.That(player.Mutate(data => data.Coins = 3), Is.True);
                Assert.That(context.Service.FlushLocalNow().IsComplete, Is.True);

                var restartedPlayer = new ProfileSlot();
                restarted = TestServiceFactory.Restart(context, restartedPlayer);

                // A pointer left behind after account A's directory was deleted
                var deviceStore = new DeviceStateStore(restarted.Storage, new TestSaveLogger(), restarted.Clock);
                DeviceStateLoadResult device = deviceStore.Load(SlotReadMode.ReadOnly);
                Assert.That(device.Status, Is.EqualTo(DeviceStateLoadStatus.Loaded), device.Message);
                device.State.LastActiveProfile = ProfileA;
                Assert.That(deviceStore.Save(device.State).IsSuccess, Is.True);
                Assert.That(restarted.Storage.HasDirectory(TestPaths.ProfileDirectory(ProfileA)), Is.False, "Premise: account A has no directory.");

                string accountBDirectory = TestPaths.ProfileDirectory(profileB) + "/";
                Dictionary<string, byte[]> accountBFiles = restarted.Storage.AllFilePaths
                    .Where(path => path.StartsWith(accountBDirectory, StringComparison.Ordinal))
                    .ToDictionary(path => path, path => restarted.Storage.GetBytes(path));
                Assert.That(accountBFiles.ContainsKey(TestPaths.ProfileSlot(profileB, TestSlotKeys.Player)), Is.True, "Premise: account B progress is on disk.");
                string guestSlotPath = TestPaths.ProfileSlot(ProfileId.Guest, TestSlotKeys.Player);
                string accountASlotPath = TestPaths.ProfileSlot(ProfileA, TestSlotKeys.Player);
                byte[] guestBytes = restarted.Storage.GetBytes(guestSlotPath);
                Assert.That(guestBytes, Is.Not.Null, "Premise: guest progress is on disk.");

                InitializeResult initialized = restarted.InitializeSync();

                Assert.That(initialized.IsSuccess, Is.True, initialized.ToString());
                Assert.That(restarted.Service.ActiveProfile, Is.EqualTo(ProfileId.Guest));
                Assert.That(restartedPlayer.State, Is.EqualTo(SlotState.Ready));
                Assert.That(restartedPlayer.PeekData().Coins, Is.EqualTo(3), "Guest data loads; nothing from account B.");

                foreach (KeyValuePair<string, byte[]> file in accountBFiles)
                {
                    Assert.That(restarted.Storage.GetBytes(file.Key), Is.EqualTo(file.Value), "Account B is untouched: " + file.Key);
                }

                Assert.That(restarted.Storage.GetBytes(guestSlotPath), Is.EqualTo(guestBytes), "Guest progress stays in guest.");
                Assert.That(restarted.Storage.HasDirectory(TestPaths.ProfileDirectory(ProfileA)), Is.False, "No directory is created for the stale account.");
                Assert.That(LoadLastActiveProfile(restarted), Is.Not.EqualTo((ProfileId?)ProfileA), "The stale pointer is replaced.");
                Assert.That(restarted.Provider.TotalCallCount, Is.EqualTo(0), "Init makes no provider calls.");

                // An explicit activation after sign-in still claims under the normal rule
                ProfileActivationResult activated = restarted.ActivateSync(ProfileA);
                Assert.That(activated.IsSuccess, Is.True, activated.ToString());
                Assert.That(activated.ClaimedGuestData, Is.True);
                Assert.That(restarted.Storage.GetBytes(accountASlotPath), Is.EqualTo(guestBytes));
            }
            finally
            {
                context.Dispose();
                restarted?.Dispose();
            }
        }

        private static ProfileId? LoadLastActiveProfile(TestServiceContext context)
        {
            DeviceStateLoadResult device = new DeviceStateStore(context.Storage, new TestSaveLogger(), context.Clock).Load(SlotReadMode.ReadOnly);
            Assert.That(device.Status, Is.EqualTo(DeviceStateLoadStatus.Loaded), device.Message);
            return device.State.LastActiveProfile;
        }

        private static async UniTask AssertDeleteCancelsPendingDebounceAsync(DeleteTarget target)
        {
            var slot = new ProfileSlot();
            using (TestServiceContext context = CreateContext(slot))
            {
                ActivateReconciledAccount(context, AccountA);
                UploadCoins(context, slot, 5);
                string version = context.Provider.Store.GetVersion(AccountA, TestSlotKeys.Player);
                string path = TestPaths.ProfileSlot(ProfileA, TestSlotKeys.Player);
                context.Provider.ClearCalls();

                // Local write delay and cloud debounce are both pending
                Assert.That(slot.Mutate(data => data.Coins = 6), Is.True);
                await AsyncTestUtility.WaitFramesAsync(2);
                Assert.That(context.Provider.WriteCallCount, Is.EqualTo(0), "Premise: the upload is still debounced.");

                DeleteResult result = await context.Service.DeleteSlotAsync(slot, target, CancellationToken.None);
                Assert.That(result.Status, Is.EqualTo(SaveStatus.Success), result.ToString());
                int writesAfterDelete = context.Storage.GetWriteCount(path);

                context.Clock.Advance(context.Options.CloudMaxWait + TimeSpan.FromMinutes(1));
                await AsyncTestUtility.WaitFramesAsync(20);

                Assert.That(context.Provider.WriteCallCount, Is.EqualTo(0), "The debounced upload never runs after the delete.");
                Assert.That(context.Storage.GetWriteCount(path), Is.EqualTo(writesAfterDelete), "The pending local write never recreates the file.");
                Assert.That(context.Storage.HasFile(path), Is.False);
                if (target == DeleteTarget.LocalAndCloud)
                {
                    Assert.That(context.Provider.Store.Contains(AccountA, TestSlotKeys.Player), Is.False);
                }
                else
                {
                    Assert.That(context.Provider.Store.GetVersion(AccountA, TestSlotKeys.Player), Is.EqualTo(version));
                }
            }
        }

        private static void AssertUnsyncedRefusal(DeleteResult result, params string[] keys)
        {
            Assert.That(result.Status, Is.EqualTo(SaveStatus.Failed), result.ToString());
            Assert.That(result.Error, Is.Not.Null);
            Assert.That(result.Error.Code, Is.EqualTo(SaveErrorCode.UnsyncedChanges), result.ToString());
            Assert.That(result.UnsyncedSlotKeys, Is.EquivalentTo(keys));
            Assert.That(result.LocalDeleted, Is.False);
            Assert.That(result.Cloud, Is.EqualTo(CloudDeleteStatus.NotRequested));
        }

        private static void AssertNoSlotFiles(TestServiceContext context, ProfileId profile, string key)
        {
            string path = TestPaths.ProfileSlot(profile, key);
            Assert.That(context.Storage.HasFile(path), Is.False, path);
            Assert.That(context.Storage.HasFile(TestPaths.Bak(path)), Is.False, TestPaths.Bak(path));
            Assert.That(context.Storage.HasFile(TestPaths.Tmp(path)), Is.False, TestPaths.Tmp(path));
        }

        private static void AssertProfileDirectoryGone(TestServiceContext context, ProfileId profile)
        {
            string directory = TestPaths.ProfileDirectory(profile);
            Assert.That(context.Storage.HasDirectory(directory), Is.False, directory);
            Assert.That(context.Storage.AllFilePaths.Where(path => path.StartsWith(directory + "/", StringComparison.Ordinal)), Is.Empty);
        }

        private static DeleteResult DeleteSlotSync(TestServiceContext context, SaveSlot slot, DeleteTarget target)
        {
            return AsyncTestUtility.RunSync(context.Service.DeleteSlotAsync(slot, target, CancellationToken.None), nameof(SaveService.DeleteSlotAsync));
        }

        private static DeleteResult DeleteProfileSync(TestServiceContext context, ProfileId profile, ProfileDeleteMode mode)
        {
            return AsyncTestUtility.RunSync(context.Service.DeleteProfileAsync(profile, mode, CancellationToken.None), nameof(SaveService.DeleteProfileAsync));
        }

        private static void UploadCoins(TestServiceContext context, ProfileSlot slot, int coins)
        {
            Assert.That(slot.Mutate(data => data.Coins = coins), Is.True);
            FlushResult flushed = context.FlushSync();
            Assert.That(flushed.IsComplete, Is.True, flushed.ToString());
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

        // Signed in, active and reconciled this epoch; counters reset afterwards
        private static void ActivateReconciledAccount(TestServiceContext context, string accountId)
        {
            context.Provider.SignedInAccountId = accountId;
            Assert.That(context.InitializeSync().IsSuccess, Is.True);
            Assert.That(context.ActivateSync(ProfileId.Account(accountId)).IsSuccess, Is.True);
            RestoreReport report = AsyncTestUtility.RunSync(context.Service.RestoreAsync(CancellationToken.None), nameof(SaveService.RestoreAsync));
            Assert.That(report.Status, Is.EqualTo(SaveStatus.Success), report.ToString());
            context.Provider.ClearCalls();
            context.Storage.ResetCounters();
        }

        // Rewrites profile.json of a profile that is not active
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
