using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Ecanakli.SaveSystem.Tests
{
    /// <summary>
    /// OffloadIo = true on a real player loop (01 section 7): encode and file IO run on the thread pool, every continuation
    /// comes back to the main thread, and slot data is only ever touched on the main thread.
    /// </summary>
    [TestFixture]
    public sealed class ThreadOffloadTests
    {
        private const int MaxFrames = 240;

        private int _mainThreadId;

        [SetUp]
        public void CaptureMainThread()
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        [UnityTest]
        public IEnumerator SaveNowAsync_WithOffloadIo_WritesOnThreadPool_ResumesOnMainThread()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                var slot = new ProfileSlot(TestSlotKeys.Player, SyncMode.LocalOnly);
                using (TestServiceContext context = CreateContext(slot))
                {
                    await InitializeAsync(context);
                    context.Storage.ResetCounters();

                    string path = TestPaths.ProfileSlot(ProfileId.Guest, TestSlotKeys.Player);
                    Assert.That(slot.Mutate(data => data.Coins = 9), Is.True);

                    SaveResult result = await slot.SaveNowAsync(CancellationToken.None);

                    Assert.That(Thread.CurrentThread.ManagedThreadId, Is.EqualTo(_mainThreadId), "SaveNowAsync must resume on the main thread.");
                    Assert.That(result.IsSuccess, Is.True, result.ToString());
                    Assert.That(context.Storage.GetWriteCount(path), Is.EqualTo(1));
                    Assert.That(context.Storage.WriteThreadIds.Count, Is.EqualTo(1), Describe(context.Storage.WriteThreadIds));
                    Assert.That(
                        ContainsThread(context.Storage.WriteThreadIds, _mainThreadId), Is.False, "The write itself must run on the thread pool.");
                    Assert.That(context.Logger.Count(TestLogLevel.Error), Is.EqualTo(0), context.Logger.Describe());
                }
            });
        }

        [UnityTest]
        public IEnumerator ScheduledWrite_WithOffloadIo_RunsOnThreadPool_ResultAppliedOnMainThread()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                var slot = new ProfileSlot(TestSlotKeys.Player, SyncMode.LocalOnly);

                // Real clock: the lane sleeps through ISaveClock.Delay, and a zero delay dispatches on the next frame
                using (TestServiceContext context = CreateContext(slot, UseRealClockWithoutWriteDelay))
                {
                    await InitializeAsync(context);
                    context.Storage.ResetCounters();

                    string path = TestPaths.ProfileSlot(ProfileId.Guest, TestSlotKeys.Player);
                    Assert.That(slot.Mutate(data => data.Coins = 12), Is.True);

                    await AsyncTestUtility.WaitUntilAsync(() => context.Storage.GetWriteCount(path) >= 1, MaxFrames, "Scheduled local write");

                    Assert.That(
                        ContainsThread(context.Storage.WriteThreadIds, _mainThreadId), Is.False,
                        "A scheduled write must not run on the main thread: " + Describe(context.Storage.WriteThreadIds));

                    // Clearing the dirty flag happens in the main-thread continuation, one or two frames after the bytes land
                    await AsyncTestUtility.WaitFramesAsync(3);
                    Assert.That(context.Service.FlushLocalNow().IsComplete, Is.True);
                    Assert.That(context.Storage.GetWriteCount(path), Is.EqualTo(1), "The continuation cleared the dirty flag, so the flush wrote nothing.");
                    Assert.That(context.Logger.Count(TestLogLevel.Error), Is.EqualTo(0), context.Logger.Describe());
                }
            });
        }

        [UnityTest]
        public IEnumerator InitializeAsync_WithOffloadIo_CompletesOnMainThread()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                var slot = new ProfileSlot(TestSlotKeys.Player, SyncMode.LocalOnly);
                using (TestServiceContext context = CreateContext(slot))
                {
                    InitializeResult result = await context.Service.InitializeAsync(CancellationToken.None);

                    Assert.That(Thread.CurrentThread.ManagedThreadId, Is.EqualTo(_mainThreadId), "InitializeAsync must resume on the main thread.");
                    Assert.That(result.IsSuccess, Is.True, result.ToString());
                    Assert.That(context.Service.IsReady, Is.True);
                    Assert.That(slot.State, Is.EqualTo(SlotState.Ready));
                }
            });
        }

        [UnityTest]
        public IEnumerator FlushAsync_WithOffloadIo_CompletesOnMainThread_AndWritesLocallyOnIt()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                var slot = new ProfileSlot(TestSlotKeys.Player, SyncMode.LocalOnly);
                using (TestServiceContext context = CreateContext(slot))
                {
                    await InitializeAsync(context);
                    context.Storage.ResetCounters();

                    string path = TestPaths.ProfileSlot(ProfileId.Guest, TestSlotKeys.Player);
                    Assert.That(slot.Mutate(data => data.Coins = 21), Is.True);

                    FlushResult result = await context.Service.FlushAsync(CancellationToken.None);

                    Assert.That(Thread.CurrentThread.ManagedThreadId, Is.EqualTo(_mainThreadId), "FlushAsync must resume on the main thread.");
                    Assert.That(result.Status, Is.EqualTo(SaveStatus.Success), result.ToString());
                    Assert.That(result.Local.IsComplete, Is.True, result.ToString());
                    Assert.That(context.Storage.GetWriteCount(path), Is.EqualTo(1));

                    // The strict flush never offloads: its writes finish before it returns
                    Assert.That(
                        context.Storage.WriteThreadIds, Is.EqualTo(new[] { _mainThreadId }), Describe(context.Storage.WriteThreadIds));
                }
            });
        }

        [UnityTest]
        public IEnumerator SlotData_WithOffloadIo_IsOnlyTouchedOnTheMainThread()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                var slot = new ThreadProbeSlot();
                using (TestServiceContext context = CreateContext(slot, UseRealClockWithoutWriteDelay))
                {
                    await InitializeAsync(context);
                    string path = TestPaths.ProfileSlot(ProfileId.Guest, ThreadProbeSlot.SlotKey);
                    context.Storage.ResetCounters();

                    Assert.That(slot.SetCoins(4), Is.True);
                    SaveResult saved = await slot.SaveNowAsync(CancellationToken.None);
                    Assert.That(saved.IsSuccess, Is.True, saved.ToString());

                    Assert.That(slot.SetCoins(5), Is.True);
                    await AsyncTestUtility.WaitUntilAsync(() => context.Storage.GetWriteCount(path) >= 2, MaxFrames, "Scheduled local write");

                    // The lane yields a frame before dispatching, so this flush is the one that writes revision 3
                    Assert.That(slot.SetCoins(6), Is.True);
                    FlushResult flushed = await context.Service.FlushAsync(CancellationToken.None);
                    Assert.That(flushed.Status, Is.EqualTo(SaveStatus.Success), flushed.ToString());
                    Assert.That(slot.ReadCoins(), Is.EqualTo(6));

                    // Only the JSON snapshot taken on the main thread crosses to the thread pool
                    Assert.That(slot.Probe.ThreadIds, Is.EqualTo(new[] { _mainThreadId }), Describe(slot.Probe.ThreadIds));
                    Assert.That(
                        ContainsThread(context.Storage.WriteThreadIds, _mainThreadId), Is.True,
                        "Premise: the strict flush wrote on the main thread.");
                    Assert.That(context.Storage.WriteThreadIds.Count, Is.GreaterThan(1), "Premise: some write ran on the thread pool.");
                }
            });
        }

        private static void UseRealClockWithoutWriteDelay(SaveServiceOptions options)
        {
            options.Clock = UnitySaveClock.Instance;
            options.LocalWriteDelay = TimeSpan.Zero;
        }

        private static TestServiceContext CreateContext(SaveSlot slot, Action<SaveServiceOptions> configure = null)
        {
            return TestServiceFactory.Create(new TestServiceSetup
            {
                Slots = new[] { slot },
                ConfigureOptions = options =>
                {
                    options.OffloadIo = true;
                    configure?.Invoke(options);
                },
            });
        }

        private static async UniTask InitializeAsync(TestServiceContext context)
        {
            InitializeResult result = await context.Service.InitializeAsync(CancellationToken.None);
            Assert.That(result.IsSuccess, Is.True, result.ToString());
        }

        private static bool ContainsThread(IReadOnlyList<int> threadIds, int threadId)
        {
            for (int i = 0; i < threadIds.Count; i++)
            {
                if (threadIds[i] == threadId)
                {
                    return true;
                }
            }

            return false;
        }

        private static string Describe(IReadOnlyList<int> threadIds)
        {
            var text = new System.Text.StringBuilder("thread ids: ");
            for (int i = 0; i < threadIds.Count; i++)
            {
                text.Append(i == 0 ? string.Empty : ", ").Append(threadIds[i]);
            }

            return text.ToString();
        }

        /// <summary>Records every thread that reads or writes the data properties.</summary>
        private sealed class ThreadProbe
        {
            private readonly object _sync = new object();
            private readonly HashSet<int> _threadIds = new HashSet<int>();

            public IReadOnlyList<int> ThreadIds
            {
                get
                {
                    lock (_sync)
                    {
                        var ids = new List<int>(_threadIds);
                        ids.Sort();
                        return ids;
                    }
                }
            }

            public void Record()
            {
                lock (_sync)
                {
                    _threadIds.Add(Thread.CurrentThread.ManagedThreadId);
                }
            }
        }

        private sealed class ThreadProbeData
        {
            private int _coins;

            /// <summary>Attached by Normalize; never serialized.</summary>
            [JsonIgnore]
            public ThreadProbe Probe { get; set; }

            public int Coins
            {
                get
                {
                    Probe?.Record();
                    return _coins;
                }

                set
                {
                    Probe?.Record();
                    _coins = value;
                }
            }
        }

        private sealed class ThreadProbeSlot : SaveSlot<ThreadProbeData>
        {
            public const string SlotKey = "thread_probe";

            public ThreadProbe Probe { get; } = new ThreadProbe();

            public override string Key => SlotKey;

            public override SyncMode SyncMode => SyncMode.LocalOnly;

            public override SlotScope Scope => SlotScope.Profile;

            public bool SetCoins(int coins)
            {
                return Mutate(coins, (data, value) => data.Coins = value);
            }

            public int ReadCoins()
            {
                return Read(data => data.Coins);
            }

            // Every instance that enters memory passes through Normalize
            protected override void Normalize(ThreadProbeData data)
            {
                data.Probe = Probe;
            }
        }
    }
}
