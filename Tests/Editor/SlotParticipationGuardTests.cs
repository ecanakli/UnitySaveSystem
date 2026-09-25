using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;

namespace Ecanakli.SaveSystem.Tests
{
    /// <summary>F1 guard: 12 generated Profile slots with provider batches of 5 take part in every flow, none dropped at a chunk edge.</summary>
    [TestFixture]
    public sealed class SlotParticipationGuardTests
    {
        private const string AccountA = "account-a";
        private const int SlotCount = 12;
        private const int ChunkSize = 5;

        private static readonly ProfileId ProfileA = ProfileId.Account(AccountA);

        [Test]
        public void InitializeAsync_LoadsEveryRegisteredSlot()
        {
            ProfileSlot[] slots = TestSlots.GenerateProfileSlots(SlotCount);
            TestServiceContext context = CreateContext(slots);
            TestServiceContext restarted = null;
            try
            {
                Assert.That(context.InitializeSync().IsSuccess, Is.True);
                MutateAll(slots);
                Assert.That(context.Service.FlushLocalNow().IsComplete, Is.True);

                ProfileSlot[] reloaded = TestSlots.GenerateProfileSlots(SlotCount);
                restarted = TestServiceFactory.Restart(context, reloaded);
                InitializeResult result = restarted.InitializeSync();

                Assert.That(result.IsSuccess, Is.True, result.ToString());
                for (int i = 0; i < SlotCount; i++)
                {
                    Assert.That(reloaded[i].State, Is.EqualTo(SlotState.Ready), reloaded[i].Key);
                    Assert.That(reloaded[i].PeekData().Coins, Is.EqualTo(ValueOf(i)), reloaded[i].Key);
                }
            }
            finally
            {
                context.Dispose();
                restarted?.Dispose();
            }
        }

        [Test]
        public void ActivateAccountAsync_ReloadsEveryProfileSlot()
        {
            ProfileSlot[] slots = TestSlots.GenerateProfileSlots(SlotCount);
            using (TestServiceContext context = CreateContext(slots))
            {
                InitializeAndActivate(context);
                MutateAll(slots);

                Assert.That(context.ActivateSync(ProfileId.Guest).IsSuccess, Is.True);
                for (int i = 0; i < SlotCount; i++)
                {
                    Assert.That(slots[i].State, Is.EqualTo(SlotState.Ready), slots[i].Key);
                    Assert.That(slots[i].PeekData().Coins, Is.EqualTo(0), slots[i].Key + " shows the guest profile.");
                }

                Assert.That(context.ActivateSync(ProfileA).IsSuccess, Is.True);
                for (int i = 0; i < SlotCount; i++)
                {
                    Assert.That(slots[i].State, Is.EqualTo(SlotState.Ready), slots[i].Key);
                    Assert.That(slots[i].PeekData().Coins, Is.EqualTo(ValueOf(i)), slots[i].Key + " is reloaded from the account profile.");
                }
            }
        }

        [Test]
        public void FlushLocalNow_WritesEveryDirtySlot()
        {
            ProfileSlot[] slots = TestSlots.GenerateProfileSlots(SlotCount);
            using (TestServiceContext context = CreateContext(slots))
            {
                Assert.That(context.InitializeSync().IsSuccess, Is.True);
                MutateAll(slots);
                context.Storage.ResetCounters();

                LocalFlushResult result = context.Service.FlushLocalNow();

                Assert.That(result.IsComplete, Is.True, result.ToString());
                foreach (ProfileSlot slot in slots)
                {
                    Assert.That(context.Storage.GetWriteCount(TestPaths.ProfileSlot(ProfileId.Guest, slot.Key)), Is.EqualTo(1), slot.Key);
                }
            }
        }

        [Test]
        public void FlushAsync_UploadsEveryDirtyCloudSyncSlot_AcrossChunks()
        {
            ProfileSlot[] slots = TestSlots.GenerateProfileSlots(SlotCount);
            using (TestServiceContext context = CreateContext(slots))
            {
                ActivateReconciledAccount(context);
                MutateAll(slots);

                FlushResult result = context.FlushSync();

                Assert.That(result.IsComplete, Is.True, result.ToString());
                Assert.That(result.Cloud.Select(entry => entry.SlotKey), Is.EquivalentTo(KeysOf(slots)));
                Assert.That(result.Cloud.All(entry => entry.Status == CloudFlushStatus.Uploaded), Is.True, string.Join(", ", result.Cloud));

                AssertBatchesWithinLimit(context.Provider, CloudOperation.Write);
                Assert.That(context.Provider.WriteCallCount, Is.GreaterThanOrEqualTo((SlotCount + ChunkSize - 1) / ChunkSize));
                Assert.That(context.Provider.GetAllWriteRequests().Select(request => request.Key), Is.EquivalentTo(KeysOf(slots)), "Every key is sent exactly once.");
                foreach (ProfileSlot slot in slots)
                {
                    Assert.That(context.Provider.Store.Contains(AccountA, slot.Key), Is.True, slot.Key);
                }
            }
        }

