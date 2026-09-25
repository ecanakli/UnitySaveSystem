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
    /// <summary>N7 cross-operation scenarios: OS-backup reinstall, two package versions, guest claim onto cloud data, R9 on two devices.</summary>
    [TestFixture]
    public sealed class ReconcileScenarioTests
    {
        private const string AccountA = "account-a";
        private const string StatsKey = "stats";
        private const string ClonedDeviceId = "device-cloned";
        private const string CompactFormat1Prefix = "{\"fmt\":1,";
        private const int MaxFrames = 120;

        private static readonly ProfileId ProfileA = ProfileId.Account(AccountA);

        // Reinstall with an OS-restored stale profile.json

        // Row 10: a clean stale copy takes the newer cloud value; nothing is uploaded before or during the reconcile
        [Test]
        public void Reinstall_StaleProfileJson_NotDirty_TakesCloud_NoUploadFirst()
        {
            using (TwoDeviceHarness harness = CreateHarness(PlayerSlots))
            {
                TestServiceContext deviceA = harness.DeviceA;
                ActivateReconciled(deviceA);
                MutateAndFlush(deviceA, 5);
                StorageSnapshot backup = deviceA.Storage.Snapshot();
                MutateAndFlush(deviceA, 7);

                TestServiceContext deviceB = harness.DeviceB;
                ActivateReconciled(deviceB);
                MutateAndFlush(deviceB, 9);
                string cloudVersion = harness.CloudStore.GetVersion(AccountA, TestSlotKeys.Player);

                TestServiceContext reinstalled = ReinstallDeviceA(harness, backup);
                ProfileSlot slot = reinstalled.Slot<ProfileSlot>();
                Assert.That(slot.PeekData().Coins, Is.EqualTo(5), "Premise: the stale backup copy is loaded.");
                Assert.That(slot.Revision, Is.EqualTo(LoadSlotSyncState(reinstalled, TestSlotKeys.Player).LastSyncedRevision), "Premise: not dirty at backup.");

                FlushResult beforeReconcile = reinstalled.FlushSync();
                Assert.That(CloudEntry(beforeReconcile, TestSlotKeys.Player).Reason, Is.EqualTo(CloudFlushReason.NotReconciled), beforeReconcile.ToString());
                Assert.That(reinstalled.Provider.WriteCallCount, Is.EqualTo(0), "No upload before the first reconcile.");

                RestoreReport report = RestoreSync(reinstalled);

                Assert.That(report.Status, Is.EqualTo(SaveStatus.Success), report.ToString());
                Assert.That(report.Completeness, Is.EqualTo(RestoreCompleteness.Full), report.ToString());
                Assert.That(report.GetResult(slot).Outcome, Is.EqualTo(SlotRestoreOutcome.Restored), report.ToString());
                Assert.That(slot.PeekData().Coins, Is.EqualTo(9));
                Assert.That(slot.ResolveConflictCount, Is.EqualTo(0));
                Assert.That(reinstalled.Storage.HasFile(PlayerConflictPath), Is.False);
                Assert.That(reinstalled.Provider.WriteCallCount, Is.EqualTo(0));
                Assert.That(harness.CloudStore.GetVersion(AccountA, TestSlotKeys.Player), Is.EqualTo(cloudVersion));

                FlushResult afterReconcile = reinstalled.FlushSync();
                Assert.That(CloudEntry(afterReconcile, TestSlotKeys.Player).Status, Is.EqualTo(CloudFlushStatus.AlreadyInSync), afterReconcile.ToString());
                Assert.That(reinstalled.Provider.WriteCallCount, Is.EqualTo(0));
            }
        }

        // Row 11: unsynced progress in the backup meets a newer cloud write; both sides land in the conflict file
        [Test]
        public void Reinstall_StaleProfileJson_DirtyAtBackup_ResolvesConflict_BacksUpBothSides()
        {
            using (TwoDeviceHarness harness = CreateHarness(PlayerSlots))
            {
                TestServiceContext deviceA = harness.DeviceA;
                ActivateReconciled(deviceA);
                MutateAndFlush(deviceA, 5);
                Assert.That(deviceA.Slot<ProfileSlot>().Mutate(data => data.Coins = 6), Is.True);
                Assert.That(deviceA.Service.FlushLocalNow().IsComplete, Is.True);
                Assert.That(deviceA.Slot<ProfileSlot>().Revision, Is.GreaterThan(LoadSlotSyncState(deviceA, TestSlotKeys.Player).LastSyncedRevision), "Premise: dirty at backup.");
                StorageSnapshot backup = deviceA.Storage.Snapshot();

                TestServiceContext deviceB = harness.DeviceB;
                ActivateReconciled(deviceB);
                MutateAndFlush(deviceB, 9);
                string cloudVersion = harness.CloudStore.GetVersion(AccountA, TestSlotKeys.Player);

                TestServiceContext reinstalled = ReinstallDeviceA(harness, backup);
                ProfileSlot slot = reinstalled.Slot<ProfileSlot>();
                Assert.That(slot.PeekData().Coins, Is.EqualTo(6), "Premise: the unsynced backup copy is loaded.");

                RestoreReport report = RestoreSync(reinstalled);

                Assert.That(report.Status, Is.EqualTo(SaveStatus.Success), report.ToString());
                Assert.That(slot.ResolveConflictCount, Is.EqualTo(1), report.ToString());

                // Default policy: neither side is empty, so cloud wins
                Assert.That(report.GetResult(slot).Outcome, Is.EqualTo(SlotRestoreOutcome.Restored), report.ToString());
                Assert.That(slot.PeekData().Coins, Is.EqualTo(9));

                JObject conflict = ReadConflictFile(reinstalled, TestSlotKeys.Player);
                Assert.That(conflict.Value<string>("resolution"), Is.EqualTo(ConflictResolutionKind.TakeCloud.ToString()));
                Assert.That(ReadInt(conflict["local"], "Coins"), Is.EqualTo(6), conflict.ToString());
                Assert.That(ReadInt(conflict["cloud"], "Coins"), Is.EqualTo(9), conflict.ToString());

                Assert.That(reinstalled.Provider.WriteCallCount, Is.EqualTo(0));
                Assert.That(harness.CloudStore.GetVersion(AccountA, TestSlotKeys.Player), Is.EqualTo(cloudVersion));
            }
        }

        // Row 8 must not match: the backed-up PendingWriteId landed but was superseded, so the dirty copy is a conflict
        [Test]
        public void Reinstall_StalePendingWriteIdSuperseded_IsConflictNotSilentOverwrite()
        {
            using (TwoDeviceHarness harness = CreateHarness(PlayerSlots))
            {
                TestServiceContext deviceA = harness.DeviceA;
                ActivateReconciled(deviceA);
                MutateAndFlush(deviceA, 5);

                // The write lands but its response is lost: PendingWriteId stays journaled
                Assert.That(deviceA.Slot<ProfileSlot>().Mutate(data => data.Coins = 6), Is.True);
                deviceA.Provider.EnqueueLostResponse(CloudOperation.Write, TestSlotKeys.Player);
                FlushResult lost = deviceA.FlushSync();
                Assert.That(lost.IsComplete, Is.False, lost.ToString());

                SlotSyncState atBackup = LoadSlotSyncState(deviceA, TestSlotKeys.Player);
                string pendingWriteId = atBackup.PendingWriteId;
                Assert.That(pendingWriteId, Is.Not.Null, "Premise: the pending write id is journaled.");
                Assert.That(CloudWriteId(harness, TestSlotKeys.Player), Is.EqualTo(pendingWriteId), "Premise: the pending write landed.");
                StorageSnapshot backup = deviceA.Storage.Snapshot();

                TestServiceContext deviceB = harness.DeviceB;
                ActivateReconciled(deviceB);
                MutateAndFlush(deviceB, 9);
                string supersedingWriteId = CloudWriteId(harness, TestSlotKeys.Player);
                string cloudVersion = harness.CloudStore.GetVersion(AccountA, TestSlotKeys.Player);
                Assert.That(supersedingWriteId, Is.Not.EqualTo(pendingWriteId), "Premise: another device superseded the pending write.");

                TestServiceContext reinstalled = ReinstallDeviceA(harness, backup);
                ProfileSlot slot = reinstalled.Slot<ProfileSlot>();

                RestoreReport report = RestoreSync(reinstalled);

                Assert.That(report.Status, Is.EqualTo(SaveStatus.Success), report.ToString());
                SlotRestoreOutcome outcome = report.GetResult(slot).Outcome;
                Assert.That(outcome, Is.Not.EqualTo(SlotRestoreOutcome.UpToDate), report.ToString());
                Assert.That(slot.ResolveConflictCount, Is.EqualTo(1), "The superseded pending write id goes to ResolveConflict. " + report);
                Assert.That(reinstalled.Storage.HasFile(PlayerConflictPath), Is.True);

                JObject conflict = ReadConflictFile(reinstalled, TestSlotKeys.Player);
                Assert.That(ReadInt(conflict["local"], "Coins"), Is.EqualTo(6), conflict.ToString());
                Assert.That(ReadInt(conflict["cloud"], "Coins"), Is.EqualTo(9), conflict.ToString());

                Assert.That(reinstalled.Provider.WriteCallCount, Is.EqualTo(0), "Nothing is uploaded during the reconcile.");
                Assert.That(harness.CloudStore.GetVersion(AccountA, TestSlotKeys.Player), Is.EqualTo(cloudVersion));

                // Default policy takes cloud; the stale pending id is dropped
                Assert.That(outcome, Is.EqualTo(SlotRestoreOutcome.Restored), report.ToString());
                SlotSyncState after = LoadSlotSyncState(reinstalled, TestSlotKeys.Player);
                Assert.That(after.PendingWriteId, Is.Null);
                Assert.That(after.LastSyncedWriteId, Is.EqualTo(supersedingWriteId));
            }
        }

        // deviceId is diagnostics only: a clone with the same id still decides by write id
        [Test]
        public void Reinstall_ClonedDeviceId_DoesNotAffectDecision()
        {
            using (TwoDeviceHarness harness = CreateHarness(PlayerSlots, options => options.DeviceIdProvider = () => ClonedDeviceId))
            {
                TestServiceContext original = harness.DeviceA;
                ActivateReconciled(original);
                MutateAndFlush(original, 5);
                StorageSnapshot backup = original.Storage.Snapshot();
                MutateAndFlush(original, 7);
                Assert.That(CloudDeviceId(harness, TestSlotKeys.Player), Is.EqualTo(ClonedDeviceId), "Premise: the cloud value names the cloned id.");

                // Device B is a new phone restored from device A's backup
                TestServiceContext clone = harness.DeviceB;
                clone.Storage.RestoreSnapshot(backup);
                InitializeAndActivate(clone);
                ProfileSlot cloneSlot = clone.Slot<ProfileSlot>();
                Assert.That(cloneSlot.PeekData().Coins, Is.EqualTo(5), "Premise: the clone loads the backup.");

                RestoreReport cloneReport = RestoreSync(clone);

                Assert.That(cloneReport.GetResult(cloneSlot).Outcome, Is.EqualTo(SlotRestoreOutcome.Restored), cloneReport.ToString());
                Assert.That(cloneSlot.PeekData().Coins, Is.EqualTo(7));
                Assert.That(cloneSlot.ResolveConflictCount, Is.EqualTo(0));
                Assert.That(clone.Storage.HasFile(PlayerConflictPath), Is.False);
                Assert.That(clone.Provider.WriteCallCount, Is.EqualTo(0));

                // The clone moves the cloud on; the original, with the same device id and unsynced progress, sees a foreign write
                MutateAndFlush(clone, 8);
                Assert.That(CloudDeviceId(harness, TestSlotKeys.Player), Is.EqualTo(ClonedDeviceId));
                ProfileSlot originalSlot = original.Slot<ProfileSlot>();
                Assert.That(originalSlot.Mutate(data => data.Coins = 9), Is.True);
                Assert.That(original.Service.FlushLocalNow().IsComplete, Is.True);
                original.Provider.ClearCalls();

                RestoreReport originalReport = RestoreSync(original);

                Assert.That(originalReport.GetResult(originalSlot).Outcome, Is.Not.EqualTo(SlotRestoreOutcome.UpToDate), originalReport.ToString());
                Assert.That(originalReport.GetResult(originalSlot).Outcome, Is.Not.EqualTo(SlotRestoreOutcome.LocalKept), originalReport.ToString());
                Assert.That(originalSlot.ResolveConflictCount, Is.EqualTo(1), originalReport.ToString());
                JObject conflict = ReadConflictFile(original, TestSlotKeys.Player);
                Assert.That(ReadInt(conflict["local"], "Coins"), Is.EqualTo(9), conflict.ToString());
                Assert.That(ReadInt(conflict["cloud"], "Coins"), Is.EqualTo(8), conflict.ToString());
                Assert.That(original.Provider.WriteCallCount, Is.EqualTo(0));
            }
        }

        // R1: the backed-up tombstone names an older write, so the rewritten cloud value is kept and restored
        [UnityTest]
        public IEnumerator Reinstall_StaleTombstone_CloudRewrittenElsewhere_KeepsCloudData()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                using (TwoDeviceHarness harness = CreateHarness(PlayerSlots))
                {
                    TestServiceContext deviceA = harness.DeviceA;
                    ActivateReconciled(deviceA);
                    MutateAndFlush(deviceA, 5);
                    string deletedWriteId = LoadSlotSyncState(deviceA, TestSlotKeys.Player).LastSyncedWriteId;

                    deviceA.Provider.EnqueueError(CloudOperation.Delete, TestSlotKeys.Player, CloudErrorKind.Permanent);
                    DeleteResult deleted = await deviceA.Service.DeleteSlotAsync(deviceA.Slot<ProfileSlot>(), DeleteTarget.LocalAndCloud, CancellationToken.None);
                    Assert.That(harness.CloudStore.Contains(AccountA, TestSlotKeys.Player), Is.True, "Premise: the cloud delete failed. " + deleted);
                    SlotSyncState tombstone = LoadSlotSyncState(deviceA, TestSlotKeys.Player);
                    Assert.That(tombstone.PendingDelete, Is.True, "Premise: the tombstone is kept.");
                    Assert.That(tombstone.DeletedWriteId, Is.EqualTo(deletedWriteId));
                    StorageSnapshot backup = deviceA.Storage.Snapshot();

                    // Device B still sees the undeleted value and rewrites it
                    TestServiceContext deviceB = harness.DeviceB;
                    ActivateReconciled(deviceB);
                    MutateAndFlush(deviceB, 9);
                    string cloudVersion = harness.CloudStore.GetVersion(AccountA, TestSlotKeys.Player);
                    Assert.That(CloudWriteId(harness, TestSlotKeys.Player), Is.Not.EqualTo(deletedWriteId), "Premise: the cloud holds a newer write.");

                    TestServiceContext reinstalled = ReinstallDeviceA(harness, backup);
                    ProfileSlot slot = reinstalled.Slot<ProfileSlot>();
                    Assert.That(LoadSlotSyncState(reinstalled, TestSlotKeys.Player).PendingDelete, Is.True, "Premise: the backup restored the tombstone.");

                    RestoreReport report = RestoreSync(reinstalled);

                    Assert.That(report.Status, Is.EqualTo(SaveStatus.Success), report.ToString());
                    Assert.That(report.GetResult(slot).Outcome, Is.EqualTo(SlotRestoreOutcome.Restored), report.ToString());
                    Assert.That(report.Completeness, Is.EqualTo(RestoreCompleteness.Full), report.ToString());
                    Assert.That(slot.PeekData().Coins, Is.EqualTo(9));
                    Assert.That(reinstalled.Provider.DeleteCallCount, Is.EqualTo(0), "No delete for a write id the tombstone does not name.");
                    Assert.That(reinstalled.Provider.WriteCallCount, Is.EqualTo(0));
                    Assert.That(harness.CloudStore.GetVersion(AccountA, TestSlotKeys.Player), Is.EqualTo(cloudVersion));
                    Assert.That(LoadSlotSyncState(reinstalled, TestSlotKeys.Player).PendingDelete, Is.False, "The stale tombstone is cleared.");
                }
            });
        }

        // Two package versions on two devices

        // Row 2 (V7): the older build refuses a newer schema, raises UpdateRequired and writes nothing locally or to the cloud
        [Test]
        public void TwoVersions_OlderBuildSeesNewerSchema_SkipsAndWritesNothing()
        {
            using (TwoDeviceHarness harness = CreateTwoVersionHarness())
            {
                TestServiceContext newer = harness.DeviceB;
                ActivateReconciled(newer);
                MutateSchemaAndFlush(newer, "hero", 50);
                byte[] cloudBytes = harness.CloudStore.GetValue(AccountA, TestSlotKeys.Schema);
                string cloudVersion = harness.CloudStore.GetVersion(AccountA, TestSlotKeys.Schema);

                TestServiceContext older = harness.DeviceA;
                InitializeAndActivate(older);
                SchemaV2Slot slot = older.Slot<SchemaV2Slot>();
                older.Provider.ClearCalls();
                older.Storage.ResetCounters();

                var updates = new List<UpdateRequiredInfo>();
                Action<UpdateRequiredInfo> onUpdate = updates.Add;
                older.Service.UpdateRequired += onUpdate;
                try
                {
                    RestoreReport report = RestoreSync(older);

                    Assert.That(report.Status, Is.EqualTo(SaveStatus.Success), report.ToString());
                    Assert.That(report.RequiresAppUpdate, Is.True, report.ToString());
                    Assert.That(report.GetResult(slot).Outcome, Is.EqualTo(SlotRestoreOutcome.SkippedSchemaTooNew), report.ToString());
                    Assert.That(report.Completeness, Is.EqualTo(RestoreCompleteness.Failed), report.ToString());
                    Assert.That(updates.Count, Is.EqualTo(1));
                    Assert.That(updates[0].SlotKey, Is.EqualTo(TestSlotKeys.Schema));
                    Assert.That(updates[0].Source, Is.EqualTo(PayloadSource.Cloud));
                    Assert.That(updates[0].FoundSchema, Is.EqualTo(3));
                    Assert.That(updates[0].SupportedSchema, Is.EqualTo(2));
                    Assert.That(slot.PeekData().DisplayName, Is.Null, "The newer payload is never applied.");

                    FlushResult flushed = older.FlushSync();
                    Assert.That(CloudEntry(flushed, TestSlotKeys.Schema).Reason, Is.EqualTo(CloudFlushReason.NotReconciled), flushed.ToString());

                    RestoreReport again = RestoreSync(older);
                    Assert.That(again.GetResult(slot).Outcome, Is.EqualTo(SlotRestoreOutcome.SkippedSchemaTooNew), again.ToString());
                    Assert.That(again.RequiresAppUpdate, Is.True, again.ToString());
                    Assert.That(updates.Count, Is.EqualTo(1), "UpdateRequired is raised once per slot, source and epoch.");

                    Assert.That(older.Provider.WriteCallCount, Is.EqualTo(0));
                    Assert.That(older.Provider.DeleteCallCount, Is.EqualTo(0));
                    Assert.That(harness.CloudStore.GetVersion(AccountA, TestSlotKeys.Schema), Is.EqualTo(cloudVersion));
                    Assert.That(harness.CloudStore.GetValue(AccountA, TestSlotKeys.Schema), Is.EqualTo(cloudBytes));

                    string directory = TestPaths.ProfileDirectory(ProfileA);
                    Assert.That(older.Storage.GetWriteCount(TestPaths.ProfileSlot(ProfileA, TestSlotKeys.Schema)), Is.EqualTo(0), "No local slot write.");
                    Assert.That(older.Storage.HasFile(SaveLayout.ConflictPath(directory, TestSlotKeys.Schema)), Is.False);
                    Assert.That(older.Storage.AllFilePaths.Any(path => path.Contains(SaveLayout.CloudCorruptMarker)), Is.False, "A newer schema is not corruption.");
                }
                finally
                {
                    older.Service.UpdateRequired -= onUpdate;
                }
            }
        }

        // Row 2 keeps offline progress local and dirty; after the app update row 11 resolves it and the upload is conditional
        [Test]
        public void TwoVersions_OlderBuildMutatesWhileBlocked_KeepsLocalDirty_ResolvesAfterUpdate()
        {
            using (TwoDeviceHarness harness = CreateTwoVersionHarness())
            {
                TestServiceContext newer = harness.DeviceB;
                ActivateReconciled(newer);
                MutateSchemaAndFlush(newer, "hero", 50);
                string cloudVersion = harness.CloudStore.GetVersion(AccountA, TestSlotKeys.Schema);

                TestServiceContext older = harness.DeviceA;
                InitializeAndActivate(older);
                SchemaV2Slot olderSlot = older.Slot<SchemaV2Slot>();
                RestoreReport blocked = RestoreSync(older);
                Assert.That(blocked.GetResult(olderSlot).Outcome, Is.EqualTo(SlotRestoreOutcome.SkippedSchemaTooNew), blocked.ToString());

                Assert.That(olderSlot.Mutate(data =>
                {
                    data.DisplayName = "offline";
                    data.Coins = 7;
                }), Is.True);
                FlushResult flushed = older.FlushSync();

                Assert.That(flushed.Local.IsComplete, Is.True, flushed.ToString());
                Assert.That(CloudEntry(flushed, TestSlotKeys.Schema).Reason, Is.EqualTo(CloudFlushReason.NotReconciled), flushed.ToString());
                Assert.That(older.Storage.HasFile(TestPaths.ProfileSlot(ProfileA, TestSlotKeys.Schema)), Is.True, "Blocked progress is kept on disk.");
                Assert.That(olderSlot.Revision, Is.GreaterThan(LoadSlotSyncState(older, TestSlotKeys.Schema).LastSyncedRevision), "The change stays unsynced.");

                RestoreReport stillBlocked = RestoreSync(older);
                Assert.That(stillBlocked.GetResult(olderSlot).Outcome, Is.EqualTo(SlotRestoreOutcome.SkippedSchemaTooNew), stillBlocked.ToString());
                Assert.That(olderSlot.PeekData().Coins, Is.EqualTo(7), "Local data survives the blocked restore.");
                Assert.That(older.Provider.WriteCallCount, Is.EqualTo(0));
                Assert.That(harness.CloudStore.GetVersion(AccountA, TestSlotKeys.Schema), Is.EqualTo(cloudVersion));

                // App update: the same device now runs the schema 3 build
                TestServiceContext updated = harness.RestartDeviceA(SchemaV3Slots);
                updated.Provider.ClearCalls();
                InitializeAndActivate(updated);
                SchemaV3Slot updatedSlot = updated.Slot<SchemaV3Slot>();
                Assert.That(updatedSlot.UpgradeFromVersions, Is.EqualTo(new[] { 2 }), "Premise: the local schema 2 file was upgraded.");
                Assert.That(updatedSlot.PeekData().Gold, Is.EqualTo(7));
                updatedSlot.ConflictResolver = (in ConflictContext<SchemaV3Data> conflict) => ConflictResolution<SchemaV3Data>.KeepLocal;

                RestoreReport resolved = RestoreSync(updated);

                Assert.That(resolved.Status, Is.EqualTo(SaveStatus.Success), resolved.ToString());
                Assert.That(resolved.RequiresAppUpdate, Is.False, resolved.ToString());
                Assert.That(resolved.GetResult(updatedSlot).Outcome, Is.EqualTo(SlotRestoreOutcome.LocalKept), resolved.ToString());
                Assert.That(updatedSlot.ResolveConflictCount, Is.EqualTo(1));
                Assert.That(updatedSlot.PeekData().Gold, Is.EqualTo(7));

                JObject conflictFile = ReadConflictFile(updated, TestSlotKeys.Schema);
                Assert.That(conflictFile.Value<string>("resolution"), Is.EqualTo(ConflictResolutionKind.KeepLocal.ToString()));
                Assert.That(ReadString(conflictFile["local"], "DisplayName"), Is.EqualTo("offline"), conflictFile.ToString());
                Assert.That(ReadLong(conflictFile["local"], "Gold"), Is.EqualTo(7), conflictFile.ToString());
                Assert.That(ReadString(conflictFile["cloud"], "DisplayName"), Is.EqualTo("hero"), conflictFile.ToString());
                Assert.That(ReadLong(conflictFile["cloud"], "Gold"), Is.EqualTo(50), conflictFile.ToString());
                Assert.That(updated.Provider.WriteCallCount, Is.EqualTo(0), "Nothing is uploaded during the reconcile.");

                FlushResult uploaded = updated.FlushSync();

                Assert.That(uploaded.IsComplete, Is.True, uploaded.ToString());
                IReadOnlyList<CloudWriteRequest> writes = updated.Provider.GetAllWriteRequests();
                Assert.That(writes.Count, Is.EqualTo(1));
                Assert.That(writes[0].ExpectedVersion, Is.EqualTo(cloudVersion), "The upload replaces exactly the resolved cloud value.");
                JObject envelope = ReadCloudEnvelope(harness, TestSlotKeys.Schema);
                Assert.That(envelope.Value<int>(SaveEnvelope.SchemaProperty), Is.EqualTo(3));
                Assert.That(ReadLong(envelope[SaveEnvelope.DataProperty], "Gold"), Is.EqualTo(7), envelope.ToString());
                Assert.That(ReadString(envelope[SaveEnvelope.DataProperty], "DisplayName"), Is.EqualTo("offline"), envelope.ToString());
            }
        }

        // Row 2 for fmt: a newer envelope format is refused and never repaired as corrupt cloud data
        [Test]
        public void TwoVersions_NewerFmt_TreatedAsSchemaTooNew()
        {
            using (TwoDeviceHarness harness = CreateHarness(SchemaV3Slots))
            {
                TestServiceContext newer = harness.DeviceB;
                ActivateReconciled(newer);
                MutateSchemaAndFlush(newer, "hero", 50);

                TestServiceContext older = harness.DeviceA;
                ActivateReconciled(older);
                Assert.That(older.Slot<SchemaV3Slot>().PeekData().Gold, Is.EqualTo(50), "Premise: the older build holds local content.");

                // A newer package build writes the next value with envelope fmt 2
                MutateSchemaAndFlush(newer, "hero", 80);
                string text = harness.CloudStore.GetText(AccountA, TestSlotKeys.Schema);
                Assert.That(text, Does.StartWith(CompactFormat1Prefix), "Premise: compact envelope with fmt first.");
                harness.CloudStore.PutText(AccountA, TestSlotKeys.Schema, "{\"fmt\":2," + text.Substring(CompactFormat1Prefix.Length));
                byte[] newerBytes = harness.CloudStore.GetValue(AccountA, TestSlotKeys.Schema);
                string newerVersion = harness.CloudStore.GetVersion(AccountA, TestSlotKeys.Schema);

                // Next launch of the older build (new epoch)
                TestServiceContext relaunched = harness.RestartDeviceA();
                InitializeAndActivate(relaunched);
                SchemaV3Slot slot = relaunched.Slot<SchemaV3Slot>();
                Assert.That(slot.PeekData().Gold, Is.EqualTo(50), "Premise: local content survives the restart.");
                relaunched.Provider.ClearCalls();

                var updates = new List<UpdateRequiredInfo>();
                var issues = new List<SlotLoadIssue>();
                Action<UpdateRequiredInfo> onUpdate = updates.Add;
                Action<SlotLoadIssue> onIssue = issues.Add;
                relaunched.Service.UpdateRequired += onUpdate;
                relaunched.Service.SlotLoadIssueDetected += onIssue;
                try
                {
                    RestoreReport report = RestoreSync(relaunched);

                    Assert.That(report.Status, Is.EqualTo(SaveStatus.Success), report.ToString());
                    Assert.That(report.RequiresAppUpdate, Is.True, report.ToString());
                    Assert.That(report.GetResult(slot).Outcome, Is.EqualTo(SlotRestoreOutcome.SkippedSchemaTooNew), report.ToString());
                    Assert.That(updates.Count, Is.EqualTo(1));
                    Assert.That(updates[0].Source, Is.EqualTo(PayloadSource.Cloud));
                    Assert.That(updates[0].FoundFormat, Is.EqualTo(2));
                    Assert.That(updates[0].SupportedFormat, Is.EqualTo(SaveEnvelope.CurrentFormat));
                    Assert.That(issues.Any(issue => issue.Kind == SlotLoadIssueKind.CloudPayloadCorrupt), Is.False, "fmt 2 is not a corrupt value.");
                    Assert.That(relaunched.Storage.AllFilePaths.Any(path => path.Contains(SaveLayout.CloudCorruptMarker)), Is.False);
                    Assert.That(slot.PeekData().Gold, Is.EqualTo(50), "Local data is kept.");

                    // No repair upload and no upload of new progress over the newer value
                    Assert.That(slot.Mutate(data => data.Gold = 55), Is.True);
                    FlushResult flushed = relaunched.FlushSync();
                    Assert.That(CloudEntry(flushed, TestSlotKeys.Schema).Reason, Is.EqualTo(CloudFlushReason.NotReconciled), flushed.ToString());
                    Assert.That(relaunched.Provider.WriteCallCount, Is.EqualTo(0));
                    Assert.That(relaunched.Provider.DeleteCallCount, Is.EqualTo(0));
                    Assert.That(harness.CloudStore.GetVersion(AccountA, TestSlotKeys.Schema), Is.EqualTo(newerVersion));
                    Assert.That(harness.CloudStore.GetValue(AccountA, TestSlotKeys.Schema), Is.EqualTo(newerBytes));
                }
                finally
                {
                    relaunched.Service.UpdateRequired -= onUpdate;
                    relaunched.Service.SlotLoadIssueDetected -= onIssue;
                }
            }
        }

        // Decisions use writeId only, so an older rewrite without the newer envelope fields cannot mislead the newer build
        [Test]
        public void TwoVersions_OlderRewriteDropsUnknownEnvelopeFields_NewerDecidesSafely()
        {
            const string unknownField = "futureVectorClock";
            using (TwoDeviceHarness harness = CreateHarness(SchemaV3Slots))
            {
                TestServiceContext newer = harness.DeviceB;
                ActivateReconciled(newer);
                MutateSchemaAndFlush(newer, "hero", 50);
                string newerWriteId = CloudWriteId(harness, TestSlotKeys.Schema);

                // The newer package adds an envelope field; data bytes and checksum stay intact
                string text = harness.CloudStore.GetText(AccountA, TestSlotKeys.Schema);
                Assert.That(text, Does.StartWith(CompactFormat1Prefix), "Premise: compact envelope with fmt first.");
                harness.CloudStore.PutText(
                    AccountA, TestSlotKeys.Schema, CompactFormat1Prefix + "\"" + unknownField + "\":{\"device-b\":3}," + text.Substring(CompactFormat1Prefix.Length));
                string extendedVersion = harness.CloudStore.GetVersion(AccountA, TestSlotKeys.Schema);

                TestServiceContext older = harness.DeviceA;
                InitializeAndActivate(older);
                SchemaV3Slot olderSlot = older.Slot<SchemaV3Slot>();
                RestoreReport olderReport = RestoreSync(older);
                Assert.That(olderReport.GetResult(olderSlot).Outcome, Is.EqualTo(SlotRestoreOutcome.Restored), "The unknown field is ignored, not corruption. " + olderReport);
                Assert.That(olderSlot.PeekData().Gold, Is.EqualTo(50));

                older.Provider.ClearCalls();
                MutateSchemaAndFlush(older, "hero", 60);
                IReadOnlyList<CloudWriteRequest> olderWrites = older.Provider.GetAllWriteRequests();
                Assert.That(olderWrites.Count, Is.EqualTo(1));
                Assert.That(olderWrites[0].ExpectedVersion, Is.EqualTo(extendedVersion));
                Assert.That(harness.CloudStore.GetText(AccountA, TestSlotKeys.Schema), Does.Not.Contain(unknownField), "Premise: the older rewrite dropped the field.");
                string olderWriteId = CloudWriteId(harness, TestSlotKeys.Schema);
                string olderVersion = harness.CloudStore.GetVersion(AccountA, TestSlotKeys.Schema);
                Assert.That(olderWriteId, Is.Not.EqualTo(newerWriteId));

                // The newer build has unsynced progress when it reconciles against the rewrite
                SchemaV3Slot newerSlot = newer.Slot<SchemaV3Slot>();
                Assert.That(newerSlot.Mutate(data => data.Gold = 70), Is.True);
                Assert.That(newer.Service.FlushLocalNow().IsComplete, Is.True);
                newer.Provider.ClearCalls();

                RestoreReport newerReport = RestoreSync(newer);

                Assert.That(newerReport.Status, Is.EqualTo(SaveStatus.Success), newerReport.ToString());
                SlotRestoreOutcome outcome = newerReport.GetResult(newerSlot).Outcome;
                Assert.That(outcome, Is.Not.EqualTo(SlotRestoreOutcome.UpToDate), newerReport.ToString());
                Assert.That(outcome, Is.Not.EqualTo(SlotRestoreOutcome.RepairingCorruptCloud), newerReport.ToString());
                Assert.That(newerSlot.ResolveConflictCount, Is.EqualTo(1), newerReport.ToString());

                JObject conflict = ReadConflictFile(newer, TestSlotKeys.Schema);
                Assert.That(ReadLong(conflict["local"], "Gold"), Is.EqualTo(70), conflict.ToString());
                Assert.That(ReadLong(conflict["cloud"], "Gold"), Is.EqualTo(60), conflict.ToString());
                Assert.That(newer.Provider.WriteCallCount, Is.EqualTo(0));
                Assert.That(newer.Provider.DeleteCallCount, Is.EqualTo(0));
                Assert.That(harness.CloudStore.GetVersion(AccountA, TestSlotKeys.Schema), Is.EqualTo(olderVersion));

                // Default policy: cloud wins and the local side is in the conflict file
                Assert.That(outcome, Is.EqualTo(SlotRestoreOutcome.Restored), newerReport.ToString());
                Assert.That(newerSlot.PeekData().Gold, Is.EqualTo(60));
            }
        }

        // Guest claim onto an account that already has cloud data

        // Row 11 (01 5.6 step 8): claimed guest progress conflicts with existing cloud progress; the loser is backed up
        [Test]
        public void GuestClaim_OntoAccountWithCloudData_ResolvesConflict_BacksUpLoser()
        {
            using (TwoDeviceHarness harness = CreateHarness(PlayerSlots))
            {
                TestServiceContext other = harness.DeviceB;
                ActivateReconciled(other);
                MutateAndFlush(other, 9);
                string cloudVersion = harness.CloudStore.GetVersion(AccountA, TestSlotKeys.Player);

                TestServiceContext device = harness.DeviceA;
                ProfileSlot slot = device.Slot<ProfileSlot>();
                PlayAsGuestThenClaim(device, () => slot.Mutate(data => data.Coins = 7));
                Assert.That(slot.PeekData().Coins, Is.EqualTo(7), "Premise: the account starts from the claimed guest data.");

                RestoreReport report = RestoreSync(device);

                Assert.That(report.Status, Is.EqualTo(SaveStatus.Success), report.ToString());
                Assert.That(report.Completeness, Is.EqualTo(RestoreCompleteness.Full), report.ToString());
                Assert.That(slot.ResolveConflictCount, Is.EqualTo(1), report.ToString());

                // Default policy: neither side is empty, so cloud wins and the guest data is the loser
                Assert.That(report.GetResult(slot).Outcome, Is.EqualTo(SlotRestoreOutcome.Restored), report.ToString());
                Assert.That(slot.PeekData().Coins, Is.EqualTo(9));

                JObject conflict = ReadConflictFile(device, TestSlotKeys.Player);
                Assert.That(conflict.Value<string>("resolution"), Is.EqualTo(ConflictResolutionKind.TakeCloud.ToString()));
                Assert.That(ReadInt(conflict["local"], "Coins"), Is.EqualTo(7), conflict.ToString());
                Assert.That(ReadInt(conflict["cloud"], "Coins"), Is.EqualTo(9), conflict.ToString());

                Assert.That(device.Provider.WriteCallCount, Is.EqualTo(0));
                Assert.That(harness.CloudStore.GetVersion(AccountA, TestSlotKeys.Player), Is.EqualTo(cloudVersion));

                FlushResult flushed = device.FlushSync();
                Assert.That(CloudEntry(flushed, TestSlotKeys.Player).Status, Is.EqualTo(CloudFlushStatus.AlreadyInSync), flushed.ToString());
                Assert.That(device.Provider.WriteCallCount, Is.EqualTo(0));
            }
        }

        // Row 9: the claimed guest never saved this slot, so the cloud value is taken without a conflict
        [Test]
        public void GuestClaim_EmptyGuestSlot_TakesCloud_NoConflictFile()
        {
            using (TwoDeviceHarness harness = CreateHarness(PlayerAndStatsSlots))
            {
                TestServiceContext other = harness.DeviceB;
                ActivateReconciled(other);
                Assert.That(other.Slot<ProfileSlot>(TestSlotKeys.Player).Mutate(data => data.Coins = 9), Is.True);
                AssertFlushComplete(other);
                Assert.That(harness.CloudStore.Contains(AccountA, StatsKey), Is.False, "Premise: stats has no cloud value.");

                // The guest only saved stats, so the claim happens while the player slot stays empty
                TestServiceContext device = harness.DeviceA;
                ProfileSlot player = device.Slot<ProfileSlot>(TestSlotKeys.Player);
                ProfileSlot stats = device.Slot<ProfileSlot>(StatsKey);
                PlayAsGuestThenClaim(device, () => stats.Mutate(data => data.Level = 3));
                Assert.That(device.Storage.HasFile(TestPaths.ProfileSlot(ProfileA, TestSlotKeys.Player)), Is.False, "Premise: the guest player slot is empty.");
                Assert.That(player.PeekData().Coins, Is.EqualTo(0));

                RestoreReport report = RestoreSync(device);

                Assert.That(report.Status, Is.EqualTo(SaveStatus.Success), report.ToString());
                Assert.That(report.Completeness, Is.EqualTo(RestoreCompleteness.Full), report.ToString());
                Assert.That(report.GetResult(player).Outcome, Is.EqualTo(SlotRestoreOutcome.Restored), report.ToString());
                Assert.That(player.PeekData().Coins, Is.EqualTo(9));
                Assert.That(player.ResolveConflictCount, Is.EqualTo(0));
                Assert.That(device.Storage.HasFile(PlayerConflictPath), Is.False, "No conflict file for an empty guest slot.");

                Assert.That(report.GetResult(stats).Outcome, Is.EqualTo(SlotRestoreOutcome.NoCloudData), report.ToString());
                Assert.That(stats.PeekData().Level, Is.EqualTo(3), "Claimed guest data without a cloud value stays.");
                Assert.That(device.Provider.WriteCallCount, Is.EqualTo(0));
            }
        }

        // Row 11 with an empty cloud side: the default policy keeps the guest data, then uploads over exactly that value
        [Test]
        public void GuestClaim_CloudPayloadEmpty_KeepsGuestAndUploads()
        {
            using (TwoDeviceHarness harness = CreateHarness(PlayerSlots))
            {
                // The other device saved default data for the account
                TestServiceContext other = harness.DeviceB;
                ActivateReconciled(other);
                MutateAndFlush(other, 0);
                string emptyVersion = harness.CloudStore.GetVersion(AccountA, TestSlotKeys.Player);
                Assert.That(emptyVersion, Is.Not.Null, "Premise: an empty cloud value exists.");

                TestServiceContext device = harness.DeviceA;
                ProfileSlot slot = device.Slot<ProfileSlot>();
                PlayAsGuestThenClaim(device, () => slot.Mutate(data => data.Coins = 7));

                RestoreReport report = RestoreSync(device);

                Assert.That(report.Status, Is.EqualTo(SaveStatus.Success), report.ToString());
                Assert.That(report.GetResult(slot).Outcome, Is.EqualTo(SlotRestoreOutcome.LocalKept), report.ToString());
                Assert.That(slot.ResolveConflictCount, Is.EqualTo(1), report.ToString());
                Assert.That(slot.PeekData().Coins, Is.EqualTo(7), "Guest data is never replaced by an empty cloud value.");

                JObject conflict = ReadConflictFile(device, TestSlotKeys.Player);
                Assert.That(conflict.Value<string>("resolution"), Is.EqualTo(ConflictResolutionKind.KeepLocal.ToString()));
                Assert.That(ReadInt(conflict["local"], "Coins"), Is.EqualTo(7), conflict.ToString());
                Assert.That(ReadInt(conflict["cloud"], "Coins"), Is.EqualTo(0), conflict.ToString());
                Assert.That(device.Provider.WriteCallCount, Is.EqualTo(0), "Nothing is uploaded during the reconcile.");
                Assert.That(harness.CloudStore.GetVersion(AccountA, TestSlotKeys.Player), Is.EqualTo(emptyVersion));

                FlushResult flushed = device.FlushSync();

                Assert.That(flushed.IsComplete, Is.True, flushed.ToString());
                Assert.That(CloudEntry(flushed, TestSlotKeys.Player).Status, Is.EqualTo(CloudFlushStatus.Uploaded), flushed.ToString());
                IReadOnlyList<CloudWriteRequest> writes = device.Provider.GetAllWriteRequests();
                Assert.That(writes.Count, Is.EqualTo(1));
                Assert.That(writes[0].ExpectedVersion, Is.EqualTo(emptyVersion), "The upload is conditional on the empty value.");
                Assert.That(ReadInt(ReadCloudEnvelope(harness, TestSlotKeys.Player)[SaveEnvelope.DataProperty], "Coins"), Is.EqualTo(7));
            }
        }

        // Claimed data is not uploaded until the first reconcile, neither by an explicit flush nor by scheduler deadlines
        [UnityTest]
        public IEnumerator GuestClaim_NoUploadBeforeFirstReconcile()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                using (TwoDeviceHarness harness = CreateHarness(PlayerSlots))
                {
                    TestServiceContext other = harness.DeviceB;
                    ActivateReconciled(other);
                    MutateAndFlush(other, 9);
                    string cloudVersion = harness.CloudStore.GetVersion(AccountA, TestSlotKeys.Player);

                    TestServiceContext device = harness.DeviceA;
                    ProfileSlot slot = device.Slot<ProfileSlot>();
                    PlayAsGuestThenClaim(device, () => slot.Mutate(data => data.Coins = 7));

                    FlushResult flushed = device.FlushSync();

                    Assert.That(flushed.IsComplete, Is.False, flushed.ToString());
                    CloudFlushSlotResult entry = CloudEntry(flushed, TestSlotKeys.Player);
                    Assert.That(entry.Status, Is.EqualTo(CloudFlushStatus.Skipped), flushed.ToString());
                    Assert.That(entry.Reason, Is.EqualTo(CloudFlushReason.NotReconciled), flushed.ToString());

                    // Scheduler deadlines pass after a new mutation without an upload
                    Assert.That(slot.Mutate(data => data.Coins = 8), Is.True);
                    for (int i = 0; i < 3; i++)
                    {
                        harness.Clock.Advance(TimeSpan.FromSeconds(30));
                        await AsyncTestUtility.WaitFramesAsync(3);
                    }

                    await AsyncTestUtility.WaitFramesAsync(2);
                    Assert.That(device.Provider.TotalCallCount, Is.EqualTo(0), string.Join(", ", device.Provider.Calls));
                    Assert.That(harness.CloudStore.GetVersion(AccountA, TestSlotKeys.Player), Is.EqualTo(cloudVersion));

                    RestoreReport report = RestoreSync(device);

                    Assert.That(report.Status, Is.EqualTo(SaveStatus.Success), report.ToString());
                    Assert.That(slot.ResolveConflictCount, Is.EqualTo(1), report.ToString());
                    IReadOnlyList<CloudCall> calls = device.Provider.Calls;
                    Assert.That(calls.Count, Is.GreaterThan(0));
                    Assert.That(calls[0].Operation, Is.EqualTo(CloudOperation.Read), "The first provider call is the reconcile read.");
                    Assert.That(device.Provider.WriteCallCount, Is.EqualTo(0));

                    // Control: once reconciled, the scheduled path uploads
                    Assert.That(slot.Mutate(data => data.Coins = 10), Is.True);
                    harness.Clock.Advance(device.Options.CloudDebounce + TimeSpan.FromMilliseconds(100));

                    // Keeps time moving in case the lane re-armed its delay after the advance
                    await AsyncTestUtility.WaitUntilAsync(() =>
                    {
                        if (device.Provider.WriteCallCount > 0)
                        {
                            return true;
                        }

                        harness.Clock.Advance(TimeSpan.FromMilliseconds(500));
                        return false;
                    }, MaxFrames, "Scheduled upload after the reconcile");
                    Assert.That(device.Provider.WriteCallCount, Is.EqualTo(1));
                    Assert.That(harness.CloudStore.GetVersion(AccountA, TestSlotKeys.Player), Is.Not.EqualTo(cloudVersion));
                }
            });
        }

        // Row 1 keeps uploads blocked while offline; the next restore merges and both devices converge
        [Test]
        public void GuestClaim_RestoreOffline_UploadsStayBlocked_NextRestoreConverges()
        {
            using (TwoDeviceHarness harness = CreateHarness(InventorySlots))
            {
                TestServiceContext other = harness.DeviceB;
                UnionMergeSlot otherSlot = other.Slot<UnionMergeSlot>();
                ActivateReconciled(other);
                Assert.That(otherSlot.Mutate(data => data.Items.Add("shield")), Is.True);
                AssertFlushComplete(other);
                string cloudVersion = harness.CloudStore.GetVersion(AccountA, TestSlotKeys.Inventory);

                TestServiceContext device = harness.DeviceA;
                UnionMergeSlot slot = device.Slot<UnionMergeSlot>();
                PlayAsGuestThenClaim(device, () => slot.Mutate(data => data.Items.Add("sword")));

                device.Provider.EnqueueError(CloudOperation.Read, null, CloudErrorKind.Transient, -1);
                RestoreReport offline = RestoreSync(device);

                Assert.That(offline.Completeness, Is.EqualTo(RestoreCompleteness.Failed), offline.ToString());
                SlotRestoreResult offlineResult = offline.GetResult(slot);
                Assert.That(offlineResult.Outcome, Is.EqualTo(SlotRestoreOutcome.Failed), offline.ToString());
                Assert.That(offlineResult.Failure, Is.EqualTo(SlotRestoreFailure.ReadFailed), offline.ToString());
                Assert.That(slot.PeekData().Items, Is.EqualTo(new[] { "sword" }));

                FlushResult blocked = device.FlushSync();

                CloudFlushSlotResult blockedEntry = CloudEntry(blocked, TestSlotKeys.Inventory);
                Assert.That(blockedEntry.Status, Is.EqualTo(CloudFlushStatus.Skipped), blocked.ToString());
                Assert.That(blockedEntry.Reason, Is.EqualTo(CloudFlushReason.NotReconciled), blocked.ToString());
                Assert.That(device.Provider.WriteCallCount, Is.EqualTo(0));
                Assert.That(harness.CloudStore.GetVersion(AccountA, TestSlotKeys.Inventory), Is.EqualTo(cloudVersion));

                device.Provider.ClearFaults();
                RestoreReport online = RestoreSync(device);

                string[] expected = { "shield", "sword" };
                Assert.That(online.Status, Is.EqualTo(SaveStatus.Success), online.ToString());
                Assert.That(online.Completeness, Is.EqualTo(RestoreCompleteness.Full), online.ToString());
                Assert.That(online.GetResult(slot).Outcome, Is.EqualTo(SlotRestoreOutcome.Merged), online.ToString());
                Assert.That(slot.PeekData().Items, Is.EqualTo(expected));

                JObject conflict = ReadConflictFile(device, TestSlotKeys.Inventory);
                Assert.That(conflict.Value<string>("resolution"), Is.EqualTo(ConflictResolutionKind.Merged.ToString()));
                Assert.That(ReadStrings(conflict["local"], "Items"), Is.EqualTo(new[] { "sword" }), conflict.ToString());
                Assert.That(ReadStrings(conflict["cloud"], "Items"), Is.EqualTo(new[] { "shield" }), conflict.ToString());
                Assert.That(device.Provider.WriteCallCount, Is.EqualTo(0), "Nothing is uploaded during the reconcile.");

                FlushResult uploaded = device.FlushSync();

                Assert.That(uploaded.IsComplete, Is.True, uploaded.ToString());
                IReadOnlyList<CloudWriteRequest> writes = device.Provider.GetAllWriteRequests();
                Assert.That(writes.Count, Is.EqualTo(1));
                Assert.That(writes[0].ExpectedVersion, Is.EqualTo(cloudVersion), "The merge replaces exactly the cloud value it merged.");

                other.Provider.ClearCalls();
                RestoreReport otherReport = RestoreSync(other);

                Assert.That(otherReport.GetResult(otherSlot).Outcome, Is.EqualTo(SlotRestoreOutcome.Restored), otherReport.ToString());
                Assert.That(otherSlot.PeekData().Items, Is.EqualTo(expected));
                Assert.That(ReadStrings(ReadCloudEnvelope(harness, TestSlotKeys.Inventory)[SaveEnvelope.DataProperty], "Items"), Is.EqualTo(expected));
                Assert.That(other.Provider.WriteCallCount, Is.EqualTo(0));
            }
        }

        // R9 across two devices

        // Both devices reconciled against a missing value; the second first upload re-reads, skips and reconciles instead of overwriting
        [UnityTest]
        public IEnumerator FirstUpload_BothDevicesNeverSynced_SecondDeviceReconcilesInsteadOfOverwriting()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                using (TwoDeviceHarness harness = CreateHarness(PlayerSlots))
                {
                    TestServiceContext first = harness.DeviceA;
                    TestServiceContext second = harness.DeviceB;
                    ActivateReconciled(first);
                    ActivateReconciled(second);
                    Assert.That(harness.CloudStore.Contains(AccountA, TestSlotKeys.Player), Is.False, "Premise: both devices reconciled against a missing value.");

                    ProfileSlot firstSlot = first.Slot<ProfileSlot>();
                    ProfileSlot secondSlot = second.Slot<ProfileSlot>();
                    Assert.That(firstSlot.Mutate(data => data.Coins = 5), Is.True);
                    Assert.That(secondSlot.Mutate(data => data.Coins = 9), Is.True);

                    AssertFlushComplete(first);
                    string firstVersion = harness.CloudStore.GetVersion(AccountA, TestSlotKeys.Player);
                    string firstWriteId = CloudWriteId(harness, TestSlotKeys.Player);

                    var completed = new List<RestoreReport>();
                    Action<RestoreReport> onCompleted = completed.Add;
                    second.Service.RestoreCompleted += onCompleted;
                    try
                    {
                        FlushResult skipped = second.FlushSync();

                        CloudFlushSlotResult entry = CloudEntry(skipped, TestSlotKeys.Player);
                        Assert.That(entry.Status, Is.EqualTo(CloudFlushStatus.Skipped), skipped.ToString());
                        Assert.That(entry.Reason, Is.EqualTo(CloudFlushReason.NotReconciled), skipped.ToString());
                        Assert.That(second.Provider.ReadCallCount, Is.EqualTo(1), "The first upload re-reads the key.");
                        Assert.That(second.Provider.GetAllWriteRequests().Count, Is.EqualTo(0), "The first save made elsewhere is not overwritten.");
                        Assert.That(harness.CloudStore.GetVersion(AccountA, TestSlotKeys.Player), Is.EqualTo(firstVersion));

                        await AsyncTestUtility.WaitUntilAsync(() => completed.Count > 0, MaxFrames, "Single-slot reconcile after the pre-upload read");

                        RestoreReport report = completed[0];
                        Assert.That(report.Status, Is.EqualTo(SaveStatus.Success), report.ToString());
                        Assert.That(report.Slots.Count, Is.EqualTo(1), report.ToString());
                        Assert.That(secondSlot.ResolveConflictCount, Is.EqualTo(1), report.ToString());

                        JObject conflict = ReadConflictFile(second, TestSlotKeys.Player);
                        Assert.That(ReadInt(conflict["local"], "Coins"), Is.EqualTo(9), conflict.ToString());
                        Assert.That(ReadInt(conflict["cloud"], "Coins"), Is.EqualTo(5), conflict.ToString());

                        // Default policy: cloud wins, so both devices hold the first save
                        Assert.That(report.GetResult(secondSlot).Outcome, Is.EqualTo(SlotRestoreOutcome.Restored), report.ToString());
                        Assert.That(secondSlot.PeekData().Coins, Is.EqualTo(5));
                        Assert.That(LoadSlotSyncState(second, TestSlotKeys.Player).LastSyncedWriteId, Is.EqualTo(firstWriteId));

                        FlushResult settled = second.FlushSync();
                        Assert.That(CloudEntry(settled, TestSlotKeys.Player).Status, Is.EqualTo(CloudFlushStatus.AlreadyInSync), settled.ToString());
                        Assert.That(second.Provider.GetAllWriteRequests().Count, Is.EqualTo(0));
                        Assert.That(harness.CloudStore.GetVersion(AccountA, TestSlotKeys.Player), Is.EqualTo(firstVersion));
                    }
                    finally
                    {
                        second.Service.RestoreCompleted -= onCompleted;
                    }
                }
            });
        }

        private static string PlayerConflictPath => SaveLayout.ConflictPath(TestPaths.ProfileDirectory(ProfileA), TestSlotKeys.Player);

        private static SaveSlot[] PlayerAndStatsSlots()
        {
            return new SaveSlot[] { new ProfileSlot(), new ProfileSlot(StatsKey) };
        }

        private static SaveSlot[] InventorySlots()
        {
            return new SaveSlot[] { new UnionMergeSlot() };
        }

        // Guest progress on disk, then the sign-in activation claims profiles/guest into the account directory
        private static void PlayAsGuestThenClaim(TestServiceContext context, Func<bool> playAsGuest)
        {
            InitializeResult initialized = context.InitializeSync();
            Assert.That(initialized.IsSuccess, Is.True, initialized.ToString());
            Assert.That(context.Service.ActiveProfile, Is.EqualTo(ProfileId.Guest), "Premise: playing as guest.");
            Assert.That(playAsGuest(), Is.True);
            Assert.That(context.Service.FlushLocalNow().IsComplete, Is.True);

            ProfileActivationResult activated = context.ActivateSync(ProfileA);
            Assert.That(activated.IsSuccess, Is.True, activated.ToString());
            Assert.That(activated.ClaimedGuestData, Is.True, "Premise: the guest data was claimed.");
            context.Provider.ClearCalls();
            context.Storage.ResetCounters();
        }

        private static SaveSlot[] SchemaV3Slots()
        {
            return new SaveSlot[] { new SchemaV3Slot() };
        }

        // Device A runs the older schema 2 build, device B the newer schema 3 build of the same key
        private static TwoDeviceHarness CreateTwoVersionHarness()
        {
            TwoDeviceHarness harness = TwoDeviceHarness.WithSeparateSlots(
                () => new SaveSlot[] { new SchemaV2Slot() }, SchemaV3Slots, null, options => options.CloudRetryCount = 0);
            harness.SignIn(AccountA);
            return harness;
        }

        private static void MutateSchemaAndFlush(TestServiceContext context, string displayName, long gold)
        {
            Assert.That(context.Slot<SchemaV3Slot>().Mutate(data =>
            {
                data.DisplayName = displayName;
                data.Gold = gold;
            }), Is.True);
            AssertFlushComplete(context);
        }

        private static SaveSlot[] PlayerSlots()
        {
            return new SaveSlot[] { new ProfileSlot() };
        }

        // Gateway retries off so provider errors surface in one call; both devices signed in
        private static TwoDeviceHarness CreateHarness(Func<SaveSlot[]> slots, Action<SaveServiceOptions> configure = null)
        {
            var harness = new TwoDeviceHarness(slots, null, options =>
            {
                options.CloudRetryCount = 0;
                configure?.Invoke(options);
            });
            harness.SignIn(AccountA);
            return harness;
        }

        // Simulated reinstall: the old service goes away, the OS restores the backup, the app starts again
        private static TestServiceContext ReinstallDeviceA(TwoDeviceHarness harness, StorageSnapshot backup)
        {
            TestServiceContext restarted = harness.RestartDeviceA();
            restarted.Storage.RestoreSnapshot(backup);
            restarted.Provider.ClearCalls();
            restarted.Storage.ResetCounters();
            InitializeAndActivate(restarted);
            return restarted;
        }

        // Init may already resolve the account from device.json
        private static void InitializeAndActivate(TestServiceContext context)
        {
            InitializeResult initialized = context.InitializeSync();
            Assert.That(initialized.IsSuccess, Is.True, initialized.ToString());
            if (context.Service.ActiveProfile != ProfileA)
            {
                ProfileActivationResult activated = context.ActivateSync(ProfileA);
                Assert.That(activated.IsSuccess, Is.True, activated.ToString());
            }
        }

        // Signed in, active and reconciled this epoch; counters reset afterwards
        private static void ActivateReconciled(TestServiceContext context)
        {
            InitializeAndActivate(context);
            RestoreReport report = RestoreSync(context);
            Assert.That(report.Status, Is.EqualTo(SaveStatus.Success), report.ToString());
            context.Provider.ClearCalls();
            context.Storage.ResetCounters();
        }

        private static void MutateAndFlush(TestServiceContext context, int coins)
        {
            Assert.That(context.Slot<ProfileSlot>().Mutate(data => data.Coins = coins), Is.True);
            AssertFlushComplete(context);
        }

        private static void AssertFlushComplete(TestServiceContext context)
        {
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
        private static SlotSyncState LoadSlotSyncState(TestServiceContext context, string key)
        {
            var store = new SyncStateStore(context.Storage, new TestSaveLogger(), context.Clock);
            SyncStateLoadResult loaded = store.Load(TestPaths.ProfileDirectory(ProfileA), SlotReadMode.ReadOnly);
            Assert.That(loaded.Status, Is.EqualTo(SyncStateLoadStatus.Loaded), loaded.Message);
            return loaded.State.PeekSlot(key);
        }

        private static JObject ReadConflictFile(TestServiceContext context, string key)
        {
            string path = SaveLayout.ConflictPath(TestPaths.ProfileDirectory(ProfileA), key);
            Assert.That(context.Storage.HasFile(path), Is.True, string.Join(", ", context.Storage.AllFilePaths));
            return (JObject)SaveJson.Parse(context.Storage.GetBytes(path));
        }

        private static JObject ReadCloudEnvelope(TwoDeviceHarness harness, string key)
        {
            byte[] value = harness.CloudStore.GetValue(AccountA, key);
            Assert.That(value, Is.Not.Null, "No cloud value for " + key + ".");
            var envelope = SaveJson.Parse(value) as JObject;
            Assert.That(envelope, Is.Not.Null, "The cloud value is an envelope object.");
            return envelope;
        }

        private static string CloudWriteId(TwoDeviceHarness harness, string key)
        {
            return ReadCloudEnvelope(harness, key).Value<string>(SaveEnvelope.WriteIdProperty);
        }

        private static string CloudDeviceId(TwoDeviceHarness harness, string key)
        {
            return ReadCloudEnvelope(harness, key).Value<string>(SaveEnvelope.DeviceIdProperty);
        }

        // Property names depend on the serializer settings, so match case-insensitively
        private static JToken ReadProperty(JToken token, string property)
        {
            Assert.That(token, Is.InstanceOf<JObject>(), token?.ToString());
            JToken value = ((JObject)token).GetValue(property, StringComparison.OrdinalIgnoreCase);
            Assert.That(value, Is.Not.Null, token.ToString());
            return value;
        }

        private static int ReadInt(JToken token, string property)
        {
            return ReadProperty(token, property).Value<int>();
        }

        private static long ReadLong(JToken token, string property)
        {
            return ReadProperty(token, property).Value<long>();
        }

        private static string ReadString(JToken token, string property)
        {
            return ReadProperty(token, property).Value<string>();
        }

        private static string[] ReadStrings(JToken token, string property)
        {
            return ReadProperty(token, property).ToObject<string[]>();
        }
    }
}
