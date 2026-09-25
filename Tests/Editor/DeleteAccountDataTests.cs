using System;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;

namespace Ecanakli.SaveSystem.Tests
{
    /// <summary>DeleteAccountDataAsync: refusals, tombstones before cloud deletes, completeness and retry.</summary>
    [TestFixture]
    public sealed class DeleteAccountDataTests
    {
        private const string AccountA = "account-a";
        private const string AccountB = "account-b";
        private const string Stats = "stats";

        private static readonly ProfileId ProfileA = ProfileId.Account(AccountA);
        private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

        [Test]
        public void ActiveAccount_RefusedProfileActive_ZeroProviderCalls()
        {
            var player = new ProfileSlot();
            using (TestServiceContext context = CreateContext(player))
            {
                ActivateReconciledAccount(context, AccountA);
                UploadCoins(context, player, 5);
                context.Provider.ClearCalls();

                AccountDataDeleteResult result = DeleteAccountDataSync(context, AccountA);

                AssertRefused(result, SaveErrorCode.ProfileActive);
                Assert.That(context.Provider.TotalCallCount, Is.EqualTo(0));
                Assert.That(context.Provider.Store.Contains(AccountA, TestSlotKeys.Player), Is.True);
                Assert.That(context.Storage.HasFile(TestPaths.ProfileSlot(ProfileA, TestSlotKeys.Player)), Is.True);
                Assert.That(player.PeekData().Coins, Is.EqualTo(5));
            }
        }

        [TestCase(null, SaveErrorCode.NotSignedIn)]
        [TestCase(AccountB, SaveErrorCode.AccountMismatch)]
        public void SignedOutOrOtherAccount_Refused_ZeroProviderCalls_NoTombstones(string signedInAccountId, SaveErrorCode expected)
        {
            var player = new ProfileSlot();
            using (TestServiceContext context = CreateContext(player))
            {
                PrepareInactiveSyncedAccount(context, player);
                context.Provider.SignedInAccountId = signedInAccountId;
                context.Storage.ResetCounters();

                AccountDataDeleteResult result = DeleteAccountDataSync(context, AccountA);

                AssertRefused(result, expected);
                Assert.That(context.Provider.TotalCallCount, Is.EqualTo(0));
                Assert.That(context.Storage.CountCalls(StorageOperation.Mutations, TestPaths.ProfileDirectory(ProfileA) + "/*"), Is.EqualTo(0), "Nothing local changes.");
                Assert.That(context.Storage.HasFile(TestPaths.ProfileSlot(ProfileA, TestSlotKeys.Player)), Is.True);
                Assert.That(LoadSyncState(context, ProfileA).PeekSlot(TestSlotKeys.Player).PendingDelete, Is.False);
            }
        }

        [Test]
        public void AllCloudKeysDeleted_DeletesLocalDirectory_IsComplete()
        {
            var player = new ProfileSlot();
            var stats = new ProfileSlot(Stats);
            var notes = new ProfileSlot("notes", SyncMode.LocalOnly);
            using (TestServiceContext context = CreateContext(player, stats, notes))
            {
                ActivateReconciledAccount(context, AccountA);
                Assert.That(player.Mutate(data => data.Coins = 5), Is.True);
                Assert.That(stats.Mutate(data => data.Level = 3), Is.True);
                Assert.That(notes.Mutate(data => data.Coins = 1), Is.True);
                Assert.That(context.FlushSync().IsComplete, Is.True);
                Assert.That(context.ActivateSync(ProfileId.Guest).IsSuccess, Is.True);
                context.Provider.ClearCalls();

                AccountDataDeleteResult result = DeleteAccountDataSync(context, AccountA);

                Assert.That(result.Status, Is.EqualTo(SaveStatus.Success), result.ToString());
                Assert.That(result.Error, Is.Null);
                Assert.That(result.AccountId, Is.EqualTo(AccountA));
                Assert.That(result.IsComplete, Is.True);
                Assert.That(result.LocalDeleted, Is.True);
                AssertCloud(result, (TestSlotKeys.Player, CloudDeleteStatus.Deleted), (Stats, CloudDeleteStatus.Deleted));

                Assert.That(context.Provider.DeleteCallCount, Is.EqualTo(2), "LocalOnly slots have no cloud key.");
                Assert.That(context.Provider.Calls.Where(call => call.Operation == CloudOperation.Delete).Select(call => call.DeleteExpectedVersion), Is.All.Null,
                    "Account data deletes are unconditional.");
                Assert.That(context.Provider.Store.GetKeys(AccountA), Is.Empty);
                AssertProfileDirectoryGone(context, ProfileA);
                Assert.That(context.Service.ActiveProfile, Is.EqualTo(ProfileId.Guest));
            }
        }