        [Test]
        public void RestoreAsync_ReadsEveryNonLocalOnlyProfileSlot_AndReportsEach()
        {
            ProfileSlot[] uploaded = TestSlots.GenerateProfileSlots(SlotCount);
            using (TestServiceContext source = CreateContext(uploaded))
            {
                ActivateReconciledAccount(source);
                MutateAll(uploaded);
                Assert.That(source.FlushSync().IsComplete, Is.True);

                // Second device over the same cloud store
                ProfileSlot[] slots = TestSlots.GenerateProfileSlots(SlotCount);
                var notes = new ProfileSlot("notes", SyncMode.LocalOnly);
                var provider = new FakeCloudSaveProvider(source.Provider.Store, CreateCapabilities(), AccountA);
                using (TestServiceContext context = CreateContext(slots.Cast<SaveSlot>().Append(notes), provider))
                {
                    InitializeAndActivate(context);
                    context.Provider.ClearCalls();

                    RestoreReport report = AsyncTestUtility.RunSync(context.Service.RestoreAsync(CancellationToken.None), nameof(SaveService.RestoreAsync));

                    Assert.That(report.Status, Is.EqualTo(SaveStatus.Success), report.ToString());
                    AssertBatchesWithinLimit(context.Provider, CloudOperation.Read);
                    List<string> readKeys = context.Provider.Calls.Where(call => call.Operation == CloudOperation.Read).SelectMany(call => call.Keys).ToList();
                    Assert.That(readKeys, Is.EquivalentTo(KeysOf(slots)), "Every CloudSync key is read once; LocalOnly is never read.");

                    for (int i = 0; i < SlotCount; i++)
                    {
                        SlotRestoreResult slotResult = report.GetResult(slots[i]);
                        Assert.That(slotResult, Is.Not.Null, slots[i].Key + " is missing from the report.");
                        Assert.That(slotResult.Outcome, Is.EqualTo(SlotRestoreOutcome.Restored), slotResult.ToString());
                        Assert.That(slots[i].PeekData().Coins, Is.EqualTo(ValueOf(i)), slots[i].Key);
                    }
                }
            }
        }

        [Test]
        public void ProbeLocalPresence_DetectsContentInEachProfileSlotIndividually()
        {
            ProfileId probed = ProfileId.Guest;
            for (int i = 0; i < SlotCount; i++)
            {
                ProfileSlot[] slots = TestSlots.GenerateProfileSlots(SlotCount);
                using (TestServiceContext context = CreateContext(slots))
                {
                    Assert.That(context.InitializeSync().IsSuccess, Is.True);
                    int value = ValueOf(i);
                    Assert.That(slots[i].Mutate(data => data.Coins = value), Is.True);

                    // Local profiles never claim the guest directory, so the guest keeps exactly one slot file
                    Assert.That(context.ActivateSync(ProfileId.Local("elsewhere")).IsSuccess, Is.True);
                    for (int j = 0; j < SlotCount; j++)
                    {
                        Assert.That(context.Storage.HasFile(TestPaths.ProfileSlot(probed, slots[j].Key)), Is.EqualTo(i == j), "Premise for " + slots[i].Key + ": " + slots[j].Key);
                    }

                    Assert.That(context.Service.ProbeLocalPresence(probed), Is.EqualTo(LocalPresence.Present), "Content only in " + slots[i].Key);
                }
            }
        }

