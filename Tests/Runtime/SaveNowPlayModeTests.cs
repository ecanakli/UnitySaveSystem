using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Ecanakli.SaveSystem.Tests
{
    /// <summary>
    /// SaveNowAsync with OffloadIo = true on a real player loop (04a A1 F4, A5): cancellation after the write started is
    /// ignored, and the promoted bytes are on disk with at least the call revision when the task completes.
    /// </summary>
    [TestFixture]
    public sealed class SaveNowPlayModeTests
    {
        private const int MaxFrames = 240;

        [UnityTest]
        public IEnumerator SaveNowAsync_CancelAfterWriteStarted_ReturnsResult()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                var inner = new InMemorySaveStorage();
                var storage = new GatedSaveStorage(inner);
                var logger = new TestSaveLogger();
                var slot = new ProfileSlot(TestSlotKeys.Player, SyncMode.LocalOnly);

                // Manual clock: nothing here waits on time, and the scheduled lane must stay asleep
                SaveServiceOptions options = TestServiceFactory.CreateOptions(new ManualSaveClock(), logger);
                options.OffloadIo = true;

                var service = new SaveService(options, storage, NullCloudSaveProvider.Instance, new SaveSlot[] { slot });
                var cts = new CancellationTokenSource();
                try
                {
                    InitializeResult init = await service.InitializeAsync(CancellationToken.None);
                    Assert.That(init.IsSuccess, Is.True, init.ToString());

                    string path = TestPaths.ProfileSlot(ProfileId.Guest, TestSlotKeys.Player);
                    Assert.That(slot.Mutate(data => data.Coins = 42), Is.True);
                    long revisionAtCall = slot.Revision;

                    storage.BlockNextWrite(path);
                    UniTask<SaveResult> save = slot.SaveNowAsync(cts.Token);

                    await AsyncTestUtility.WaitUntilAsync(() => storage.BlockedWriteEntered, MaxFrames, "Write started on the thread pool");
                    Assert.That(
                        storage.BlockedWriteThreadId, Is.Not.EqualTo(Thread.CurrentThread.ManagedThreadId), "Premise: the write runs on the thread pool.");
                    Assert.That(inner.GetWriteCount(path), Is.EqualTo(0), "Premise: the write is still blocked.");

                    // Caller cancellation is honored only before the write starts
                    cts.Cancel();
                    storage.ReleaseBlockedWrite();

                    SaveResult result = await save;

                    Assert.That(result.Status, Is.EqualTo(SaveStatus.Success), result.ToString());
                    Assert.That(result.Error, Is.Null);
                    Assert.That(result.DurableRevision, Is.GreaterThanOrEqualTo(revisionAtCall));
                    Assert.That(inner.GetWriteCount(path), Is.EqualTo(1), "The started write is never abandoned.");
                    Assert.That(service.FlushLocalNow().IsComplete, Is.True);
                    Assert.That(inner.GetWriteCount(path), Is.EqualTo(1), "The write cleared the dirty flag.");
                    Assert.That(logger.Count(TestLogLevel.Error), Is.EqualTo(0), logger.Describe());
                }
                finally
                {
                    storage.ReleaseBlockedWrite();
                    service.Dispose();
                    storage.Dispose();
                    cts.Dispose();
                }
            });
        }

        [UnityTest]
        public IEnumerator SaveNowAsync_WithOffloadIo_LeavesPromotedBytesOnDisk()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                string root = PersistentDataTestRoot.Create();
                var logger = new TestSaveLogger();
                var slot = new ProfileSlot(TestSlotKeys.Player, SyncMode.LocalOnly);
                var storage = new AtomicFileStorage(root);
                SaveServiceOptions options = TestServiceFactory.CreateOptions(new ManualSaveClock(), logger, TestServiceFactory.DefaultDeviceId, root);
                options.OffloadIo = true;

                var service = new SaveService(options, storage, NullCloudSaveProvider.Instance, new SaveSlot[] { slot });
                try
                {
                    InitializeResult init = await service.InitializeAsync(CancellationToken.None);
                    Assert.That(init.IsSuccess, Is.True, init.ToString());

                    Assert.That(slot.Mutate(data => data.Coins = 3), Is.True);
                    Assert.That(slot.Mutate(data => data.Coins = 7), Is.True);
                    long revisionAtCall = slot.Revision;

                    SaveResult result = await slot.SaveNowAsync(CancellationToken.None);

                    Assert.That(result.Status, Is.EqualTo(SaveStatus.Success), result.ToString());
                    Assert.That(result.DurableRevision, Is.GreaterThanOrEqualTo(revisionAtCall));

                    // Read through the file system, not through the service
                    string file = PersistentDataTestRoot.SlotFilePath(root, ProfileId.Guest, TestSlotKeys.Player);
                    Assert.That(File.Exists(file), Is.True, file);
                    Assert.That(File.Exists(file + SaveLayout.TmpSuffix), Is.False, "The tmp file is promoted, never left behind.");

                    EnvelopeDecodeResult decoded = EnvelopeCodec.Decode(File.ReadAllBytes(file), 1);
                    Assert.That(decoded.IsOk, Is.True, decoded.Message);
                    Assert.That(decoded.Checksum, Is.EqualTo(ChecksumStatus.Verified));
                    Assert.That(decoded.Envelope.Revision, Is.GreaterThanOrEqualTo(revisionAtCall));
                    Assert.That(decoded.Envelope.Data.Value<int>("Coins"), Is.EqualTo(7));
                    Assert.That(logger.Count(TestLogLevel.Error), Is.EqualTo(0), logger.Describe());
                }
                finally
                {
                    service.Dispose();
                    PersistentDataTestRoot.Delete(root);
                }
            });
        }

        /// <summary>Blocks one WriteAtomic on the calling thread until the test releases it; everything else delegates.</summary>
        private sealed class GatedSaveStorage : ISaveStorage, IDisposable
        {
            private readonly ISaveStorage _inner;
            private readonly ManualResetEventSlim _release = new ManualResetEventSlim(false);
            private volatile string _blockedPath;
            private volatile bool _entered;
            private volatile int _blockedWriteThreadId;

            public GatedSaveStorage(ISaveStorage inner)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            }

            /// <summary>True once the blocked write reached the gate.</summary>
            public bool BlockedWriteEntered => _entered;

            /// <summary>Managed thread id that entered the gate; 0 before that.</summary>
            public int BlockedWriteThreadId => _blockedWriteThreadId;

            public void BlockNextWrite(string relativePath)
            {
                _entered = false;
                _blockedWriteThreadId = 0;
                _release.Reset();
                _blockedPath = relativePath;
            }

            public void ReleaseBlockedWrite()
            {
                _blockedPath = null;
                _release.Set();
            }

            public void WriteAtomic(string relativePath, byte[] bytes)
            {
                string blocked = _blockedPath;
                if (blocked != null && string.Equals(blocked, relativePath, StringComparison.Ordinal))
                {
                    _blockedPath = null;
                    _blockedWriteThreadId = Thread.CurrentThread.ManagedThreadId;
                    _entered = true;
                    _release.Wait();
                }

                _inner.WriteAtomic(relativePath, bytes);
            }

            public byte[] ReadAllBytes(string relativePath)
            {
                return _inner.ReadAllBytes(relativePath);
            }

            public bool FileExists(string relativePath)
            {
                return _inner.FileExists(relativePath);
            }

            public void DeleteFile(string relativePath)
            {
                _inner.DeleteFile(relativePath);
            }

            public void MoveFile(string sourceRelativePath, string destinationRelativePath)
            {
                _inner.MoveFile(sourceRelativePath, destinationRelativePath);
            }

            public bool DirectoryExists(string relativeDirectory)
            {
                return _inner.DirectoryExists(relativeDirectory);
            }

            public void CreateDirectory(string relativeDirectory)
            {
                _inner.CreateDirectory(relativeDirectory);
            }

            public IReadOnlyList<string> ListFileNames(string relativeDirectory)
            {
                return _inner.ListFileNames(relativeDirectory);
            }

            public IReadOnlyList<string> ListDirectoryNames(string relativeDirectory)
            {
                return _inner.ListDirectoryNames(relativeDirectory);
            }

            public void MoveDirectory(string sourceRelativeDirectory, string destinationRelativeDirectory)
            {
                _inner.MoveDirectory(sourceRelativeDirectory, destinationRelativeDirectory);
            }

            public void DeleteDirectory(string relativeDirectory)
            {
                _inner.DeleteDirectory(relativeDirectory);
            }

            public void Dispose()
            {
                _release.Dispose();
            }
        }
    }
}
