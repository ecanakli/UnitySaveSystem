using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Ecanakli.SaveSystem.Tests
{
    /// <summary>04a N4 liveness: every refusal clears within one restore and upload cycle, except SchemaTooNew and LocalOnly NormalizeFailed.</summary>
    [TestFixture]
    public sealed class LivenessTests
    {
        private const string AccountA = "account-a";
        private const string LocalSchemaKey = "schema_local";
        private const string CorruptText = "{ \"fmt\": 1, \"data\": ";
        private const int MaxFrames = 120;

        private static readonly ProfileId ProfileA = ProfileId.Account(AccountA);

        /// <summary>Refusal paths listed for LivenessTests in 04a A5.</summary>
        public enum RefusalPath
        {
            ReadFailure,
            CorruptCloudPayload,
            PermanentUploadFailure,
            NotReconciled,
            LocalIoError,
            TombstonePending,
            LocalWriteFailureDiskRecovered,
        }

        // N4: provoke the refusal, remove the fault, then exactly one RestoreAsync and one FlushAsync converge local and cloud
        [TestCase(RefusalPath.ReadFailure)]
        [TestCase(RefusalPath.CorruptCloudPayload)]
        [TestCase(RefusalPath.PermanentUploadFailure)]
        [TestCase(RefusalPath.NotReconciled)]
        [TestCase(RefusalPath.LocalIoError)]
        [TestCase(RefusalPath.TombstonePending)]
        [TestCase(RefusalPath.LocalWriteFailureDiskRecovered)]
        public void EveryRefusalPath_ConvergesWithinOneRestoreAndUploadCycle(RefusalPath path)
        {
            using (TwoDeviceHarness harness = CreateHarness(PlayerSlots))
            {
                ActivateReconciled(harness.DeviceA);
                MutateAndFlush(harness.DeviceA, 5);

                Scenario scenario = ProvokeRefusalAndRemoveFault(harness, path);
                TestServiceContext device = scenario.Device;
                device.Provider.ClearCalls();

                RestoreReport report = RestoreSync(device);

                Assert.That(report.Status, Is.EqualTo(SaveStatus.Success), path + ": " + report);
                SlotRestoreResult restored = report.GetResult(device.Slot<ProfileSlot>());
                Assert.That(restored, Is.Not.Null, report.ToString());
                Assert.That(restored.IsSuccess, Is.True, path + ": " + report);
                Assert.That(restored.LocalPersistFailed, Is.False, report.ToString());

                FlushResult flushed = device.FlushSync();

                Assert.That(flushed.IsComplete, Is.True, path + ": " + flushed);
                CloudFlushStatus status = CloudEntry(flushed, TestSlotKeys.Player).Status;
                Assert.That(status == CloudFlushStatus.Uploaded || status == CloudFlushStatus.AlreadyInSync, Is.True, path + ": " + flushed);

                scenario.VerifyAfterCycle?.Invoke(device);
                AssertConverged(harness, device, scenario.ExpectedCoins);
                AssertOtherDeviceConverges(harness, scenario);
            }
        }

        // N4 + N6: both devices repair the same corrupt value conditionally; the loser gets Conflict, reconciles and merges, nothing is lost
        [UnityTest]
        public IEnumerator CorruptCloudRepair_ConflictFromOtherDeviceRepair_Reconciles()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                using (TwoDeviceHarness harness = CreateHarness(InventorySlots))
                {
                    TestServiceContext first = harness.DeviceA;
                    TestServiceContext second = harness.DeviceB;
                    UnionMergeSlot firstSlot = first.Slot<UnionMergeSlot>();
                    UnionMergeSlot secondSlot = second.Slot<UnionMergeSlot>();

                    ActivateReconciled(first);
                    Assert.That(firstSlot.Mutate(data => data.Items.Add("shield")), Is.True);
                    AssertFlushComplete(first);
                    ActivateReconciled(second);
                    Assert.That(secondSlot.PeekData().Items, Is.EqualTo(new[] { "shield" }), "Premise: both devices start from the same synced value.");

                    byte[] corrupt = Encoding.UTF8.GetBytes(CorruptText);
                    string corruptVersion = harness.CloudStore.Put(AccountA, TestSlotKeys.Inventory, corrupt);

                    // Both devices keep playing offline
                    Assert.That(firstSlot.Mutate(data => data.Items.Add("sword")), Is.True);
                    Assert.That(first.Service.FlushLocalNow().IsComplete, Is.True);
                    Assert.That(secondSlot.Mutate(data => data.Items.Add("bow")), Is.True);
                    Assert.That(second.Service.FlushLocalNow().IsComplete, Is.True);

                    // Both devices see the same corrupt value and plan a conditional repair
                    foreach (TestServiceContext device in new[] { first, second })
                    {
                        RestoreReport seen = RestoreSync(device);
                        Assert.That(seen.Status, Is.EqualTo(SaveStatus.Success), seen.ToString());
                        Assert.That(seen.GetResult(device.Slot<UnionMergeSlot>()).Outcome, Is.EqualTo(SlotRestoreOutcome.RepairingCorruptCloud), seen.ToString());
                        List<byte[]> backups = CloudCorruptBackups(device, TestSlotKeys.Inventory);
                        Assert.That(backups.Count, Is.EqualTo(1));
                        Assert.That(backups[0], Is.EqualTo(corrupt), "Raw cloud bytes are backed up verbatim.");
                        device.Provider.ClearCalls();
                    }

                    // First repair wins
                    FlushResult firstRepair = first.FlushSync();

                    Assert.That(firstRepair.IsComplete, Is.True, firstRepair.ToString());
                    IReadOnlyList<CloudWriteRequest> firstWrites = first.Provider.GetAllWriteRequests();
                    Assert.That(firstWrites.Count, Is.EqualTo(1));
                    Assert.That(firstWrites[0].ExpectedVersion, Is.EqualTo(corruptVersion), "The repair is conditional on the corrupt value.");
                    string repairedVersion = harness.CloudStore.GetVersion(AccountA, TestSlotKeys.Inventory);
                    Assert.That(ReadStrings(ReadCloudEnvelope(harness, TestSlotKeys.Inventory)[SaveEnvelope.DataProperty], "Items"), Is.EqualTo(new[] { "shield", "sword" }));

                    var completed = new List<RestoreReport>();
                    Action<RestoreReport> onCompleted = completed.Add;
                    second.Service.RestoreCompleted += onCompleted;
                    try
                    {
                        // Second repair loses the conditional write and must not overwrite the first repair
                        FlushResult secondRepair = second.FlushSync();

                        CloudFlushSlotResult lost = CloudEntry(secondRepair, TestSlotKeys.Inventory);
                        Assert.That(lost.Status, Is.EqualTo(CloudFlushStatus.Failed), secondRepair.ToString());
                        Assert.That(lost.Reason, Is.EqualTo(CloudFlushReason.CloudError), secondRepair.ToString());
                        Assert.That(lost.Error, Is.Not.Null, secondRepair.ToString());
                        Assert.That(lost.Error.Kind, Is.EqualTo(CloudErrorKind.Conflict), secondRepair.ToString());
                        IReadOnlyList<CloudWriteRequest> secondWrites = second.Provider.GetAllWriteRequests();
                        Assert.That(secondWrites.Count, Is.EqualTo(1));
                        Assert.That(secondWrites[0].ExpectedVersion, Is.EqualTo(corruptVersion));
                        Assert.That(harness.CloudStore.GetVersion(AccountA, TestSlotKeys.Inventory), Is.EqualTo(repairedVersion));

                        await AsyncTestUtility.WaitUntilAsync(() => completed.Count > 0, MaxFrames, "Single-slot reconcile after the repair conflict");

                        RestoreReport reconciled = completed[0];
                        Assert.That(reconciled.Status, Is.EqualTo(SaveStatus.Success), reconciled.ToString());
                        Assert.That(reconciled.GetResult(secondSlot).Outcome, Is.EqualTo(SlotRestoreOutcome.Merged), reconciled.ToString());
                        Assert.That(secondSlot.PeekData().Items, Is.EqualTo(new[] { "bow", "shield", "sword" }));

                        JObject conflict = ReadConflictFile(second, TestSlotKeys.Inventory);
                        Assert.That(ReadStrings(conflict["local"], "Items"), Is.EqualTo(new[] { "shield", "bow" }), conflict.ToString());
                        Assert.That(ReadStrings(conflict["cloud"], "Items"), Is.EqualTo(new[] { "shield", "sword" }), conflict.ToString());
                    }
                    finally
                    {
                        second.Service.RestoreCompleted -= onCompleted;
                    }

                    FlushResult merged = second.FlushSync();

                    Assert.That(merged.IsComplete, Is.True, merged.ToString());
                    Assert.That(CloudEntry(merged, TestSlotKeys.Inventory).Status, Is.EqualTo(CloudFlushStatus.Uploaded), merged.ToString());
                    IReadOnlyList<CloudWriteRequest> mergeWrites = second.Provider.GetAllWriteRequests();
                    Assert.That(mergeWrites.Count, Is.EqualTo(2));
                    Assert.That(mergeWrites[1].ExpectedVersion, Is.EqualTo(repairedVersion), "The merge replaces exactly the repair it merged.");

                    RestoreReport firstFinal = RestoreSync(first);
                    Assert.That(firstFinal.GetResult(firstSlot).Outcome, Is.EqualTo(SlotRestoreOutcome.Restored), firstFinal.ToString());

                    // One final value everywhere
                    string[] expected = { "bow", "shield", "sword" };
                    Assert.That(firstSlot.PeekData().Items, Is.EqualTo(expected));
                    Assert.That(secondSlot.PeekData().Items, Is.EqualTo(expected));
                    Assert.That(ReadStrings(ReadCloudEnvelope(harness, TestSlotKeys.Inventory)[SaveEnvelope.DataProperty], "Items"), Is.EqualTo(expected));
                    foreach (TestServiceContext device in new[] { first, second })
                    {
                        int writes = device.Provider.WriteCallCount;
                        FlushResult settled = device.FlushSync();
                        Assert.That(CloudEntry(settled, TestSlotKeys.Inventory).Status, Is.EqualTo(CloudFlushStatus.AlreadyInSync), settled.ToString());
                        Assert.That(device.Provider.WriteCallCount, Is.EqualTo(writes));
                        Assert.That(ReadStrings(ReadLocalEnvelope(device, TestSlotKeys.Inventory)[SaveEnvelope.DataProperty], "Items"), Is.EqualTo(expected));
                        Assert.That(CloudCorruptBackups(device, TestSlotKeys.Inventory).Count, Is.EqualTo(1), "No extra backup after the repair.");
                    }
                }
            });
        }

        // N4 row 3: one corrupt provider value is backed up once across restores, a failed repair and a relaunch
        [Test]
        public void CorruptCloudSameVersion_BackedUpOnce()
        {
            using (TwoDeviceHarness harness = CreateHarness(PlayerSlots))
            {
                TestServiceContext device = harness.DeviceA;
                ActivateReconciled(device);
                MutateAndFlush(device, 5);

                byte[] corrupt = Encoding.UTF8.GetBytes(CorruptText);
                string corruptVersion = harness.CloudStore.Put(AccountA, TestSlotKeys.Player, corrupt);

                var issues = new List<SlotLoadIssue>();
                Action<SlotLoadIssue> onIssue = issues.Add;
                device.Service.SlotLoadIssueDetected += onIssue;
                try
                {
                    RestoreReport first = RestoreSync(device);

                    Assert.That(first.GetResult(device.Slot<ProfileSlot>()).Outcome, Is.EqualTo(SlotRestoreOutcome.RepairingCorruptCloud), first.ToString());
                    List<byte[]> backups = CloudCorruptBackups(device, TestSlotKeys.Player);
                    Assert.That(backups.Count, Is.EqualTo(1));
                    Assert.That(backups[0], Is.EqualTo(corrupt));
                    Assert.That(issues.Count(issue => issue.Kind == SlotLoadIssueKind.CloudPayloadCorrupt), Is.EqualTo(1), string.Join(", ", issues));

                    // The repair upload fails, so the same corrupt version stays in the cloud
                    device.Provider.EnqueueError(CloudOperation.Write, TestSlotKeys.Player, CloudErrorKind.Permanent);
                    FlushResult failed = device.FlushSync();
                    Assert.That(CloudEntry(failed, TestSlotKeys.Player).Status, Is.EqualTo(CloudFlushStatus.Failed), failed.ToString());
                    Assert.That(harness.CloudStore.GetVersion(AccountA, TestSlotKeys.Player), Is.EqualTo(corruptVersion), "Premise: same corrupt version.");

                    RestoreReport second = RestoreSync(device);

                    Assert.That(second.GetResult(device.Slot<ProfileSlot>()).Outcome, Is.EqualTo(SlotRestoreOutcome.RepairingCorruptCloud), second.ToString());
                    Assert.That(CloudCorruptBackups(device, TestSlotKeys.Player).Count, Is.EqualTo(1), "The same version is not backed up twice.");
                }
                finally
                {
                    device.Service.SlotLoadIssueDetected -= onIssue;
                }

                TestServiceContext relaunched = RelaunchDeviceA(harness);
                relaunched.Provider.ClearCalls();

                RestoreReport third = RestoreSync(relaunched);

                Assert.That(third.GetResult(relaunched.Slot<ProfileSlot>()).Outcome, Is.EqualTo(SlotRestoreOutcome.RepairingCorruptCloud), third.ToString());
                Assert.That(CloudCorruptBackups(relaunched, TestSlotKeys.Player).Count, Is.EqualTo(1), "A relaunch does not back up the same version again.");

                FlushResult repaired = relaunched.FlushSync();

                Assert.That(repaired.IsComplete, Is.True, repaired.ToString());
                IReadOnlyList<CloudWriteRequest> writes = relaunched.Provider.GetAllWriteRequests();
                Assert.That(writes.Count, Is.EqualTo(1));
                Assert.That(writes[0].ExpectedVersion, Is.EqualTo(corruptVersion));
                Assert.That(ReadInt(ReadCloudEnvelope(harness, TestSlotKeys.Player)[SaveEnvelope.DataProperty], "Coins"), Is.EqualTo(5));
                Assert.That(CloudCorruptBackups(relaunched, TestSlotKeys.Player).Count, Is.EqualTo(1));

                // Control: a different corrupt value is a new backup
                byte[] otherCorrupt = Encoding.UTF8.GetBytes("{\"broken\":");
                harness.CloudStore.Put(AccountA, TestSlotKeys.Player, otherCorrupt);

                RestoreReport fourth = RestoreSync(relaunched);

                Assert.That(fourth.GetResult(relaunched.Slot<ProfileSlot>()).Outcome, Is.EqualTo(SlotRestoreOutcome.RepairingCorruptCloud), fourth.ToString());
                List<byte[]> finalBackups = CloudCorruptBackups(relaunched, TestSlotKeys.Player);
                Assert.That(finalBackups.Count, Is.EqualTo(2));
                Assert.That(finalBackups.Any(bytes => bytes.SequenceEqual(otherCorrupt)), Is.True);
                Assert.That(finalBackups.Any(bytes => bytes.SequenceEqual(corrupt)), Is.True);
            }
        }

        // N4 exceptions: cloud and local SchemaTooNew plus LocalOnly NormalizeFailed stay refused over two cycles, signal, and clear only with a new build
        [Test]
        public void SchemaTooNew_OnlyNonConvergingRefusal_SignalsUpdateRequired()
        {
            using (TwoDeviceHarness harness = TwoDeviceHarness.WithSeparateSlots(OlderBuildSlots, NewerBuildSlots, null, options => options.CloudRetryCount = 0))
            {
                harness.SignIn(AccountA);

                // The newer build writes schema 2 for the shared key and moves the player slot on
                TestServiceContext newer = harness.DeviceB;
                ActivateReconciled(newer);
                Assert.That(newer.Slot<SchemaV2Slot>().Mutate(data =>
                {
                    data.DisplayName = "hero";
                    data.Coins = 3;
                }), Is.True);
                Assert.That(newer.Slot<ProfileSlot>().Mutate(data => data.Coins = 9), Is.True);
                AssertFlushComplete(newer);
                byte[] cloudSchemaBytes = harness.CloudStore.GetValue(AccountA, TestSlotKeys.Schema);
                string cloudSchemaVersion = harness.CloudStore.GetVersion(AccountA, TestSlotKeys.Schema);

                // A schema 2 file of another key is already on the older device's disk
                TestServiceContext older = harness.DeviceA;
                string localTooNewPath = TestPaths.ProfileSlot(ProfileA, LocalSchemaKey);
                older.Storage.SetBytes(localTooNewPath, EncodeSchema2Payload("future", 4));
                byte[] localTooNewBytes = older.Storage.GetBytes(localTooNewPath);

                var updates = new List<UpdateRequiredInfo>();
                var issues = new List<SlotLoadIssue>();
                Action<UpdateRequiredInfo> onUpdate = updates.Add;
                Action<SlotLoadIssue> onIssue = issues.Add;
                older.Service.UpdateRequired += onUpdate;
                older.Service.SlotLoadIssueDetected += onIssue;
                try
                {
                    InitializeAndActivate(older);
                    ProfileSlot player = older.Slot<ProfileSlot>();
                    SchemaV1Slot cloudTooNew = older.Slot<SchemaV1Slot>(TestSlotKeys.Schema);
                    SchemaV1Slot localTooNew = older.Slot<SchemaV1Slot>(LocalSchemaKey);
                    NormalizeThrowsSlot broken = older.Slot<NormalizeThrowsSlot>();

                    Assert.That(localTooNew.State, Is.EqualTo(SlotState.Failed), "Premise: the local file is too new.");
                    Assert.That(broken.State, Is.EqualTo(SlotState.Failed), "Premise: Normalize throws.");
                    Assert.That(updates.Count(info => info.SlotKey == LocalSchemaKey && info.Source == PayloadSource.Local), Is.EqualTo(1), string.Join(", ", updates.Select(Describe)));
                    Assert.That(
                        issues.Count(issue => issue.SlotKey == TestSlotKeys.NormalizeThrows && issue.Kind == SlotLoadIssueKind.NormalizeFailed && issue.Profile == ProfileA),
                        Is.EqualTo(1), "LocalOnly NormalizeFailed signals through SlotLoadIssueDetected. " + string.Join(", ", issues));

                    FlushResult beforeCycle = older.FlushSync();
                    Assert.That(CloudEntry(beforeCycle, TestSlotKeys.Player).Reason, Is.EqualTo(CloudFlushReason.NotReconciled), beforeCycle.ToString());
                    Assert.That(CloudEntry(beforeCycle, TestSlotKeys.Schema).Reason, Is.EqualTo(CloudFlushReason.NotReconciled), beforeCycle.ToString());
                    Assert.That(CloudEntry(beforeCycle, LocalSchemaKey).Reason, Is.EqualTo(CloudFlushReason.SchemaTooNew), beforeCycle.ToString());

                    for (int cycle = 1; cycle <= 2; cycle++)
                    {
                        string label = "Cycle " + cycle + ": ";
                        RestoreReport report = RestoreSync(older);

                        Assert.That(report.Status, Is.EqualTo(SaveStatus.Success), label + report);
                        Assert.That(report.RequiresAppUpdate, Is.True, label + report);
                        Assert.That(report.Completeness, Is.EqualTo(RestoreCompleteness.Partial), label + report);
                        Assert.That(report.GetResult(player).IsSuccess, Is.True, label + report);
                        Assert.That(report.GetResult(cloudTooNew).Outcome, Is.EqualTo(SlotRestoreOutcome.SkippedSchemaTooNew), label + report);
                        SlotRestoreResult localResult = report.GetResult(localTooNew);
                        Assert.That(localResult.Outcome, Is.EqualTo(SlotRestoreOutcome.SkippedSchemaTooNew), label + report);
                        Assert.That(localResult.Failure, Is.EqualTo(SlotRestoreFailure.LocalSchemaTooNew), label + report);
                        Assert.That(report.GetResult(broken), Is.Null, "LocalOnly slots are not part of a restore.");

                        FlushResult flushed = older.FlushSync();

                        Assert.That(flushed.IsComplete, Is.False, label + flushed);
                        Assert.That(CloudEntry(flushed, TestSlotKeys.Player).Status, Is.EqualTo(CloudFlushStatus.AlreadyInSync), label + flushed);
                        Assert.That(CloudEntry(flushed, TestSlotKeys.Schema).Reason, Is.EqualTo(CloudFlushReason.NotReconciled), label + flushed);
                        Assert.That(CloudEntry(flushed, LocalSchemaKey).Reason, Is.EqualTo(CloudFlushReason.SchemaTooNew), label + flushed);

                        // The ordinary slot converged in the first cycle; the two refusals do not
                        Assert.That(player.PeekData().Coins, Is.EqualTo(9), label);
                        Assert.That(broken.State, Is.EqualTo(SlotState.Failed), label);
                        Assert.That(broken.Mutate(data => data.Coins = cycle), Is.False, label + "Failed slots refuse mutations.");
                        Assert.That(localTooNew.State, Is.EqualTo(SlotState.Failed), label);
                    }

                    Assert.That(updates.Count, Is.EqualTo(2), "One UpdateRequired per slot and source this epoch. " + string.Join(", ", updates.Select(Describe)));
                    Assert.That(updates.Count(info => info.SlotKey == TestSlotKeys.Schema && info.Source == PayloadSource.Cloud), Is.EqualTo(1));
                    Assert.That(older.Provider.GetAllWriteRequests().Any(request => request.Key != TestSlotKeys.Player), Is.False, "Nothing but the player slot is uploaded.");
                    Assert.That(harness.CloudStore.GetValue(AccountA, TestSlotKeys.Schema), Is.EqualTo(cloudSchemaBytes));
                    Assert.That(harness.CloudStore.GetVersion(AccountA, TestSlotKeys.Schema), Is.EqualTo(cloudSchemaVersion));
                    Assert.That(harness.CloudStore.Contains(AccountA, LocalSchemaKey), Is.False);
                    Assert.That(older.Storage.GetBytes(localTooNewPath), Is.EqualTo(localTooNewBytes), "The too-new local file is never rewritten.");
                }
                finally
                {
                    older.Service.UpdateRequired -= onUpdate;
                    older.Service.SlotLoadIssueDetected -= onIssue;
                }

                // Only a new build clears these refusals
                TestServiceContext updated = harness.RestartDeviceA(FixedBuildSlots);
                InitializeAndActivate(updated);

                RestoreReport fixedReport = RestoreSync(updated);

                Assert.That(fixedReport.Status, Is.EqualTo(SaveStatus.Success), fixedReport.ToString());
                Assert.That(fixedReport.RequiresAppUpdate, Is.False, fixedReport.ToString());
                SchemaV2Slot fixedCloudSlot = updated.Slot<SchemaV2Slot>(TestSlotKeys.Schema);
                SchemaV2Slot fixedLocalSlot = updated.Slot<SchemaV2Slot>(LocalSchemaKey);
                Assert.That(fixedReport.GetResult(fixedCloudSlot).Outcome, Is.EqualTo(SlotRestoreOutcome.Restored), fixedReport.ToString());
                Assert.That(fixedCloudSlot.PeekData().DisplayName, Is.EqualTo("hero"));
                Assert.That(fixedReport.GetResult(fixedLocalSlot).IsSuccess, Is.True, fixedReport.ToString());
                Assert.That(fixedLocalSlot.PeekData().DisplayName, Is.EqualTo("future"));
                Assert.That(updated.Slot<NormalizeThrowsSlot>().State, Is.EqualTo(SlotState.Ready));

                FlushResult fixedFlush = updated.FlushSync();

                Assert.That(fixedFlush.IsComplete, Is.True, fixedFlush.ToString());
                Assert.That(harness.CloudStore.Contains(AccountA, LocalSchemaKey), Is.True, "The formerly blocked local progress is uploaded.");
            }
        }

        // Row 2: a cloud payload this build cannot read revokes the reconcile granted earlier in the same epoch
        [Test]
        public void Row2_CloudTooNewAfterReconcile_RefusesTheNextUpload()
        {
            var slot = new SchemaV1Slot();
            using (TestServiceContext context = CreateSingleDeviceContext(slot))
            {
                context.Provider.SignedInAccountId = AccountA;
                InitializeAndActivate(context);
                Assert.That(RestoreSync(context).Status, Is.EqualTo(SaveStatus.Success), "Premise: the slot reconciled this epoch.");
                Assert.That(slot.Mutate(data => data.Coins = 1), Is.True);
                AssertFlushComplete(context);

                // A newer build replaces the value while this device keeps playing
                string tooNewVersion = context.Provider.SetRawValue(AccountA, TestSlotKeys.Schema, EncodeSchema2Payload("hero", 3));
                Assert.That(slot.Mutate(data => data.Coins = 2), Is.True);
                context.Provider.ClearCalls();

                RestoreReport report = RestoreSync(context);

                Assert.That(report.GetResult(slot).Outcome, Is.EqualTo(SlotRestoreOutcome.SkippedSchemaTooNew), report.ToString());
                Assert.That(report.RequiresAppUpdate, Is.True, report.ToString());

                FlushResult flushed = context.FlushSync();

                CloudFlushSlotResult entry = CloudEntry(flushed, TestSlotKeys.Schema);
                Assert.That(entry.Status, Is.EqualTo(CloudFlushStatus.Skipped), flushed.ToString());
                Assert.That(entry.Reason, Is.EqualTo(CloudFlushReason.NotReconciled), flushed.ToString());
                Assert.That(context.Provider.WriteCallCount, Is.EqualTo(0), "Nothing is sent over a payload this build cannot read.");
                Assert.That(context.Provider.Store.GetVersion(AccountA, TestSlotKeys.Schema), Is.EqualTo(tooNewVersion));
            }
        }

        // Row 1 must not revoke: a device that reconciled and then lost the network keeps uploading
        [Test]
        public void Row1_ReadFailureAfterReconcile_StillAllowsTheNextUpload()
        {
            using (TwoDeviceHarness harness = CreateHarness(PlayerSlots))
            {
                TestServiceContext device = harness.DeviceA;
                ActivateReconciled(device);
                MutateAndFlush(device, 5);

                Assert.That(device.Slot<ProfileSlot>().Mutate(data => data.Coins = 6), Is.True);
                device.Provider.EnqueueError(CloudOperation.Read, null, CloudErrorKind.Transient, -1);

                RestoreReport report = RestoreSync(device);

                Assert.That(report.GetResult(device.Slot<ProfileSlot>()).Failure, Is.EqualTo(SlotRestoreFailure.ReadFailed), report.ToString());

                device.Provider.ClearFaults();
                FlushResult flushed = device.FlushSync();

                Assert.That(flushed.IsComplete, Is.True, flushed.ToString());
                Assert.That(CloudEntry(flushed, TestSlotKeys.Player).Status, Is.EqualTo(CloudFlushStatus.Uploaded), flushed.ToString());
                Assert.That(ReadInt(ReadCloudEnvelope(harness, TestSlotKeys.Player)[SaveEnvelope.DataProperty], "Coins"), Is.EqualTo(6));
            }
        }

        private static Scenario ProvokeRefusalAndRemoveFault(TwoDeviceHarness harness, RefusalPath path)
        {
            switch (path)
            {
                case RefusalPath.ReadFailure:
                    return ProvokeReadFailure(harness);
                case RefusalPath.CorruptCloudPayload:
                    return ProvokeCorruptCloudPayload(harness);
                case RefusalPath.PermanentUploadFailure:
                    return ProvokePermanentUploadFailure(harness);
                case RefusalPath.NotReconciled:
                    return ProvokeNotReconciled(harness);
                case RefusalPath.LocalIoError:
                    return ProvokeLocalIoError(harness);
                case RefusalPath.TombstonePending:
                    return ProvokeTombstonePending(harness);
                case RefusalPath.LocalWriteFailureDiskRecovered:
                    return ProvokeLocalWriteFailure(harness);
                default:
                    throw new ArgumentOutOfRangeException(nameof(path), path, null);
            }
        }

        // Row 1: offline progress, a relaunch and a failed read keep uploads blocked
        private static Scenario ProvokeReadFailure(TwoDeviceHarness harness)
        {
            MutateLocally(harness.DeviceA, 6);
            TestServiceContext device = RelaunchDeviceA(harness);
            device.Provider.EnqueueError(CloudOperation.Read, null, CloudErrorKind.Transient, -1);

            RestoreReport offline = RestoreSync(device);

            SlotRestoreResult result = offline.GetResult(device.Slot<ProfileSlot>());
            Assert.That(result.Outcome, Is.EqualTo(SlotRestoreOutcome.Failed), offline.ToString());
            Assert.That(result.Failure, Is.EqualTo(SlotRestoreFailure.ReadFailed), offline.ToString());
            AssertUploadRefused(harness, device, CloudFlushReason.NotReconciled);

            device.Provider.ClearFaults();
            return new Scenario(device, 6);
        }

        // Row 3: the corrupt value stays in the cloud; the cycle itself backs it up and repairs it conditionally
        private static Scenario ProvokeCorruptCloudPayload(TwoDeviceHarness harness)
        {
            MutateLocally(harness.DeviceA, 6);
            byte[] corrupt = Encoding.UTF8.GetBytes(CorruptText);
            string corruptVersion = harness.CloudStore.Put(AccountA, TestSlotKeys.Player, corrupt);
            TestServiceContext device = RelaunchDeviceA(harness);
            AssertUploadRefused(harness, device, CloudFlushReason.NotReconciled);

            return new Scenario(device, 6)
            {
                VerifyAfterCycle = context =>
                {
                    IReadOnlyList<CloudWriteRequest> writes = context.Provider.GetAllWriteRequests();
                    Assert.That(writes.Count, Is.EqualTo(1));
                    Assert.That(writes[0].ExpectedVersion, Is.EqualTo(corruptVersion), "The repair is conditional on the corrupt value.");
                    List<byte[]> backups = CloudCorruptBackups(context, TestSlotKeys.Player);
                    Assert.That(backups.Count, Is.EqualTo(1));
                    Assert.That(backups[0], Is.EqualTo(corrupt));
                },
            };
        }

        // 5.3 permanent row: suspended until the next successful reconcile
        private static Scenario ProvokePermanentUploadFailure(TwoDeviceHarness harness)
        {
            TestServiceContext device = harness.DeviceA;
            Assert.That(device.Slot<ProfileSlot>().Mutate(data => data.Coins = 6), Is.True);
            device.Provider.EnqueueError(CloudOperation.Write, TestSlotKeys.Player, CloudErrorKind.Permanent);

            FlushResult failed = device.FlushSync();

            CloudFlushSlotResult entry = CloudEntry(failed, TestSlotKeys.Player);
            Assert.That(entry.Status, Is.EqualTo(CloudFlushStatus.Failed), failed.ToString());
            Assert.That(entry.Error, Is.Not.Null, failed.ToString());
            Assert.That(entry.Error.Kind, Is.EqualTo(CloudErrorKind.Permanent), failed.ToString());
            AssertUploadRefused(harness, device, CloudFlushReason.SuspendedUntilReconcile);

            device.Provider.ClearFaults();
            return new Scenario(device, 6);
        }

        // A relaunched epoch refuses uploads until its first reconcile
        private static Scenario ProvokeNotReconciled(TwoDeviceHarness harness)
        {
            MutateLocally(harness.DeviceA, 6);
            TestServiceContext device = RelaunchDeviceA(harness);
            AssertUploadRefused(harness, device, CloudFlushReason.NotReconciled);
            return new Scenario(device, 6);
        }

        // Failed(IoError) at load while the other device moves the cloud on; restore start reloads the slot
        private static Scenario ProvokeLocalIoError(TwoDeviceHarness harness)
        {
            TestServiceContext other = harness.DeviceB;
            ActivateReconciled(other);
            MutateAndFlush(other, 9);

            string slotPath = TestPaths.ProfileSlot(ProfileA, TestSlotKeys.Player);
            StorageFault readFault = null;
            TestServiceContext device = RelaunchDeviceA(harness, context => readFault = context.Storage.FailWithIOException(StorageOperation.Read, slotPath));
            ProfileSlot slot = device.Slot<ProfileSlot>();
            Assert.That(slot.State, Is.EqualTo(SlotState.Failed), "Premise: the slot file could not be read.");
            Assert.That(slot.Failure, Is.EqualTo(SlotFailure.IoError));
            AssertUploadRefused(harness, device, CloudFlushReason.SlotNotReady);

            device.Storage.RemoveFault(readFault);
            return new Scenario(device, 9, true);
        }

        // Rows 4-5: a failed cloud delete leaves a tombstone that blocks uploads of later progress
        private static Scenario ProvokeTombstonePending(TwoDeviceHarness harness)
        {
            TestServiceContext device = harness.DeviceA;
            ProfileSlot slot = device.Slot<ProfileSlot>();
            device.Provider.EnqueueError(CloudOperation.Delete, TestSlotKeys.Player, CloudErrorKind.Transient);

            DeleteResult deleted = AsyncTestUtility.RunSync(device.Service.DeleteSlotAsync(slot, DeleteTarget.LocalAndCloud, CancellationToken.None), nameof(SaveService.DeleteSlotAsync));

            Assert.That(deleted.Cloud, Is.EqualTo(CloudDeleteStatus.Failed), deleted.ToString());
            Assert.That(harness.CloudStore.Contains(AccountA, TestSlotKeys.Player), Is.True, "Premise: the cloud delete failed.");
            Assert.That(LoadSlotSyncState(device, TestSlotKeys.Player).PendingDelete, Is.True, "Premise: the tombstone is pending.");

            Assert.That(slot.Mutate(data => data.Coins = 8), Is.True);
            AssertUploadRefused(harness, device, CloudFlushReason.NotReconciled);

            device.Provider.ClearFaults();
            return new Scenario(device, 8)
            {
                VerifyAfterCycle = context => Assert.That(context.Provider.DeleteCallCount, Is.EqualTo(1), "The restore finished the delete before the upload."),
            };
        }

        // Disk first: a failing slot write skips the upload; the disk recovers before the cycle
        private static Scenario ProvokeLocalWriteFailure(TwoDeviceHarness harness)
        {
            TestServiceContext device = harness.DeviceA;
            string slotPath = TestPaths.ProfileSlot(ProfileA, TestSlotKeys.Player);
            StorageFault diskFull = device.Storage.FailWithKind(StorageOperation.Write, slotPath, LocalWriteErrorKind.DiskFull);
            Assert.That(device.Slot<ProfileSlot>().Mutate(data => data.Coins = 6), Is.True);

            FlushResult refused = AssertUploadRefused(harness, device, CloudFlushReason.LocalWriteFailed);

            Assert.That(refused.Local.IsComplete, Is.False, refused.ToString());
            device.Storage.RemoveFault(diskFull);
            return new Scenario(device, 6);
        }

        private static FlushResult AssertUploadRefused(TwoDeviceHarness harness, TestServiceContext device, CloudFlushReason reason)
        {
            string cloudVersion = harness.CloudStore.GetVersion(AccountA, TestSlotKeys.Player);
            int writes = device.Provider.WriteCallCount;

            FlushResult refused = device.FlushSync();

            CloudFlushSlotResult entry = CloudEntry(refused, TestSlotKeys.Player);
            Assert.That(entry.Status, Is.EqualTo(CloudFlushStatus.Skipped), refused.ToString());
            Assert.That(entry.Reason, Is.EqualTo(reason), refused.ToString());
            Assert.That(refused.IsComplete, Is.False, refused.ToString());
            Assert.That(device.Provider.WriteCallCount, Is.EqualTo(writes), "Premise: the refusal sends nothing.");
            Assert.That(harness.CloudStore.GetVersion(AccountA, TestSlotKeys.Player), Is.EqualTo(cloudVersion));
            return refused;
        }

        // Memory, local file and cloud hold the same data; sync state names the cloud write; nothing is left to upload
        private static void AssertConverged(TwoDeviceHarness harness, TestServiceContext device, int expectedCoins)
        {
            ProfileSlot slot = device.Slot<ProfileSlot>();
            Assert.That(slot.State, Is.EqualTo(SlotState.Ready));
            Assert.That(slot.PeekData().Coins, Is.EqualTo(expectedCoins));

            JObject cloud = ReadCloudEnvelope(harness, TestSlotKeys.Player);
            JObject local = ReadLocalEnvelope(device, TestSlotKeys.Player);
            Assert.That(ReadInt(cloud[SaveEnvelope.DataProperty], "Coins"), Is.EqualTo(expectedCoins), cloud.ToString());
            Assert.That(JToken.DeepEquals(local[SaveEnvelope.DataProperty], cloud[SaveEnvelope.DataProperty]), Is.True, "Local: " + local + "\nCloud: " + cloud);

            SlotSyncState sync = LoadSlotSyncState(device, TestSlotKeys.Player);
            Assert.That(sync.LastSyncedWriteId, Is.EqualTo(cloud.Value<string>(SaveEnvelope.WriteIdProperty)));
            Assert.That(sync.PendingWriteId, Is.Null);
            Assert.That(sync.PendingDelete, Is.False);
            Assert.That(sync.NeedsCloudRecovery, Is.False);

            int writes = device.Provider.WriteCallCount;
            FlushResult settled = device.FlushSync();
            Assert.That(settled.IsComplete, Is.True, settled.ToString());
            Assert.That(CloudEntry(settled, TestSlotKeys.Player).Status, Is.EqualTo(CloudFlushStatus.AlreadyInSync), settled.ToString());
            Assert.That(device.Provider.WriteCallCount, Is.EqualTo(writes), "Nothing is left to upload.");
        }

        // The other device reaches the same value with one restore
        private static void AssertOtherDeviceConverges(TwoDeviceHarness harness, Scenario scenario)
        {
            TestServiceContext other = harness.DeviceB;
            if (!scenario.OtherDeviceActive)
            {
                InitializeAndActivate(other);
            }

            RestoreReport report = RestoreSync(other);
            Assert.That(report.Status, Is.EqualTo(SaveStatus.Success), report.ToString());
            Assert.That(other.Slot<ProfileSlot>().PeekData().Coins, Is.EqualTo(scenario.ExpectedCoins), report.ToString());
        }

        private static byte[] EncodeSchema2Payload(string displayName, int coins)
        {
            var envelope = new SaveEnvelope
            {
                Format = SaveEnvelope.CurrentFormat,
                Schema = 2,
                Revision = 1,
                DeviceId = "device-newer",
                AccountId = AccountA,
                Data = new JObject { { "DisplayName", displayName }, { "Coins", coins } },
            };

            return EnvelopeCodec.Encode(envelope).Bytes;
        }

        private static string Describe(UpdateRequiredInfo info)
        {
            return info.SlotKey + "/" + info.Source;
        }

        private static SaveSlot[] PlayerSlots()
        {
            return new SaveSlot[] { new ProfileSlot() };
        }

        private static SaveSlot[] InventorySlots()
        {
            return new SaveSlot[] { new UnionMergeSlot() };
        }

        private static SaveSlot[] OlderBuildSlots()
        {
            return new SaveSlot[] { new ProfileSlot(), new SchemaV1Slot(), new SchemaV1Slot(LocalSchemaKey), new NormalizeThrowsSlot() };
        }

        private static SaveSlot[] NewerBuildSlots()
        {
            return new SaveSlot[] { new ProfileSlot(), new SchemaV2Slot() };
        }

        private static SaveSlot[] FixedBuildSlots()
        {
            return new SaveSlot[] { new ProfileSlot(), new SchemaV2Slot(), new SchemaV2Slot(LocalSchemaKey), new NormalizeThrowsSlot { ThrowOnNormalize = false } };
        }

        // One device over fresh fakes; gateway retries off so provider errors surface in one call
        private static TestServiceContext CreateSingleDeviceContext(params SaveSlot[] slots)
        {
            return TestServiceFactory.Create(new TestServiceSetup
            {
                Slots = slots,
                ConfigureOptions = options => options.CloudRetryCount = 0,
            });
        }

        // Gateway retries off so provider errors surface in one call; both devices signed in
        private static TwoDeviceHarness CreateHarness(Func<SaveSlot[]> slots)
        {
            var harness = new TwoDeviceHarness(slots, null, options => options.CloudRetryCount = 0);
            harness.SignIn(AccountA);
            return harness;
        }

        // New epoch over the same disk and provider; beforeInitialize runs on the fresh service
        private static TestServiceContext RelaunchDeviceA(TwoDeviceHarness harness, Action<TestServiceContext> beforeInitialize = null)
        {
            TestServiceContext relaunched = harness.RestartDeviceA();
            beforeInitialize?.Invoke(relaunched);
            InitializeAndActivate(relaunched);
            return relaunched;
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

        private static void MutateLocally(TestServiceContext context, int coins)
        {
            Assert.That(context.Slot<ProfileSlot>().Mutate(data => data.Coins = coins), Is.True);
            Assert.That(context.Service.FlushLocalNow().IsComplete, Is.True);
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

        // Raw bytes of every cloud-corrupt backup of key in the account directory
        private static List<byte[]> CloudCorruptBackups(TestServiceContext context, string key)
        {
            string directory = TestPaths.ProfileDirectory(ProfileA);
            string prefix = directory + "/";
            return context.Storage.AllFilePaths
                .Where(path => path.StartsWith(prefix, StringComparison.Ordinal) && path.IndexOf('/', prefix.Length) < 0)
                .Where(path => SaveLayout.IsBackupFileName(BackupFileFamily.CloudCorrupt, key, path.Substring(prefix.Length)))
                .Select(path => context.Storage.GetBytes(path))
                .ToList();
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

        private static JObject ReadLocalEnvelope(TestServiceContext context, string key)
        {
            string path = TestPaths.ProfileSlot(ProfileA, key);
            byte[] bytes = context.Storage.GetBytes(path);
            Assert.That(bytes, Is.Not.Null, "No local file " + path + ".");
            var envelope = SaveJson.Parse(bytes) as JObject;
            Assert.That(envelope, Is.Not.Null, "The local file is an envelope object.");
            return envelope;
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

        private static string[] ReadStrings(JToken token, string property)
        {
            return ReadProperty(token, property).ToObject<string[]>();
        }

        /// <summary>Device under test after the refusal, and the value every side must hold after the cycle.</summary>
        private sealed class Scenario
        {
            public Scenario(TestServiceContext device, int expectedCoins, bool otherDeviceActive = false)
            {
                Device = device;
                ExpectedCoins = expectedCoins;
                OtherDeviceActive = otherDeviceActive;
            }

            public TestServiceContext Device { get; }

            public int ExpectedCoins { get; }

            public bool OtherDeviceActive { get; }

            /// <summary>Path-specific checks right after the cycle's flush.</summary>
            public Action<TestServiceContext> VerifyAfterCycle { get; set; }
        }
    }
}