        [Test]
        public void DeleteSlotAsync_RemovesAllFilesForEachSlot()
        {
            ProfileSlot[] slots = TestSlots.GenerateProfileSlots(SlotCount);
            using (TestServiceContext context = CreateContext(slots))
            {
                ActivateReconciledAccount(context);
                WriteTwiceAndUpload(context, slots);

                foreach (ProfileSlot slot in slots)
                {
                    DeleteResult result = AsyncTestUtility.RunSync(
                        context.Service.DeleteSlotAsync(slot, DeleteTarget.LocalAndCloud, CancellationToken.None), nameof(SaveService.DeleteSlotAsync));

                    Assert.That(result.Status, Is.EqualTo(SaveStatus.Success), slot.Key + ": " + result);
                    Assert.That(result.Cloud, Is.EqualTo(CloudDeleteStatus.Deleted), slot.Key);
                    AssertNoSlotFiles(context, ProfileA, slot.Key);
                    Assert.That(context.Provider.Store.Contains(AccountA, slot.Key), Is.False, slot.Key);
                }

                Assert.That(context.Provider.Store.GetKeys(AccountA), Is.Empty);
            }
        }

        [Test]
        public void DeleteProfileAsync_RemovesFilesOfEverySlot()
        {
            ProfileSlot[] slots = TestSlots.GenerateProfileSlots(SlotCount);
            using (TestServiceContext context = CreateContext(slots))
            {
                ActivateReconciledAccount(context);
                WriteTwiceAndUpload(context, slots);
                Assert.That(context.ActivateSync(ProfileId.Guest).IsSuccess, Is.True);

                DeleteResult result = AsyncTestUtility.RunSync(
                    context.Service.DeleteProfileAsync(ProfileA, ProfileDeleteMode.RequireSynced, CancellationToken.None), nameof(SaveService.DeleteProfileAsync));

                Assert.That(result.Status, Is.EqualTo(SaveStatus.Success), result.ToString());
                foreach (ProfileSlot slot in slots)
                {
                    AssertNoSlotFiles(context, ProfileA, slot.Key);
                }

                string directory = TestPaths.ProfileDirectory(ProfileA);
                Assert.That(context.Storage.HasDirectory(directory), Is.False);
                Assert.That(context.Storage.AllFilePaths.Where(path => path.StartsWith(directory + "/", StringComparison.Ordinal)), Is.Empty);
            }
        }

        [Test]
        public void DeleteProfileAsync_RequireSynced_ChecksEverySlot_LastSlotDirtyRefused()
        {
            ProfileSlot[] slots = TestSlots.GenerateProfileSlots(SlotCount);
            using (TestServiceContext context = CreateContext(slots))
            {
                ActivateReconciledAccount(context);
                MutateAll(slots);
                Assert.That(context.FlushSync().IsComplete, Is.True);
                ProfileSlot last = slots[SlotCount - 1];
                Assert.That(last.Mutate(data => data.Coins = 999), Is.True);
                Assert.That(context.ActivateSync(ProfileId.Guest).IsSuccess, Is.True);

                DeleteResult result = AsyncTestUtility.RunSync(
                    context.Service.DeleteProfileAsync(ProfileA, ProfileDeleteMode.RequireSynced, CancellationToken.None), nameof(SaveService.DeleteProfileAsync));

                Assert.That(result.Error?.Code, Is.EqualTo(SaveErrorCode.UnsyncedChanges), result.ToString());
                Assert.That(result.UnsyncedSlotKeys, Is.EqualTo(new[] { last.Key }));
                Assert.That(context.Storage.HasDirectory(TestPaths.ProfileDirectory(ProfileA)), Is.True);
            }
        }

        [Test]
        public void DeleteAccountData_CoversEverySlot()
        {
            ProfileSlot[] slots = TestSlots.GenerateProfileSlots(SlotCount);
            using (TestServiceContext context = CreateContext(slots))
            {
                ActivateReconciledAccount(context);
                MutateAll(slots);
                Assert.That(context.FlushSync().IsComplete, Is.True);
                Assert.That(context.ActivateSync(ProfileId.Guest).IsSuccess, Is.True);
                context.Provider.ClearCalls();

                AccountDataDeleteResult result = AsyncTestUtility.RunSync(
                    context.Service.DeleteAccountDataAsync(AccountA, CancellationToken.None), nameof(SaveService.DeleteAccountDataAsync));

                Assert.That(result.IsComplete, Is.True, result.ToString());
                Assert.That(result.Cloud.Select(entry => entry.SlotKey), Is.EqualTo(KeysOf(slots)), "One entry per slot in registry order.");
                Assert.That(result.Cloud.All(entry => entry.Status == CloudDeleteStatus.Deleted), Is.True, string.Join(", ", result.Cloud));
                foreach (ProfileSlot slot in slots)
                {
                    Assert.That(context.Provider.CountCalls(CloudOperation.Delete, slot.Key), Is.EqualTo(1), slot.Key);
                    AssertNoSlotFiles(context, ProfileA, slot.Key);
                }

                Assert.That(context.Provider.Store.GetKeys(AccountA), Is.Empty);
                Assert.That(context.Storage.HasDirectory(TestPaths.ProfileDirectory(ProfileA)), Is.False);
            }
        }