        [Test]
        public void NotFoundCountsAsAlreadyAbsent_IsComplete()
        {
            var player = new ProfileSlot();
            var stats = new ProfileSlot(Stats);
            using (TestServiceContext context = CreateContext(player, stats))
            {
                PrepareInactiveSyncedAccount(context, player);
                Assert.That(context.Provider.Store.Contains(AccountA, Stats), Is.False, "Premise: stats was never uploaded.");
                context.Provider.ClearCalls();

                AccountDataDeleteResult result = DeleteAccountDataSync(context, AccountA);

                Assert.That(result.Status, Is.EqualTo(SaveStatus.Success), result.ToString());
                Assert.That(result.IsComplete, Is.True);
                AssertCloud(result, (TestSlotKeys.Player, CloudDeleteStatus.Deleted), (Stats, CloudDeleteStatus.AlreadyAbsent));
                AssertProfileDirectoryGone(context, ProfileA);
            }
        }

        [Test]
        public void PartialCloudFailure_KeepsLocalAndTombstones_IsCompleteFalse()
        {
            var player = new ProfileSlot();
            var stats = new ProfileSlot(Stats);
            using (TestServiceContext context = CreateContext(player, stats))
            {
                string playerWriteId = PrepareTwoUploadedSlots(context, player, stats);
                context.Provider.EnqueueError(CloudOperation.Delete, Stats, CloudErrorKind.Transient);

                AccountDataDeleteResult result = DeleteAccountDataSync(context, AccountA);

                Assert.That(result.Status, Is.EqualTo(SaveStatus.Failed), result.ToString());
                Assert.That(result.Error.Code, Is.EqualTo(SaveErrorCode.CloudError));
                Assert.That(result.IsComplete, Is.False);
                Assert.That(result.LocalDeleted, Is.False);
                AssertCloud(result, (TestSlotKeys.Player, CloudDeleteStatus.Deleted), (Stats, CloudDeleteStatus.Failed));
                Assert.That(result.Cloud[1].Error, Is.Not.Null);

                Assert.That(context.Provider.Store.Contains(AccountA, TestSlotKeys.Player), Is.False);
                Assert.That(context.Provider.Store.Contains(AccountA, Stats), Is.True);
                Assert.That(context.Storage.HasDirectory(TestPaths.ProfileDirectory(ProfileA)), Is.True);
                Assert.That(context.Storage.HasFile(TestPaths.ProfileSlot(ProfileA, TestSlotKeys.Player)), Is.True, "Local data stays until every key is gone.");
                Assert.That(context.Storage.HasFile(TestPaths.ProfileSlot(ProfileA, Stats)), Is.True);

                ProfileSyncState state = LoadSyncState(context, ProfileA);
                Assert.That(state.PeekSlot(TestSlotKeys.Player).PendingDelete, Is.True);
                Assert.That(state.PeekSlot(TestSlotKeys.Player).DeletedWriteId, Is.EqualTo(playerWriteId));
                Assert.That(state.PeekSlot(Stats).PendingDelete, Is.True);
            }
        }

        [Test]
        public void RetryAfterPartialFailure_Completes()
        {
            var player = new ProfileSlot();
            var stats = new ProfileSlot(Stats);
            using (TestServiceContext context = CreateContext(player, stats))
            {
                PrepareTwoUploadedSlots(context, player, stats);
                context.Provider.EnqueueError(CloudOperation.Delete, Stats, CloudErrorKind.Transient);
                Assert.That(DeleteAccountDataSync(context, AccountA).IsComplete, Is.False, "Premise: the first run is partial.");
                context.Provider.ClearCalls();

                AccountDataDeleteResult retry = DeleteAccountDataSync(context, AccountA);

                Assert.That(retry.Status, Is.EqualTo(SaveStatus.Success), retry.ToString());
                Assert.That(retry.IsComplete, Is.True);
                Assert.That(retry.LocalDeleted, Is.True);
                AssertCloud(retry, (TestSlotKeys.Player, CloudDeleteStatus.AlreadyAbsent), (Stats, CloudDeleteStatus.Deleted));
                Assert.That(context.Provider.Store.GetKeys(AccountA), Is.Empty);
                AssertProfileDirectoryGone(context, ProfileA);
            }
        }

