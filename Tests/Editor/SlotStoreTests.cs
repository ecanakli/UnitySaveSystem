using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ecanakli.SaveSystem.Tests
{
    /// <summary>SlotStore load recovery order, quarantine, too-new protection, Normalize count, backups and conflict files.</summary>
    [TestFixture]
    public sealed class SlotStoreTests
    {
        private const string SlotDirectory = "profiles/guest";
        private const string Key = TestSlotKeys.Player;

        private static readonly byte[] Garbage = Utf8("{\"fmt\":1,\"schema\":");

        /// <summary>Long enough for a recovering read to reach the write gate while a background write holds it.</summary>
        private static readonly TimeSpan ParkDuration = TimeSpan.FromMilliseconds(500);

        private static readonly TimeSpan TaskTimeout = TimeSpan.FromSeconds(10);

        private InMemorySaveStorage _storage;
        private TestSaveLogger _logger;
        private ManualSaveClock _clock;
        private SlotStore _store;

        private static string Primary => SaveLayout.SlotPath(SlotDirectory, Key);

        private static string Tmp => SaveLayout.TmpPath(Primary);

        private static string Bak => SaveLayout.BakPath(Primary);

        [SetUp]
        public void SetUp()
        {
            _storage = new InMemorySaveStorage();
            _logger = new TestSaveLogger();
            _clock = new ManualSaveClock();
            _store = new SlotStore(_storage, _logger, _clock);
        }

        [TearDown]
        public void TearDown()
        {
            Assert.That(HookScope.IsActive, Is.False, "A test left the hook scope active.");
        }

        [Test]
        public void Load_Absent_ReturnsNormalizedDefault_WithoutWriting()
        {
            var slot = new ProfileSlot();

            SlotLoadResult result = _store.Load(slot, SlotDirectory);

            Assert.That(result.File.Status, Is.EqualTo(SlotFileReadStatus.Absent));
            Assert.That(result.Recovery, Is.EqualTo(SlotRecovery.None));
            Assert.That(result.IsReady, Is.True);
            Assert.That(result.NeedsCloudRecovery, Is.False);
            Assert.That(result.Presence, Is.EqualTo(LocalPresence.Absent));
            Assert.That(result.Revision, Is.EqualTo(0));
            Assert.That(Coins(result), Is.EqualTo(0));
            Assert.That(slot.NormalizeCount, Is.EqualTo(1));
            Assert.That(result.CreateIssues(ProfileId.Guest, Key), Is.Empty);
            Assert.That(_storage.MutationCallCount, Is.EqualTo(0));
        }

        [Test]
        public void Load_ValidPrimary_LoadsWithoutRepair()
        {
            _storage.SetBytes(Primary, Envelope(5, 7));
            var slot = new ProfileSlot();

            SlotLoadResult result = _store.Load(slot, SlotDirectory);

            Assert.That(result.File.Status, Is.EqualTo(SlotFileReadStatus.Found));
            Assert.That(result.File.Source, Is.EqualTo(SlotFileSource.Primary));
            Assert.That(result.File.Checksum, Is.EqualTo(ChecksumStatus.Verified));
            Assert.That(result.Recovery, Is.EqualTo(SlotRecovery.None));
            Assert.That(result.Presence, Is.EqualTo(LocalPresence.Present));
            Assert.That(result.Revision, Is.EqualTo(7));
            Assert.That(Coins(result), Is.EqualTo(5));
            Assert.That(slot.NormalizeCount, Is.EqualTo(1));
            Assert.That(_storage.MutationCallCount, Is.EqualTo(0));
            Assert.That(_logger.Count(TestLogLevel.Warning), Is.EqualTo(0), _logger.Describe());
        }

        [Test]
        public void Load_ChecksumMismatch_QuarantinesWithCauseChecksumMismatch_RecoversFromBak()
        {
            byte[] tampered = Tamper(Envelope(5, 2), 5, 6);
            byte[] backup = Envelope(3, 1);
            _storage.SetBytes(Primary, tampered);
            _storage.SetBytes(Bak, backup);

            SlotLoadResult result = _store.Load(new ProfileSlot(), SlotDirectory);

            string quarantine = SaveLayout.QuarantineFileName(Key, _clock.UtcNow);
            Assert.That(result.IsReady, Is.True);
            Assert.That(result.Recovery, Is.EqualTo(SlotRecovery.CorruptRecoveredFromBackup));
            Assert.That(result.File.Source, Is.EqualTo(SlotFileSource.Backup));
            Assert.That(result.File.PrimaryWasCorrupt, Is.True);
            Assert.That(result.File.Cause, Is.EqualTo(CorruptionCause.ChecksumMismatch));
            Assert.That(result.File.QuarantineFileName, Is.EqualTo(quarantine));
            Assert.That(result.NeedsCloudRecovery, Is.False);
            Assert.That(Coins(result), Is.EqualTo(3));
            Assert.That(result.Revision, Is.EqualTo(1));
            Assert.That(_storage.GetBytes(SaveLayout.Combine(SlotDirectory, quarantine)), Is.EqualTo(tampered), "Quarantine keeps the original bytes.");
            Assert.That(_storage.GetBytes(Primary), Is.EqualTo(backup), "The backup is rewritten as primary.");

            IReadOnlyList<SlotLoadIssue> issues = result.CreateIssues(ProfileId.Guest, Key);
            Assert.That(issues.Count, Is.EqualTo(1));
            Assert.That(issues[0].Kind, Is.EqualTo(SlotLoadIssueKind.CorruptRecoveredFromBackup));
            Assert.That(issues[0].Cause, Is.EqualTo(CorruptionCause.ChecksumMismatch));
            Assert.That(issues[0].BackupFileName, Is.EqualTo(quarantine));
        }

        [Test]
        public void Load_ValidTmpWithHigherRev_PromotedOverPrimary()
        {
            byte[] primary = Envelope(1, 1);
            byte[] tmp = Envelope(2, 2);
            _storage.SetBytes(Primary, primary);
            _storage.SetBytes(Tmp, tmp);

            SlotLoadResult result = _store.Load(new ProfileSlot(), SlotDirectory);

            Assert.That(result.IsReady, Is.True);
            Assert.That(result.File.Source, Is.EqualTo(SlotFileSource.Tmp));
            Assert.That(result.Recovery, Is.EqualTo(SlotRecovery.PromotedTmp));
            Assert.That(result.File.PrimaryWasCorrupt, Is.False);
            Assert.That(result.File.RepairFailed, Is.False);
            Assert.That(Coins(result), Is.EqualTo(2));
            Assert.That(result.Revision, Is.EqualTo(2));
            Assert.That(_storage.GetBytes(Primary), Is.EqualTo(tmp));
            Assert.That(_storage.GetBytes(Bak), Is.EqualTo(primary));
            Assert.That(_storage.HasFile(Tmp), Is.False);
            Assert.That(BackupNames(BackupFileFamily.LocalCorrupt), Is.Empty);
            Assert.That(result.CreateIssues(ProfileId.Guest, Key), Is.Empty, "Crash recovery is not corruption.");
        }

        [TestCase(2L)]
        [TestCase(1L)]
        public void Load_TmpWithSameOrLowerRev_Ignored(long tmpRevision)
        {
            byte[] primary = Envelope(1, 2);
            byte[] tmp = Envelope(9, tmpRevision);
            _storage.SetBytes(Primary, primary);
            _storage.SetBytes(Tmp, tmp);

            SlotLoadResult result = _store.Load(new ProfileSlot(), SlotDirectory);

            Assert.That(result.File.Source, Is.EqualTo(SlotFileSource.Primary));
            Assert.That(Coins(result), Is.EqualTo(1));
            Assert.That(_storage.GetBytes(Primary), Is.EqualTo(primary));
            Assert.That(_storage.MutationCallCount, Is.EqualTo(0));
        }

        [Test]
        public void Load_TmpWithBadChecksum_NotPromoted()
        {
            byte[] primary = Envelope(1, 1);
            byte[] tmp = Tamper(Envelope(2, 2), 2, 3);
            _storage.SetBytes(Primary, primary);
            _storage.SetBytes(Tmp, tmp);

            SlotLoadResult result = _store.Load(new ProfileSlot(), SlotDirectory);

            Assert.That(result.File.Source, Is.EqualTo(SlotFileSource.Primary));
            Assert.That(result.Recovery, Is.EqualTo(SlotRecovery.None));
            Assert.That(Coins(result), Is.EqualTo(1));
            Assert.That(_storage.GetBytes(Primary), Is.EqualTo(primary));
            Assert.That(_storage.GetBytes(Tmp), Is.EqualTo(tmp));
            Assert.That(_storage.MutationCallCount, Is.EqualTo(0));
        }

        [Test]
        public void Load_TmpWithoutChecksum_NotPromoted()
        {
            _storage.SetBytes(Primary, Envelope(1, 1));
            _storage.SetBytes(Tmp, Utf8("{\"fmt\":1,\"schema\":1,\"rev\":5,\"data\":{\"Coins\":5}}"));

            SlotLoadResult result = _store.Load(new ProfileSlot(), SlotDirectory);

            Assert.That(result.File.Source, Is.EqualTo(SlotFileSource.Primary));
            Assert.That(Coins(result), Is.EqualTo(1));
            Assert.That(_storage.MutationCallCount, Is.EqualTo(0));
        }

        [Test]
        public void Load_CorruptPrimary_TriesTmpBeforeBak()
        {
            byte[] tmp = Envelope(3, 3);
            byte[] backup = Envelope(1, 1);
            _storage.SetBytes(Primary, Garbage);
            _storage.SetBytes(Tmp, tmp);
            _storage.SetBytes(Bak, backup);

            SlotLoadResult result = _store.Load(new ProfileSlot(), SlotDirectory);

            Assert.That(result.IsReady, Is.True);
            Assert.That(result.File.Source, Is.EqualTo(SlotFileSource.Tmp));
            Assert.That(result.Recovery, Is.EqualTo(SlotRecovery.CorruptRecoveredFromTmp));
            Assert.That(result.File.Cause, Is.EqualTo(CorruptionCause.ParseFailed));
            Assert.That(Coins(result), Is.EqualTo(3));
            Assert.That(_storage.CountCalls(StorageOperation.Read, Bak), Is.EqualTo(0), "The backup is not consulted when the tmp is valid.");
            Assert.That(_storage.GetBytes(Primary), Is.EqualTo(tmp));
            Assert.That(_storage.HasFile(Tmp), Is.False);
            Assert.That(_storage.GetBytes(Bak), Is.EqualTo(backup));
            Assert.That(_storage.GetBytes(SaveLayout.Combine(SlotDirectory, result.File.QuarantineFileName)), Is.EqualTo(Garbage));

            IReadOnlyList<SlotLoadIssue> issues = result.CreateIssues(ProfileId.Guest, Key);
            Assert.That(issues.Count, Is.EqualTo(1));
            Assert.That(issues[0].Kind, Is.EqualTo(SlotLoadIssueKind.CorruptRecoveredFromTmp));
        }

        [Test]
        public void Load_CorruptPrimaryAndCorruptTmp_FallsBackToBak()
        {
            byte[] badTmp = Tamper(Envelope(4, 4), 4, 5);
            byte[] backup = Envelope(1, 1);
            _storage.SetBytes(Primary, Garbage);
            _storage.SetBytes(Tmp, badTmp);
            _storage.SetBytes(Bak, backup);

            SlotLoadResult result = _store.Load(new ProfileSlot(), SlotDirectory);

            Assert.That(result.Recovery, Is.EqualTo(SlotRecovery.CorruptRecoveredFromBackup));
            Assert.That(Coins(result), Is.EqualTo(1));
            Assert.That(_storage.GetBytes(Primary), Is.EqualTo(backup));
        }

        [Test]
        public void Load_NoValidCopy_CorruptResetWithNeedsCloudRecovery()
        {
            _storage.SetBytes(Primary, Garbage);
            _storage.SetBytes(Bak, Tamper(Envelope(1, 1), 1, 2));

            SlotLoadResult result = _store.Load(new ProfileSlot(), SlotDirectory);

            Assert.That(result.File.Status, Is.EqualTo(SlotFileReadStatus.CorruptNoValidCopy));
            Assert.That(result.Recovery, Is.EqualTo(SlotRecovery.CorruptReset));
            Assert.That(result.NeedsCloudRecovery, Is.True);
            Assert.That(result.IsReady, Is.True);
            Assert.That(result.Presence, Is.EqualTo(LocalPresence.Undetermined));
            Assert.That(result.File.Cause, Is.EqualTo(CorruptionCause.ParseFailed));
            Assert.That(Coins(result), Is.EqualTo(0));
            Assert.That(result.Revision, Is.EqualTo(0));
            Assert.That(_storage.HasFile(Primary), Is.False);
            Assert.That(_storage.HasFile(Bak), Is.False);
            Assert.That(_storage.CountCalls(StorageOperation.Write, Primary), Is.EqualTo(0), "Nothing is written over the corrupt copies.");

            List<string> quarantined = BackupNames(BackupFileFamily.LocalCorrupt);
            Assert.That(quarantined, Is.EquivalentTo(new[]
            {
                SaveLayout.QuarantineFileName(Key, _clock.UtcNow),
                SaveLayout.QuarantineFileName(Key, _clock.UtcNow, 1),
            }));

            IReadOnlyList<SlotLoadIssue> issues = result.CreateIssues(ProfileId.Guest, Key);
            Assert.That(issues.Count, Is.EqualTo(1));
            Assert.That(issues[0].Kind, Is.EqualTo(SlotLoadIssueKind.CorruptReset));
        }

        [Test]
        public void Load_CorruptPrimaryOnly_CorruptReset_OneQuarantine()
        {
            _storage.SetBytes(Primary, Garbage);

            SlotLoadResult result = _store.Load(new ProfileSlot(), SlotDirectory);

            Assert.That(result.Recovery, Is.EqualTo(SlotRecovery.CorruptReset));
            Assert.That(result.NeedsCloudRecovery, Is.True);
            Assert.That(BackupNames(BackupFileFamily.LocalCorrupt).Count, Is.EqualTo(1));
            Assert.That(_storage.HasFile(Primary), Is.False);
        }

        [Test]
        public void Load_QuarantineCap_KeepsNewestThree()
        {
            DateTime now = _clock.UtcNow;
            string oldest = SaveLayout.QuarantineFileName(Key, now.AddHours(-3));
            string cloudCorrupt = SaveLayout.CloudCorruptFileName(Key, now.AddHours(-5));
            string otherSlot = SaveLayout.QuarantineFileName("inventory", now.AddHours(-9));
            for (int i = 1; i <= 3; i++)
            {
                _storage.SetText(SaveLayout.Combine(SlotDirectory, SaveLayout.QuarantineFileName(Key, now.AddHours(-i))), "old" + i);
            }

            _storage.SetText(SaveLayout.Combine(SlotDirectory, cloudCorrupt), "cloud");
            _storage.SetText(SaveLayout.Combine(SlotDirectory, otherSlot), "other");
            _storage.SetBytes(Primary, Garbage);
            _storage.SetBytes(Bak, Envelope(1, 1));

            SlotLoadResult result = _store.Load(new ProfileSlot(), SlotDirectory);

            List<string> kept = BackupNames(BackupFileFamily.LocalCorrupt);
            Assert.That(result.IsReady, Is.True);
            Assert.That(kept.Count, Is.EqualTo(SaveLayout.QuarantineCap));
            Assert.That(kept, Does.Contain(SaveLayout.QuarantineFileName(Key, now)));
            Assert.That(kept, Does.Not.Contain(oldest));
            Assert.That(_storage.HasFile(SaveLayout.Combine(SlotDirectory, cloudCorrupt)), Is.True, "Other backup families are not pruned.");
            Assert.That(_storage.HasFile(SaveLayout.Combine(SlotDirectory, otherSlot)), Is.True, "Other slots are not pruned.");
        }

        // Quarantines dated ahead of a clock that was set back must not make the new evidence prune itself
        [Test]
        public void Load_QuarantineCap_ClockBehindTheOlderQuarantines_KeepsTheNewEvidence()
        {
            DateTime now = _clock.UtcNow;
            for (int i = 1; i <= SaveLayout.QuarantineCap; i++)
            {
                _storage.SetText(SaveLayout.Combine(SlotDirectory, SaveLayout.QuarantineFileName(Key, now.AddDays(i))), "future" + i);
            }

            _storage.SetBytes(Primary, Garbage);
            _storage.SetBytes(Bak, Envelope(1, 1));

            SlotLoadResult result = _store.Load(new ProfileSlot(), SlotDirectory);

            List<string> kept = BackupNames(BackupFileFamily.LocalCorrupt);
            Assert.That(result.File.QuarantineFileName, Is.EqualTo(SaveLayout.QuarantineFileName(Key, now)), "Premise: the load reports this copy.");
            Assert.That(kept, Does.Contain(result.File.QuarantineFileName), "The reported evidence must exist.");
            Assert.That(kept.Count, Is.EqualTo(SaveLayout.QuarantineCap));
            Assert.That(kept, Does.Not.Contain(SaveLayout.QuarantineFileName(Key, now.AddDays(1))), "The oldest of the others goes instead.");
        }

        [Test]
        public void Load_PrimaryReadIoError_NoQuarantineNoWrite()
        {
            _storage.SetBytes(Primary, Garbage);
            _storage.FailWithIOException(StorageOperation.Read, Primary);

            SlotLoadResult result = _store.Load(new ProfileSlot(), SlotDirectory);

            Assert.That(result.File.Status, Is.EqualTo(SlotFileReadStatus.IoError));
            Assert.That(result.Failure, Is.EqualTo(SlotFailure.IoError));
            Assert.That(result.IsReady, Is.False);
            Assert.That(result.Presence, Is.EqualTo(LocalPresence.Undetermined));
            Assert.That(result.File.ErrorKind, Is.EqualTo(LocalWriteErrorKind.IoError));
            Assert.That(result.Data, Is.Not.Null);
            Assert.That(_storage.MutationCallCount, Is.EqualTo(0));
            Assert.That(_storage.GetBytes(Primary), Is.EqualTo(Garbage));
            Assert.That(BackupNames(BackupFileFamily.LocalCorrupt), Is.Empty);
            Assert.That(_logger.Count(TestLogLevel.Error), Is.GreaterThanOrEqualTo(1));
        }

        [Test]
        public void Load_TmpReadIoError_NoQuarantineNoWrite()
        {
            _storage.SetBytes(Primary, Garbage);
            _storage.SetBytes(Bak, Envelope(1, 1));
            _storage.FailWithIOException(StorageOperation.Read, Tmp);

            SlotLoadResult result = _store.Load(new ProfileSlot(), SlotDirectory);

            Assert.That(result.Failure, Is.EqualTo(SlotFailure.IoError));
            Assert.That(_storage.MutationCallCount, Is.EqualTo(0));
            Assert.That(_storage.GetBytes(Primary), Is.EqualTo(Garbage));
        }

        [Test]
        public void Load_QuarantineRenameFails_IoError_PrimaryKept()
        {
            byte[] backup = Envelope(1, 1);
            _storage.SetBytes(Primary, Garbage);
            _storage.SetBytes(Bak, backup);
            _storage.FailWithIOException(StorageOperation.MoveFile, Primary);

            SlotLoadResult result = _store.Load(new ProfileSlot(), SlotDirectory);

            Assert.That(result.Failure, Is.EqualTo(SlotFailure.IoError));
            Assert.That(result.File.PrimaryWasCorrupt, Is.True);
            Assert.That(_storage.GetBytes(Primary), Is.EqualTo(Garbage));
            Assert.That(_storage.GetBytes(Bak), Is.EqualTo(backup));
            Assert.That(_storage.CountCalls(StorageOperation.Write), Is.EqualTo(0));
        }

        [Test]
        public void Load_SchemaTooNew_FailsAndFileNeverOverwritten()
        {
            byte[] newer = Envelope(9, 4, 2);
            _storage.SetBytes(Primary, newer);

            SlotLoadResult result = _store.Load(new ProfileSlot(), SlotDirectory);
            SlotWriteResult write = _store.WriteEnvelope(SlotDirectory, Key, new SaveEnvelope { Schema = 1, Revision = 1, Data = new JObject() });

            Assert.That(result.Failure, Is.EqualTo(SlotFailure.SchemaTooNew));
            Assert.That(result.File.Status, Is.EqualTo(SlotFileReadStatus.SchemaTooNew));
            Assert.That(result.File.FoundSchema, Is.EqualTo(2));
            Assert.That(result.Presence, Is.EqualTo(LocalPresence.Undetermined));
            Assert.That(_store.IsKnownSchemaTooNew(SlotDirectory, Key), Is.True);
            Assert.That(write.Status, Is.EqualTo(SlotWriteStatus.RefusedSchemaTooNew));
            Assert.That(write.ToSaveError().Code, Is.EqualTo(SaveErrorCode.SlotNotReady));
            Assert.That(_storage.GetBytes(Primary), Is.EqualTo(newer));
            Assert.That(_storage.MutationCallCount, Is.EqualTo(0));
        }

        [Test]
        public void Load_FormatTooNew_FailsAndFileUntouched()
        {
            byte[] newer = Utf8("{\"fmt\":2,\"schema\":1,\"rev\":1,\"data\":{\"Coins\":1}}");
            _storage.SetBytes(Primary, newer);

            SlotLoadResult result = _store.Load(new ProfileSlot(), SlotDirectory);

            Assert.That(result.Failure, Is.EqualTo(SlotFailure.SchemaTooNew));
            Assert.That(result.File.FoundFormat, Is.EqualTo(2));
            Assert.That(_storage.GetBytes(Primary), Is.EqualTo(newer));
            Assert.That(_storage.MutationCallCount, Is.EqualTo(0));
        }

        [Test]
        public void Load_CorruptPrimary_TooNewBak_BakUntouched_WritesRefused()
        {
            byte[] newerBak = Envelope(1, 8, 3);
            _storage.SetBytes(Primary, Garbage);
            _storage.SetBytes(Bak, newerBak);

            SlotLoadResult result = _store.Load(new ProfileSlot(), SlotDirectory);
            SlotWriteResult write = _store.WriteEnvelope(SlotDirectory, Key, new SaveEnvelope { Schema = 1, Revision = 1, Data = new JObject() });

            Assert.That(result.Failure, Is.EqualTo(SlotFailure.SchemaTooNew));
            Assert.That(result.File.Source, Is.EqualTo(SlotFileSource.Backup));
            Assert.That(_storage.GetBytes(Bak), Is.EqualTo(newerBak));
            Assert.That(write.Status, Is.EqualTo(SlotWriteStatus.RefusedSchemaTooNew));
            Assert.That(_storage.CountCalls(StorageOperation.Write), Is.EqualTo(0));
        }

        [Test]
        public void DeleteSlotFiles_AfterTooNew_AllowsWritesAgain()
        {
            _storage.SetBytes(Primary, Envelope(1, 1, 5));
            _store.Load(new ProfileSlot(), SlotDirectory);

            Assert.That(_store.DeleteSlotFiles(SlotDirectory, Key).IsSuccess, Is.True);
            SlotWriteResult write = _store.WriteEnvelope(SlotDirectory, Key, new SaveEnvelope { Schema = 1, Revision = 1, Data = new JObject() });

            Assert.That(write.IsSuccess, Is.True);
            Assert.That(_store.IsKnownSchemaTooNew(SlotDirectory, Key), Is.False);
        }

        [TestCase("absent")]
        [TestCase("primary")]
        [TestCase("tmpNewer")]
        [TestCase("primaryMissingBak")]
        [TestCase("corruptToTmp")]
        [TestCase("corruptToBak")]
        [TestCase("corruptReset")]
        public void Load_NormalizeRunsExactlyOncePerLoad(string scenario)
        {
            switch (scenario)
            {
                case "primary":
                    _storage.SetBytes(Primary, Envelope(5, 5));
                    break;
                case "tmpNewer":
                    _storage.SetBytes(Primary, Envelope(1, 1));
                    _storage.SetBytes(Tmp, Envelope(2, 2));
                    break;
                case "primaryMissingBak":
                    _storage.SetBytes(Bak, Envelope(3, 3));
                    break;
                case "corruptToTmp":
                    _storage.SetBytes(Primary, Garbage);
                    _storage.SetBytes(Tmp, Envelope(4, 4));
                    break;
                case "corruptToBak":
                    _storage.SetBytes(Primary, Garbage);
                    _storage.SetBytes(Bak, Envelope(6, 6));
                    break;
                case "corruptReset":
                    _storage.SetBytes(Primary, Garbage);
                    break;
            }

            var slot = new ProfileSlot();
            var normalized = new List<ProfileData>();
            slot.NormalizeAction = normalized.Add;

            SlotLoadResult result = _store.Load(slot, SlotDirectory);
            slot.ApplyLoadResult(in result);

            Assert.That(result.IsReady, Is.True, result.Message);
            Assert.That(slot.NormalizeCount, Is.EqualTo(1));
            Assert.That(normalized[0], Is.SameAs(result.Data));
            Assert.That(slot.PeekData(), Is.SameAs(result.Data));
            Assert.That(slot.NormalizeCount, Is.EqualTo(1), "Applying the result does not normalize again.");
        }

        [Test]
        public void Initialize_ExistingContent_EveryInstanceNormalizedOnce_IncludingEmptyReference()
        {
            var slot = new ProfileSlot();
            var normalized = new List<ProfileData>();
            slot.NormalizeAction = normalized.Add;
            var storage = new InMemorySaveStorage();
            storage.SetBytes(TestPaths.ProfileSlot(ProfileId.Guest, Key), Envelope(5, 3));

            using (TestServiceContext context = TestServiceFactory.Create(new TestServiceSetup { Slots = new SaveSlot[] { slot }, Storage = storage }))
            {
                Assert.That(context.InitializeSync().IsSuccess, Is.True);
                ProfileData loaded = slot.PeekData();

                Assert.That(loaded.Coins, Is.EqualTo(5));
                Assert.That(CountSame(normalized, loaded), Is.EqualTo(1), "The loaded instance is normalized exactly once.");
                // The default IsEmpty adds one separately normalized empty reference; no instance may be normalized twice
                for (int i = 0; i < normalized.Count; i++)
                {
                    Assert.That(CountSame(normalized, normalized[i]), Is.EqualTo(1), "Instance " + i + " was normalized more than once.");
                }
            }
        }

        [Test]
        public void Load_OlderSchema_RunsUpgradeChainOnce_FileUntouched()
        {
            var slot = new SchemaV3Slot();
            string path = SaveLayout.SlotPath(SlotDirectory, slot.Key);
            byte[] v1 = EnvelopeCodec.Encode(new SaveEnvelope { Schema = 1, Revision = 6, Data = new JObject { { "name", "Bob" }, { "Coins", 5 } } }).Bytes;
            _storage.SetBytes(path, v1);

            SlotLoadResult result = _store.Load(slot, SlotDirectory);

            Assert.That(result.IsReady, Is.True, result.Message);
            Assert.That(slot.UpgradeFromVersions, Is.EqualTo(new[] { 1, 2 }));
            var data = (SchemaV3Data)result.Data;
            Assert.That(data.DisplayName, Is.EqualTo("Bob"));
            Assert.That(data.Gold, Is.EqualTo(5));
            Assert.That(result.Revision, Is.EqualTo(6));
            Assert.That(slot.NormalizeCount, Is.EqualTo(1));
            Assert.That(_storage.GetBytes(path), Is.EqualTo(v1));
            Assert.That(_storage.MutationCallCount, Is.EqualTo(0));
        }

        [Test]
        public void Load_UpgradeThrows_NormalizeFailedAtUpgrade_FileUntouched()
        {
            var slot = new SchemaV3Slot();
            slot.UpgradeFunc = (payload, from) => from == 2 ? throw new InvalidOperationException("Scripted upgrade failure.") : SchemaMigrations.Step(payload, from);
            string path = SaveLayout.SlotPath(SlotDirectory, slot.Key);
            byte[] v1 = EnvelopeCodec.Encode(new SaveEnvelope { Schema = 1, Revision = 2, Data = new JObject { { "name", "Bob" } } }).Bytes;
            _storage.SetBytes(path, v1);

            SlotLoadResult result = _store.Load(slot, SlotDirectory);

            Assert.That(result.Failure, Is.EqualTo(SlotFailure.NormalizeFailed));
            Assert.That(result.FailedStage, Is.EqualTo(SlotMaterializeStage.Upgrade));
            Assert.That(result.Data, Is.InstanceOf<SchemaV3Data>());
            Assert.That(_storage.GetBytes(path), Is.EqualTo(v1));
            Assert.That(_storage.MutationCallCount, Is.EqualTo(0));
        }

        [Test]
        public void Load_NormalizeThrows_NormalizeFailed_FileUntouched()
        {
            var slot = new NormalizeThrowsSlot();
            string path = SaveLayout.SlotPath(SlotDirectory, slot.Key);
            byte[] bytes = Envelope(4, 4);
            _storage.SetBytes(path, bytes);

            SlotLoadResult result = _store.Load(slot, SlotDirectory);

            Assert.That(result.Failure, Is.EqualTo(SlotFailure.NormalizeFailed));
            Assert.That(result.FailedStage, Is.EqualTo(SlotMaterializeStage.Normalize));
            Assert.That(result.IsReady, Is.False);
            Assert.That(_storage.GetBytes(path), Is.EqualTo(bytes));
            Assert.That(_storage.MutationCallCount, Is.EqualTo(0));
        }

        [Test]
        public void Load_ReadOnlyMode_NeverRepairs()
        {
            byte[] backup = Envelope(2, 2);
            _storage.SetBytes(Primary, Garbage);
            _storage.SetBytes(Bak, backup);

            SlotLoadResult result = _store.Load(new ProfileSlot(), SlotDirectory, SlotReadMode.ReadOnly);

            Assert.That(result.File.Source, Is.EqualTo(SlotFileSource.Backup));
            Assert.That(Coins(result), Is.EqualTo(2));
            Assert.That(_storage.MutationCallCount, Is.EqualTo(0));
            Assert.That(_storage.GetBytes(Primary), Is.EqualTo(Garbage));
        }

        [Test]
        public void PeekHeader_ValidPresent_CorruptUndetermined_NeverRepairs()
        {
            _storage.SetBytes(Primary, EnvelopeCodec.Encode(new SaveEnvelope { Schema = 1, Revision = 12, WriteId = "write-12", Data = new JObject() }).Bytes);
            SlotHeaderPeekResult present = _store.PeekHeader(SlotDirectory, Key, 1);

            _storage.SetBytes(Primary, Garbage);
            _storage.SetBytes(Bak, Envelope(1, 1));
            SlotHeaderPeekResult corrupt = _store.PeekHeader(SlotDirectory, Key, 1);

            Assert.That(present.Presence, Is.EqualTo(LocalPresence.Present));
            Assert.That(present.Revision, Is.EqualTo(12));
            Assert.That(present.WriteId, Is.EqualTo("write-12"));
            Assert.That(corrupt.Presence, Is.EqualTo(LocalPresence.Undetermined));
            Assert.That(_storage.MutationCallCount, Is.EqualTo(0));
        }

        // 01 s13.3: a crash after any atomic write step leaves a valid copy; a restarted load recovers the newest valid revision
        [TestCase("TmpPartial", true, "primary=2 tmp=invalid bak=1", 2L, "primary=2 tmp=invalid bak=1")]
        [TestCase("TmpFlushed", true, "primary=2 tmp=3 bak=1", 3L, "primary=3 tmp=- bak=2")]
        [TestCase("BakDeleted", true, "primary=2 tmp=3 bak=-", 3L, "primary=3 tmp=- bak=2")]
        [TestCase("PrimaryMovedToBak", true, "primary=- tmp=3 bak=2", 3L, "primary=3 tmp=- bak=2")]
        [TestCase("TmpPromoted", true, "primary=3 tmp=- bak=2", 3L, "primary=3 tmp=- bak=2")]
        [TestCase("TmpPartial", false, "primary=- tmp=invalid bak=-", 0L, "primary=- tmp=invalid bak=-")]
        [TestCase("TmpFlushed", false, "primary=- tmp=3 bak=-", 3L, "primary=3 tmp=- bak=-")]
        [TestCase("TmpPromoted", false, "primary=3 tmp=- bak=-", 3L, "primary=3 tmp=- bak=-")]
        public void Load_AfterCrashAtEachAtomicWriteStep_RecoversNewestValidRevision(
            string crashPoint, bool hadPreviousWrites, string filesAfterCrash, long expectedRevision, string filesAfterLoad)
        {
            long lastDurableRevision = 0;
            if (hadPreviousWrites)
            {
                Assert.That(_store.WriteEnvelope(SlotDirectory, Key, EnvelopeOf(1, 1)).IsSuccess, Is.True);
                Assert.That(_store.WriteEnvelope(SlotDirectory, Key, EnvelopeOf(2, 2)).IsSuccess, Is.True);
                lastDurableRevision = 2;
            }

            switch (crashPoint)
            {
                case "TmpPartial":
                    _storage.SimulateCrashAfter(Primary, AtomicWriteStep.WriteTmp, 16);
                    break;
                case "TmpFlushed":
                    _storage.SimulateCrashAfter(Primary, AtomicWriteStep.WriteTmp);
                    break;
                case "BakDeleted":
                    _storage.SimulateCrashAfter(Primary, AtomicWriteStep.DeleteBak);
                    break;
                case "PrimaryMovedToBak":
                    _storage.SimulateCrashAfter(Primary, AtomicWriteStep.MovePrimaryToBak);
                    break;
                case "TmpPromoted":
                    _storage.SimulateCrashAfter(Primary, AtomicWriteStep.PromoteTmp);
                    break;
                default:
                    Assert.Fail("Unknown crash point " + crashPoint);
                    break;
            }

            SlotWriteResult crashed = _store.WriteEnvelope(SlotDirectory, Key, EnvelopeOf(3, 3));

            Assert.That(crashed.IsSuccess, Is.False, "The crash interrupts the write.");
            Assert.That(_storage.IsCrashed, Is.True, "Premise: the simulated crash happened.");
            _storage.RecoverFromCrash();
            Assert.That(DescribeSlotFiles(), Is.EqualTo(filesAfterCrash), "Premise: the crash stopped at " + crashPoint + ".");

            long newestOnDisk = NewestValidRevisionOnDisk();
            Assert.That(newestOnDisk, Is.GreaterThanOrEqualTo(lastDurableRevision), "The last durable revision is never lost.");
            Assert.That(newestOnDisk, Is.EqualTo(expectedRevision));

            // Restart: a new store has no in-memory knowledge of the interrupted write
            var restarted = new SlotStore(_storage, _logger, _clock);
            SlotLoadResult result = restarted.Load(new ProfileSlot(), SlotDirectory);

            Assert.That(result.IsReady, Is.True, result.Message);
            Assert.That(result.Revision, Is.EqualTo(expectedRevision), "Load picks the newest valid revision.");
            Assert.That(Coins(result), Is.EqualTo((int)expectedRevision));
            Assert.That(result.Presence, Is.EqualTo(expectedRevision == 0 ? LocalPresence.Absent : LocalPresence.Present));
            Assert.That(result.NeedsCloudRecovery, Is.False);
            Assert.That(result.CreateIssues(ProfileId.Guest, Key), Is.Empty, "Crash recovery is not corruption.");
            Assert.That(BackupNames(BackupFileFamily.LocalCorrupt), Is.Empty, "Nothing is quarantined.");
            Assert.That(DescribeSlotFiles(), Is.EqualTo(filesAfterLoad));

            // The next write and load continue normally
            Assert.That(restarted.WriteEnvelope(SlotDirectory, Key, EnvelopeOf(4, 4)).IsSuccess, Is.True);
            SlotLoadResult next = restarted.Load(new ProfileSlot(), SlotDirectory);
            Assert.That(next.Revision, Is.EqualTo(4));
            Assert.That(_storage.HasFile(Tmp), Is.False);
        }

        [Test]
        public void WriteEnvelope_Success_ReportsChecksum_FailureClassified()
        {
            var envelope = new SaveEnvelope { Schema = 1, Revision = 3, Data = new JObject { { "Coins", 3 } } };

            SlotWriteResult written = _store.WriteEnvelope(SlotDirectory, Key, envelope);
            byte[] onDisk = _storage.GetBytes(Primary);
            _storage.FailWithKind(StorageOperation.Write, Primary, LocalWriteErrorKind.DiskFull);
            SlotWriteResult failed = _store.WriteEnvelope(SlotDirectory, Key, new SaveEnvelope { Schema = 1, Revision = 4, Data = new JObject() });

            Assert.That(written.IsSuccess, Is.True);
            Assert.That(written.Revision, Is.EqualTo(3));
            Assert.That(written.ByteCount, Is.EqualTo(onDisk.Length));
            Assert.That(written.DataSha256, Is.EqualTo(EnvelopeCodec.Decode(onDisk, 1).Envelope.DataSha256));
            Assert.That(failed.Status, Is.EqualTo(SlotWriteStatus.Failed));
            Assert.That(failed.ErrorKind, Is.EqualTo(LocalWriteErrorKind.DiskFull));
            Assert.That(_storage.GetBytes(Primary), Is.EqualTo(onDisk));
        }

        // B1: the revision check and the write are one step, so no caller can put an older revision on top of a newer one
        [Test]
        public void WriteEncoded_LowerRevisionThanTheDurableOne_Refused_FileKept()
        {
            SlotWriteResult written = _store.WriteEncoded(SlotDirectory, Key, Encoded(9, 9), 9);
            byte[] onDisk = _storage.GetBytes(Primary);

            SlotWriteResult stale = _store.WriteEncoded(SlotDirectory, Key, Encoded(4, 4), 4);

            Assert.That(written.IsSuccess, Is.True);
            Assert.That(stale.Status, Is.EqualTo(SlotWriteStatus.RefusedStaleRevision));
            Assert.That(stale.IsSuccess, Is.False);
            Assert.That(stale.IsRefused, Is.True);
            Assert.That(stale.DurableRevision, Is.EqualTo(9));
            Assert.That(stale.ToSaveError().Code, Is.EqualTo(SaveErrorCode.Superseded));
            Assert.That(_storage.GetBytes(Primary), Is.EqualTo(onDisk), "The newer file is untouched.");
            Assert.That(_storage.GetWriteCount(Primary), Is.EqualTo(1), "The refused write never reaches the storage.");
        }

        [Test]
        public void WriteEncoded_SameRevisionAgain_Written()
        {
            Assert.That(_store.WriteEncoded(SlotDirectory, Key, Encoded(1, 3), 3).IsSuccess, Is.True);

            SlotWriteResult repeat = _store.WriteEncoded(SlotDirectory, Key, Encoded(2, 3), 3);

            Assert.That(repeat.IsSuccess, Is.True, "A rewrite of the same revision is idempotent, not stale.");
            Assert.That(Coins(_store.Load(new ProfileSlot(), SlotDirectory)), Is.EqualTo(2));
        }

        [Test]
        public void DeleteSlotFiles_ResetsTheStaleWriteGuard()
        {
            Assert.That(_store.WriteEncoded(SlotDirectory, Key, Encoded(9, 9), 9).IsSuccess, Is.True);
            Assert.That(_store.DeleteSlotFiles(SlotDirectory, Key).IsSuccess, Is.True);

            SlotWriteResult afterDelete = _store.WriteEncoded(SlotDirectory, Key, Encoded(1, 1), 1);

            Assert.That(afterDelete.IsSuccess, Is.True, "A deleted slot starts a new revision line.");
            Assert.That(_storage.GetBytes(Primary), Is.Not.Null);
        }

        [Test]
        public void Load_ReBaselinesTheStaleWriteGuard_FromTheFilesOnDisk()
        {
            Assert.That(_store.WriteEncoded(SlotDirectory, Key, Encoded(9, 9), 9).IsSuccess, Is.True);

            // Replaced behind the store's back (restored backup, hand-edited file)
            _storage.SetBytes(Primary, Envelope(2, 2));
            SlotLoadResult loaded = _store.Load(new ProfileSlot(), SlotDirectory);
            SlotWriteResult next = _store.WriteEncoded(SlotDirectory, Key, Encoded(3, 3), 3);

            Assert.That(loaded.Revision, Is.EqualTo(2));
            Assert.That(next.IsSuccess, Is.True, "The guard follows the revision the load really found.");
            Assert.That(_store.Load(new ProfileSlot(), SlotDirectory).Revision, Is.EqualTo(3));
        }

        // F10: a write that fails while promoting leaves the new revision in a valid .tmp, so it still has to be protected
        [Test]
        public void WriteEncoded_AfterAFailedPromote_LowerRevisionIsRefused_TmpKept()
        {
            Assert.That(_store.WriteEncoded(SlotDirectory, Key, Encoded(5, 5), 5).IsSuccess, Is.True);
            _storage.FailWriteStep(Primary, AtomicWriteStep.PromoteTmp, () => new IOException("Scripted promote failure."), 1);

            SlotWriteResult failed = _store.WriteEncoded(SlotDirectory, Key, Encoded(7, 7), 7);
            Assert.That(failed.Status, Is.EqualTo(SlotWriteStatus.Failed));
            Assert.That(DescribeSlotFiles(), Is.EqualTo("primary=- tmp=7 bak=5"), "Premise: the failed promote left revision 7 in a valid tmp.");

            SlotWriteResult superseded = _store.WriteEncoded(SlotDirectory, Key, Encoded(6, 6), 6);

            Assert.That(superseded.Status, Is.EqualTo(SlotWriteStatus.RefusedStaleRevision));
            Assert.That(superseded.DurableRevision, Is.EqualTo(7), "The attempted revision guards the path as well.");
            Assert.That(DescribeSlotFiles(), Is.EqualTo("primary=- tmp=7 bak=5"), "The refused write never reaches the storage.");
            Assert.That(_store.Load(new ProfileSlot(), SlotDirectory).Revision, Is.EqualTo(7), "The newest valid copy survives.");
        }

        // F11: the file reads happen outside the gate, so a write that lands in between must not be rolled back
        [Test]
        public void ReadFiles_WhileAWriteLands_KeepsTheStaleWriteGuard()
        {
            Assert.That(_store.WriteEncoded(SlotDirectory, Key, Encoded(5, 5), 5).IsSuccess, Is.True);

            // Parked before any file change: the read below still sees revision 5 while the writer holds the gate
            _storage.ParkWrites(Primary, ParkDuration);
            Task<SlotWriteResult> landing = Task.Run(() => _store.WriteEncoded(SlotDirectory, Key, Encoded(7, 7), 7));
            WaitUntil(() => _storage.IsWriteParked, "Parked background write");

            // Reads revision 5, then waits for the gate; the parked write lands before the observe runs
            SlotFileReadResult read = _store.ReadFiles(SlotDirectory, Key, 1, SlotReadMode.Recover);
            Assert.That(landing.Wait(TaskTimeout), Is.True, "The parked write did not finish.");

            SlotWriteResult stale = _store.WriteEncoded(SlotDirectory, Key, Encoded(6, 6), 6);

            Assert.That(read.Revision, Is.EqualTo(5), "Premise: the read saw the file before the write landed.");
            Assert.That(landing.Result.IsSuccess, Is.True, landing.Result.Message);
            Assert.That(stale.Status, Is.EqualTo(SlotWriteStatus.RefusedStaleRevision), "The observe must not undo a write it did not see.");
            Assert.That(stale.DurableRevision, Is.EqualTo(7));
            Assert.That(_store.Load(new ProfileSlot(), SlotDirectory).Revision, Is.EqualTo(7));
        }

        // F12: only the primary is renamed, so the guard still has to protect the surviving .tmp and .bak
        [Test]
        public void QuarantinePrimary_KeepsTheStaleWriteGuard()
        {
            Assert.That(_store.WriteEncoded(SlotDirectory, Key, Encoded(5, 5), 5).IsSuccess, Is.True);
            Assert.That(_store.WriteEncoded(SlotDirectory, Key, Encoded(9, 9), 9).IsSuccess, Is.True);
            Assert.That(_store.QuarantinePrimary(SlotDirectory, Key, CorruptionCause.None).Status, Is.EqualTo(SlotFileOpStatus.Done));
            Assert.That(DescribeSlotFiles(), Is.EqualTo("primary=- tmp=- bak=5"), "Premise: the quarantine left the backup on disk.");

            SlotWriteResult stale = _store.WriteEncoded(SlotDirectory, Key, Encoded(4, 4), 4);

            Assert.That(stale.Status, Is.EqualTo(SlotWriteStatus.RefusedStaleRevision));
            Assert.That(stale.DurableRevision, Is.EqualTo(9));
            Assert.That(_store.Load(new ProfileSlot(), SlotDirectory).Revision, Is.EqualTo(5), "The surviving backup is still the newest copy.");
        }

        // S8: the bak rewrite writes through {key}.json.tmp, which would destroy an unverifiable tmp
        [Test]
        public void Load_CorruptPrimaryAndUnusableTmp_QuarantinesTheTmpBeforeTheBakRewrite()
        {
            byte[] badTmp = Tamper(Envelope(4, 4), 4, 5);
            byte[] backup = Envelope(1, 1);
            _storage.SetBytes(Primary, Garbage);
            _storage.SetBytes(Tmp, badTmp);
            _storage.SetBytes(Bak, backup);

            SlotLoadResult result = _store.Load(new ProfileSlot(), SlotDirectory);

            List<string> quarantined = BackupNames(BackupFileFamily.LocalCorrupt);
            Assert.That(result.Recovery, Is.EqualTo(SlotRecovery.CorruptRecoveredFromBackup));
            Assert.That(quarantined.Count, Is.EqualTo(2), string.Join(", ", quarantined));
            Assert.That(QuarantineHasBytes(quarantined, Garbage), Is.True, "The corrupt primary is kept.");
            Assert.That(QuarantineHasBytes(quarantined, badTmp), Is.True, "The unusable tmp is kept, not overwritten.");
            Assert.That(result.File.QuarantineFileName, Is.Not.Null);
            Assert.That(_storage.GetBytes(Primary), Is.EqualTo(backup));
            Assert.That(_storage.HasFile(Tmp), Is.False);
        }

        [Test]
        public void Load_MissingPrimaryAndUnusableTmp_QuarantinesTheTmpBeforeTheBakRewrite()
        {
            byte[] badTmp = Tamper(Envelope(4, 4), 4, 5);
            byte[] backup = Envelope(1, 1);
            _storage.SetBytes(Tmp, badTmp);
            _storage.SetBytes(Bak, backup);

            SlotLoadResult result = _store.Load(new ProfileSlot(), SlotDirectory);

            List<string> quarantined = BackupNames(BackupFileFamily.LocalCorrupt);
            Assert.That(result.Recovery, Is.EqualTo(SlotRecovery.PromotedBackup));
            Assert.That(quarantined.Count, Is.EqualTo(1), string.Join(", ", quarantined));
            Assert.That(QuarantineHasBytes(quarantined, badTmp), Is.True, "The unusable tmp is kept, not overwritten.");
            Assert.That(_storage.GetBytes(Primary), Is.EqualTo(backup));
        }

        // S8: a tmp from a newer build binds the path just like a too-new primary
        [Test]
        public void Load_TooNewTmp_SchemaTooNew_FilesUntouched_WritesRefused()
        {
            byte[] primary = Envelope(1, 1);
            byte[] newerTmp = Envelope(9, 9, 2);
            _storage.SetBytes(Primary, primary);
            _storage.SetBytes(Tmp, newerTmp);

            SlotLoadResult result = _store.Load(new ProfileSlot(), SlotDirectory);
            SlotWriteResult write = _store.WriteEnvelope(SlotDirectory, Key, new SaveEnvelope { Schema = 1, Revision = 5, Data = new JObject() });

            Assert.That(result.Failure, Is.EqualTo(SlotFailure.SchemaTooNew));
            Assert.That(result.File.Status, Is.EqualTo(SlotFileReadStatus.SchemaTooNew));
            Assert.That(result.File.Source, Is.EqualTo(SlotFileSource.Tmp));
            Assert.That(result.File.FoundSchema, Is.EqualTo(2));
            Assert.That(result.Presence, Is.EqualTo(LocalPresence.Undetermined));
            Assert.That(_store.IsKnownSchemaTooNew(SlotDirectory, Key), Is.True);
            Assert.That(write.Status, Is.EqualTo(SlotWriteStatus.RefusedSchemaTooNew));
            Assert.That(_storage.GetBytes(Tmp), Is.EqualTo(newerTmp));
            Assert.That(_storage.GetBytes(Primary), Is.EqualTo(primary));
            Assert.That(_storage.MutationCallCount, Is.EqualTo(0));
        }

        [Test]
        public void WriteCloudCorruptBackup_SkipsIdenticalBytes_CapsAtThree()
        {
            byte[] first = Utf8("{\"broken\":");

            SlotFileOpResult written = _store.WriteCloudCorruptBackup(SlotDirectory, Key, first);
            SlotFileOpResult duplicate = _store.WriteCloudCorruptBackup(SlotDirectory, Key, first);

            Assert.That(written.Status, Is.EqualTo(SlotFileOpStatus.Done));
            Assert.That(written.FileName, Is.EqualTo(SaveLayout.CloudCorruptFileName(Key, _clock.UtcNow)));
            Assert.That(_storage.GetBytes(SaveLayout.Combine(SlotDirectory, written.FileName)), Is.EqualTo(first));
            Assert.That(duplicate.Status, Is.EqualTo(SlotFileOpStatus.Skipped));
            Assert.That(duplicate.FileName, Is.EqualTo(written.FileName));
            Assert.That(BackupNames(BackupFileFamily.CloudCorrupt).Count, Is.EqualTo(1));

            for (int i = 1; i <= 3; i++)
            {
                _clock.Advance(TimeSpan.FromSeconds(1));
                Assert.That(_store.WriteCloudCorruptBackup(SlotDirectory, Key, Utf8("{\"broken\":" + i)).Status, Is.EqualTo(SlotFileOpStatus.Done));
            }

            List<string> kept = BackupNames(BackupFileFamily.CloudCorrupt);
            Assert.That(kept.Count, Is.EqualTo(SaveLayout.CloudCorruptCap));
            Assert.That(kept, Does.Not.Contain(written.FileName), "The oldest backup is pruned.");
            Assert.That(BackupNames(BackupFileFamily.LocalCorrupt), Is.Empty);
        }

        [Test]
        public void WriteConflictFile_HoldsResolutionLocalAndCloud_OneGeneration()
        {
            var local = new JObject { { "Coins", 1 } };
            var cloud = new JObject { { "Coins", 2 } };
            string path = SaveLayout.ConflictPath(SlotDirectory, Key);

            SlotFileOpResult first = _store.WriteConflictFile(SlotDirectory, Key, ConflictResolutionKind.TakeCloud, local, cloud);
            var firstDocument = (JObject)SaveJson.Parse(_storage.GetBytes(path));

            _clock.Advance(TimeSpan.FromMinutes(1));
            SlotFileOpResult second = _store.WriteConflictFile(SlotDirectory, Key, ConflictResolutionKind.KeepLocal, new JObject { { "Coins", 3 } }, null);
            var secondDocument = (JObject)SaveJson.Parse(_storage.GetBytes(path));

            Assert.That(first.Status, Is.EqualTo(SlotFileOpStatus.Done));
            Assert.That(first.FileName, Is.EqualTo(Key + SaveLayout.ConflictSuffix));
            Assert.That((string)firstDocument["resolution"], Is.EqualTo("TakeCloud"));
            Assert.That((string)firstDocument["savedAtUtc"], Is.EqualTo("2026-01-01T00:00:00.000Z"));
            Assert.That(JToken.DeepEquals(firstDocument["local"], local), Is.True);
            Assert.That(JToken.DeepEquals(firstDocument["cloud"], cloud), Is.True);

            Assert.That(second.IsSuccess, Is.True);
            Assert.That((string)secondDocument["resolution"], Is.EqualTo("KeepLocal"));
            Assert.That((string)secondDocument["savedAtUtc"], Is.EqualTo("2026-01-01T00:01:00.000Z"));
            Assert.That((int)secondDocument["local"]["Coins"], Is.EqualTo(3));
            Assert.That(secondDocument["cloud"].Type, Is.EqualTo(JTokenType.Null));
            Assert.That(_storage.HasFile(SaveLayout.BakPath(path)), Is.False, "Only one generation is kept.");
            Assert.That(CountNamesStartingWith(Key + ".conflict"), Is.EqualTo(1));
        }

        [Test]
        public void DeleteSlotFiles_RemovesTmpBakPrimaryConflictInOrder_KeepsQuarantine()
        {
            string conflict = SaveLayout.ConflictPath(SlotDirectory, Key);
            string quarantine = SaveLayout.Combine(SlotDirectory, SaveLayout.QuarantineFileName(Key, _clock.UtcNow));
            _storage.SetBytes(Primary, Envelope(1, 1));
            _storage.SetBytes(Tmp, Envelope(2, 2));
            _storage.SetBytes(Bak, Envelope(0, 0));
            _storage.SetText(conflict, "{}");
            _storage.SetText(quarantine, "corrupt");

            SlotFileOpResult result = _store.DeleteSlotFiles(SlotDirectory, Key);

            Assert.That(result.Status, Is.EqualTo(SlotFileOpStatus.Done));
            Assert.That(_storage.HasFile(Primary) || _storage.HasFile(Tmp) || _storage.HasFile(Bak) || _storage.HasFile(conflict), Is.False);
            Assert.That(_storage.HasFile(quarantine), Is.True);

            var deleted = new List<string>();
            foreach (StorageCall call in _storage.Calls)
            {
                if (call.Operation == StorageOperation.DeleteFile)
                {
                    deleted.Add(call.Path);
                }
            }

            Assert.That(deleted, Is.EqualTo(new[] { Tmp, Bak, Primary, conflict }));
        }

        [Test]
        public void DeleteSlotFiles_BakDeleteFails_StopsBeforePrimary()
        {
            _storage.SetBytes(Primary, Envelope(1, 1));
            _storage.SetBytes(Bak, Envelope(0, 0));
            _storage.FailWithIOException(StorageOperation.DeleteFile, Bak);

            SlotFileOpResult result = _store.DeleteSlotFiles(SlotDirectory, Key);

            Assert.That(result.Status, Is.EqualTo(SlotFileOpStatus.Failed));
            Assert.That(_storage.HasFile(Primary), Is.True);
            Assert.That(_storage.HasFile(Bak), Is.True);
        }

        // Blocks the test thread until the condition holds; the store is synchronous, so no player loop is needed
        private static void WaitUntil(Func<bool> condition, string description)
        {
            DateTime deadline = DateTime.UtcNow + TaskTimeout;
            while (!condition())
            {
                Assert.That(DateTime.UtcNow, Is.LessThan(deadline), description + " did not happen.");
                Thread.Sleep(5);
            }
        }

        private List<string> BackupNames(BackupFileFamily family)
        {
            var names = new List<string>();
            foreach (string name in _storage.ListFileNames(SlotDirectory))
            {
                if (SaveLayout.IsBackupFileName(family, Key, name))
                {
                    names.Add(name);
                }
            }

            return names;
        }

        private int CountNamesStartingWith(string prefix)
        {
            int count = 0;
            foreach (string name in _storage.ListFileNames(SlotDirectory))
            {
                if (name.StartsWith(prefix, StringComparison.Ordinal))
                {
                    count++;
                }
            }

            return count;
        }

        private static int CountSame(List<ProfileData> instances, ProfileData target)
        {
            int count = 0;
            foreach (ProfileData instance in instances)
            {
                if (ReferenceEquals(instance, target))
                {
                    count++;
                }
            }

            return count;
        }

        private static int Coins(SlotLoadResult result)
        {
            return ((ProfileData)result.Data).Coins;
        }

        private static EncodedEnvelope Encoded(int coins, long revision, int schema = 1)
        {
            return EnvelopeCodec.Encode(new SaveEnvelope { Schema = schema, Revision = revision, Data = new JObject { { "Coins", coins } } });
        }

        private bool QuarantineHasBytes(List<string> fileNames, byte[] expected)
        {
            for (int i = 0; i < fileNames.Count; i++)
            {
                byte[] bytes = _storage.GetBytes(SaveLayout.Combine(SlotDirectory, fileNames[i]));
                if (bytes != null && bytes.Length == expected.Length && SameBytes(bytes, expected))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool SameBytes(byte[] left, byte[] right)
        {
            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static SaveEnvelope EnvelopeOf(int coins, long revision)
        {
            return new SaveEnvelope { Schema = 1, Revision = revision, Data = new JObject { { "Coins", coins } } };
        }

        // "primary=R tmp=R bak=R": R is the revision, "-" when missing, "invalid" when it would not be loaded
        private string DescribeSlotFiles()
        {
            return "primary=" + DescribeCopy(Primary, false) + " tmp=" + DescribeCopy(Tmp, true) + " bak=" + DescribeCopy(Bak, false);
        }

        private string DescribeCopy(string path, bool requireVerifiedChecksum)
        {
            long? revision = ValidRevision(path, requireVerifiedChecksum);
            if (revision.HasValue)
            {
                return revision.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            return _storage.HasFile(path) ? "invalid" : "-";
        }

        private long NewestValidRevisionOnDisk()
        {
            long newest = 0;
            foreach (long? revision in new[] { ValidRevision(Primary, false), ValidRevision(Tmp, true), ValidRevision(Bak, false) })
            {
                if (revision.HasValue && revision.Value > newest)
                {
                    newest = revision.Value;
                }
            }

            return newest;
        }

        // Raw read; a tmp counts only with a verified checksum, as in load recovery
        private long? ValidRevision(string path, bool requireVerifiedChecksum)
        {
            byte[] bytes = _storage.GetBytes(path);
            if (bytes == null)
            {
                return null;
            }

            EnvelopeDecodeResult decoded = EnvelopeCodec.Decode(bytes, 1);
            if (!decoded.IsOk || (requireVerifiedChecksum && decoded.Checksum != ChecksumStatus.Verified))
            {
                return null;
            }

            return decoded.Envelope.Revision;
        }

        private static byte[] Envelope(int coins, long revision, int schema = 1)
        {
            return EnvelopeCodec.Encode(new SaveEnvelope { Schema = schema, Revision = revision, Data = new JObject { { "Coins", coins } } }).Bytes;
        }

        // Changes one data byte without touching the checksum
        private static byte[] Tamper(byte[] bytes, int coins, int newCoins)
        {
            string text = Encoding.UTF8.GetString(bytes);
            string from = "\"Coins\":" + coins;
            if (text.IndexOf(from, StringComparison.Ordinal) < 0)
            {
                throw new InvalidDataException("Tamper target not found.");
            }

            return Utf8(text.Replace(from, "\"Coins\":" + newCoins));
        }

        private static byte[] Utf8(string text)
        {
            return new UTF8Encoding(false).GetBytes(text);
        }
    }
}