        private static int ValueOf(int index)
        {
            return index + 1;
        }

        private static string[] KeysOf(IEnumerable<ProfileSlot> slots)
        {
            return slots.Select(slot => slot.Key).ToArray();
        }

        private static void MutateAll(ProfileSlot[] slots)
        {
            for (int i = 0; i < slots.Length; i++)
            {
                int value = ValueOf(i);
                Assert.That(slots[i].Mutate(data => data.Coins = value), Is.True, slots[i].Key);
            }
        }

        // Two local writes leave a .bak for every slot, then everything is uploaded
        private static void WriteTwiceAndUpload(TestServiceContext context, ProfileSlot[] slots)
        {
            MutateAll(slots);
            Assert.That(context.Service.FlushLocalNow().IsComplete, Is.True);
            foreach (ProfileSlot slot in slots)
            {
                Assert.That(slot.Mutate(data => data.Level = 1), Is.True, slot.Key);
            }

            FlushResult flushed = context.FlushSync();
            Assert.That(flushed.IsComplete, Is.True, flushed.ToString());
            foreach (ProfileSlot slot in slots)
            {
                Assert.That(context.Storage.HasFile(TestPaths.Bak(TestPaths.ProfileSlot(ProfileA, slot.Key))), Is.True, "Premise: backup of " + slot.Key);
            }
        }

        private static void AssertBatchesWithinLimit(FakeCloudSaveProvider provider, CloudOperation operation)
        {
            Assert.That(provider.BatchLimitViolationCount, Is.EqualTo(0));
            foreach (CloudCall call in provider.Calls.Where(call => call.Operation == operation))
            {
                Assert.That(call.Keys.Count, Is.LessThanOrEqualTo(ChunkSize), string.Join(", ", call.Keys));
            }
        }

        private static void AssertNoSlotFiles(TestServiceContext context, ProfileId profile, string key)
        {
            string path = TestPaths.ProfileSlot(profile, key);
            Assert.That(context.Storage.HasFile(path), Is.False, path);
            Assert.That(context.Storage.HasFile(TestPaths.Tmp(path)), Is.False, TestPaths.Tmp(path));
            Assert.That(context.Storage.HasFile(TestPaths.Bak(path)), Is.False, TestPaths.Bak(path));
        }

        private static CloudCapabilities CreateCapabilities()
        {
            return FakeCloudSaveProvider.CreateCapabilities(maxKeysPerWrite: ChunkSize, maxKeysPerRead: ChunkSize);
        }

        // Gateway retries off; provider batches capped at the chunk size
        private static TestServiceContext CreateContext(IEnumerable<SaveSlot> slots, FakeCloudSaveProvider provider = null)
        {
            return TestServiceFactory.Create(new TestServiceSetup
            {
                Slots = slots.ToArray(),
                Provider = provider ?? new FakeCloudSaveProvider(null, CreateCapabilities()),
                ConfigureOptions = options => options.CloudRetryCount = 0,
            });
        }

        private static void InitializeAndActivate(TestServiceContext context)
        {
            context.Provider.SignedInAccountId = AccountA;
            Assert.That(context.InitializeSync().IsSuccess, Is.True);
            Assert.That(context.ActivateSync(ProfileA).IsSuccess, Is.True);
        }

        // Signed in, active and reconciled this epoch; counters reset afterwards
        private static void ActivateReconciledAccount(TestServiceContext context)
        {
            InitializeAndActivate(context);
            RestoreReport report = AsyncTestUtility.RunSync(context.Service.RestoreAsync(CancellationToken.None), nameof(SaveService.RestoreAsync));
            Assert.That(report.Status, Is.EqualTo(SaveStatus.Success), report.ToString());
            AssertBatchesWithinLimit(context.Provider, CloudOperation.Read);
            context.Provider.ClearCalls();
            context.Storage.ResetCounters();
        }
    }
}