        // The deletes are unconditional and the provider resolves them against the live session, so every key is re-checked
        [Test]
        public void AccountChangesMidDeleteLoop_RemainingKeysUntouched_RetryAfterSignInCompletes()
        {
            var player = new ProfileSlot();
            var stats = new ProfileSlot(Stats);
            using (TestServiceContext context = CreateContext(player, stats))
            {
                PrepareTwoUploadedSlots(context, player, stats);
                string otherVersion = context.Provider.SetRawText(AccountB, Stats, "{\"other\":true}");
                context.Provider.AfterDelete = SwitchToAccountBOnce;

                AccountDataDeleteResult result = DeleteAccountDataSync(context, AccountA);

                Assert.That(result.Status, Is.EqualTo(SaveStatus.Failed), result.ToString());
                Assert.That(result.Error.Code, Is.EqualTo(SaveErrorCode.AccountMismatch), result.Error.ToString());
                Assert.That(result.IsComplete, Is.False);
                Assert.That(result.LocalDeleted, Is.False);
                AssertCloud(result, (TestSlotKeys.Player, CloudDeleteStatus.Deleted), (Stats, CloudDeleteStatus.Failed));
                Assert.That(result.Cloud[1].Error, Is.Not.Null);

                Assert.That(context.Provider.DeleteCallCount, Is.EqualTo(1), "The keys after the session change are never sent.");
                Assert.That(context.Provider.Store.GetVersion(AccountB, Stats), Is.EqualTo(otherVersion), "The new account's data is untouched.");
                Assert.That(context.Provider.Store.Contains(AccountA, Stats), Is.True);

                // Partial-failure semantics: local data and the tombstones stay for the retry
                Assert.That(context.Storage.HasDirectory(TestPaths.ProfileDirectory(ProfileA)), Is.True);
                Assert.That(context.Storage.HasFile(TestPaths.ProfileSlot(ProfileA, TestSlotKeys.Player)), Is.True);
                ProfileSyncState state = LoadSyncState(context, ProfileA);
                Assert.That(state.PeekSlot(TestSlotKeys.Player).PendingDelete, Is.True);
                Assert.That(state.PeekSlot(Stats).PendingDelete, Is.True);

                context.Provider.AfterDelete = null;
                context.Provider.SignedInAccountId = AccountA;
                context.Provider.ClearCalls();

                AccountDataDeleteResult retry = DeleteAccountDataSync(context, AccountA);

                Assert.That(retry.Status, Is.EqualTo(SaveStatus.Success), retry.ToString());
                Assert.That(retry.IsComplete, Is.True);
                AssertCloud(retry, (TestSlotKeys.Player, CloudDeleteStatus.AlreadyAbsent), (Stats, CloudDeleteStatus.Deleted));
                Assert.That(context.Provider.Store.GetKeys(AccountA), Is.Empty);
                Assert.That(context.Provider.Store.GetVersion(AccountB, Stats), Is.EqualTo(otherVersion));
                AssertProfileDirectoryGone(context, ProfileA);
            }
        }

        // D4: the gateway retries after a backoff, so a sign-out during that backoff must stop the retry
        [Test]
        public void AccountChangesDuringGatewayRetryBackoff_RetryNeverReachesTheOtherAccount()
        {
            var player = new ProfileSlot();
            var stats = new ProfileSlot(Stats);
            using (TestServiceContext context = CreateRetryingContext(player, stats))
            {
                PrepareTwoUploadedSlots(context, player, stats);
                string otherVersion = context.Provider.SetRawText(AccountB, TestSlotKeys.Player, "{\"other\":true}");

                // The first delete fails transiently and the account changes before the gateway's retry
                context.Provider.EnqueueError(CloudOperation.Delete, TestSlotKeys.Player, CloudErrorKind.Transient);
                context.Provider.AfterDelete = SwitchToAccountBOnce;

                UniTask<AccountDataDeleteResult> pending = context.Service.DeleteAccountDataAsync(AccountA, CancellationToken.None);
                Assert.That(pending.Status, Is.EqualTo(UniTaskStatus.Pending), "Premise: the delete waits for the retry backoff.");

                context.Clock.Advance(RetryDelay);

                AccountDataDeleteResult result = AsyncTestUtility.RunSync(pending, nameof(SaveService.DeleteAccountDataAsync));
                Assert.That(result.Status, Is.EqualTo(SaveStatus.Failed), result.ToString());
                Assert.That(result.Error.Code, Is.EqualTo(SaveErrorCode.AccountMismatch), result.Error.ToString());
                Assert.That(result.IsComplete, Is.False);

                Assert.That(context.Provider.CountCalls(CloudOperation.Delete, TestSlotKeys.Player), Is.EqualTo(1), "The retry is refused before it reaches the provider.");
                Assert.That(context.Provider.Store.GetVersion(AccountB, TestSlotKeys.Player), Is.EqualTo(otherVersion), "The new account's data is untouched.");
                Assert.That(context.Provider.Store.Contains(AccountA, TestSlotKeys.Player), Is.True, "The targeted key is still there for the retry.");
                Assert.That(context.Storage.HasDirectory(TestPaths.ProfileDirectory(ProfileA)), Is.True);
            }
        }

