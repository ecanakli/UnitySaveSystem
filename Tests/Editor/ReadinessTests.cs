using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Ecanakli.SaveSystem.Tests
{
    /// <summary>F5 readiness: signal timing around init and activation, listener awaits, Dispose, access-before-ready warning.</summary>
    [TestFixture]
    public sealed class ReadinessTests
    {
        private const int MaxFrames = 120;
        private const string AccessWarning = "before it was loaded";

        [Test]
        public void WhenReadyAsync_BeforeInit_CompletesAfterSlotsLoadAndBeforeRestoreListeners()
        {
            var player = new ProfileSlot();
            using (TestServiceContext context = CreateContext(null, player))
            {
                UniTask<bool> ready = context.Service.WhenReadyAsync(CancellationToken.None).Preserve();
                Assert.That(ready.Status.IsCompleted(), Is.False);
                Assert.That(context.Service.IsReady, Is.False);

                var events = new List<string>();
                bool completedDuringLoad = false;
                StorageFault spy = context.Storage.Fail(StorageOperation.All, PlayerPathPrefix(ProfileId.Guest), () =>
                {
                    completedDuringLoad |= ready.Status.IsCompleted() || context.Service.IsReady;
                    return null;
                });
                var listener = new DelegateRestoreListener(0, (report, ct) =>
                {
                    events.Add("listener ready=" + context.Service.IsReady);
                    return UniTask.CompletedTask;
                });
                context.Service.AddRestoreListener(listener);
                try
                {
                    UniTask waiter = RecordWhenReadyAsync(context.Service, player, events).Preserve();

                    InitializeResult result = context.InitializeSync();
                    AsyncTestUtility.RunSync(waiter, "Readiness waiter");

                    Assert.That(result.IsSuccess, Is.True, result.ToString());
                    Assert.That(spy.HitCount, Is.GreaterThan(0), "Premise: the slot load touched the player file.");
                    Assert.That(completedDuringLoad, Is.False, "Readiness is not signalled while slots load.");
                    Assert.That(AsyncTestUtility.RunSync(ready), Is.True);
                    Assert.That(events, Is.EqualTo(new[] { "ready=True slot=Ready profile=guest", "listener ready=True" }), "Ready before restore listeners run.");
                }
                finally
                {
                    context.Storage.RemoveFault(spy);
                    context.Service.RemoveRestoreListener(listener);
                }
            }
        }

        [UnityTest]
        public IEnumerator WhenReadyAsync_FromInitAndActivationListeners_ReturnsImmediately()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                using (TestServiceContext context = CreateContext(null, new ProfileSlot()))
                {
                    var calls = new List<string>();
                    var listener = new DelegateRestoreListener(0, async (report, ct) =>
                    {
                        UniTask<bool> ready = context.Service.WhenReadyAsync(ct);
                        bool completedAtCall = ready.Status.IsCompleted();
                        bool result = await ready;
                        calls.Add(report.Trigger + " completedAtCall=" + completedAtCall + " result=" + result);
                    });
                    context.Service.AddRestoreListener(listener);
                    try
                    {
                        UniTask<InitializeResult> init = context.Service.InitializeAsync(CancellationToken.None).Preserve();
                        await AsyncTestUtility.WaitUntilAsync(() => init.Status.IsCompleted(), MaxFrames, "Initialize with a listener awaiting readiness");
                        InitializeResult initialized = await init;
                        Assert.That(initialized.IsSuccess, Is.True, initialized.ToString());

                        UniTask<ProfileActivationResult> activation = context.Service.ActivateProfileAsync(ProfileId.Local("next"), CancellationToken.None).Preserve();
                        await AsyncTestUtility.WaitUntilAsync(() => activation.Status.IsCompleted(), MaxFrames, "Activation with a listener awaiting readiness");
                        ProfileActivationResult activated = await activation;
                        Assert.That(activated.IsSuccess, Is.True, activated.ToString());

                        Assert.That(calls, Is.EqualTo(new[]
                        {
                            "ProfileActivated completedAtCall=True result=True",
                            "ProfileActivated completedAtCall=True result=True",
                        }));
                    }
                    finally
                    {
                        context.Service.RemoveRestoreListener(listener);
                    }
                }
            });
        }

        [UnityTest]
        public IEnumerator DuringActivation_NotReady_CompletesAfterTheNewProfileLoads()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                var player = new ProfileSlot();
                using (TestServiceContext context = CreateContext(null, player))
                {
                    Assert.That(context.InitializeSync().IsSuccess, Is.True);
                    ProfileId next = ProfileId.Local("next");
                    var events = new List<string>();
                    bool readyInHook = true;
                    bool readyDuringLoad = false;
                    UniTask<bool> ready = default;
                    bool readyCreated = false;

                    var hook = new HoldingDeactivationListener(0) { OnCalled = () => readyInHook = context.Service.IsReady };
                    var listener = new DelegateRestoreListener(0, (report, ct) =>
                    {
                        events.Add("listener ready=" + context.Service.IsReady);
                        return UniTask.CompletedTask;
                    });
                    StorageFault spy = context.Storage.Fail(StorageOperation.All, PlayerPathPrefix(next), () =>
                    {
                        readyDuringLoad |= context.Service.IsReady || (readyCreated && ready.Status.IsCompleted());
                        return null;
                    });
                    context.Service.AddDeactivationListener(hook);
                    context.Service.AddRestoreListener(listener);
                    try
                    {
                        UniTask<ProfileActivationResult> activation = context.Service.ActivateProfileAsync(next, CancellationToken.None).Preserve();
                        await AsyncTestUtility.WaitUntilAsync(() => hook.CallCount == 1, MaxFrames, "Deactivation hook");

                        Assert.That(readyInHook, Is.False, "Readiness resets before the hooks run.");
                        Assert.That(context.Service.IsReady, Is.False);
                        ready = context.Service.WhenReadyAsync(CancellationToken.None).Preserve();
                        readyCreated = true;
                        UniTask waiter = RecordWhenReadyAsync(context.Service, player, events).Preserve();
                        await AsyncTestUtility.WaitFramesAsync(3);

                        Assert.That(ready.Status.IsCompleted(), Is.False, "Waiters block while the activation is in progress.");
                        Assert.That(context.Service.ActiveProfile, Is.EqualTo(ProfileId.Guest));

                        hook.Release();
                        await AsyncTestUtility.WaitUntilAsync(() => activation.Status.IsCompleted() && waiter.Status.IsCompleted(), MaxFrames, "Activation and readiness");

                        ProfileActivationResult result = await activation;
                        Assert.That(result.IsSuccess, Is.True, result.ToString());
                        Assert.That(await ready, Is.True);
                        Assert.That(spy.HitCount, Is.GreaterThan(0), "Premise: the incoming load touched the player file.");
                        Assert.That(readyDuringLoad, Is.False, "Readiness is not signalled while the new profile loads.");
                        Assert.That(events, Is.EqualTo(new[] { "ready=True slot=Ready profile=local:next", "listener ready=True" }));
                        Assert.That(context.Service.IsReady, Is.True);
                    }
                    finally
                    {
                        hook.Release();
                        context.Storage.RemoveFault(spy);
                        context.Service.RemoveDeactivationListener(hook);
                        context.Service.RemoveRestoreListener(listener);
                    }
                }
            });
        }

        // S5: a failed init completes its waiters, like a failed switch does; only Dispose used to release them
        [Test]
        public void FailedInitialize_PendingWaiter_Completes()
        {
            var storage = new InMemorySaveStorage();
            var logger = new TestSaveLogger();
            var clock = new ThrowingClock(new ManualSaveClock());
            SaveServiceOptions options = TestServiceFactory.CreateOptions(clock, logger, null);
            options.CloudRetryCount = 0;
            var service = new SaveService(options, storage, new FakeCloudSaveProvider(), new SaveSlot[] { new ProfileSlot() });
            try
            {
                // device.json cannot be written, and recording that failure asks the clock for the time
                storage.FailWithKind(StorageOperation.Write, TestPaths.DeviceState, LocalWriteErrorKind.DiskFull);
                clock.ThrowOnUtcNow = true;

                UniTask<bool> ready = service.WhenReadyAsync(CancellationToken.None).Preserve();
                Assert.That(ready.Status.IsCompleted(), Is.False, "Premise: the waiter is pending before init.");

                InitializeResult result = AsyncTestUtility.RunSync(service.InitializeAsync(CancellationToken.None), nameof(ISaveService.InitializeAsync));

                Assert.That(result.IsSuccess, Is.False, result.ToString());
                Assert.That(service.IsInitialized, Is.False);
                Assert.That(logger.Contains(TestLogLevel.Error, "Initialization failed"), Is.True, logger.Describe());
                Assert.That(ready.Status.IsCompleted(), Is.True, "A failed init must not strand readiness waiters.");
                Assert.That(
                    AsyncTestUtility.RunSync(ready, "Pending readiness waiter"),
                    Is.False,
                    "IsReady means initialized, so a failed init completes its waiters with false.");
                Assert.That(service.IsReady, Is.False);
            }
            finally
            {
                clock.ThrowOnUtcNow = false;
                service.Dispose();
            }
        }

        [Test]
        public void Dispose_PendingWaiterBeforeInit_CompletesFalse()
        {
            TestServiceContext context = CreateContext(null, new ProfileSlot());
            try
            {
                UniTask<bool> ready = context.Service.WhenReadyAsync(CancellationToken.None).Preserve();
                Assert.That(ready.Status.IsCompleted(), Is.False);

                context.Dispose();

                Assert.That(AsyncTestUtility.RunSync(ready, "Pending readiness waiter"), Is.False);
                Assert.That(AsyncTestUtility.RunSync(context.Service.WhenReadyAsync(CancellationToken.None)), Is.False);
                Assert.That(context.Service.IsReady, Is.False);
            }
            finally
            {
                context.Dispose();
            }
        }

        [Test]
        public void Dispose_AfterReady_WhenReadyAsyncReturnsFalse()
        {
            TestServiceContext context = CreateContext(null, new ProfileSlot());
            try
            {
                Assert.That(context.InitializeSync().IsSuccess, Is.True);
                Assert.That(AsyncTestUtility.RunSync(context.Service.WhenReadyAsync(CancellationToken.None)), Is.True);

                context.Dispose();

                Assert.That(context.Service.IsReady, Is.False);
                Assert.That(AsyncTestUtility.RunSync(context.Service.WhenReadyAsync(CancellationToken.None)), Is.False);
            }
            finally
            {
                context.Dispose();
            }
        }

        // Dispose cancels the hook; the aborted activation's finally must not mark the service ready
        [UnityTest]
        public IEnumerator Dispose_DuringActivationHook_PendingWaiterCompletesFalse()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                TestServiceContext context = CreateContext(null, new ProfileSlot());
                var hook = new HoldingDeactivationListener(0);
                try
                {
                    Assert.That(context.InitializeSync().IsSuccess, Is.True);
                    context.Service.AddDeactivationListener(hook);
                    UniTask<ProfileActivationResult> activation = context.Service.ActivateProfileAsync(ProfileId.Local("next"), CancellationToken.None).Preserve();
                    await AsyncTestUtility.WaitUntilAsync(() => hook.CallCount == 1, MaxFrames, "Deactivation hook");
                    UniTask<bool> ready = context.Service.WhenReadyAsync(CancellationToken.None).Preserve();
                    Assert.That(ready.Status.IsCompleted(), Is.False, "Premise: the waiter is pending during the activation.");

                    context.Dispose();
                    await AsyncTestUtility.WaitUntilAsync(() => ready.Status.IsCompleted() && activation.Status.IsCompleted(), MaxFrames, "Waiter and activation after Dispose");

                    ProfileActivationResult result = await activation;
                    Assert.That(result.IsSuccess, Is.False, result.ToString());
                    Assert.That(result.Error.Code, Is.EqualTo(SaveErrorCode.Disposed), result.ToString());
                    Assert.That(context.Service.IsReady, Is.False);
                    Assert.That(await ready, Is.False, "F5: Dispose completes pending waiters with false.");
                }
                finally
                {
                    hook.Release();
                    context.Dispose();
                }
            });
        }

        [Test]
        public void ReadBeforeReady_WarnsOncePerUnloadedPeriod()
        {
            var player = new ProfileSlot();
            using (TestServiceContext context = CreateContext(options => options.WarnOnAccessBeforeReady = true, player))
            {
                Assert.That(player.State, Is.EqualTo(SlotState.Unloaded), "Premise: nothing is loaded before init.");
                player.Read(data => data.Coins);
                player.Read(data => data.Coins);
                Assert.That(player.Mutate(data => data.Coins = 1), Is.False, "Mutate is refused before the load.");
                Assert.That(CountAccessWarnings(context), Is.EqualTo(1), context.Logger.Describe());

                Assert.That(context.InitializeSync().IsSuccess, Is.True);
                player.Read(data => data.Coins);
                Assert.That(player.Mutate(data => data.Coins = 2), Is.True);
                Assert.That(CountAccessWarnings(context), Is.EqualTo(1), "No warning once Ready.");

                // Reads made while the incoming profile loads fall in a new unloaded period
                ProfileId next = ProfileId.Local("next");
                var statesDuringLoad = new List<SlotState>();
                StorageFault spy = context.Storage.Fail(StorageOperation.All, PlayerPathPrefix(next), () =>
                {
                    statesDuringLoad.Add(player.State);
                    player.Read(data => data.Coins);
                    player.Read(data => data.Coins);
                    return null;
                });
                try
                {
                    Assert.That(context.ActivateSync(next).IsSuccess, Is.True);
                }
                finally
                {
                    context.Storage.RemoveFault(spy);
                }

                Assert.That(statesDuringLoad.Count, Is.GreaterThan(0), "Premise: the incoming load touched the player file.");
                Assert.That(statesDuringLoad[0], Is.EqualTo(SlotState.Unloaded), "Premise: the slot was unloaded during the switch.");
                Assert.That(CountAccessWarnings(context), Is.EqualTo(2), context.Logger.Describe());

                player.Read(data => data.Coins);
                Assert.That(CountAccessWarnings(context), Is.EqualTo(2));
            }
        }

        [Test]
        public void ReadBeforeReady_OptionOff_NoWarning()
        {
            var player = new ProfileSlot();
            using (TestServiceContext context = CreateContext(options => options.WarnOnAccessBeforeReady = false, player))
            {
                player.Read(data => data.Coins);
                Assert.That(player.Mutate(data => data.Coins = 1), Is.False);

                Assert.That(CountAccessWarnings(context), Is.EqualTo(0), context.Logger.Describe());
            }
        }

        private static int CountAccessWarnings(TestServiceContext context)
        {
            return context.Logger.Count(TestLogLevel.Warning, AccessWarning);
        }

        // Matches the primary, tmp and bak paths of the player slot
        private static string PlayerPathPrefix(ProfileId profile)
        {
            return TestPaths.ProfileSlot(profile, TestSlotKeys.Player) + "*";
        }

        private static async UniTask RecordWhenReadyAsync(ISaveService service, SaveSlot slot, List<string> events)
        {
            bool ready = await service.WhenReadyAsync(CancellationToken.None);
            events.Add("ready=" + ready + " slot=" + slot.State + " profile=" + service.ActiveProfile);
        }

        private static TestServiceContext CreateContext(Action<SaveServiceOptions> configure, params SaveSlot[] slots)
        {
            return TestServiceFactory.Create(new TestServiceSetup
            {
                Slots = slots,
                ConfigureOptions = options =>
                {
                    options.CloudRetryCount = 0;
                    configure?.Invoke(options);
                },
            });
        }

        /// <summary>Clock whose UtcNow can be made to throw; Delay is delegated.</summary>
        private sealed class ThrowingClock : ISaveClock
        {
            private readonly ISaveClock _inner;

            public ThrowingClock(ISaveClock inner)
            {
                _inner = inner;
            }

            public bool ThrowOnUtcNow { get; set; }

            public DateTime UtcNow => ThrowOnUtcNow ? throw new InvalidOperationException("Scripted clock failure.") : _inner.UtcNow;

            public UniTask Delay(TimeSpan delay, CancellationToken ct)
            {
                return _inner.Delay(delay, ct);
            }
        }

        /// <summary>Restore listener backed by a delegate.</summary>
        private sealed class DelegateRestoreListener : IRestoreListener
        {
            private readonly Func<RestoreReport, CancellationToken, UniTask> _onRestored;

            public DelegateRestoreListener(int order, Func<RestoreReport, CancellationToken, UniTask> onRestored)
            {
                Order = order;
                _onRestored = onRestored;
            }

            public int Order { get; }

            public UniTask OnRestoredAsync(RestoreReport report, CancellationToken ct)
            {
                return _onRestored(report, ct);
            }
        }

        /// <summary>Deactivation hook that stays pending until Release.</summary>
        private sealed class HoldingDeactivationListener : IProfileDeactivatingListener
        {
            private readonly UniTaskCompletionSource _release = new UniTaskCompletionSource();

            public HoldingDeactivationListener(int order)
            {
                Order = order;
            }

            public int Order { get; }

            public int CallCount { get; private set; }

            public Action OnCalled { get; set; }

            public UniTask OnProfileDeactivatingAsync(ProfileDeactivation context, CancellationToken ct)
            {
                CallCount++;
                OnCalled?.Invoke();
                return _release.Task;
            }

            public void Release()
            {
                _release.TrySetResult();
            }
        }
    }
}
