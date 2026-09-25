using System;
using NUnit.Framework;

namespace Ecanakli.SaveSystem.Tests
{
    /// <summary>HadContent marker and the local inputs of the empty-over-content upload guard.</summary>
    [TestFixture]
    public sealed class EmptyGuardTests
    {
        [Test]
        public void NewInstall_InitializeAndFlush_DoesNotSetHadContent()
        {
            using (TestServiceContext context = TestServiceFactory.Create(new ProfileSlot()))
            {
                Assert.That(context.InitializeSync().IsSuccess, Is.True);
                Assert.That(context.Service.FlushLocalNow().IsComplete, Is.True);

                SyncStateLoadResult state = LoadGuestSyncState(context);
                Assert.That(state.State.PeekSlot(TestSlotKeys.Player).HadContent, Is.False);
                Assert.That(state.State.UnknownSlotsHadContent, Is.False);
            }
        }

        [Test]
        public void MutateToDefaultValues_WritesFile_ButDoesNotSetHadContent()
        {
            var slot = new ProfileSlot();
            using (TestServiceContext context = TestServiceFactory.Create(slot))
            {
                Assert.That(context.InitializeSync().IsSuccess, Is.True);

                Assert.That(slot.Mutate(data => data.Coins = 0), Is.True);
                Assert.That(context.Service.FlushLocalNow().IsComplete, Is.True);

                Assert.That(context.Storage.GetWriteCount(TestPaths.ProfileSlot(ProfileId.Guest, TestSlotKeys.Player)), Is.GreaterThan(0));
                Assert.That(LoadGuestSyncState(context).State.PeekSlot(TestSlotKeys.Player).HadContent, Is.False);
            }
        }

        [Test]
        public void FirstRealContent_SetsHadContent_AndPersistsAcrossRestart()
        {
            var slot = new ProfileSlot();
            TestServiceContext context = TestServiceFactory.Create(slot);
            TestServiceContext restarted = null;
            try
            {
                Assert.That(context.InitializeSync().IsSuccess, Is.True);
                Assert.That(slot.Mutate(data => data.Coins = 5), Is.True);
                Assert.That(context.Service.FlushLocalNow().IsComplete, Is.True);

                Assert.That(LoadGuestSyncState(context).State.PeekSlot(TestSlotKeys.Player).HadContent, Is.True);

                restarted = TestServiceFactory.Restart(context, new ProfileSlot());
                Assert.That(restarted.InitializeSync().IsSuccess, Is.True);
                Assert.That(LoadGuestSyncState(restarted).State.PeekSlot(TestSlotKeys.Player).HadContent, Is.True);
            }
            finally
            {
                context.Dispose();
                restarted?.Dispose();
            }
        }

        [Test]
        public void ContentThenResetToDefault_KeepsHadContent_AndSnapshotReportsEmpty()
        {
            var slot = new ProfileSlot();
            using (TestServiceContext context = TestServiceFactory.Create(slot))
            {
                Assert.That(context.InitializeSync().IsSuccess, Is.True);
                Assert.That(slot.Mutate(data => data.Coins = 5), Is.True);
                Assert.That(context.Service.FlushLocalNow().IsComplete, Is.True);

                Assert.That(slot.Mutate(data => data.Coins = 0), Is.True);
                Assert.That(context.Service.FlushLocalNow().IsComplete, Is.True);

                Assert.That(LoadGuestSyncState(context).State.PeekSlot(TestSlotKeys.Player).HadContent, Is.True, "HadContent is never downgraded by a local write.");
                Assert.That(slot.CaptureSnapshot(true).IsEmpty, Is.EqualTo(true), "This is the state the upload guard refuses (empty over content).");
            }
        }

        [Test]
        public void LocalOnlySlot_DoesNotTrackHadContent()
        {
            var slot = new ProfileSlot("notes", SyncMode.LocalOnly);
            using (TestServiceContext context = TestServiceFactory.Create(slot))
            {
                Assert.That(context.InitializeSync().IsSuccess, Is.True);
                Assert.That(slot.Mutate(data => data.Coins = 5), Is.True);
                Assert.That(context.Service.FlushLocalNow().IsComplete, Is.True);

                Assert.That(LoadGuestSyncState(context).State.PeekSlot("notes").HadContent, Is.False);
            }
        }