        // D5: an upload that landed without a response is still the cloud value, so the tombstone must target it
        [Test]
        public void UnconfirmedWriteIsTombstoned_NextRestoreDeletesTheCloudValue()
        {
            var player = new ProfileSlot();
            using (TestServiceContext context = CreateContext(player))
            {
                ActivateReconciledAccount(context, AccountA);
                Assert.That(player.Mutate(data => data.Coins = 5), Is.True);
                context.Provider.EnqueueLostResponse(CloudOperation.Write, TestSlotKeys.Player);
                Assert.That(context.FlushSync().IsComplete, Is.False, "Premise: the upload landed but its response was lost.");
                Assert.That(context.Provider.Store.Contains(AccountA, TestSlotKeys.Player), Is.True, "Premise: the value is in the cloud.");

                SlotSyncState afterUpload = LoadSyncState(context, ProfileA).PeekSlot(TestSlotKeys.Player);
                string unconfirmedWriteId = afterUpload.PendingWriteId;
                Assert.That(unconfirmedWriteId, Is.Not.Null, "Premise: the write id is unconfirmed.");
                Assert.That(afterUpload.LastSyncedWriteId, Is.Null, "Premise: nothing was ever confirmed.");

                Assert.That(context.ActivateSync(ProfileId.Guest).IsSuccess, Is.True);
                context.Provider.EnqueueError(CloudOperation.Delete, TestSlotKeys.Player, CloudErrorKind.Transient);

                AccountDataDeleteResult deleted = DeleteAccountDataSync(context, AccountA);

                Assert.That(deleted.IsComplete, Is.False, "Premise: the cloud delete failed, so the tombstone stays.");
                SlotSyncState tombstone = LoadSyncState(context, ProfileA).PeekSlot(TestSlotKeys.Player);
                Assert.That(tombstone.PendingDelete, Is.True);
                Assert.That(tombstone.DeletedWriteId, Is.EqualTo(unconfirmedWriteId), "The tombstone covers the unconfirmed write.");

                // Re-activating the account must replay the delete (row 5), not clear the tombstone (row 8)
                Assert.That(context.ActivateSync(ProfileA).IsSuccess, Is.True);
                RestoreReport report = AsyncTestUtility.RunSync(context.Service.RestoreAsync(CancellationToken.None), nameof(SaveService.RestoreAsync));

                Assert.That(report.Status, Is.EqualTo(SaveStatus.Success), report.ToString());
                Assert.That(context.Provider.Store.Contains(AccountA, TestSlotKeys.Player), Is.False, "The data the player asked to delete is gone.");
                Assert.That(LoadSyncState(context, ProfileA).PeekSlot(TestSlotKeys.Player).PendingDelete, Is.False, "The tombstone is cleared only after the delete landed.");
            }
        }

        [Test]
        public void TombstonePersistFails_NoCloudCalls_LocalKept()
        {
            var player = new ProfileSlot();
            var stats = new ProfileSlot(Stats);
            using (TestServiceContext context = CreateContext(player, stats))
            {
                PrepareTwoUploadedSlots(context, player, stats);
                StorageFault diskFull = context.Storage.FailWithKind(StorageOperation.Write, TestPaths.ProfileState(ProfileA), LocalWriteErrorKind.DiskFull);

                AccountDataDeleteResult result = DeleteAccountDataSync(context, AccountA);

                context.Storage.RemoveFault(diskFull);
                Assert.That(result.Status, Is.EqualTo(SaveStatus.Failed), result.ToString());
                Assert.That(result.Error.Code, Is.EqualTo(SaveErrorCode.DiskFull));
                Assert.That(result.IsComplete, Is.False);
                Assert.That(result.LocalDeleted, Is.False);
                Assert.That(result.Cloud, Is.Empty);
                Assert.That(context.Provider.TotalCallCount, Is.EqualTo(0), "No cloud delete without a durable tombstone.");
                Assert.That(context.Provider.Store.Contains(AccountA, TestSlotKeys.Player), Is.True);
                Assert.That(context.Provider.Store.Contains(AccountA, Stats), Is.True);
                Assert.That(context.Storage.HasFile(TestPaths.ProfileSlot(ProfileA, TestSlotKeys.Player)), Is.True);
                Assert.That(LoadSyncState(context, ProfileA).PeekSlot(TestSlotKeys.Player).PendingDelete, Is.False);
            }
        }

