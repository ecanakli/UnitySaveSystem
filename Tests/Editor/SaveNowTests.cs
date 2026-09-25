using System;
using System.Collections;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Ecanakli.SaveSystem.Tests
{
    /// <summary>SaveNowAsync: durable completion, cancellation, immediate upload request and refusals.</summary>
    [TestFixture]
    public sealed class SaveNowTests
    {
        private const string AccountA = "account-a";
        private const int MaxFrames = 120;

        private static readonly ProfileId ProfileA = ProfileId.Account(AccountA);

        [Test]
        public void SaveNowAsync_CompletesAfterDurableWrite_DurableRevisionAtLeastCallRevision()
        {
            var slot = new ProfileSlot();
            using (TestServiceContext context = CreateInitializedGuest(slot))
            {
                string path = TestPaths.ProfileSlot(ProfileId.Guest, TestSlotKeys.Player);
                Assert.That(slot.Mutate(data => data.Coins = 3), Is.True);
                Assert.That(slot.Mutate(data => data.Coins = 7), Is.True);
                long revisionAtCall = slot.Revision;

                SaveResult result = SaveNowSync(slot, CancellationToken.None);

                Assert.That(result.Status, Is.EqualTo(SaveStatus.Success), result.ToString());
                Assert.That(result.Error, Is.Null);
                Assert.That(result.DurableRevision, Is.GreaterThanOrEqualTo(revisionAtCall));
                Assert.That(context.Storage.GetWriteCount(path), Is.EqualTo(1), "The file is promoted when the call completes.");

                // A copy of the disk taken at completion loads the saved value in a fresh service
                StorageSnapshot disk = context.Storage.Snapshot();
                var copy = new InMemorySaveStorage();
                copy.RestoreSnapshot(disk);
                var reloaded = new ProfileSlot();
                using (TestServiceContext restarted = TestServiceFactory.Create(new TestServiceSetup { Slots = new SaveSlot[] { reloaded }, Storage = copy }))
                {
                    Assert.That(restarted.InitializeSync().IsSuccess, Is.True);
                    Assert.That(reloaded.PeekData().Coins, Is.EqualTo(7));
                    Assert.That(reloaded.Revision, Is.GreaterThanOrEqualTo(revisionAtCall));
                }

                // The write cleared the dirty flag
                Assert.That(context.Service.FlushLocalNow().IsComplete, Is.True);
                Assert.That(context.Storage.GetWriteCount(path), Is.EqualTo(1));
            }
        }

        [Test]
        public void SaveNowAsync_CanceledBeforeWrite_ThrowsOperationCanceled_NothingWritten()
        {
            var slot = new ProfileSlot();
            using (TestServiceContext context = CreateInitializedGuest(slot))
            using (var canceled = new CancellationTokenSource())
            {
                string path = TestPaths.ProfileSlot(ProfileId.Guest, TestSlotKeys.Player);
                Assert.That(slot.Mutate(data => data.Coins = 3), Is.True);
                canceled.Cancel();

                Assert.That(() => SaveNowSync(slot, canceled.Token), Throws.InstanceOf<OperationCanceledException>());

                Assert.That(context.Storage.CountCalls(StorageOperation.Write, path), Is.EqualTo(0));
                Assert.That(context.Service.FlushLocalNow().IsComplete, Is.True);
                Assert.That(context.Storage.GetWriteCount(path), Is.EqualTo(1), "The slot stayed dirty.");
            }
        }

        [UnityTest]
        public IEnumerator SaveNowAsync_RequestsImmediateUpload_DoesNotAwaitIt()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                var slot = new ProfileSlot();
                using (TestServiceContext context = CreateContext(slot))
                {
                    ActivateReconciledAccount(context, AccountA);
                    context.Provider.HoldWrites();
                    try
                    {
                        Assert.That(slot.Mutate(data => data.Coins = 11), Is.True);

                        SaveResult result = SaveNowSync(slot, CancellationToken.None);

                        Assert.That(result.IsSuccess, Is.True, result.ToString());
                        Assert.That(context.Storage.GetWriteCount(TestPaths.ProfileSlot(ProfileA, TestSlotKeys.Player)), Is.GreaterThanOrEqualTo(1));
                        Assert.That(context.Provider.WriteCallCount, Is.EqualTo(0), "SaveNowAsync returns before any provider call.");

                        // No clock advance: the upload skips the debounce window
                        await AsyncTestUtility.WaitUntilAsync(() => context.Provider.HeldCallCount == 1, MaxFrames, "Immediate upload");
                        Assert.That(context.Provider.Store.Contains(AccountA, TestSlotKeys.Player), Is.False, "The upload is still held.");

                        context.Provider.ReleaseWrites();
                        await AsyncTestUtility.WaitUntilAsync(() => context.Provider.Store.Contains(AccountA, TestSlotKeys.Player), MaxFrames, "Upload lands");
                        await AsyncTestUtility.WaitFramesAsync(2);
                        Assert.That(context.Provider.WriteCallCount, Is.EqualTo(1));
                    }
                    finally
                    {
                        context.Provider.ReleaseWrites();
                    }
                }
            });
        }

        [Test]
        public void SaveNowAsync_SlotNotReady_ReturnsSlotNotReady()
        {
            var slot = new NormalizeThrowsSlot();
            using (TestServiceContext context = CreateContext(slot))
            {
                // Init reports the failed slot; readiness of the service is not the subject
                context.InitializeSync();
                context.Storage.ResetCounters();
                Assert.That(slot.State, Is.Not.EqualTo(SlotState.Ready), "Premise: the slot failed to load.");

                SaveResult result = SaveNowSync(slot, CancellationToken.None);

                Assert.That(result.Status, Is.EqualTo(SaveStatus.Failed), result.ToString());
                Assert.That(result.Error.Code, Is.EqualTo(SaveErrorCode.SlotNotReady));
                Assert.That(context.Storage.CountCalls(StorageOperation.Write, TestPaths.ProfileSlot(ProfileId.Guest, TestSlotKeys.NormalizeThrows)), Is.EqualTo(0));
            }
        }

        [Test]
        public void SaveNowAsync_BeforeInitialize_ReturnsNotInitialized()
        {
            var slot = new ProfileSlot();
            using (TestServiceContext context = CreateContext(slot))
            {
                SaveResult result = SaveNowSync(slot, CancellationToken.None);

                Assert.That(result.Status, Is.EqualTo(SaveStatus.Failed), result.ToString());
                Assert.That(result.Error.Code, Is.EqualTo(SaveErrorCode.NotInitialized));
                Assert.That(context.Storage.WriteAttemptCount, Is.EqualTo(0));
            }
        }

        [Test]
        public void SaveNowAsync_CloudReadOnly_ReturnsSlotReadOnly()
        {
            var slot = new CloudReadOnlySlot();
            using (TestServiceContext context = CreateInitializedGuest(slot))
            {
                Assert.That(slot.State, Is.EqualTo(SlotState.Ready), "Premise: the read-only slot is loaded.");

                SaveResult result = SaveNowSync(slot, CancellationToken.None);

                Assert.That(result.Status, Is.EqualTo(SaveStatus.Failed), result.ToString());
                Assert.That(result.Error.Code, Is.EqualTo(SaveErrorCode.SlotReadOnly));
                Assert.That(context.Storage.CountCalls(StorageOperation.Write, TestPaths.ProfileSlot(ProfileId.Guest, TestSlotKeys.ServerRewards)), Is.EqualTo(0));
                Assert.That(context.Provider.TotalCallCount, Is.EqualTo(0));
            }
        }

        private static SaveResult SaveNowSync<TData>(SaveSlot<TData> slot, CancellationToken ct) where TData : class, new()
        {
            return AsyncTestUtility.RunSync(slot.SaveNowAsync(ct), "SaveNowAsync");
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

        // Guest profile; counters reset after init
        private static TestServiceContext CreateInitializedGuest(params SaveSlot[] slots)
        {
            TestServiceContext context = CreateContext(slots);
            Assert.That(context.InitializeSync().IsSuccess, Is.True);
            context.Storage.ResetCounters();
            return context;
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
    }
}
