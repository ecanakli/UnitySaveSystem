using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Ecanakli.SaveSystem.Tests
{
    /// <summary>
    /// The real AtomicFileStorage under Application.persistentDataPath (01 5.2, 04b B2 item 1): round trip across services,
    /// .bak rotation, quarantine and recovery of a corrupt primary, and the quarantine cap. Every root is deleted in teardown.
    /// </summary>
    [TestFixture]
    public sealed class PersistentDataPathTests
    {
        private const string CorruptContent = "this is not a save file";
        private const int SupportedSchema = 1;

        private readonly List<SaveService> _services = new List<SaveService>();

        private string _root;
        private TestSaveLogger _logger;
        private ManualSaveClock _clock;

        [SetUp]
        public void CreateRoot()
        {
            _root = PersistentDataTestRoot.Create();
            _logger = new TestSaveLogger();
            _clock = new ManualSaveClock();
        }

        [TearDown]
        public void DeleteRoot()
        {
            for (int i = _services.Count - 1; i >= 0; i--)
            {
                try
                {
                    _services[i].Dispose();
                }
                catch (Exception)
                {
                    // A test may have disposed it already; teardown must not mask the failure
                }
            }

            _services.Clear();
            PersistentDataTestRoot.Delete(_root);
            _root = null;
        }

        [UnityTest]
        public IEnumerator SaveAndReload_RealFiles_RoundTripThroughPersistentDataPath()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                var written = new ProfileSlot(TestSlotKeys.Player, SyncMode.LocalOnly);
                SaveService writer = CreateService(written);
                InitializeResult init = await writer.InitializeAsync(CancellationToken.None);
                Assert.That(init.IsSuccess, Is.True, init.ToString());
                Assert.That(init.Issues.Count, Is.EqualTo(0), Describe(init.Issues));

                Assert.That(written.Mutate(data =>
                {
                    data.Coins = 31;
                    data.Level = 4;
                    data.Items.Add("axe");
                }), Is.True);

                SaveResult saved = await written.SaveNowAsync(CancellationToken.None);
                Assert.That(saved.IsSuccess, Is.True, saved.ToString());
                writer.Dispose();

                string file = SlotFile();
                Assert.That(File.Exists(file), Is.True, file);
                Assert.That(file.StartsWith(Application.persistentDataPath, StringComparison.Ordinal), Is.True, file);
                Assert.That(File.Exists(file + SaveLayout.TmpSuffix), Is.False, "The tmp file is promoted, never left behind.");

                var reloaded = new ProfileSlot(TestSlotKeys.Player, SyncMode.LocalOnly);
                SaveService reader = CreateService(reloaded);
                InitializeResult second = await reader.InitializeAsync(CancellationToken.None);

                Assert.That(second.IsSuccess, Is.True, second.ToString());
                Assert.That(second.Issues.Count, Is.EqualTo(0), Describe(second.Issues));
                Assert.That(reloaded.State, Is.EqualTo(SlotState.Ready));
                Assert.That(reloaded.PeekData().Coins, Is.EqualTo(31));
                Assert.That(reloaded.PeekData().Level, Is.EqualTo(4));
                Assert.That(reloaded.PeekData().Items, Is.EqualTo(new[] { "axe" }));
                Assert.That(_logger.Count(TestLogLevel.Error), Is.EqualTo(0), _logger.Describe());
            });
        }

        [UnityTest]
        public IEnumerator SecondWrite_KeepsThePreviousGenerationAsBak()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                var slot = new ProfileSlot(TestSlotKeys.Player, SyncMode.LocalOnly);
                SaveService service = CreateService(slot);
                InitializeResult init = await service.InitializeAsync(CancellationToken.None);
                Assert.That(init.IsSuccess, Is.True, init.ToString());

                string file = SlotFile();
                Assert.That(slot.Mutate(data => data.Coins = 11), Is.True);
                Assert.That((await slot.SaveNowAsync(CancellationToken.None)).IsSuccess, Is.True);
                Assert.That(File.Exists(file + SaveLayout.BakSuffix), Is.False, "The first write has no previous generation.");

                Assert.That(slot.Mutate(data => data.Coins = 22), Is.True);
                Assert.That((await slot.SaveNowAsync(CancellationToken.None)).IsSuccess, Is.True);

                Assert.That(ReadCoins(file), Is.EqualTo(22));
                Assert.That(File.Exists(file + SaveLayout.BakSuffix), Is.True, "The previous generation is kept as .bak.");
                Assert.That(ReadCoins(file + SaveLayout.BakSuffix), Is.EqualTo(11));
                Assert.That(File.Exists(file + SaveLayout.TmpSuffix), Is.False);
                Assert.That(_logger.Count(TestLogLevel.Error), Is.EqualTo(0), _logger.Describe());
            });
        }

        [UnityTest]
        public IEnumerator CorruptPrimary_IsQuarantined_AndRecoveredFromBak()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                var slot = new ProfileSlot(TestSlotKeys.Player, SyncMode.LocalOnly);
                SaveService service = CreateService(slot);
                Assert.That((await service.InitializeAsync(CancellationToken.None)).IsSuccess, Is.True);

                string file = SlotFile();
                Assert.That(slot.Mutate(data => data.Coins = 11), Is.True);
                Assert.That((await slot.SaveNowAsync(CancellationToken.None)).IsSuccess, Is.True);
                Assert.That(slot.Mutate(data => data.Coins = 22), Is.True);
                Assert.That((await slot.SaveNowAsync(CancellationToken.None)).IsSuccess, Is.True);
                service.Dispose();

                // Torn write: the primary is unreadable, the previous generation is intact
                File.WriteAllText(file, CorruptContent, new UTF8Encoding(false));

                var recovered = new ProfileSlot(TestSlotKeys.Player, SyncMode.LocalOnly);
                SaveService next = CreateService(recovered);
                InitializeResult init = await next.InitializeAsync(CancellationToken.None);

                Assert.That(init.IsSuccess, Is.True, init.ToString());
                Assert.That(recovered.State, Is.EqualTo(SlotState.Ready));
                Assert.That(recovered.PeekData().Coins, Is.EqualTo(11), "The .bak generation is loaded.");

                SlotLoadIssue issue = FindIssue(init.Issues, TestSlotKeys.Player);
                Assert.That(issue, Is.Not.Null, Describe(init.Issues));
                Assert.That(issue.Kind, Is.EqualTo(SlotLoadIssueKind.CorruptRecoveredFromBackup), issue.ToString());
                Assert.That(issue.Cause, Is.EqualTo(CorruptionCause.ParseFailed), issue.ToString());
                Assert.That(issue.BackupFileName, Is.Not.Null, issue.ToString());

                string quarantine = Path.Combine(ProfileDirectory(), issue.BackupFileName);
                Assert.That(File.Exists(quarantine), Is.True, quarantine);
                Assert.That(File.ReadAllText(quarantine), Is.EqualTo(CorruptContent), "The corrupt bytes are kept verbatim.");
                Assert.That(QuarantineFileCount(TestSlotKeys.Player), Is.EqualTo(1));

                // The primary is repaired from .bak, so the next load is clean
                Assert.That(File.Exists(file), Is.True, file);
                Assert.That(ReadCoins(file), Is.EqualTo(11));
            });
        }

        [UnityTest]
        public IEnumerator RepeatedCorruption_KeepsOnlyTheNewestQuarantineFiles()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                string file = SlotFile();
                const int rounds = 6;
                for (int round = 0; round < rounds; round++)
                {
                    var slot = new ProfileSlot(TestSlotKeys.Player, SyncMode.LocalOnly);
                    SaveService service = CreateService(slot);
                    InitializeResult init = await service.InitializeAsync(CancellationToken.None);
                    Assert.That(init.IsSuccess, Is.True, init.ToString());

                    Assert.That(slot.Mutate(data => data.Coins = round), Is.True);
                    Assert.That(service.FlushLocalNow().IsComplete, Is.True);
                    service.Dispose();

                    File.WriteAllText(file, CorruptContent, new UTF8Encoding(false));

                    // Distinct quarantine timestamps; the file name carries whole seconds
                    _clock.Advance(TimeSpan.FromMinutes(1));
                }

                Assert.That(
                    QuarantineFileCount(TestSlotKeys.Player), Is.EqualTo(SaveLayout.QuarantineCap),
                    "Five corrupt loads must leave the newest " + SaveLayout.QuarantineCap + " quarantine files.");
                Assert.That(_logger.Count(TestLogLevel.Warning), Is.GreaterThanOrEqualTo(rounds - 1), _logger.Describe());
            });
        }

        private SaveService CreateService(params SaveSlot[] slots)
        {
            SaveServiceOptions options = TestServiceFactory.CreateOptions(_clock, _logger, TestServiceFactory.DefaultDeviceId, _root);

            // Real files with the production threading mode
            options.OffloadIo = true;
            var service = new SaveService(options, new AtomicFileStorage(_root), NullCloudSaveProvider.Instance, slots);
            _services.Add(service);
            return service;
        }

        private string ProfileDirectory()
        {
            return PersistentDataTestRoot.ToAbsolute(_root, SaveLayout.ProfileDirectory(ProfileId.Guest));
        }

        private string SlotFile()
        {
            return PersistentDataTestRoot.SlotFilePath(_root, ProfileId.Guest, TestSlotKeys.Player);
        }

        private int QuarantineFileCount(string key)
        {
            string directory = ProfileDirectory();
            if (!Directory.Exists(directory))
            {
                return 0;
            }

            int count = 0;
            string[] files = Directory.GetFiles(directory);
            for (int i = 0; i < files.Length; i++)
            {
                if (SaveLayout.IsBackupFileName(BackupFileFamily.LocalCorrupt, key, Path.GetFileName(files[i])))
                {
                    count++;
                }
            }

            return count;
        }

        private static int ReadCoins(string path)
        {
            EnvelopeDecodeResult decoded = EnvelopeCodec.Decode(File.ReadAllBytes(path), SupportedSchema);
            Assert.That(decoded.IsOk, Is.True, path + ": " + decoded.Message);
            Assert.That(decoded.Checksum, Is.EqualTo(ChecksumStatus.Verified), path);
            return decoded.Envelope.Data.Value<int>("Coins");
        }

        private static SlotLoadIssue FindIssue(IReadOnlyList<SlotLoadIssue> issues, string key)
        {
            for (int i = 0; i < issues.Count; i++)
            {
                if (string.Equals(issues[i].SlotKey, key, StringComparison.Ordinal))
                {
                    return issues[i];
                }
            }

            return null;
        }

        private static string Describe(IReadOnlyList<SlotLoadIssue> issues)
        {
            var text = new StringBuilder("issues: ").Append(issues.Count);
            for (int i = 0; i < issues.Count; i++)
            {
                text.Append(" | ").Append(issues[i]);
            }

            return text.ToString();
        }
    }
}
