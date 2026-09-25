using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Ecanakli.SaveSystem.Tests
{
    /// <summary>Sync safety invariants: no force API, write-id journaling, failed writes, conditional tombstones and writes, read-only queries.</summary>
    [TestFixture]
    public sealed class SafetyInvariantTests
    {
        private const string AccountA = "account-a";
        private const string OtherDeviceId = "device-other";
        private const string StatsKey = "stats";
        private const int MaxFrames = 120;

        private static readonly ProfileId ProfileA = ProfileId.Account(AccountA);
        private static readonly string[] ForbiddenNameParts = { "Force", "Overwrite" };

        [Test]
        public void PublicApi_HasNoForceOrOverwriteMembers()
        {
            Type[] types = typeof(ISaveService).Assembly.GetExportedTypes()
                .Where(type => type.Namespace != null && type.Namespace.StartsWith("Ecanakli.SaveSystem", StringComparison.Ordinal))
                .ToArray();
            Assert.That(types, Does.Contain(typeof(ISaveService)), "Premise: the public API is scanned.");
            Assert.That(types, Does.Contain(typeof(SaveSlot)));

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var offenders = new List<string>();
            foreach (Type type in types)
            {
                AddIfForbidden(offenders, type.FullName, type.Name);
                foreach (MemberInfo member in type.GetMembers(flags))
                {
                    if (!IsVisibleOutsideAssembly(member))
                    {
                        continue;
                    }

                    string label = type.FullName + "." + member.Name;
                    AddIfForbidden(offenders, label, member.Name);
                    if (member is MethodBase method)
                    {
                        foreach (ParameterInfo parameter in method.GetParameters())
                        {
                            AddIfForbidden(offenders, label + "(" + parameter.Name + ")", parameter.Name);
                        }
                    }
                }
            }

            Assert.That(offenders, Is.Empty, "Public API must not offer a way around the sync safety rules:\n" + string.Join("\n", offenders));
        }

        [UnityTest]
        public IEnumerator PendingWriteId_PersistedBeforeTheHeldProviderWriteCompletes()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                var slot = new ProfileSlot();
                using (TestServiceContext context = CreateContext(slot))
                {
                    ActivateReconciledAccount(context, AccountA);
                    Assert.That(slot.Mutate(data => data.Coins = 5), Is.True);
                    context.Provider.HoldWrites();
                    try
                    {
                        UniTask<FlushResult> flush = context.Service.FlushAsync(CancellationToken.None).Preserve();
                        await AsyncTestUtility.WaitUntilAsync(() => context.Provider.HeldCallCount == 1, MaxFrames, "Held provider write");

                        IReadOnlyList<CloudWriteRequest> writes = context.Provider.GetAllWriteRequests();
                        Assert.That(writes.Count, Is.EqualTo(1));
                        string writeId = ReadWriteId(writes[0].Value);
                        Assert.That(writeId, Is.Not.Null.And.Not.Empty);
                        Assert.That(context.Provider.Store.Contains(AccountA, TestSlotKeys.Player), Is.False, "Premise: the write has not landed.");

                        SlotSyncState during = LoadSyncState(context, ProfileA).PeekSlot(TestSlotKeys.Player);
                        Assert.That(during.PendingWriteId, Is.EqualTo(writeId), "profile.json journals the write id before the provider call.");

                        context.Provider.ReleaseWrites();
                        await AsyncTestUtility.WaitUntilAsync(() => flush.Status.IsCompleted(), MaxFrames, "Flush");

                        FlushResult result = await flush;
                        Assert.That(result.IsComplete, Is.True, result.ToString());
                        SlotSyncState after = LoadSyncState(context, ProfileA).PeekSlot(TestSlotKeys.Player);
                        Assert.That(after.PendingWriteId, Is.Null);
                        Assert.That(after.LastSyncedWriteId, Is.EqualTo(writeId));
                    }
                    finally
                    {
                        context.Provider.ReleaseWrites();
                    }
                }
            });
        }

        [Test]
        public void PendingWriteIds_OfABatch_OnDiskWhenTheProviderAppliesTheWrite()
        {
            var player = new ProfileSlot();
            var stats = new ProfileSlot(StatsKey);
            using (TestServiceContext context = CreateContext(player, stats))
            {
                ActivateReconciledAccount(context, AccountA);
                Assert.That(player.Mutate(data => data.Coins = 5), Is.True);
                Assert.That(stats.Mutate(data => data.Level = 2), Is.True);

                // Recorded inside the provider call; asserted afterwards so a failure never throws through the provider
                var seen = new Dictionary<string, string>(StringComparer.Ordinal);
                SyncStateLoadStatus? seenStatus = null;
                context.Provider.AfterWrite = (provider, requests) =>
                {
                    var store = new SyncStateStore(context.Storage, new TestSaveLogger(), context.Clock);
                    SyncStateLoadResult loaded = store.Load(TestPaths.ProfileDirectory(ProfileA), SlotReadMode.ReadOnly);
                    seenStatus = loaded.Status;
                    if (loaded.Status == SyncStateLoadStatus.Loaded)
                    {
                        foreach (CloudWriteRequest request in requests)
                        {
                            seen[request.Key] = loaded.State.PeekSlot(request.Key)?.PendingWriteId;
                        }
                    }
                };

                try
                {
                    FlushResult result = context.FlushSync();

                    Assert.That(result.IsComplete, Is.True, result.ToString());
                    Assert.That(context.Provider.WriteCallCount, Is.EqualTo(1), "Premise: one batch.");
                    Assert.That(seenStatus, Is.EqualTo(SyncStateLoadStatus.Loaded));

                    IReadOnlyList<CloudWriteRequest> writes = context.Provider.GetAllWriteRequests();
                    Assert.That(writes.Select(write => write.Key), Is.EquivalentTo(new[] { TestSlotKeys.Player, StatsKey }));
                    foreach (CloudWriteRequest write in writes)
                    {
                        Assert.That(seen.ContainsKey(write.Key), Is.True, write.Key);
                        Assert.That(seen[write.Key], Is.EqualTo(ReadWriteId(write.Value)), write.Key);
                    }
                }
                finally
                {
                    context.Provider.AfterWrite = null;
                }
            }
        }

        [TestCase(CloudErrorKind.Permanent)]
        [TestCase(CloudErrorKind.Transient)]
        [TestCase(CloudErrorKind.Conflict)]
        public void ProviderWriteError_DoesNotAdvanceLastSyncedState(CloudErrorKind kind)
        {
            AssertWriteFailureKeepsSyncedState(provider => provider.EnqueueError(CloudOperation.Write, TestSlotKeys.Player, kind));
        }

        [Test]
        public void ProviderWriteThrows_DoesNotAdvanceLastSyncedState()
        {
            AssertWriteFailureKeepsSyncedState(provider => provider.EnqueueThrow(CloudOperation.Write, new InvalidOperationException("Scripted provider throw.")));
        }

        // ADR-003 1: a write Conflict blocks further uploads of that slot until its single-slot reconcile succeeds
        [UnityTest]
        public IEnumerator AfterConflictRefusal_NoUploadUntilReconciled()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                var slot = new ProfileSlot();
                using (TestServiceContext context = CreateContext(slot))
                {
                    ActivateReconciledAccount(context, AccountA);
                    Assert.That(slot.Mutate(data => data.Coins = 5), Is.True);
                    Assert.That(context.FlushSync().IsComplete, Is.True);

                    // Another device moves the cloud on, so the next conditional write hits a real version conflict
                    var other = new ProfileSlot();
                    UploadFromOtherDevice(context.Provider.Store, () => Assert.That(other.Mutate(data => data.Coins = 9), Is.True), other);
                    string otherVersion = context.Provider.Store.GetVersion(AccountA, TestSlotKeys.Player);
                    context.Provider.ClearCalls();

                    // The reconcile the conflict schedules cannot finish while the read fails
                    context.Provider.EnqueueError(CloudOperation.Read, TestSlotKeys.Player, CloudErrorKind.Transient);
                    var reports = new List<RestoreReport>();
                    Action<RestoreReport> onCompleted = reports.Add;
                    context.Service.RestoreCompleted += onCompleted;
                    try
                    {
                        Assert.That(slot.Mutate(data => data.Coins = 6), Is.True);
                        FlushResult conflicted = context.FlushSync();

                        CloudFlushSlotResult conflictEntry = conflicted.Cloud.First(item => item.SlotKey == TestSlotKeys.Player);
                        Assert.That(conflictEntry.Status, Is.EqualTo(CloudFlushStatus.Failed), conflicted.ToString());
                        Assert.That(conflictEntry.Reason, Is.EqualTo(CloudFlushReason.CloudError), conflicted.ToString());
                        Assert.That(conflictEntry.Error, Is.Not.Null, conflicted.ToString());
                        Assert.That(conflictEntry.Error.Kind, Is.EqualTo(CloudErrorKind.Conflict), conflicted.ToString());
                        Assert.That(context.Provider.WriteCallCount, Is.EqualTo(1), "Premise: one refused write.");
                        Assert.That(context.Provider.Store.GetVersion(AccountA, TestSlotKeys.Player), Is.EqualTo(otherVersion), "The other device's value is untouched.");

                        await AsyncTestUtility.WaitUntilAsync(() => reports.Count == 1, MaxFrames, "Single-slot reconcile after the write conflict");
                        Assert.That(reports[0].Slots.Count, Is.EqualTo(1), reports[0].ToString());
                        Assert.That(reports[0].GetResult(slot).Failure, Is.EqualTo(SlotRestoreFailure.ReadFailed), reports[0].ToString());

                        // New progress while the slot is still unreconciled
                        Assert.That(slot.Mutate(data => data.Coins = 7), Is.True);
                        FlushResult refused = context.FlushSync();

                        CloudFlushSlotResult refusedEntry = refused.Cloud.First(item => item.SlotKey == TestSlotKeys.Player);
                        Assert.That(refusedEntry.Status, Is.EqualTo(CloudFlushStatus.Skipped), refused.ToString());
                        Assert.That(refusedEntry.Reason, Is.EqualTo(CloudFlushReason.NotReconciled), refused.ToString());
                        Assert.That(context.Provider.WriteCallCount, Is.EqualTo(1), "No upload before the reconcile completes.");
                        Assert.That(context.Provider.Store.GetVersion(AccountA, TestSlotKeys.Player), Is.EqualTo(otherVersion));

                        // The reconcile completes; uploads resume
                        context.Provider.ClearFaults();
                        RestoreReport reconciled = await context.Service.RestoreAsync(CancellationToken.None);
                        Assert.That(reconciled.Status, Is.EqualTo(SaveStatus.Success), reconciled.ToString());
                        Assert.That(slot.ResolveConflictCount, Is.EqualTo(1), reconciled.ToString());

                        Assert.That(slot.Mutate(data => data.Coins = 12), Is.True);
                        FlushResult uploaded = context.FlushSync();

                        Assert.That(uploaded.IsComplete, Is.True, uploaded.ToString());
                        Assert.That(context.Provider.WriteCallCount, Is.EqualTo(2));
                        Assert.That(context.Provider.Store.GetVersion(AccountA, TestSlotKeys.Player), Is.Not.EqualTo(otherVersion));
                    }
                    finally
                    {
                        context.Service.RestoreCompleted -= onCompleted;
                    }
                }
            });
        }

        // ADR-003 3: HadContent is set only after a local write of non-empty data lands
        [Test]
        public void LocalWriteFailure_DoesNotSetHadContentOrClearDirty()
        {
            var slot = new ProfileSlot();
            using (TestServiceContext context = CreateContext(slot))
            {
                ActivateReconciledAccount(context, AccountA);
                string slotPath = TestPaths.ProfileSlot(ProfileA, TestSlotKeys.Player);
                Assert.That(ReadSyncState(context, ProfileA).State.PeekSlot(TestSlotKeys.Player).HadContent, Is.False, "Premise: nothing was written yet.");

                StorageFault fault = context.Storage.FailWithKind(StorageOperation.Write, slotPath, LocalWriteErrorKind.DiskFull);
                Assert.That(slot.Mutate(data => data.Coins = 5), Is.True);
                LocalFlushResult failed = context.Service.FlushLocalNow();

                Assert.That(failed.IsComplete, Is.False);
                Assert.That(failed.Failures.Single().SlotKey, Is.EqualTo(TestSlotKeys.Player));
                Assert.That(failed.Failures.Single().Kind, Is.EqualTo(LocalWriteErrorKind.DiskFull));
                Assert.That(context.Storage.HasFile(slotPath), Is.False);
                Assert.That(ReadSyncState(context, ProfileA).State.PeekSlot(TestSlotKeys.Player).HadContent, Is.False, "A failed write never claims content.");

                // Only dirty slots are flushed, so a second failure proves the slot stayed dirty
                LocalFlushResult again = context.Service.FlushLocalNow();
                Assert.That(again.IsComplete, Is.False);
                Assert.That(again.Failures.Single().SlotKey, Is.EqualTo(TestSlotKeys.Player));
                Assert.That(ReadSyncState(context, ProfileA).State.PeekSlot(TestSlotKeys.Player).HadContent, Is.False);

                // Control: the same data reaches disk once the fault is gone, and only then is content recorded
                context.Storage.RemoveFault(fault);
                LocalFlushResult recovered = context.Service.FlushLocalNow();

                Assert.That(recovered.IsComplete, Is.True, string.Join(", ", recovered.Failures));
                Assert.That(context.Storage.HasFile(slotPath), Is.True);
                Assert.That(ReadSyncState(context, ProfileA).State.PeekSlot(TestSlotKeys.Player).HadContent, Is.True);
                Assert.That(slot.PeekData().Coins, Is.EqualTo(5));
            }
        }

        // R1: another device wrote after our delete, so the tombstone must not delete its value
        [UnityTest]
        public IEnumerator TombstoneForOlderWrite_CloudHasNewerWrite_DoesNotDelete()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                var slot = new ProfileSlot();
                using (TestServiceContext context = CreateContext(slot))
                {
                    string deletedWriteId = await UploadThenDeleteWithFailedCloudDeleteAsync(context, slot);

                    var other = new ProfileSlot();
                    UploadFromOtherDevice(context.Provider.Store, () => Assert.That(other.Mutate(data => data.Coins = 9), Is.True), other);
                    string newerVersion = context.Provider.Store.GetVersion(AccountA, TestSlotKeys.Player);
                    string newerWriteId = ReadWriteId(context.Provider.Store.GetValue(AccountA, TestSlotKeys.Player));
                    Assert.That(newerWriteId, Is.Not.EqualTo(deletedWriteId), "Premise: the cloud holds a newer write.");
                    context.Provider.ClearCalls();

                    RestoreReport report = await context.Service.RestoreAsync(CancellationToken.None);

                    Assert.That(report.Status, Is.EqualTo(SaveStatus.Success), report.ToString());
                    Assert.That(context.Provider.DeleteCallCount, Is.EqualTo(0), "No delete for a write id the tombstone does not name.");
                    Assert.That(context.Provider.Store.GetVersion(AccountA, TestSlotKeys.Player), Is.EqualTo(newerVersion), "The newer cloud value is untouched.");
                    Assert.That(report.GetResult(slot).IsSuccess, Is.True, report.ToString());
                    Assert.That(slot.PeekData().Coins, Is.EqualTo(9), "The other device's data reaches this device.");
                    Assert.That(LoadSyncState(context, ProfileA).PeekSlot(TestSlotKeys.Player).PendingDelete, Is.False, "The stale tombstone is cleared.");
                }
            });
        }

        // Control for R1: our own tombstoned write is deleted conditionally on its version
        [UnityTest]
        public IEnumerator TombstoneForSameWrite_DeletesWithExpectedVersion()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                var slot = new ProfileSlot();
                using (TestServiceContext context = CreateContext(slot))
                {
                    await UploadThenDeleteWithFailedCloudDeleteAsync(context, slot);
                    string version = context.Provider.Store.GetVersion(AccountA, TestSlotKeys.Player);
                    context.Provider.ClearCalls();

                    RestoreReport report = await context.Service.RestoreAsync(CancellationToken.None);

                    Assert.That(report.GetResult(slot).Outcome, Is.EqualTo(SlotRestoreOutcome.NoCloudData), report.ToString());
                    Assert.That(context.Provider.DeleteCallCount, Is.EqualTo(1));
                    CloudCall delete = context.Provider.Calls.Single(call => call.Operation == CloudOperation.Delete);
                    Assert.That(delete.DeleteExpectedVersion, Is.EqualTo(version));
                    Assert.That(context.Provider.Store.Contains(AccountA, TestSlotKeys.Player), Is.False);
                    Assert.That(LoadSyncState(context, ProfileA).PeekSlot(TestSlotKeys.Player).PendingDelete, Is.False);
                }
            });
        }

        // ADR-003 4: a corrupt profile.json keeps HadContent true and drops LastSyncedWriteId
        [Test]
        public void CorruptProfileJson_FallsBackToHadContentTrueAndNullSyncedWriteId()
        {
            var slot = new ProfileSlot();
            TestServiceContext context = CreateContext(slot);
            TestServiceContext restarted = null;
            try
            {
                string cloudVersion = UploadThenLeaveUnsyncedLocalChange(context, slot);
                string statePath = TestPaths.ProfileState(ProfileA);
                Assert.That(context.Storage.HasFile(statePath), Is.True, "Premise: profile.json exists.");

                var reloaded = new ProfileSlot();
                reloaded.ConflictResolver = (in ConflictContext<ProfileData> conflict) => ConflictResolution<ProfileData>.KeepLocal;
                restarted = TestServiceFactory.Restart(context, reloaded);

                // No readable copy: the primary is unparseable and the crash copies are gone
                restarted.Storage.SetText(statePath, "{ \"version\": 1, \"slots\": ");
                restarted.Storage.RemoveFile(TestPaths.Tmp(statePath));
                restarted.Storage.RemoveFile(TestPaths.Bak(statePath));
                InitializeAndActivate(restarted, AccountA);

                Assert.That(restarted.Logger.Contains(TestLogLevel.Error, "is corrupt; using safe sync-state fallbacks"), Is.True, restarted.Logger.Describe());
                SyncStateLoadResult state = ReadSyncState(restarted, ProfileA);
                Assert.That(state.Status, Is.EqualTo(SyncStateLoadStatus.Loaded), "The fallback replaced the corrupt file. " + state.Message);
                Assert.That(state.State.UnknownSlotsHadContent, Is.True, "The fallback is persisted so it survives restarts.");
                Assert.That(state.State.PeekSlot(TestSlotKeys.Player).HadContent, Is.True);
                Assert.That(state.State.PeekSlot(TestSlotKeys.Player).LastSyncedWriteId, Is.Null);
                Assert.That(reloaded.PeekData().Coins, Is.EqualTo(6), "Premise: the unsynced local change is loaded.");
                restarted.Provider.ClearCalls();

                RestoreReport report = RestoreSync(restarted);

                // Unknown provenance ends in ResolveConflict, never in a silent TakeCloud over dirty local data
                Assert.That(report.Status, Is.EqualTo(SaveStatus.Success), report.ToString());
                Assert.That(reloaded.ResolveConflictCount, Is.EqualTo(1), report.ToString());
                Assert.That(report.GetResult(reloaded).Outcome, Is.EqualTo(SlotRestoreOutcome.LocalKept), report.ToString());
                Assert.That(reloaded.PeekData().Coins, Is.EqualTo(6));
                JObject conflict = ReadConflictFile(restarted, TestSlotKeys.Player);
                Assert.That(conflict.Value<string>("resolution"), Is.EqualTo(ConflictResolutionKind.KeepLocal.ToString()));
                Assert.That(ReadInt(conflict["local"], "Coins"), Is.EqualTo(6), conflict.ToString());
                Assert.That(ReadInt(conflict["cloud"], "Coins"), Is.EqualTo(5), conflict.ToString());
                Assert.That(restarted.Provider.WriteCallCount, Is.EqualTo(0), "Nothing is uploaded during the restore.");

                // HadContent came back true, so an empty payload is still refused
                var failures = new List<UploadFailure>();
                Action<UploadFailure> onUploadFailed = failures.Add;
                restarted.Service.UploadFailed += onUploadFailed;
                try
                {
                    Assert.That(reloaded.Mutate(data => data.Coins = 0), Is.True);
                    FlushResult flushed = restarted.FlushSync();

                    CloudFlushSlotResult entry = flushed.Cloud.First(item => item.SlotKey == TestSlotKeys.Player);
                    Assert.That(entry.Status, Is.EqualTo(CloudFlushStatus.Skipped), flushed.ToString());
                    Assert.That(entry.Reason, Is.EqualTo(CloudFlushReason.EmptyOverContent), flushed.ToString());
                    Assert.That(restarted.Provider.WriteCallCount, Is.EqualTo(0));
                    Assert.That(restarted.Provider.Store.GetVersion(AccountA, TestSlotKeys.Player), Is.EqualTo(cloudVersion));
                    Assert.That(failures.Count, Is.EqualTo(1), string.Join(", ", failures));
                    Assert.That(failures[0].SlotKey, Is.EqualTo(TestSlotKeys.Player));
                    Assert.That(failures[0].Reason, Is.EqualTo(CloudFlushReason.EmptyOverContent));
                }
                finally
                {
                    restarted.Service.UploadFailed -= onUploadFailed;
                }
            }
            finally
            {
                context.Dispose();
                restarted?.Dispose();
            }
        }

        // ADR-003 4: a profile.json from a newer build loads its known fields with the same safe overrides
        [Test]
        public void NewerProfileJsonVersion_UsesSafeFallback_NeverTakesCloudOverDirtyLocal()
        {
            var slot = new ProfileSlot();
            TestServiceContext context = CreateContext(slot);
            TestServiceContext restarted = null;
            try
            {
                string cloudVersion = UploadThenLeaveUnsyncedLocalChange(context, slot);
                string statePath = TestPaths.ProfileState(ProfileA);

                var reloaded = new ProfileSlot();
                reloaded.ConflictResolver = (in ConflictContext<ProfileData> conflict) => ConflictResolution<ProfileData>.KeepLocal;
                restarted = TestServiceFactory.Restart(context, reloaded);

                // Same document, written by a build that knows one more field and a higher version
                var document = (JObject)SaveJson.Parse(restarted.Storage.GetBytes(statePath));
                document[StateFileReader.VersionProperty] = ProfileSyncState.CurrentVersion + 1;
                document["futureSlotHints"] = new JObject { { TestSlotKeys.Player, "unknown to this build" } };
                restarted.Storage.SetBytes(statePath, SaveJson.ToUtf8Bytes(document));
                InitializeAndActivate(restarted, AccountA);

                SyncStateLoadResult state = ReadSyncState(restarted, ProfileA);
                Assert.That(state.Status, Is.EqualTo(SyncStateLoadStatus.NewerVersion), state.Message);
                Assert.That(state.FoundVersion, Is.EqualTo(ProfileSyncState.CurrentVersion + 1));
                Assert.That(state.UsedSafeFallback, Is.True);
                Assert.That(state.State.PeekSlot(TestSlotKeys.Player).HadContent, Is.True);
                Assert.That(state.State.PeekSlot(TestSlotKeys.Player).LastSyncedWriteId, Is.Null);
                Assert.That(restarted.Storage.GetText(statePath), Does.Contain("futureSlotHints"), "A newer file is not rewritten while it is only read.");
                Assert.That(restarted.Logger.Contains(TestLogLevel.Warning, "has version " + (ProfileSyncState.CurrentVersion + 1)), Is.True, restarted.Logger.Describe());
                Assert.That(reloaded.PeekData().Coins, Is.EqualTo(6), "Premise: the unsynced local change is loaded.");
                restarted.Provider.ClearCalls();

                RestoreReport report = RestoreSync(restarted);

                Assert.That(report.Status, Is.EqualTo(SaveStatus.Success), report.ToString());
                Assert.That(reloaded.ResolveConflictCount, Is.EqualTo(1), report.ToString());
                Assert.That(report.GetResult(reloaded).Outcome, Is.EqualTo(SlotRestoreOutcome.LocalKept), report.ToString());
                Assert.That(reloaded.PeekData().Coins, Is.EqualTo(6), "Dirty local data is never replaced by the cloud value.");
                JObject conflict = ReadConflictFile(restarted, TestSlotKeys.Player);
                Assert.That(ReadInt(conflict["local"], "Coins"), Is.EqualTo(6), conflict.ToString());
                Assert.That(ReadInt(conflict["cloud"], "Coins"), Is.EqualTo(5), conflict.ToString());
                Assert.That(restarted.Provider.WriteCallCount, Is.EqualTo(0), "Nothing is uploaded during the restore.");
                Assert.That(restarted.Provider.DeleteCallCount, Is.EqualTo(0), "A newer metadata version never deletes cloud data.");
                Assert.That(restarted.Provider.Store.GetVersion(AccountA, TestSlotKeys.Player), Is.EqualTo(cloudVersion));
            }
            finally
            {
                context.Dispose();
                restarted?.Dispose();
            }
        }

        // ADR-003 4: a cloud value whose envelope carries no write id cannot claim to be our own write
        [Test]
        public void CloudEnvelopeWithoutWriteId_IsUnknownProvenance_NotTakeCloudOverDirty()
        {
            var slot = new ProfileSlot();
            using (TestServiceContext context = CreateContext(slot))
            {
                ActivateReconciledAccount(context, AccountA);
                Assert.That(slot.Mutate(data => data.Coins = 5), Is.True);
                Assert.That(context.FlushSync().IsComplete, Is.True);

                // A writer that does not journal write ids replaces the value; the data bytes and the checksum stay intact
                string text = context.Provider.Store.GetText(AccountA, TestSlotKeys.Player);
                string writeIdProperty = "\"" + SaveEnvelope.WriteIdProperty + "\":\"" + ReadWriteId(context.Provider.Store.GetValue(AccountA, TestSlotKeys.Player)) + "\",";
                Assert.That(text, Does.Contain(writeIdProperty), "Premise: the compact envelope names the write id.");
                context.Provider.Store.PutText(AccountA, TestSlotKeys.Player, text.Replace(writeIdProperty, string.Empty));
                Assert.That(context.Provider.Store.GetText(AccountA, TestSlotKeys.Player), Does.Not.Contain(SaveEnvelope.WriteIdProperty));
                string cloudVersion = context.Provider.Store.GetVersion(AccountA, TestSlotKeys.Player);

                // Unsynced local progress meets that value
                Assert.That(slot.Mutate(data => data.Coins = 6), Is.True);
                Assert.That(context.Service.FlushLocalNow().IsComplete, Is.True);
                slot.ConflictResolver = (in ConflictContext<ProfileData> conflict) => ConflictResolution<ProfileData>.KeepLocal;
                context.Provider.ClearCalls();

                RestoreReport report = RestoreSync(context);

                Assert.That(report.Status, Is.EqualTo(SaveStatus.Success), report.ToString());
                Assert.That(report.GetResult(slot).Outcome, Is.Not.EqualTo(SlotRestoreOutcome.UpToDate), "A value without a write id is not our own write. " + report);
                Assert.That(slot.ResolveConflictCount, Is.EqualTo(1), report.ToString());
                Assert.That(report.GetResult(slot).Outcome, Is.EqualTo(SlotRestoreOutcome.LocalKept), report.ToString());
                Assert.That(slot.PeekData().Coins, Is.EqualTo(6), "Dirty local data is not replaced.");
                JObject conflict = ReadConflictFile(context, TestSlotKeys.Player);
                Assert.That(ReadInt(conflict["local"], "Coins"), Is.EqualTo(6), conflict.ToString());
                Assert.That(ReadInt(conflict["cloud"], "Coins"), Is.EqualTo(5), conflict.ToString());
                Assert.That(context.Provider.WriteCallCount, Is.EqualTo(0), "Nothing is uploaded during the restore.");
                Assert.That(context.Provider.Store.GetVersion(AccountA, TestSlotKeys.Player), Is.EqualTo(cloudVersion));

                // The resolved value supersedes exactly the cloud value it was resolved against
                FlushResult uploaded = context.FlushSync();

                Assert.That(uploaded.IsComplete, Is.True, uploaded.ToString());
                IReadOnlyList<CloudWriteRequest> writes = context.Provider.GetAllWriteRequests();
                Assert.That(writes.Count, Is.EqualTo(1));
                Assert.That(writes[0].ExpectedVersion, Is.EqualTo(cloudVersion));
            }
        }

        [UnityTest]
        public IEnumerator ConditionalWriteSupported_EveryWriteOverAKnownCloudValueCarriesExpectedVersion()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                var slot = new ProfileSlot();
                using (TestServiceContext context = CreateContext(slot))
                {
                    Assert.That(context.Provider.Capabilities.SupportsConditionalWrite, Is.True);
                    var other = new ProfileSlot();
                    UploadFromOtherDevice(context.Provider.Store, () => Assert.That(other.Mutate(data => data.Coins = 1), Is.True), other);
                    InitializeAndActivate(context, AccountA);
                    RestoreReport restored = RestoreSync(context);
                    Assert.That(restored.GetResult(slot).Outcome, Is.EqualTo(SlotRestoreOutcome.Restored), restored.ToString());
                    context.Provider.ClearCalls();

                    var expected = new List<string>();
                    for (int i = 0; i < 2; i++)
                    {
                        expected.Add(context.Provider.Store.GetVersion(AccountA, TestSlotKeys.Player));
                        int coins = 10 + i;
                        Assert.That(slot.Mutate(data => data.Coins = coins), Is.True);
                        FlushResult flushed = context.FlushSync();
                        Assert.That(flushed.IsComplete, Is.True, flushed.ToString());
                    }

                    // Scheduled (debounced) path
                    expected.Add(context.Provider.Store.GetVersion(AccountA, TestSlotKeys.Player));
                    Assert.That(slot.Mutate(data => data.Coins = 20), Is.True);
                    context.Clock.Advance(context.Options.CloudDebounce + TimeSpan.FromMilliseconds(100));
                    await AsyncTestUtility.WaitUntilAsync(() => context.Provider.WriteCallCount == 3, MaxFrames, "Scheduled upload");
                    await AsyncTestUtility.WaitFramesAsync(2);

                    IReadOnlyList<CloudWriteRequest> writes = context.Provider.GetAllWriteRequests();
                    Assert.That(writes.Count, Is.EqualTo(3));
                    Assert.That(writes.All(write => write.ExpectedVersion != null), Is.True, string.Join(", ", writes.Select(write => write.ExpectedVersion ?? "<null>")));
                    Assert.That(writes.Select(write => write.ExpectedVersion), Is.EqualTo(expected), "Each write expects the version it replaces.");
                }
            });
        }

        // R9: a never-synced slot re-reads its key before the first upload
        [Test]
        public void FirstUploadOverMissingCloudValue_ValueCreatedElsewhere_IsNotOverwritten()
        {
            var slot = new ProfileSlot();
            using (TestServiceContext context = CreateContext(slot))
            {
                ActivateReconciledAccount(context, AccountA);

                // Another device creates the value after this device reconciled against a missing cloud value
                var other = new ProfileSlot();
                UploadFromOtherDevice(context.Provider.Store, () => Assert.That(other.Mutate(data => data.Coins = 9), Is.True), other);
                string otherVersion = context.Provider.Store.GetVersion(AccountA, TestSlotKeys.Player);
                Assert.That(otherVersion, Is.Not.Null);

                Assert.That(slot.Mutate(data => data.Coins = 5), Is.True);
                FlushResult result = context.FlushSync();

                Assert.That(context.Provider.GetAllWriteRequests().Count, Is.EqualTo(0), result.ToString());
                Assert.That(context.Provider.Store.GetVersion(AccountA, TestSlotKeys.Player), Is.EqualTo(otherVersion), "The other device's first save is kept.");
                Assert.That(result.IsComplete, Is.False, result.ToString());
                CloudFlushSlotResult slotResult = result.Cloud.First(item => item.SlotKey == TestSlotKeys.Player);
                Assert.That(slotResult.Status, Is.EqualTo(CloudFlushStatus.Skipped), result.ToString());
                Assert.That(slotResult.Reason, Is.EqualTo(CloudFlushReason.NotReconciled), result.ToString());
            }
        }

        [Test]
        public void ProbeLocalPresenceAndGetLocalProfiles_PerformNoStorageWrites()
        {
            var slot = new ProfileSlot();
            using (TestServiceContext context = CreateContext(slot))
            {
                ProfileId offline = ProfileId.Local("offline");
                Assert.That(context.InitializeSync().IsSuccess, Is.True);
                Assert.That(context.ActivateSync(offline).IsSuccess, Is.True);
                Assert.That(slot.Mutate(data => data.Coins = 1), Is.True);
                Assert.That(context.Service.FlushLocalNow().IsComplete, Is.True);

                context.Provider.SignedInAccountId = AccountA;
                Assert.That(context.ActivateSync(ProfileA).IsSuccess, Is.True);
                Assert.That(slot.Mutate(data => data.Coins = 2), Is.True);
                Assert.That(context.Service.FlushLocalNow().IsComplete, Is.True);
                Assert.That(context.ActivateSync(ProfileId.Guest).IsSuccess, Is.True);

                // States a repairing read would touch: a stray tmp and a corrupt primary
                string offlineSlot = TestPaths.ProfileSlot(offline, TestSlotKeys.Player);
                string accountSlot = TestPaths.ProfileSlot(ProfileA, TestSlotKeys.Player);
                context.Storage.SetText(TestPaths.Tmp(offlineSlot), "{ \"fmt\": 1 ");
                context.Storage.SetText(accountSlot, "{ \"fmt\": 1, \"data\": ");

                string[] filesBefore = context.Storage.AllFilePaths.OrderBy(path => path, StringComparer.Ordinal).ToArray();
                Dictionary<string, byte[]> bytesBefore = filesBefore.ToDictionary(path => path, path => context.Storage.GetBytes(path), StringComparer.Ordinal);
                context.Storage.ResetCounters();

                context.Service.ProbeLocalPresence(ProfileA);
                LocalPresence offlinePresence = context.Service.ProbeLocalPresence(offline);
                LocalPresence missingPresence = context.Service.ProbeLocalPresence(ProfileId.Local("missing"));
                context.Service.ProbeLocalPresence(ProfileId.Guest);
                IReadOnlyList<ProfileId> profiles = context.Service.GetLocalProfiles();

                Assert.That(context.Storage.MutationCallCount, Is.EqualTo(0), string.Join("\n", context.Storage.Calls));
                Assert.That(context.Storage.WriteAttemptCount, Is.EqualTo(0));
                string[] filesAfter = context.Storage.AllFilePaths.OrderBy(path => path, StringComparer.Ordinal).ToArray();
                Assert.That(filesAfter, Is.EqualTo(filesBefore));
                foreach (string path in filesAfter)
                {
                    Assert.That(context.Storage.GetBytes(path), Is.EqualTo(bytesBefore[path]), path);
                }

                // Sanity only; the invariant is the zero writes above
                Assert.That(offlinePresence, Is.EqualTo(LocalPresence.Present));
                Assert.That(missingPresence, Is.EqualTo(LocalPresence.Absent));
                Assert.That(profiles, Does.Contain(offline));
            }
        }

        private static void AssertWriteFailureKeepsSyncedState(Action<FakeCloudSaveProvider> injectFailure)
        {
            var slot = new ProfileSlot();
            using (TestServiceContext context = CreateContext(slot))
            {
                ActivateReconciledAccount(context, AccountA);
                Assert.That(slot.Mutate(data => data.Coins = 5), Is.True);
                Assert.That(context.FlushSync().IsComplete, Is.True);

                SlotSyncState before = LoadSyncState(context, ProfileA).PeekSlot(TestSlotKeys.Player);
                long syncedRevision = before.LastSyncedRevision;
                string syncedWriteId = before.LastSyncedWriteId;
                string cloudVersion = context.Provider.Store.GetVersion(AccountA, TestSlotKeys.Player);
                Assert.That(syncedWriteId, Is.Not.Null, "Premise: a synced write exists.");

                int writesBefore = context.Provider.WriteCallCount;
                injectFailure(context.Provider);
                Assert.That(slot.Mutate(data => data.Coins = 6), Is.True);
                FlushResult result = context.FlushSync();

                Assert.That(result.IsComplete, Is.False, result.ToString());
                Assert.That(context.Provider.WriteCallCount, Is.EqualTo(writesBefore + 1), "Premise: the provider was called.");

                SlotSyncState after = LoadSyncState(context, ProfileA).PeekSlot(TestSlotKeys.Player);
                Assert.That(after.LastSyncedRevision, Is.EqualTo(syncedRevision), result.ToString());
                Assert.That(after.LastSyncedWriteId, Is.EqualTo(syncedWriteId), result.ToString());
                Assert.That(slot.Revision, Is.GreaterThan(after.LastSyncedRevision), "The change still counts as unsynced.");
                Assert.That(context.Provider.Store.GetVersion(AccountA, TestSlotKeys.Player), Is.EqualTo(cloudVersion));
            }
        }

        // Uploads coins 5, then deletes LocalAndCloud while the cloud delete fails, leaving a tombstone; returns its write id
        private static async UniTask<string> UploadThenDeleteWithFailedCloudDeleteAsync(TestServiceContext context, ProfileSlot slot)
        {
            ActivateReconciledAccount(context, AccountA);
            Assert.That(slot.Mutate(data => data.Coins = 5), Is.True);
            Assert.That(context.FlushSync().IsComplete, Is.True);
            string uploadedWriteId = LoadSyncState(context, ProfileA).PeekSlot(TestSlotKeys.Player).LastSyncedWriteId;
            Assert.That(uploadedWriteId, Is.Not.Null);

            context.Provider.EnqueueError(CloudOperation.Delete, TestSlotKeys.Player, CloudErrorKind.Permanent);
            DeleteResult deleted = await context.Service.DeleteSlotAsync(slot, DeleteTarget.LocalAndCloud, CancellationToken.None);

            Assert.That(context.Provider.DeleteCallCount, Is.EqualTo(1), deleted.ToString());
            Assert.That(context.Provider.Store.Contains(AccountA, TestSlotKeys.Player), Is.True, "Premise: the cloud delete failed.");
            SlotSyncState tombstone = LoadSyncState(context, ProfileA).PeekSlot(TestSlotKeys.Player);
            Assert.That(tombstone.PendingDelete, Is.True, "Premise: the tombstone is kept.");
            Assert.That(tombstone.DeletedWriteId, Is.EqualTo(uploadedWriteId));
            return uploadedWriteId;
        }

        // Account A: coins 5 uploaded, then coins 6 written locally only; returns the cloud provider version
        private static string UploadThenLeaveUnsyncedLocalChange(TestServiceContext context, ProfileSlot slot)
        {
            ActivateReconciledAccount(context, AccountA);
            Assert.That(slot.Mutate(data => data.Coins = 5), Is.True);
            Assert.That(context.FlushSync().IsComplete, Is.True);
            string version = context.Provider.Store.GetVersion(AccountA, TestSlotKeys.Player);
            Assert.That(version, Is.Not.Null);
            Assert.That(LoadSyncState(context, ProfileA).PeekSlot(TestSlotKeys.Player).LastSyncedWriteId, Is.Not.Null, "Premise: the write id is journaled.");

            Assert.That(slot.Mutate(data => data.Coins = 6), Is.True);
            Assert.That(context.Service.FlushLocalNow().IsComplete, Is.True);
            return version;
        }

        // Reads profile.json directly without repairing anything; any load status is returned
        private static SyncStateLoadResult ReadSyncState(TestServiceContext context, ProfileId profile)
        {
            var store = new SyncStateStore(context.Storage, new TestSaveLogger(), context.Clock);
            return store.Load(TestPaths.ProfileDirectory(profile), SlotReadMode.ReadOnly);
        }

        private static JObject ReadConflictFile(TestServiceContext context, string key)
        {
            string path = SaveLayout.ConflictPath(TestPaths.ProfileDirectory(ProfileA), key);
            Assert.That(context.Storage.HasFile(path), Is.True, string.Join(", ", context.Storage.AllFilePaths));
            return (JObject)SaveJson.Parse(context.Storage.GetBytes(path));
        }

        // Property names depend on the serializer settings, so match case-insensitively
        private static int ReadInt(JToken token, string property)
        {
            Assert.That(token, Is.InstanceOf<JObject>(), token?.ToString());
            JToken value = ((JObject)token).GetValue(property, StringComparison.OrdinalIgnoreCase);
            Assert.That(value, Is.Not.Null, token.ToString());
            return value.Value<int>();
        }

        private static bool IsVisibleOutsideAssembly(MemberInfo member)
        {
            switch (member)
            {
                case MethodBase method:
                    return method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly;
                case FieldInfo field:
                    return field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly;
                case PropertyInfo property:
                    return property.GetAccessors(true).Any(accessor => IsVisibleOutsideAssembly(accessor));
                case EventInfo eventInfo:
                    MethodInfo add = eventInfo.GetAddMethod(true);
                    return add != null && IsVisibleOutsideAssembly(add);
                case Type nested:
                    return nested.IsNestedPublic || nested.IsNestedFamily || nested.IsNestedFamORAssem;
                default:
                    return false;
            }
        }

        private static void AddIfForbidden(List<string> offenders, string label, string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return;
            }

            foreach (string part in ForbiddenNameParts)
            {
                if (name.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    offenders.Add(label);
                    return;
                }
            }
        }

        private static string ReadWriteId(byte[] envelopeBytes)
        {
            var envelope = SaveJson.Parse(envelopeBytes) as JObject;
            Assert.That(envelope, Is.Not.Null, "The value is an envelope object.");
            return envelope.Value<string>(SaveEnvelope.WriteIdProperty);
        }

        private static RestoreReport RestoreSync(TestServiceContext context)
        {
            return AsyncTestUtility.RunSync(context.Service.RestoreAsync(CancellationToken.None), nameof(SaveService.RestoreAsync));
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
    }
}