        [Test]
        public void CloudReadOnlyKeys_NotDeleted()
        {
            var player = new ProfileSlot();
            var rewards = new CloudReadOnlySlot();
            using (TestServiceContext context = CreateContext(player, rewards))
            {
                PrepareInactiveSyncedAccount(context, player);
                string rewardsVersion = context.Provider.SetRawText(AccountA, TestSlotKeys.ServerRewards, "{\"server\":true}", CloudAccess.ServerOwned);
                context.Provider.ClearCalls();

                AccountDataDeleteResult result = DeleteAccountDataSync(context, AccountA);

                Assert.That(result.Status, Is.EqualTo(SaveStatus.Success), result.ToString());
                Assert.That(result.IsComplete, Is.True);
                AssertCloud(result, (TestSlotKeys.Player, CloudDeleteStatus.Deleted));
                Assert.That(context.Provider.CountCalls(CloudOperation.Delete, TestSlotKeys.ServerRewards), Is.EqualTo(0));
                Assert.That(context.Provider.Store.GetVersion(AccountA, TestSlotKeys.ServerRewards), Is.EqualTo(rewardsVersion));
                AssertProfileDirectoryGone(context, ProfileA);
            }
        }

        // Returns player's LastSyncedWriteId; account A inactive afterwards, calls cleared
        private static string PrepareTwoUploadedSlots(TestServiceContext context, ProfileSlot player, ProfileSlot stats)
        {
            ActivateReconciledAccount(context, AccountA);
            Assert.That(player.Mutate(data => data.Coins = 5), Is.True);
            Assert.That(stats.Mutate(data => data.Level = 3), Is.True);
            Assert.That(context.FlushSync().IsComplete, Is.True);
            string writeId = LoadSyncState(context, ProfileA).PeekSlot(TestSlotKeys.Player).LastSyncedWriteId;
            Assert.That(writeId, Is.Not.Null);
            Assert.That(context.ActivateSync(ProfileId.Guest).IsSuccess, Is.True);
            context.Provider.ClearCalls();
            return writeId;
        }

        // Signs the provider in as another account after the first delete of the loop
        private static void SwitchToAccountBOnce(FakeCloudSaveProvider provider, string key)
        {
            provider.AfterDelete = null;
            provider.SignedInAccountId = AccountB;
        }

        private static void PrepareInactiveSyncedAccount(TestServiceContext context, ProfileSlot player)
        {
            ActivateReconciledAccount(context, AccountA);
            UploadCoins(context, player, 5);
            Assert.That(context.ActivateSync(ProfileId.Guest).IsSuccess, Is.True);
            context.Provider.ClearCalls();
        }

        private static void AssertRefused(AccountDataDeleteResult result, SaveErrorCode code)
        {
            Assert.That(result.Status, Is.EqualTo(SaveStatus.Failed), result.ToString());
            Assert.That(result.Error, Is.Not.Null);
            Assert.That(result.Error.Code, Is.EqualTo(code), result.Error.ToString());
            Assert.That(result.IsComplete, Is.False);
            Assert.That(result.LocalDeleted, Is.False);
            Assert.That(result.Cloud, Is.Empty);
        }

        private static void AssertCloud(AccountDataDeleteResult result, params (string Key, CloudDeleteStatus Status)[] expected)
        {
            string actual = string.Join(", ", result.Cloud);
            Assert.That(result.Cloud.Count, Is.EqualTo(expected.Length), actual);
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.That(result.Cloud[i].SlotKey, Is.EqualTo(expected[i].Key), actual);
                Assert.That(result.Cloud[i].Status, Is.EqualTo(expected[i].Status), actual);
            }
        }

        private static void AssertProfileDirectoryGone(TestServiceContext context, ProfileId profile)
        {
            string directory = TestPaths.ProfileDirectory(profile);
            Assert.That(context.Storage.HasDirectory(directory), Is.False, directory);
            Assert.That(context.Storage.AllFilePaths.Where(path => path.StartsWith(directory + "/", StringComparison.Ordinal)), Is.Empty);
        }

        private static AccountDataDeleteResult DeleteAccountDataSync(TestServiceContext context, string accountId)
        {
            return AsyncTestUtility.RunSync(context.Service.DeleteAccountDataAsync(accountId, CancellationToken.None), nameof(SaveService.DeleteAccountDataAsync));
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

        // One gateway retry on a deterministic backoff; only the retry delay may fire while the clock moves
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
                    options.CloudDebounce = TimeSpan.FromMinutes(5);
                    options.CloudMaxWait = TimeSpan.FromMinutes(5);
                },
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
