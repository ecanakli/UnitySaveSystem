using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ecanakli.SaveSystem.Tests
{
    /// <summary>profile.json and device.json persistence: round trips, safe fallbacks, quarantine and newer versions.</summary>
    [TestFixture]
    public sealed class SyncStateStoreTests
    {
        private const string ProfileDirectory = "profiles/acc-account_1";
        private const string AccountId = "account_1";

        private InMemorySaveStorage _storage;
        private TestSaveLogger _logger;
        private ManualSaveClock _clock;
        private SyncStateStore _store;
        private DeviceStateStore _deviceStore;

        private static string StatePath => SaveLayout.ProfileStatePath(ProfileDirectory);

        [SetUp]
        public void SetUp()
        {
            _storage = new InMemorySaveStorage();
            _logger = new TestSaveLogger();
            _clock = new ManualSaveClock();
            _store = new SyncStateStore(_storage, _logger, _clock);
            _deviceStore = new DeviceStateStore(_storage, _logger, _clock);
        }

        [Test]
        public void RoundTrip_AllFieldsIncludingTombstone()
        {
            var state = new ProfileSyncState { OwnerAccountId = AccountId };
            SlotSyncState player = state.GetOrCreateSlot("player");
            player.HadContent = true;
            player.LastSyncedRevision = 5;
            player.LastSyncedWriteId = "write-5";
            player.LastSyncedProviderVersion = "v5";
            player.PendingWriteId = "write-6";
            player.NeedsCloudRecovery = true;
            player.LastUploadedBytes = 1234;
            state.GetOrCreateSlot("inventory").SetTombstone("write-9", "v9");
            state.GetOrCreateSlot("rewards").SetTombstone(null, null);

            StateWriteResult saved = _store.Save(ProfileDirectory, state);
            SyncStateLoadResult loaded = _store.Load(ProfileDirectory);

            Assert.That(saved.IsSuccess, Is.True, saved.Message);
            Assert.That(loaded.Status, Is.EqualTo(SyncStateLoadStatus.Loaded));
            Assert.That(loaded.UsedSafeFallback, Is.False);
            Assert.That(loaded.PrimaryWasCorrupt, Is.False);
            Assert.That(loaded.FoundVersion, Is.EqualTo(ProfileSyncState.CurrentVersion));
            Assert.That(loaded.CanOverwrite, Is.True);
            Assert.That(loaded.CreateIssue(ProfileId.Account(AccountId)), Is.Null);

            ProfileSyncState result = loaded.State;
            Assert.That(result.OwnerAccountId, Is.EqualTo(AccountId));
            Assert.That(result.UnknownSlotsHadContent, Is.False);
            Assert.That(result.LoadedVersion, Is.EqualTo(ProfileSyncState.CurrentVersion));
            Assert.That(result.Slots.Count, Is.EqualTo(3));

            Assert.That(result.TryGetSlot("PLAYER", out SlotSyncState loadedPlayer), Is.True, "Slot lookup is case-insensitive.");
            Assert.That(loadedPlayer.HadContent, Is.True);
            Assert.That(loadedPlayer.LastSyncedRevision, Is.EqualTo(5));
            Assert.That(loadedPlayer.LastSyncedWriteId, Is.EqualTo("write-5"));
            Assert.That(loadedPlayer.LastSyncedProviderVersion, Is.EqualTo("v5"));
            Assert.That(loadedPlayer.PendingWriteId, Is.EqualTo("write-6"));
            Assert.That(loadedPlayer.NeedsCloudRecovery, Is.True);
            Assert.That(loadedPlayer.LastUploadedBytes, Is.EqualTo(1234));
            Assert.That(loadedPlayer.PendingDelete, Is.False);

            Assert.That(result.TryGetSlot("inventory", out SlotSyncState inventory), Is.True);
            Assert.That(inventory.PendingDelete, Is.True);
            Assert.That(inventory.DeletedWriteId, Is.EqualTo("write-9"));
            Assert.That(inventory.DeletedProviderVersion, Is.EqualTo("v9"));
            Assert.That(inventory.HadContent, Is.False);

            Assert.That(result.TryGetSlot("rewards", out SlotSyncState rewards), Is.True);
            Assert.That(rewards.PendingDelete, Is.True);
            Assert.That(rewards.DeletedWriteId, Is.Null);
        }

        // B2: two uploads in a row can be unconfirmed, so every candidate write id has to survive a restart
        [Test]
        public void RoundTrip_SeveralUnconfirmedWriteIds_NewestFirst()
        {
            var state = new ProfileSyncState();
            SlotSyncState player = state.GetOrCreateSlot("player");
            player.PendingWriteId = "write-1";
            player.PendingWriteId = "write-2";

            Assert.That(_store.Save(ProfileDirectory, state).IsSuccess, Is.True);
            var root = (JObject)SaveJson.Parse(_storage.GetBytes(StatePath));
            SyncStateLoadResult loaded = _store.Load(ProfileDirectory);

            var slot = (JObject)root["slots"]["player"];
            Assert.That(slot.Value<string>("pendingWriteId"), Is.EqualTo("write-2"), "Older builds read the newest id from its own property.");
            Assert.That(slot["pendingWriteIds"].Values<string>(), Is.EqualTo(new[] { "write-2", "write-1" }), root.ToString());

            SlotSyncState result = loaded.State.PeekSlot("player");
            Assert.That(result.PendingWriteId, Is.EqualTo("write-2"));
            Assert.That(result.PendingWriteIds, Is.EqualTo(new[] { "write-2", "write-1" }));
            Assert.That(result.IsPendingWriteId("write-1"), Is.True);
        }

        [Test]
        public void RoundTrip_SingleUnconfirmedWriteId_WritesNoArray()
        {
            var state = new ProfileSyncState();
            state.GetOrCreateSlot("player").PendingWriteId = "write-1";

            Assert.That(_store.Save(ProfileDirectory, state).IsSuccess, Is.True);
            var root = (JObject)SaveJson.Parse(_storage.GetBytes(StatePath));

            Assert.That(((JObject)root["slots"]["player"]).ContainsKey("pendingWriteIds"), Is.False, root.ToString());
            Assert.That(_store.Load(ProfileDirectory).State.PeekSlot("player").PendingWriteIds, Is.EqualTo(new[] { "write-1" }));
        }

        // A file written before the ring existed carries the single value only
        [Test]
        public void Load_SinglePendingWriteIdWithoutArray_IsOneEntryRing()
        {
            _storage.SetText(StatePath, "{\"version\":1,\"slots\":{\"player\":{\"pendingWriteId\":\"write-1\"}}}");

            SyncStateLoadResult loaded = _store.Load(ProfileDirectory);

            Assert.That(loaded.Status, Is.EqualTo(SyncStateLoadStatus.Loaded));
            SlotSyncState player = loaded.State.PeekSlot("player");
            Assert.That(player.PendingWriteId, Is.EqualTo("write-1"));
            Assert.That(player.PendingWriteIds, Is.EqualTo(new[] { "write-1" }));
            Assert.That(player.IsPendingWriteId("write-2"), Is.False);
        }

        // An empty array is not "nothing pending": it must not erase the id the legacy property carries
        [Test]
        public void Load_EmptyPendingWriteIdsArray_KeepsTheLegacySingleId()
        {
            _storage.SetText(StatePath, "{\"version\":1,\"slots\":{\"player\":{\"pendingWriteId\":\"a\",\"pendingWriteIds\":[]}}}");

            SyncStateLoadResult loaded = _store.Load(ProfileDirectory);

            Assert.That(loaded.Status, Is.EqualTo(SyncStateLoadStatus.Loaded));
            SlotSyncState player = loaded.State.PeekSlot("player");
            Assert.That(player.PendingWriteId, Is.EqualTo("a"));
            Assert.That(player.PendingWriteIds, Is.EqualTo(new[] { "a" }));
            Assert.That(player.IsPendingWriteId("a"), Is.True);
        }

        [Test]
        public void Load_PendingWriteIdsWithoutTheLegacyProperty_KeepsTheRing()
        {
            _storage.SetText(StatePath, "{\"version\":1,\"slots\":{\"player\":{\"pendingWriteIds\":[\"b\",\"a\"]}}}");

            SlotSyncState player = _store.Load(ProfileDirectory).State.PeekSlot("player");

            Assert.That(player.PendingWriteIds, Is.EqualTo(new[] { "b", "a" }));
        }

        [Test]
        public void Load_MorePendingWriteIdsThanTheCap_KeepsTheNewestOnes()
        {
            _storage.SetText(
                StatePath, "{\"version\":1,\"slots\":{\"player\":{\"pendingWriteId\":\"d\",\"pendingWriteIds\":[\"d\",\"c\",\"b\",\"a\"]}}}");

            SlotSyncState player = _store.Load(ProfileDirectory).State.PeekSlot("player");

            Assert.That(player.PendingWriteIds.Count, Is.EqualTo(SlotSyncState.MaxPendingWriteIds));
            Assert.That(player.PendingWriteIds, Is.EqualTo(new[] { "d", "c", "b" }), "Newest first, the oldest entry beyond the cap is dropped.");
            Assert.That(player.IsPendingWriteId("a"), Is.False);
        }

        [Test]
        public void Load_DuplicatePendingWriteIds_AreDedupedNewestFirst()
        {
            _storage.SetText(StatePath, "{\"version\":1,\"slots\":{\"player\":{\"pendingWriteId\":\"b\",\"pendingWriteIds\":[\"b\",\"a\",\"b\"]}}}");

            SlotSyncState player = _store.Load(ProfileDirectory).State.PeekSlot("player");

            Assert.That(player.PendingWriteIds, Is.EqualTo(new[] { "b", "a" }));
            Assert.That(player.PendingWriteId, Is.EqualTo("b"));
        }

        [Test]
        public void PendingWriteIds_ConfirmDropAndCap()
        {
            var state = new SlotSyncState();
            for (int i = 1; i <= SlotSyncState.MaxPendingWriteIds + 1; i++)
            {
                state.PendingWriteId = "write-" + i;
            }

            Assert.That(state.PendingWriteIds.Count, Is.EqualTo(SlotSyncState.MaxPendingWriteIds), "The oldest id is dropped at the cap.");
            Assert.That(state.IsPendingWriteId("write-1"), Is.False);
            Assert.That(state.PendingWriteId, Is.EqualTo("write-4"));

            Assert.That(state.DropPendingWriteId("write-4"), Is.True, "A refused write leaves the older candidates alone.");
            Assert.That(state.PendingWriteId, Is.EqualTo("write-3"));
            Assert.That(state.PendingWriteIds, Is.EqualTo(new[] { "write-3", "write-2" }));

            Assert.That(state.ConfirmPendingWriteId("write-3"), Is.True, "A confirmed write settles every older one.");
            Assert.That(state.PendingWriteIds, Is.Empty);
            Assert.That(state.PendingWriteId, Is.Null);
            Assert.That(state.DropPendingWriteId("write-3"), Is.False);
        }

        [Test]
        public void RoundTrip_UnknownSlotsHadContent_Persisted()
        {
            Assert.That(_store.Save(ProfileDirectory, ProfileSyncState.CreateSafeFallback()).IsSuccess, Is.True);

            SyncStateLoadResult loaded = _store.Load(ProfileDirectory);

            Assert.That(loaded.Status, Is.EqualTo(SyncStateLoadStatus.Loaded));
            Assert.That(loaded.State.UnknownSlotsHadContent, Is.True);
            Assert.That(loaded.State.PeekSlot("never_seen").HadContent, Is.True);
        }

        [Test]
        public void RoundTrip_ClearedTombstone_IsNotWritten()
        {
            var state = new ProfileSyncState();
            SlotSyncState slot = state.GetOrCreateSlot("player");
            slot.SetTombstone("write-1", "v1");
            slot.ClearTombstone();

            Assert.That(_store.Save(ProfileDirectory, state).IsSuccess, Is.True);
            var root = (JObject)SaveJson.Parse(_storage.GetBytes(StatePath));
            SyncStateLoadResult loaded = _store.Load(ProfileDirectory);

            Assert.That((int)root["version"], Is.EqualTo(ProfileSyncState.CurrentVersion));
            Assert.That(((JObject)root["slots"]["player"]).ContainsKey("tombstone"), Is.False);
            Assert.That(loaded.State.PeekSlot("player").PendingDelete, Is.False);
        }

        [Test]
        public void Load_Missing_FreshStateWithoutFallback()
        {
            SyncStateLoadResult loaded = _store.Load(ProfileDirectory);

            Assert.That(loaded.Status, Is.EqualTo(SyncStateLoadStatus.Missing));
            Assert.That(loaded.UsedSafeFallback, Is.False);
            Assert.That(loaded.CanOverwrite, Is.True);
            Assert.That(loaded.State, Is.Not.Null);
            Assert.That(loaded.State.UnknownSlotsHadContent, Is.False);
            Assert.That(loaded.State.PeekSlot("player").HadContent, Is.False);
            Assert.That(_storage.MutationCallCount, Is.EqualTo(0));
        }

        [Test]
        public void Load_CorruptPrimary_SafeFallbackAndQuarantine()
        {
            byte[] corrupt = Utf8("{\"version\":1,\"slots\":");
            _storage.SetBytes(StatePath, corrupt);

            SyncStateLoadResult loaded = _store.Load(ProfileDirectory);

            string quarantine = SaveLayout.QuarantineFileName("profile", _clock.UtcNow);
            Assert.That(loaded.Status, Is.EqualTo(SyncStateLoadStatus.Corrupt));
            Assert.That(loaded.UsedSafeFallback, Is.True);
            Assert.That(loaded.PrimaryWasCorrupt, Is.True);
            Assert.That(loaded.CanOverwrite, Is.True);
            Assert.That(loaded.State.UnknownSlotsHadContent, Is.True);
            Assert.That(loaded.State.PeekSlot("player").HadContent, Is.True);
            Assert.That(loaded.State.PeekSlot("player").LastSyncedWriteId, Is.Null);
            Assert.That(loaded.State.GetOrCreateSlot("inventory").HadContent, Is.True);
            Assert.That(loaded.QuarantineFileName, Is.EqualTo(quarantine));
            Assert.That(_storage.GetBytes(SaveLayout.Combine(ProfileDirectory, quarantine)), Is.EqualTo(corrupt));
            Assert.That(_storage.HasFile(StatePath), Is.False);

            SlotLoadIssue issue = loaded.CreateIssue(ProfileId.Account(AccountId));
            Assert.That(issue, Is.Not.Null);
            Assert.That(issue.Kind, Is.EqualTo(SlotLoadIssueKind.SyncStateCorrupt));
            Assert.That(issue.Cause, Is.EqualTo(CorruptionCause.ParseFailed));
            Assert.That(issue.BackupFileName, Is.EqualTo(quarantine));
            Assert.That(_logger.Count(TestLogLevel.Error), Is.GreaterThanOrEqualTo(1));
        }

        [TestCase("{\"version\":1,\"slots\":{\"player\":{\"hadContent\":\"yes\"}}}")]
        [TestCase("{\"version\":1,\"slots\":{\"bad key!\":{}}}")]
        [TestCase("{\"version\":1,\"slots\":{\"player\":5}}")]
        [TestCase("{\"version\":1,\"slots\":{\"player\":{\"lastSyncedRevision\":-1}}}")]
        [TestCase("{\"version\":1,\"slots\":{\"player\":{\"pendingWriteIds\":\"write-1\"}}}")]
        [TestCase("{\"version\":1,\"slots\":{\"player\":{\"pendingWriteIds\":[1]}}}")]
        [TestCase("{\"version\":1,\"slots\":{\"player\":{\"pendingWriteIds\":[\"\"]}}}")]
        [TestCase("{\"slots\":{}}")]
        [TestCase("{\"version\":0}")]
        [TestCase("{\"version\":\"1\"}")]
        [TestCase("[]")]
        public void Load_KnownVersionMalformed_IsCorruptWithSafeFallback(string json)
        {
            _storage.SetText(StatePath, json);

            SyncStateLoadResult loaded = _store.Load(ProfileDirectory);

            Assert.That(loaded.Status, Is.EqualTo(SyncStateLoadStatus.Corrupt));
            Assert.That(loaded.UsedSafeFallback, Is.True);
            Assert.That(loaded.State.UnknownSlotsHadContent, Is.True);
        }

        [Test]
        public void Load_NewerVersion_QuarantinesTheFileVerbatim_BeforeThisBuildRewritesIt()
        {
            byte[] bytes = Utf8("{\"version\":2,\"ownerAccountId\":\"account_1\",\"futureField\":{\"x\":1},\"slots\":{}}");
            _storage.SetBytes(StatePath, bytes);
            string quarantine = SaveLayout.NewerVersionFileName("profile", _clock.UtcNow);

            SyncStateLoadResult loaded = _store.Load(ProfileDirectory);

            Assert.That(loaded.Status, Is.EqualTo(SyncStateLoadStatus.NewerVersion));
            Assert.That(loaded.QuarantineFileName, Is.EqualTo(quarantine), "The newer file must be backed up before it can be rewritten.");

            string quarantinePath = SaveLayout.Combine(ProfileDirectory, quarantine);
            Assert.That(_storage.GetBytes(quarantinePath), Is.EqualTo(bytes), "The backup holds the newer build's bytes verbatim.");
            Assert.That(_storage.HasFile(StatePath), Is.True, "The live file stays until this build rewrites it.");

            // A newer build's fields survive in the backup even after this build writes its own format
            Assert.That(_store.Save(ProfileDirectory, loaded.State).IsSuccess, Is.True);
            Assert.That(_storage.GetBytes(quarantinePath), Is.EqualTo(bytes));
        }

        [Test]
        public void Load_ReadOnly_NewerVersion_WritesNoBackup()
        {
            _storage.SetBytes(StatePath, Utf8("{\"version\":2,\"ownerAccountId\":\"account_1\",\"slots\":{}}"));

            SyncStateLoadResult loaded = _store.Load(ProfileDirectory, SlotReadMode.ReadOnly);

            Assert.That(loaded.Status, Is.EqualTo(SyncStateLoadStatus.NewerVersion));
            Assert.That(loaded.QuarantineFileName, Is.Null);
            Assert.That(_storage.WriteCount, Is.EqualTo(0), "A query never writes.");
        }

        [Test]
        public void Load_NewerVersion_SafeFallback_KnownFieldsKept_NotOverwritten()
        {
            byte[] bytes = Utf8(
                "{\"version\":2,\"ownerAccountId\":\"account_1\",\"futureField\":{\"x\":1},\"slots\":{"
                + "\"player\":{\"hadContent\":false,\"lastSyncedRevision\":3,\"lastSyncedWriteId\":\"write-3\",\"pendingWriteId\":\"write-4\","
                + "\"lastUploadedBytes\":\"lots\",\"futureSlotField\":\"x\"},\"bad key!\":{}}}");
            _storage.SetBytes(StatePath, bytes);

            SyncStateLoadResult loaded = _store.Load(ProfileDirectory);

            Assert.That(loaded.Status, Is.EqualTo(SyncStateLoadStatus.NewerVersion));
            Assert.That(loaded.UsedSafeFallback, Is.True);
            Assert.That(loaded.FoundVersion, Is.EqualTo(2));
            Assert.That(loaded.State.LoadedVersion, Is.EqualTo(2));
            Assert.That(loaded.State.OwnerAccountId, Is.EqualTo(AccountId));
            Assert.That(loaded.State.UnknownSlotsHadContent, Is.True);
            Assert.That(loaded.State.Slots.Count, Is.EqualTo(1), "Invalid keys are skipped leniently.");

            SlotSyncState player = loaded.State.PeekSlot("player");
            Assert.That(player.HadContent, Is.True);
            Assert.That(player.LastSyncedWriteId, Is.Null);
            Assert.That(player.LastSyncedRevision, Is.EqualTo(3));
            Assert.That(player.PendingWriteId, Is.EqualTo("write-4"));
            Assert.That(player.LastUploadedBytes, Is.EqualTo(0));

            Assert.That(loaded.CreateIssue(ProfileId.Account(AccountId)), Is.Null);
            Assert.That(_storage.GetBytes(StatePath), Is.EqualTo(bytes), "The live file is left alone until this build writes it.");

            // The only write is the verbatim backup of the newer file
            Assert.That(_storage.MutationCallCount, Is.EqualTo(1));
            Assert.That(_storage.GetBytes(SaveLayout.Combine(ProfileDirectory, loaded.QuarantineFileName)), Is.EqualTo(bytes));
        }

        // A backup that failed must not look like "no backup needed": the live file has no copy anywhere
        [Test]
        public void Load_TheSameNewerVersionTwice_WritesOneBackup()
        {
            _storage.SetBytes(StatePath, Utf8("{\"version\":2,\"ownerAccountId\":\"account_1\",\"futureField\":{\"x\":1},\"slots\":{}}"));

            for (int i = 0; i < 3; i++)
            {
                Assert.That(_store.Load(ProfileDirectory).Status, Is.EqualTo(SyncStateLoadStatus.NewerVersion));
            }

            string[] backups = NewerVersionBackups();
            Assert.That(backups.Length, Is.EqualTo(1), "An identical document is backed up once: " + string.Join(", ", backups));
        }

        // The whole point of the separate family: a rollback must not evict evidence of real corruption
        [Test]
        public void Load_ManyDistinctNewerVersions_DoesNotPruneTheCorruptionQuarantine()
        {
            _storage.SetText(StatePath, "{broken");
            Assert.That(_store.Load(ProfileDirectory).Status, Is.EqualTo(SyncStateLoadStatus.Corrupt));

            string corruptQuarantine = SaveLayout.Combine(ProfileDirectory, SaveLayout.QuarantineFileName("profile", _clock.UtcNow));
            Assert.That(_storage.HasFile(corruptQuarantine), Is.True, "Premise: real corruption evidence exists.");

            for (int i = 0; i < 4; i++)
            {
                _storage.SetBytes(StatePath, Utf8("{\"version\":2,\"ownerAccountId\":\"account_1\",\"futureField\":{\"x\":" + i + "},\"slots\":{}}"));
                Assert.That(_store.Load(ProfileDirectory).Status, Is.EqualTo(SyncStateLoadStatus.NewerVersion));
            }

            Assert.That(_storage.HasFile(corruptQuarantine), Is.True, "The corruption quarantine must survive a rollback.");
            Assert.That(NewerVersionBackups().Length, Is.EqualTo(SaveLayout.NewerVersionCap), "The newer-version family prunes within its own quota.");
        }

        // Copies made while the clock ran ahead sort newest; the next copy must not be pruned by its own write
        [Test]
        public void Load_NewerVersionAfterTheClockWasSetBack_KeepsTheCopyItReports()
        {
            System.DateTime realNow = _clock.UtcNow;
            _clock.SetUtcNow(realNow.AddDays(1));
            for (int i = 0; i < SaveLayout.NewerVersionCap; i++)
            {
                _storage.SetBytes(StatePath, Utf8("{\"version\":2,\"ownerAccountId\":\"account_1\",\"futureField\":{\"x\":" + i + "},\"slots\":{}}"));
                Assert.That(_store.Load(ProfileDirectory).Status, Is.EqualTo(SyncStateLoadStatus.NewerVersion));
            }

            _clock.SetUtcNow(realNow);
            byte[] latest = Utf8("{\"version\":2,\"ownerAccountId\":\"account_1\",\"futureField\":{\"x\":99},\"slots\":{}}");
            _storage.SetBytes(StatePath, latest);

            SyncStateLoadResult loaded = _store.Load(ProfileDirectory);

            Assert.That(loaded.CanOverwrite, Is.True, "Premise: a copy was reported, so this build may rewrite the file.");
            string copy = SaveLayout.Combine(ProfileDirectory, loaded.QuarantineFileName);
            Assert.That(_storage.HasFile(copy), Is.True, "The copy that permits the overwrite must exist.");
            Assert.That(_storage.GetBytes(copy), Is.EqualTo(latest));
            Assert.That(NewerVersionBackups().Length, Is.EqualTo(SaveLayout.NewerVersionCap), "The cap still holds; an older copy goes instead.");
        }

        [Test]
        public void Load_NewerVersion_BackupWriteFails_RefusesOverwrite()
        {
            byte[] bytes = Utf8("{\"version\":2,\"ownerAccountId\":\"account_1\",\"futureField\":{\"x\":1},\"slots\":{}}");
            _storage.SetBytes(StatePath, bytes);
            string quarantine = SaveLayout.Combine(ProfileDirectory, SaveLayout.NewerVersionFileName("profile", _clock.UtcNow));
            _storage.FailWithKind(StorageOperation.Write, quarantine, LocalWriteErrorKind.DiskFull);

            SyncStateLoadResult loaded = _store.Load(ProfileDirectory);

            Assert.That(loaded.Status, Is.EqualTo(SyncStateLoadStatus.NewerVersion));
            Assert.That(loaded.NewerVersionUnbacked, Is.True);
            Assert.That(loaded.QuarantineFileName, Is.Null);
            Assert.That(loaded.CanOverwrite, Is.False, "The newer file has no copy, so this build must not rewrite it.");
            Assert.That(_storage.HasFile(quarantine), Is.False);
            Assert.That(_storage.GetBytes(StatePath), Is.EqualTo(bytes), "The live file is left untouched.");
        }

        // The version check belongs to the document, not to the copy it was read from
        [Test]
        public void Load_CorruptPrimary_NewerVersionBak_IsQuarantinedBeforeTheRewrite()
        {
            byte[] newer = Utf8("{\"version\":2,\"ownerAccountId\":\"account_1\",\"futureField\":{\"x\":1},\"slots\":{}}");
            _storage.SetText(StatePath, "{broken");
            _storage.SetBytes(SaveLayout.BakPath(StatePath), newer);

            SyncStateLoadResult loaded = _store.Load(ProfileDirectory);

            // Its own family, so the corrupt primary's attempt 0 does not push this one to attempt 1
            string bakQuarantine = SaveLayout.Combine(ProfileDirectory, SaveLayout.NewerVersionFileName("profile", _clock.UtcNow));
            Assert.That(loaded.Status, Is.EqualTo(SyncStateLoadStatus.NewerVersion));
            Assert.That(loaded.FoundVersion, Is.EqualTo(2));
            Assert.That(loaded.PrimaryWasCorrupt, Is.True);
            Assert.That(loaded.UsedSafeFallback, Is.True);
            Assert.That(_storage.GetBytes(bakQuarantine), Is.EqualTo(newer), "The newer .bak must be quarantined before this build rewrites it.");
        }

        [Test]
        public void Load_PrimaryMissing_NewerVersionTmp_IsQuarantinedAndNotTrustedAsIs()
        {
            byte[] newer = Utf8("{\"version\":2,\"ownerAccountId\":\"account_1\",\"slots\":{\"player\":{\"lastSyncedWriteId\":\"write-1\"}}}");
            _storage.SetBytes(SaveLayout.TmpPath(StatePath), newer);
            string quarantine = SaveLayout.Combine(ProfileDirectory, SaveLayout.NewerVersionFileName("profile", _clock.UtcNow));

            SyncStateLoadResult loaded = _store.Load(ProfileDirectory);

            Assert.That(loaded.Status, Is.EqualTo(SyncStateLoadStatus.NewerVersion));
            Assert.That(loaded.UsedSafeFallback, Is.True);
            Assert.That(loaded.State.PeekSlot("player").HadContent, Is.True);
            Assert.That(loaded.State.PeekSlot("player").LastSyncedWriteId, Is.Null);
            Assert.That(_storage.GetBytes(quarantine), Is.EqualTo(newer));
        }

        [Test]
        public void Load_RecoveredFromBak_SafeFallback()
        {
            _storage.SetBytes(SaveLayout.BakPath(StatePath), SyncStateStore.Encode(CreateOlderState()));

            SyncStateLoadResult loaded = _store.Load(ProfileDirectory);

            Assert.That(loaded.Status, Is.EqualTo(SyncStateLoadStatus.RecoveredFromBackup));
            Assert.That(loaded.UsedSafeFallback, Is.True);
            Assert.That(loaded.PrimaryWasCorrupt, Is.False);
            Assert.That(loaded.State.OwnerAccountId, Is.EqualTo(AccountId));
            Assert.That(loaded.State.UnknownSlotsHadContent, Is.True);
            SlotSyncState player = loaded.State.PeekSlot("player");
            Assert.That(player.HadContent, Is.True);
            Assert.That(player.LastSyncedWriteId, Is.Null);
            Assert.That(player.LastSyncedRevision, Is.EqualTo(1));
            Assert.That(player.PendingWriteId, Is.EqualTo("write-2"));
            Assert.That(_storage.MutationCallCount, Is.EqualTo(0));
            Assert.That(_logger.Contains(TestLogLevel.Warning, "recovered from an older copy"), Is.True, _logger.Describe());
        }

        [Test]
        public void Load_PrimaryMissing_ValidTmp_UsedAsIs()
        {
            _storage.SetBytes(SaveLayout.TmpPath(StatePath), SyncStateStore.Encode(CreateOlderState()));

            SyncStateLoadResult loaded = _store.Load(ProfileDirectory);

            Assert.That(loaded.Status, Is.EqualTo(SyncStateLoadStatus.RecoveredFromTmp));
            Assert.That(loaded.UsedSafeFallback, Is.False);
            Assert.That(loaded.State.PeekSlot("player").HadContent, Is.False);
            Assert.That(loaded.State.PeekSlot("player").LastSyncedWriteId, Is.EqualTo("write-1"));
        }

        [Test]
        public void Load_CorruptPrimary_ValidTmp_SafeFallback()
        {
            _storage.SetText(StatePath, "{broken");
            _storage.SetBytes(SaveLayout.TmpPath(StatePath), SyncStateStore.Encode(CreateOlderState()));

            SyncStateLoadResult loaded = _store.Load(ProfileDirectory);

            Assert.That(loaded.Status, Is.EqualTo(SyncStateLoadStatus.RecoveredFromBackup));
            Assert.That(loaded.UsedSafeFallback, Is.True);
            Assert.That(loaded.PrimaryWasCorrupt, Is.True);
            Assert.That(loaded.QuarantineFileName, Is.Not.Null);
            Assert.That(loaded.State.PeekSlot("player").HadContent, Is.True);
            Assert.That(loaded.State.PeekSlot("player").LastSyncedWriteId, Is.Null);
            Assert.That(loaded.CreateIssue(ProfileId.Account(AccountId)), Is.Not.Null);
        }

        [Test]
        public void Load_ReadIoError_SafeFallback_CannotOverwrite()
        {
            byte[] bytes = SyncStateStore.Encode(CreateOlderState());
            _storage.SetBytes(StatePath, bytes);
            _storage.FailWithKind(StorageOperation.Read, StatePath, LocalWriteErrorKind.AccessDenied);

            SyncStateLoadResult loaded = _store.Load(ProfileDirectory);

            Assert.That(loaded.Status, Is.EqualTo(SyncStateLoadStatus.IoError));
            Assert.That(loaded.CanOverwrite, Is.False);
            Assert.That(loaded.UsedSafeFallback, Is.True);
            Assert.That(loaded.ErrorKind, Is.EqualTo(LocalWriteErrorKind.AccessDenied));
            Assert.That(loaded.State.UnknownSlotsHadContent, Is.True);
            Assert.That(loaded.CreateIssue(ProfileId.Account(AccountId)).Cause, Is.EqualTo(CorruptionCause.None));
            Assert.That(_storage.GetBytes(StatePath), Is.EqualTo(bytes));
            Assert.That(_storage.MutationCallCount, Is.EqualTo(0));
        }

        [Test]
        public void Load_ReadOnlyMode_CorruptPrimary_NotQuarantined()
        {
            _storage.SetText(StatePath, "{broken");

            SyncStateLoadResult loaded = _store.Load(ProfileDirectory, SlotReadMode.ReadOnly);

            Assert.That(loaded.Status, Is.EqualTo(SyncStateLoadStatus.Corrupt));
            Assert.That(loaded.QuarantineFileName, Is.Null);
            Assert.That(_storage.HasFile(StatePath), Is.True);
            Assert.That(_storage.MutationCallCount, Is.EqualTo(0));
        }

        [Test]
        public void Save_WriteFailure_ReturnsClassifiedError()
        {
            _storage.FailWithKind(StorageOperation.Write, StatePath, LocalWriteErrorKind.DiskFull);

            StateWriteResult result = _store.Save(ProfileDirectory, new ProfileSyncState());

            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.ErrorKind, Is.EqualTo(LocalWriteErrorKind.DiskFull));
            Assert.That(result.ToSaveError(), Is.Not.Null);
            Assert.That(_storage.HasFile(StatePath), Is.False);
        }

        [Test]
        public void SafeFallback_NewAndPeekedSlots_DefaultToHadContent()
        {
            ProfileSyncState fallback = ProfileSyncState.CreateSafeFallback();
            var existing = new ProfileSyncState();
            SlotSyncState entry = existing.GetOrCreateSlot("player");
            entry.LastSyncedWriteId = "write-1";
            entry.LastSyncedProviderVersion = "v1";
            existing.ApplySafeFallback();

            Assert.That(fallback.GetOrCreateSlot("created").HadContent, Is.True);
            Assert.That(fallback.PeekSlot("peeked").HadContent, Is.True);
            Assert.That(fallback.TryGetSlot("peeked", out _), Is.False, "Peeking never adds an entry.");
            Assert.That(entry.HadContent, Is.True);
            Assert.That(entry.LastSyncedWriteId, Is.Null);
            Assert.That(existing.UnknownSlotsHadContent, Is.True);
        }

        [TestCase("none")]
        [TestCase("guest")]
        [TestCase("account")]
        [TestCase("local")]
        public void DeviceState_RoundTrip(string kind)
        {
            ProfileId? profile = CreateProfile(kind);
            var state = new DeviceState { DeviceId = "device-1", LastActiveProfile = profile };

            StateWriteResult saved = _deviceStore.Save(state);
            DeviceStateLoadResult loaded = _deviceStore.Load();

            Assert.That(saved.IsSuccess, Is.True, saved.Message);
            Assert.That(_storage.HasFile(SaveLayout.DeviceStatePath), Is.True);
            Assert.That(loaded.Status, Is.EqualTo(DeviceStateLoadStatus.Loaded));
            Assert.That(loaded.CanOverwrite, Is.True);
            Assert.That(loaded.State.DeviceId, Is.EqualTo("device-1"));
            Assert.That(loaded.State.LastActiveProfile, Is.EqualTo(profile));
            Assert.That(loaded.State.LoadedVersion, Is.EqualTo(DeviceState.CurrentVersion));
        }

        [Test]
        public void DeviceState_Corrupt_NewStateAndQuarantine()
        {
            _storage.SetText(SaveLayout.DeviceStatePath, "{\"version\":1,\"deviceId\":5}");

            DeviceStateLoadResult loaded = _deviceStore.Load();

            string quarantine = SaveLayout.QuarantineFileName("device", _clock.UtcNow);
            Assert.That(loaded.Status, Is.EqualTo(DeviceStateLoadStatus.Corrupt));
            Assert.That(loaded.State.DeviceId, Is.Null);
            Assert.That(loaded.State.LastActiveProfile, Is.Null);
            Assert.That(loaded.QuarantineFileName, Is.EqualTo(quarantine));
            Assert.That(_storage.HasFile(SaveLayout.Combine(SaveLayout.DeviceDirectory, quarantine)), Is.True);
            Assert.That(_storage.HasFile(SaveLayout.DeviceStatePath), Is.False);
            Assert.That(loaded.CanOverwrite, Is.True);
        }

        [Test]
        public void DeviceState_NewerVersion_KnownFieldsLoadedLeniently()
        {
            byte[] bytes = Utf8("{\"version\":3,\"deviceId\":\"device-9\",\"lastActiveProfile\":{\"kind\":\"martian\"},\"future\":true}");
            _storage.SetBytes(SaveLayout.DeviceStatePath, bytes);

            DeviceStateLoadResult loaded = _deviceStore.Load();

            Assert.That(loaded.Status, Is.EqualTo(DeviceStateLoadStatus.NewerVersion));
            Assert.That(loaded.State.DeviceId, Is.EqualTo("device-9"));
            Assert.That(loaded.State.LastActiveProfile, Is.Null);
            Assert.That(_storage.GetBytes(SaveLayout.DeviceStatePath), Is.EqualTo(bytes), "The live file is left alone until this build writes it.");

            // The only write is the verbatim backup of the newer file
            Assert.That(_storage.MutationCallCount, Is.EqualTo(1));
            Assert.That(_storage.GetBytes(SaveLayout.Combine(SaveLayout.DeviceDirectory, loaded.QuarantineFileName)), Is.EqualTo(bytes));
        }

        // A downgrade on a full disk must not destroy the newer build's device.json
        [Test]
        public void DeviceState_NewerVersion_BackupWriteFails_RefusesOverwrite()
        {
            byte[] bytes = Utf8("{\"version\":3,\"deviceId\":\"device-9\",\"future\":true}");
            _storage.SetBytes(SaveLayout.DeviceStatePath, bytes);
            string quarantine = SaveLayout.Combine(SaveLayout.DeviceDirectory, SaveLayout.NewerVersionFileName("device", _clock.UtcNow));
            _storage.FailWithKind(StorageOperation.Write, quarantine, LocalWriteErrorKind.DiskFull);

            DeviceStateLoadResult loaded = _deviceStore.Load();

            Assert.That(loaded.Status, Is.EqualTo(DeviceStateLoadStatus.NewerVersion));
            Assert.That(loaded.NewerVersionUnbacked, Is.True);
            Assert.That(loaded.QuarantineFileName, Is.Null);
            Assert.That(loaded.CanOverwrite, Is.False, "PersistDeviceState is refused while the newer file has no copy.");
            Assert.That(_storage.GetBytes(SaveLayout.DeviceStatePath), Is.EqualTo(bytes));
        }

        [Test]
        public void DeviceState_NewerVersionBak_IsQuarantinedAndReportedAsNewerVersion()
        {
            byte[] newer = Utf8("{\"version\":3,\"deviceId\":\"device-9\",\"future\":true}");
            _storage.SetBytes(SaveLayout.BakPath(SaveLayout.DeviceStatePath), newer);
            string quarantine = SaveLayout.Combine(SaveLayout.DeviceDirectory, SaveLayout.NewerVersionFileName("device", _clock.UtcNow));

            DeviceStateLoadResult loaded = _deviceStore.Load();

            Assert.That(loaded.Status, Is.EqualTo(DeviceStateLoadStatus.NewerVersion));
            Assert.That(_storage.GetBytes(quarantine), Is.EqualTo(newer));
        }

        [Test]
        public void DeviceState_IoError_CannotOverwrite_RecoveredFromBak()
        {
            _storage.SetBytes(SaveLayout.BakPath(SaveLayout.DeviceStatePath), DeviceStateStore.Encode(new DeviceState { DeviceId = "device-old" }));
            DeviceStateLoadResult recovered = _deviceStore.Load();

            _storage.FailWithIOException(StorageOperation.Read, SaveLayout.DeviceStatePath);
            DeviceStateLoadResult failed = _deviceStore.Load();

            Assert.That(recovered.Status, Is.EqualTo(DeviceStateLoadStatus.Recovered));
            Assert.That(recovered.State.DeviceId, Is.EqualTo("device-old"));
            Assert.That(failed.Status, Is.EqualTo(DeviceStateLoadStatus.IoError));
            Assert.That(failed.CanOverwrite, Is.False);
            Assert.That(failed.State, Is.Not.Null);
        }

        private static ProfileSyncState CreateOlderState()
        {
            var older = new ProfileSyncState { OwnerAccountId = AccountId };
            SlotSyncState player = older.GetOrCreateSlot("player");
            player.HadContent = false;
            player.LastSyncedWriteId = "write-1";
            player.LastSyncedRevision = 1;
            player.PendingWriteId = "write-2";
            return older;
        }

        private static ProfileId? CreateProfile(string kind)
        {
            switch (kind)
            {
                case "guest":
                    return ProfileId.Guest;
                case "account":
                    return ProfileId.Account(AccountId);
                case "local":
                    return ProfileId.Local("alice");
                default:
                    return null;
            }
        }

        private string[] NewerVersionBackups()
        {
            return _storage.AllFilePaths.Where(path => path.Contains(SaveLayout.NewerVersionMarker)).ToArray();
        }

        private static byte[] Utf8(string text)
        {
            return new UTF8Encoding(false).GetBytes(text);
        }
    }
}