        [Test]
        public void CustomIsEmpty_DrivesHadContent()
        {
            var slot = new ProfileSlot { IsEmptyFunc = data => data.Coins == 0 };
            using (TestServiceContext context = TestServiceFactory.Create(slot))
            {
                Assert.That(context.InitializeSync().IsSuccess, Is.True);
                Assert.That(slot.Mutate(data => data.Items.Add("cosmetic")), Is.True);
                Assert.That(context.Service.FlushLocalNow().IsComplete, Is.True);

                Assert.That(LoadGuestSyncState(context).State.PeekSlot(TestSlotKeys.Player).HadContent, Is.False);

                Assert.That(slot.Mutate(data => data.Coins = 1), Is.True);
                Assert.That(context.Service.FlushLocalNow().IsComplete, Is.True);

                Assert.That(LoadGuestSyncState(context).State.PeekSlot(TestSlotKeys.Player).HadContent, Is.True);
            }
        }

        [Test]
        public void CaptureSnapshot_DefaultIsEmpty_ContentIsNot()
        {
            var slot = new ProfileSlot();
            using (TestServiceContext context = TestServiceFactory.Create(slot))
            {
                Assert.That(context.InitializeSync().IsSuccess, Is.True);

                Assert.That(slot.CaptureSnapshot(true).IsEmpty, Is.EqualTo(true));
                Assert.That(slot.CaptureSnapshot(false).IsEmpty, Is.Null);

                Assert.That(slot.Mutate(data => data.Items.Add("sword")), Is.True);

                Assert.That(slot.CaptureSnapshot(true).IsEmpty, Is.EqualTo(false));
                Assert.That(slot.IsEmptyCount, Is.GreaterThanOrEqualTo(2));
            }
        }

        [Test]
        public void IsEmptyHookThrows_CountsAsNotEmpty_AndLogsError()
        {
            var slot = new ProfileSlot();
            using (TestServiceContext context = TestServiceFactory.Create(slot))
            {
                Assert.That(context.InitializeSync().IsSuccess, Is.True);
                slot.IsEmptyFunc = _ => throw new InvalidOperationException("Scripted IsEmpty failure.");

                Assert.That(slot.EvaluateIsEmpty(slot.PeekData()), Is.False);
                Assert.That(slot.CaptureSnapshot(true).IsEmpty, Is.EqualTo(false));
                Assert.That(context.Logger.Contains(TestLogLevel.Error, "IsEmpty of slot 'player' threw."), Is.True, context.Logger.Describe());
                Assert.That(HookScope.IsActive, Is.False);
            }
        }

        [Test]
        public void IsEmptyHookThrows_DefaultDataFlushed_SetsHadContentOnTheSafeSide()
        {
            var slot = new ProfileSlot();
            using (TestServiceContext context = TestServiceFactory.Create(slot))
            {
                Assert.That(context.InitializeSync().IsSuccess, Is.True);
                slot.IsEmptyFunc = _ => throw new InvalidOperationException("Scripted IsEmpty failure.");

                Assert.That(slot.Mutate(data => data.Coins = 0), Is.True);
                Assert.That(context.Service.FlushLocalNow().IsComplete, Is.True);

                Assert.That(LoadGuestSyncState(context).State.PeekSlot(TestSlotKeys.Player).HadContent, Is.True);
            }
        }

        [Test]
        public void ClearSyncHistory_ClearsHadContent_KeepsTombstone()
        {
            var state = new SlotSyncState
            {
                HadContent = true,
                LastSyncedRevision = 4,
                LastSyncedWriteId = "write-4",
                LastSyncedProviderVersion = "v4",
                PendingWriteId = "write-5",
                NeedsCloudRecovery = true,
                LastUploadedBytes = 10,
            };
            state.SetTombstone("write-4", "v4");

            state.ClearSyncHistory();

            Assert.That(state.HadContent, Is.False);
            Assert.That(state.LastSyncedRevision, Is.EqualTo(0));
            Assert.That(state.LastSyncedWriteId, Is.Null);
            Assert.That(state.LastSyncedProviderVersion, Is.Null);
            Assert.That(state.PendingWriteId, Is.Null);
            Assert.That(state.NeedsCloudRecovery, Is.False);
            Assert.That(state.LastUploadedBytes, Is.EqualTo(0));
            Assert.That(state.PendingDelete, Is.True);
            Assert.That(state.DeletedWriteId, Is.EqualTo("write-4"));
        }

        // Reads profile.json directly without repairing anything
        private static SyncStateLoadResult LoadGuestSyncState(TestServiceContext context)
        {
            var store = new SyncStateStore(context.Storage, new TestSaveLogger(), context.Clock);
            SyncStateLoadResult loaded = store.Load(TestPaths.ProfileDirectory(ProfileId.Guest), SlotReadMode.ReadOnly);
            Assert.That(loaded.Status, Is.EqualTo(SyncStateLoadStatus.Missing).Or.EqualTo(SyncStateLoadStatus.Loaded), loaded.Message);
            return loaded;
        }
    }
}
