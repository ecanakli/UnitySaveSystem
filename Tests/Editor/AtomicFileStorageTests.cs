using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ecanakli.SaveSystem.Tests
{
    /// <summary>AtomicFileStorage over a real temp directory with FakeFileOps: tmp flush, rotation, Windows retries and path rules.</summary>
    [TestFixture]
    public sealed class AtomicFileStorageTests
    {
        private const string SlotDirectory = "profiles/guest";
        private const string SlotKey = "player";
        private const string SlotPath = SlotDirectory + "/" + SlotKey + ".json";

        private string _root;
        private FakeFileOps _fileOps;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "EcanakliSaveSystemTests", "atomic-" + Guid.NewGuid().ToString("N"));
            _fileOps = new FakeFileOps();
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }

        [Test]
        public void Write_FlushesTmpToDisk_BeforeRename()
        {
            AtomicFileStorage storage = CreateStorage(false);
            byte[] first = Utf8("first");
            byte[] second = Utf8("second");
            storage.WriteAtomic(SlotPath, first);

            byte[] tmpAtPromote = null;
            _fileOps.BeforeOperation = (kind, path) =>
            {
                if (kind == FileOpKind.MoveFile && Normalize(path).EndsWith("/player.json.tmp", StringComparison.Ordinal))
                {
                    tmpAtPromote = File.ReadAllBytes(path);
                }
            };
            _fileOps.ClearLog();

            storage.WriteAtomic(SlotPath, second);

            IReadOnlyList<FileOpCall> calls = _fileOps.Calls;
            int writeIndex = IndexOf(calls, FileOpKind.WriteAllBytesFlushed, "/player.json.tmp", null);
            int rotateIndex = IndexOf(calls, FileOpKind.MoveFile, "/player.json", "/player.json.bak");
            int promoteIndex = IndexOf(calls, FileOpKind.MoveFile, "/player.json.tmp", "/player.json");
            Assert.That(writeIndex, Is.GreaterThanOrEqualTo(0), Describe(calls));
            Assert.That(rotateIndex, Is.GreaterThan(writeIndex), Describe(calls));
            Assert.That(promoteIndex, Is.GreaterThan(rotateIndex), Describe(calls));
            Assert.That(IndexOf(calls, FileOpKind.WriteAllBytesFlushed, "/player.json", null), Is.EqualTo(-1), "The primary is never written in place.");
            Assert.That(tmpAtPromote, Is.EqualTo(second), "The tmp holds the full payload before the rename.");

            Assert.That(File.ReadAllBytes(Full(SlotPath)), Is.EqualTo(second));
            Assert.That(File.ReadAllBytes(Full(SlotPath + ".bak")), Is.EqualTo(first));
            Assert.That(File.Exists(Full(SlotPath + ".tmp")), Is.False);
        }

        [Test]
        public void Write_FirstWrite_CreatesParentAndNoBak()
        {
            AtomicFileStorage storage = CreateStorage(false);

            storage.WriteAtomic(SlotPath, Utf8("one"));

            Assert.That(File.ReadAllBytes(Full(SlotPath)), Is.EqualTo(Utf8("one")));
            Assert.That(File.Exists(Full(SlotPath + ".bak")), Is.False);
            Assert.That(File.Exists(Full(SlotPath + ".tmp")), Is.False);
            Assert.That(storage.ReadAllBytes(SlotPath), Is.EqualTo(Utf8("one")));
        }

        [Test]
        public void Write_ThirdWrite_BakHoldsOnlyPreviousGeneration()
        {
            AtomicFileStorage storage = CreateStorage(false);

            storage.WriteAtomic(SlotPath, Utf8("one"));
            storage.WriteAtomic(SlotPath, Utf8("two"));
            storage.WriteAtomic(SlotPath, Utf8("three"));

            Assert.That(File.ReadAllBytes(Full(SlotPath)), Is.EqualTo(Utf8("three")));
            Assert.That(File.ReadAllBytes(Full(SlotPath + ".bak")), Is.EqualTo(Utf8("two")));
        }

        [Test]
        public void Write_TmpWriteFails_DeletesPartialTmp_KeepsPrimary()
        {
            AtomicFileStorage storage = CreateStorage(true);
            storage.WriteAtomic(SlotPath, Utf8("first"));
            File.WriteAllBytes(Full(SlotPath + ".tmp"), Utf8("partial"));
            _fileOps.FailWithIOException(FileOpKind.WriteAllBytesFlushed, "player.json.tmp");

            Assert.Throws<IOException>(() => storage.WriteAtomic(SlotPath, Utf8("second")));

            Assert.That(File.Exists(Full(SlotPath + ".tmp")), Is.False);
            Assert.That(File.ReadAllBytes(Full(SlotPath)), Is.EqualTo(Utf8("first")));
            Assert.That(File.Exists(Full(SlotPath + ".bak")), Is.False);
            Assert.That(_fileOps.Sleeps, Is.Empty, "The tmp write step is never retried.");
        }

        [Test]
        public void Replace_OnWindows_RetriesTransientIOException_ThenSucceeds()
        {
            AtomicFileStorage storage = CreateStorage(true);
            storage.WriteAtomic(SlotPath, Utf8("first"));
            FileOpFault fault = _fileOps.FailWithIOException(FileOpKind.MoveFile, "player.json.tmp", times: 2);

            storage.WriteAtomic(SlotPath, Utf8("second"));

            Assert.That(fault.HitCount, Is.EqualTo(2));
            Assert.That(_fileOps.Sleeps, Is.EqualTo(new[] { 15, 30 }));
            Assert.That(File.ReadAllBytes(Full(SlotPath)), Is.EqualTo(Utf8("second")));
            Assert.That(File.ReadAllBytes(Full(SlotPath + ".bak")), Is.EqualTo(Utf8("first")));
            Assert.That(File.Exists(Full(SlotPath + ".tmp")), Is.False);
        }

        [Test]
        public void Replace_OnWindows_RetriesUnauthorizedAccessOnRotation()
        {
            AtomicFileStorage storage = CreateStorage(true);
            storage.WriteAtomic(SlotPath, Utf8("first"));
            _fileOps.FailOn(FileOpKind.MoveFile, "player.json.bak", () => new UnauthorizedAccessException("Scripted lock."));

            storage.WriteAtomic(SlotPath, Utf8("second"));

            Assert.That(_fileOps.Sleeps, Is.EqualTo(new[] { 15 }));
            Assert.That(File.ReadAllBytes(Full(SlotPath)), Is.EqualTo(Utf8("second")));
        }

        [Test]
        public void Replace_OnWindows_ExhaustedRetries_RethrowsAndKeepsBakAndTmpRecoverable()
        {
            AtomicFileStorage storage = CreateStorage(true);
            byte[] older = EnvelopeBytes(1, 1);
            byte[] newer = EnvelopeBytes(2, 2);
            storage.WriteAtomic(SlotPath, older);
            _fileOps.FailWithIOException(FileOpKind.MoveFile, "player.json.tmp", times: -1);
            _fileOps.ClearLog();

            Assert.Throws<IOException>(() => storage.WriteAtomic(SlotPath, newer));

            Assert.That(_fileOps.Sleeps, Is.EqualTo(new[] { 15, 30, 60 }));
            Assert.That(CountMoves(_fileOps.Calls, "/player.json.tmp"), Is.EqualTo(AtomicFileStorage.DefaultReplaceRetryCount + 1));
            Assert.That(File.Exists(Full(SlotPath)), Is.False);
            Assert.That(File.ReadAllBytes(Full(SlotPath + ".bak")), Is.EqualTo(older));
            Assert.That(File.ReadAllBytes(Full(SlotPath + ".tmp")), Is.EqualTo(newer));

            // Load-time recovery picks up the flushed tmp
            _fileOps.ClearFaults();
            var store = new SlotStore(storage, new TestSaveLogger(), new ManualSaveClock());
            SlotFileReadResult read = store.ReadFiles(SlotDirectory, SlotKey, 1, SlotReadMode.Recover);

            Assert.That(read.Status, Is.EqualTo(SlotFileReadStatus.Found), read.Message);
            Assert.That(read.Source, Is.EqualTo(SlotFileSource.Tmp));
            Assert.That(read.Revision, Is.EqualTo(2));
            Assert.That(read.RepairFailed, Is.False);
            Assert.That(File.ReadAllBytes(Full(SlotPath)), Is.EqualTo(newer));
            Assert.That(File.Exists(Full(SlotPath + ".tmp")), Is.False);
        }

        [Test]
        public void Replace_OnNonWindows_DoesNotRetryOrSleep()
        {
            AtomicFileStorage storage = CreateStorage(false);
            storage.WriteAtomic(SlotPath, Utf8("first"));
            _fileOps.FailWithIOException(FileOpKind.MoveFile, "player.json.tmp", times: 1);
            _fileOps.ClearLog();

            Assert.Throws<IOException>(() => storage.WriteAtomic(SlotPath, Utf8("second")));

            Assert.That(_fileOps.Sleeps, Is.Empty);
            Assert.That(CountMoves(_fileOps.Calls, "/player.json.tmp"), Is.EqualTo(1));
            Assert.That(File.ReadAllBytes(Full(SlotPath + ".tmp")), Is.EqualTo(Utf8("second")));
            Assert.That(File.ReadAllBytes(Full(SlotPath + ".bak")), Is.EqualTo(Utf8("first")));

            // The next write succeeds once the transient failure is gone
            storage.WriteAtomic(SlotPath, Utf8("third"));
            Assert.That(File.ReadAllBytes(Full(SlotPath)), Is.EqualTo(Utf8("third")));
        }

        [TestCase("FileNotFound")]
        [TestCase("DirectoryNotFound")]
        [TestCase("PathTooLong")]
        [TestCase("DiskFull")]
        [TestCase("SaveStorageException")]
        public void Replace_NonRetryableFailure_IsNotRetried(string kind)
        {
            AtomicFileStorage storage = CreateStorage(true);
            storage.WriteAtomic(SlotPath, Utf8("first"));
            _fileOps.FailOn(FileOpKind.MoveFile, "player.json.tmp", () => CreateNonRetryable(kind), -1);
            _fileOps.ClearLog();

            Assert.That(() => storage.WriteAtomic(SlotPath, Utf8("second")), Throws.InstanceOf<IOException>());

            Assert.That(_fileOps.Sleeps, Is.Empty);
            Assert.That(CountMoves(_fileOps.Calls, "/player.json.tmp"), Is.EqualTo(1));
        }

        [Test]
        public void Replace_FileNotFound_IsNotRetried()
        {
            AtomicFileStorage storage = CreateStorage(true);
            _fileOps.FailOn(FileOpKind.MoveFile, "player.json.tmp", () => new FileNotFoundException("Scripted missing tmp."), -1);

            Assert.Throws<FileNotFoundException>(() => storage.WriteAtomic(SlotPath, Utf8("first")));

            Assert.That(_fileOps.Sleeps, Is.Empty);
            Assert.That(StorageErrorClassifier.IsRetryableReplaceFailure(new FileNotFoundException()), Is.False);
            Assert.That(StorageErrorClassifier.IsRetryableReplaceFailure(new IOException("busy", FakeFileOps.SharingViolationHResult)), Is.True);
        }

        [Test]
        public void Replace_RetryHoldsPathLock_ConcurrentWriteToSamePathWaits()
        {
            AtomicFileStorage storage = CreateStorage(true);
            storage.WriteAtomic(SlotPath, Utf8("first"));
            _fileOps.FailWithIOException(FileOpKind.MoveFile, "player.json.tmp", times: 1);

            Task concurrent = null;
            bool completedDuringRetry = true;
            _fileOps.OnSleep = milliseconds =>
            {
                if (concurrent != null)
                {
                    return;
                }

                concurrent = Task.Run(() => storage.WriteAtomic(SlotPath, Utf8("third")));
                completedDuringRetry = concurrent.Wait(200);
            };

            storage.WriteAtomic(SlotPath, Utf8("second"));

            Assert.That(concurrent, Is.Not.Null);
            Assert.That(completedDuringRetry, Is.False, "The concurrent write must wait while the retry holds the path lock.");
            Assert.That(concurrent.Wait(5000), Is.True);
            Assert.That(File.ReadAllBytes(Full(SlotPath)), Is.EqualTo(Utf8("third")));
            Assert.That(File.ReadAllBytes(Full(SlotPath + ".bak")), Is.EqualTo(Utf8("second")));
        }

        [TestCase(-1, 15)]
        [TestCase(0, 15)]
        [TestCase(1, 30)]
        [TestCase(2, 60)]
        [TestCase(3, 120)]
        [TestCase(6, 960)]
        [TestCase(7, 1000)]
        [TestCase(30, 1000)]
        public void GetRetryDelayMilliseconds_DoublesAndCaps(int attempt, int expected)
        {
            Assert.That(AtomicFileStorage.GetRetryDelayMilliseconds(attempt), Is.EqualTo(expected));
        }

        [Test]
        public void ReadAllBytes_MissingFile_ReturnsNull()
        {
            AtomicFileStorage storage = CreateStorage(false);

            Assert.That(storage.ReadAllBytes(SlotPath), Is.Null);
            Assert.That(storage.FileExists(SlotPath), Is.False);
        }

        [Test]
        public void MoveFile_DestinationExists_Throws_AndKeepsBoth()
        {
            AtomicFileStorage storage = CreateStorage(false);
            storage.WriteAtomic("a/source.json", Utf8("source"));
            storage.WriteAtomic("a/destination.json", Utf8("destination"));

            Assert.Throws<IOException>(() => storage.MoveFile("a/source.json", "a/destination.json"));

            Assert.That(File.ReadAllBytes(Full("a/source.json")), Is.EqualTo(Utf8("source")));
            Assert.That(File.ReadAllBytes(Full("a/destination.json")), Is.EqualTo(Utf8("destination")));
        }

        [Test]
        public void MoveFile_CreatesDestinationParent_AndMovesBytes()
        {
            AtomicFileStorage storage = CreateStorage(false);
            storage.WriteAtomic("a/source.json", Utf8("source"));

            storage.MoveFile("a/source.json", "b/c/moved.json");

            Assert.That(storage.FileExists("a/source.json"), Is.False);
            Assert.That(File.ReadAllBytes(Full("b/c/moved.json")), Is.EqualTo(Utf8("source")));
        }

        [Test]
        public void DeleteFile_Missing_IsNoOp_Existing_Removes()
        {
            AtomicFileStorage storage = CreateStorage(false);
            storage.WriteAtomic(SlotPath, Utf8("x"));

            Assert.DoesNotThrow(() => storage.DeleteFile("profiles/guest/missing.json"));
            storage.DeleteFile(SlotPath);

            Assert.That(File.Exists(Full(SlotPath)), Is.False);
        }

        [Test]
        public void MoveDirectory_MovesTree_DestinationExistsThrows()
        {
            AtomicFileStorage storage = CreateStorage(false);
            storage.WriteAtomic(SlotPath, Utf8("guest"));
            storage.WriteAtomic("profiles/acc-taken/player.json", Utf8("taken"));

            Assert.Throws<IOException>(() => storage.MoveDirectory(SlotDirectory, "profiles/acc-taken"));
            storage.MoveDirectory(SlotDirectory, "profiles/acc-new");

            Assert.That(storage.DirectoryExists(SlotDirectory), Is.False);
            Assert.That(File.ReadAllBytes(Full("profiles/acc-new/player.json")), Is.EqualTo(Utf8("guest")));
            Assert.That(File.ReadAllBytes(Full("profiles/acc-taken/player.json")), Is.EqualTo(Utf8("taken")));
        }

        [Test]
        public void DeleteDirectory_RemovesTree_MissingIsNoOp()
        {
            AtomicFileStorage storage = CreateStorage(false);
            storage.WriteAtomic("profiles/guest/nested/deep.json", Utf8("x"));

            storage.DeleteDirectory(SlotDirectory);

            Assert.That(Directory.Exists(Full(SlotDirectory)), Is.False);
            Assert.That(Directory.Exists(Full("profiles")), Is.True);
            Assert.DoesNotThrow(() => storage.DeleteDirectory("profiles/missing"));
        }

        [Test]
        public void ListNames_SortedOrdinal_MissingDirectoryEmpty()
        {
            AtomicFileStorage storage = CreateStorage(false);
            storage.WriteAtomic("dir/b.json", Utf8("b"));
            storage.WriteAtomic("dir/B.json.x", Utf8("B"));
            storage.WriteAtomic("dir/a.json", Utf8("a"));
            storage.CreateDirectory("dir/sub2");
            storage.CreateDirectory("dir/sub1");

            Assert.That(storage.ListFileNames("dir"), Is.EqualTo(new[] { "B.json.x", "a.json", "b.json" }));
            Assert.That(storage.ListDirectoryNames("dir"), Is.EqualTo(new[] { "sub1", "sub2" }));
            Assert.That(storage.ListFileNames("missing"), Is.Empty);
            Assert.That(storage.ListDirectoryNames("missing"), Is.Empty);
        }

        [TestCase("")]
        [TestCase("../escape.json")]
        [TestCase("profiles/../../escape.json")]
        [TestCase("./player.json")]
        [TestCase("profiles//player.json")]
        [TestCase("profiles/")]
        [TestCase("profiles\\..\\..\\escape.json")]
        public void Paths_OutsideRootOrMalformed_AreRejected(string relativePath)
        {
            AtomicFileStorage storage = CreateStorage(false);

            Assert.Throws<ArgumentException>(() => storage.WriteAtomic(relativePath, Utf8("x")));
            Assert.Throws<ArgumentException>(() => storage.ReadAllBytes(relativePath));
            Assert.Throws<ArgumentException>(() => storage.DeleteFile(relativePath));
            Assert.That(File.Exists(Path.Combine(Path.GetDirectoryName(_root), "escape.json")), Is.False);
        }

        [Test]
        public void Paths_Rooted_AreRejected()
        {
            AtomicFileStorage storage = CreateStorage(false);
            string rooted = Path.Combine(_root, "absolute.json");

            Assert.Throws<ArgumentException>(() => storage.WriteAtomic(rooted, Utf8("x")));
            Assert.Throws<ArgumentException>(() => storage.MoveFile(SlotPath, rooted));
            Assert.Throws<ArgumentException>(() => storage.DeleteDirectory(_root));
        }

        [Test]
        public void Constructor_InvalidArguments_Throw()
        {
            Assert.Throws<ArgumentException>(() => new AtomicFileStorage(null, 3, _fileOps, true));
            Assert.Throws<ArgumentException>(() => new AtomicFileStorage(string.Empty, 3, _fileOps, true));
            Assert.Throws<ArgumentOutOfRangeException>(() => new AtomicFileStorage(_root, -1, _fileOps, true));
            Assert.Throws<ArgumentNullException>(() => new AtomicFileStorage(_root, 3, null, true));
            Assert.That(new AtomicFileStorage(_root, 0, _fileOps, true).RootDirectory, Is.EqualTo(Path.GetFullPath(_root)));
        }

        [Test]
        public void WriteAtomic_NullBytes_Throws()
        {
            AtomicFileStorage storage = CreateStorage(false);

            Assert.Throws<ArgumentNullException>(() => storage.WriteAtomic(SlotPath, null));
        }

        private AtomicFileStorage CreateStorage(bool retryTransientFailures)
        {
            return new AtomicFileStorage(_root, AtomicFileStorage.DefaultReplaceRetryCount, _fileOps, retryTransientFailures);
        }

        private string Full(string relativePath)
        {
            return Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        private static Exception CreateNonRetryable(string kind)
        {
            switch (kind)
            {
                case "FileNotFound":
                    return new FileNotFoundException("Scripted missing file.");
                case "DirectoryNotFound":
                    return new DirectoryNotFoundException("Scripted missing directory.");
                case "PathTooLong":
                    return new PathTooLongException("Scripted long path.");
                case "DiskFull":
                    return new IOException("Scripted disk full.", unchecked((int)0x80070070));
                default:
                    return new SaveStorageException(LocalWriteErrorKind.AccessDenied, "Scripted storage exception.");
            }
        }

        private static byte[] EnvelopeBytes(int coins, long revision)
        {
            return EnvelopeCodec.Encode(new SaveEnvelope { Schema = 1, Revision = revision, Data = new JObject { { "Coins", coins } } }).Bytes;
        }

        private static int IndexOf(IReadOnlyList<FileOpCall> calls, FileOpKind kind, string pathSuffix, string destinationSuffix)
        {
            for (int i = 0; i < calls.Count; i++)
            {
                FileOpCall call = calls[i];
                if (call.Kind != kind || call.Path == null || !Normalize(call.Path).EndsWith(pathSuffix, StringComparison.Ordinal))
                {
                    continue;
                }

                if (destinationSuffix == null || (call.DestinationPath != null && Normalize(call.DestinationPath).EndsWith(destinationSuffix, StringComparison.Ordinal)))
                {
                    return i;
                }
            }

            return -1;
        }

        private static int CountMoves(IReadOnlyList<FileOpCall> calls, string sourceSuffix)
        {
            int count = 0;
            foreach (FileOpCall call in calls)
            {
                if (call.Kind == FileOpKind.MoveFile && call.Path != null && Normalize(call.Path).EndsWith(sourceSuffix, StringComparison.Ordinal))
                {
                    count++;
                }
            }

            return count;
        }

        private static string Describe(IReadOnlyList<FileOpCall> calls)
        {
            var builder = new StringBuilder();
            foreach (FileOpCall call in calls)
            {
                builder.AppendLine(call.ToString());
            }

            return builder.ToString();
        }

        private static string Normalize(string path)
        {
            return path.Replace('\\', '/');
        }

        private static byte[] Utf8(string text)
        {
            return new UTF8Encoding(false).GetBytes(text);
        }
    }
}
