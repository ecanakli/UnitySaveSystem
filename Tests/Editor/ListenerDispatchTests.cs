using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Ecanakli.SaveSystem.Tests
{
    /// <summary>Restore listener dispatch table (02 H): init and activation dispatch, ordering, registration rules, reentrancy, deadlock freedom.</summary>
    [TestFixture]
    public sealed class ListenerDispatchTests
    {
        private const string AccountA = "account-a";
        private const int MaxFrames = 120;

        private static readonly ProfileId ProfileA = ProfileId.Account(AccountA);

        [Test]
        public void Init_DispatchesOnceWithLoadedLocalAndFailedOutcomes_ThenRaisesProfileActivated()
        {
            var settings = new DeviceSettingsSlot();
            var player = new ProfileSlot();
            var broken = new NormalizeThrowsSlot();
            using (TestServiceContext context = CreateContext(settings, player, broken))
            {
                var events = new List<string>();
                var listener = new CountingRestoreListener(0, () => events.Add("listener"));
                var activations = new List<ProfileActivationResult>();
                Action<ProfileActivationResult> onActivated = result =>
                {
                    events.Add("event");
                    activations.Add(result);
                };
                context.Service.AddRestoreListener(listener);
                context.Service.ProfileActivated += onActivated;
                try
                {
                    InitializeResult initialized = context.InitializeSync();

                    Assert.That(initialized.IsSuccess, Is.True, initialized.ToString());
                    Assert.That(events, Is.EqualTo(new[] { "listener", "event" }), "Listeners run before ProfileActivated is raised.");
                    Assert.That(listener.Reports.Count, Is.EqualTo(1));

                    RestoreReport report = listener.Reports[0];
                    Assert.That(report.Trigger, Is.EqualTo(RestoreTrigger.ProfileActivated));
                    Assert.That(report.Status, Is.EqualTo(SaveStatus.Success), report.ToString());
                    Assert.That(report.Profile, Is.EqualTo(ProfileId.Guest));
                    Assert.That(report.Slots.Count, Is.EqualTo(3), report.ToString());
                    Assert.That(report.GetResult(settings).Outcome, Is.EqualTo(SlotRestoreOutcome.LoadedLocal), report.ToString());
                    Assert.That(report.GetResult(player).Outcome, Is.EqualTo(SlotRestoreOutcome.LoadedLocal), report.ToString());
                    Assert.That(report.GetResult(broken).Outcome, Is.EqualTo(SlotRestoreOutcome.Failed), report.ToString());
                    Assert.That(report.GetResult(broken).Failure, Is.EqualTo(SlotRestoreFailure.LocalNormalizeFailed), report.ToString());
                    Assert.That(report.Completeness, Is.EqualTo(RestoreCompleteness.Partial), report.ToString());

                    Assert.That(activations.Count, Is.EqualTo(1));
                    Assert.That(activations[0].IsSuccess, Is.True, activations[0].ToString());
                    Assert.That(activations[0].Profile, Is.EqualTo(ProfileId.Guest));
                    Assert.That(activations[0].Previous, Is.Null, "The initial activation has no previous profile.");
                }
                finally
                {
                    context.Service.ProfileActivated -= onActivated;
                    context.Service.RemoveRestoreListener(listener);
                }
            }
        }

        [Test]
        public void AlreadyActive_DispatchesNothing()
        {
            using (TestServiceContext context = CreateContext(new ProfileSlot()))
            {
                Assert.That(context.InitializeSync().IsSuccess, Is.True);
                var listener = new CountingRestoreListener(0);
                var activations = new List<ProfileActivationResult>();
                Action<ProfileActivationResult> onActivated = activations.Add;
                context.Service.AddRestoreListener(listener);
                context.Service.ProfileActivated += onActivated;
                try
                {
                    ProfileActivationResult result = context.ActivateSync(ProfileId.Guest);

                    Assert.That(result.AlreadyActive, Is.True, result.ToString());
                    Assert.That(listener.CallCount, Is.EqualTo(0));
                    Assert.That(activations.Count, Is.EqualTo(0));
                }
                finally
                {
                    context.Service.ProfileActivated -= onActivated;
                    context.Service.RemoveRestoreListener(listener);
                }
            }
        }

        [Test]
        public void Dispatch_SortsByOrder_TiesRunInRegistrationOrder_EqualOrderWarnsWithBothTypes()
        {
            using (TestServiceContext context = CreateContext(new ProfileSlot()))
            {
                var calls = new List<string>();
                var late = new LateListener(10, calls);
                var zuluTie = new ZuluTieListener(5, calls);
                var alphaTie = new AlphaTieListener(5, calls);
                var early = new EarlyListener(-1, calls);

                // Zulu registers before Alpha, so a name sort would give the opposite tie order
                context.Service.AddRestoreListener(late);
                context.Service.AddRestoreListener(zuluTie);
                context.Service.AddRestoreListener(alphaTie);
                context.Service.AddRestoreListener(early);
                try
                {
                    Assert.That(context.InitializeSync().IsSuccess, Is.True);

                    Assert.That(calls, Is.EqualTo(new[] { nameof(EarlyListener), nameof(ZuluTieListener), nameof(AlphaTieListener), nameof(LateListener) }));

                    List<string> warnings = context.Logger.GetMessages(TestLogLevel.Warning)
                        .Where(message => message.Contains(nameof(ZuluTieListener)) && message.Contains(nameof(AlphaTieListener)))
                        .ToList();
                    Assert.That(warnings.Count, Is.EqualTo(1), context.Logger.Describe());
                }
                finally
                {
                    context.Service.RemoveRestoreListener(late);
                    context.Service.RemoveRestoreListener(zuluTie);
                    context.Service.RemoveRestoreListener(alphaTie);
                    context.Service.RemoveRestoreListener(early);
                }
            }
        }

        // 04b B2 F2, 04 R4: final order logged at verbose on every dispatch of both dispatchers, with the trigger
        [UnityTest]
        public IEnumerator Dispatch_LogsFinalOrderAtVerbose_WithTrigger()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                using (TestServiceContext context = CreateContext(new ProfileSlot()))
                {
                    context.Provider.SignedInAccountId = AccountA;
                    var calls = new List<string>();
                    var late = new LateListener(300, calls);
                    var early = new EarlyListener(100, calls);
                    var middle = new AlphaTieListener(200, calls);
                    var hook = new CountingDeactivationListener(50);

                    // Registration order differs from Order so the log must show the sorted order
                    context.Service.AddRestoreListener(late);
                    context.Service.AddRestoreListener(early);
                    context.Service.AddRestoreListener(middle);
                    context.Service.AddDeactivationListener(hook);
                    try
                    {
                        Assert.That(context.Logger.IsVerboseEnabled, Is.True, "Premise: verbose logging is on.");

                        UniTask<InitializeResult> init = context.Service.InitializeAsync(CancellationToken.None).Preserve();
                        await AsyncTestUtility.WaitUntilAsync(() => init.Status.IsCompleted(), MaxFrames, "Initialize");
                        Assert.That((await init).IsSuccess, Is.True);

                        UniTask<ProfileActivationResult> activation = context.Service.ActivateProfileAsync(ProfileA, CancellationToken.None).Preserve();
                        await AsyncTestUtility.WaitUntilAsync(() => activation.Status.IsCompleted(), MaxFrames, "Activation");
                        ProfileActivationResult activated = await activation;
                        Assert.That(activated.IsSuccess, Is.True, activated.ToString());

                        UniTask<RestoreReport> restore = context.Service.RestoreAsync(CancellationToken.None).Preserve();
                        await AsyncTestUtility.WaitUntilAsync(() => restore.Status.IsCompleted(), MaxFrames, "Restore");
                        RestoreReport report = await restore;
                        Assert.That(report.Status, Is.EqualTo(SaveStatus.Success), report.ToString());

                        const string restoreOrder = "100 EarlyListener, 200 AlphaTieListener, 300 LateListener";
                        Assert.That(DispatchOrderLogs(context), Is.EqualTo(new[]
                        {
                            "[SaveSystem] Dispatching restore listeners (ProfileActivated): " + restoreOrder,
                            "[SaveSystem] Dispatching deactivation listeners (ProfileDeactivating): 50 CountingDeactivationListener",
                            "[SaveSystem] Dispatching restore listeners (ProfileActivated): " + restoreOrder,
                            "[SaveSystem] Dispatching restore listeners (CloudRestore): " + restoreOrder,
                        }), context.Logger.Describe(TestLogLevel.Verbose));

                        string[] oneDispatch = { nameof(EarlyListener), nameof(AlphaTieListener), nameof(LateListener) };
                        Assert.That(calls, Is.EqualTo(oneDispatch.Concat(oneDispatch).Concat(oneDispatch)), "The logged order is the order that ran.");

                        // Verbose off: no order log, dispatch still runs
                        context.Logger.Clear();
                        context.Logger.IsVerboseEnabled = false;
                        UniTask<ProfileActivationResult> quiet = context.Service.ActivateProfileAsync(ProfileId.Local("next"), CancellationToken.None).Preserve();
                        await AsyncTestUtility.WaitUntilAsync(() => quiet.Status.IsCompleted(), MaxFrames, "Activation with verbose off");
                        Assert.That((await quiet).IsSuccess, Is.True);

                        Assert.That(DispatchOrderLogs(context), Is.Empty);
                        Assert.That(calls.Count, Is.EqualTo(12));
                        Assert.That(hook.CallCount, Is.EqualTo(2));
                    }
                    finally
                    {
                        context.Service.RemoveRestoreListener(late);
                        context.Service.RemoveRestoreListener(early);
                        context.Service.RemoveRestoreListener(middle);
                        context.Service.RemoveDeactivationListener(hook);
                    }
                }
            });
        }

        // 04b B2 F2: distinct Order values never warn; the equal-Order control proves the filter matches
        [Test]
        public void AddRestoreListener_DistinctOrder_NoWarning()
        {
            using (TestServiceContext context = CreateContext(new ProfileSlot()))
            {
                var calls = new List<string>();
                var early = new EarlyListener(0, calls);
                var alpha = new AlphaTieListener(10, calls);
                var late = new LateListener(20, calls);
                var zulu = new ZuluTieListener(10, calls);
                var hookA = new CountingDeactivationListener(0);
                var hookB = new CountingDeactivationListener(10);
                var tie = new AlphaTieListener(20, calls);
                var hookTie = new CountingDeactivationListener(10);
                try
                {
                    context.Service.AddRestoreListener(late);
                    context.Service.AddRestoreListener(early);
                    context.Service.AddRestoreListener(alpha);
                    context.Service.AddRestoreListener(alpha);
                    context.Service.AddDeactivationListener(hookA);
                    context.Service.AddDeactivationListener(hookB);
                    Assert.That(OrderWarnings(context), Is.Empty, "Distinct orders and an ignored duplicate never warn. " + context.Logger.Describe());

                    // A removed listener is no longer a tie
                    context.Service.RemoveRestoreListener(alpha);
                    context.Service.AddRestoreListener(zulu);
                    Assert.That(OrderWarnings(context), Is.Empty, context.Logger.Describe());

                    Assert.That(context.InitializeSync().IsSuccess, Is.True);
                    Assert.That(calls, Is.EqualTo(new[] { nameof(EarlyListener), nameof(ZuluTieListener), nameof(LateListener) }));
                    Assert.That(OrderWarnings(context), Is.Empty, "Dispatch itself never warns about order. " + context.Logger.Describe());

                    // Control: equal Order on either dispatcher warns once per add
                    context.Service.AddRestoreListener(tie);
                    context.Service.AddDeactivationListener(hookTie);
                    List<string> warnings = OrderWarnings(context);
                    Assert.That(warnings.Count, Is.EqualTo(2), context.Logger.Describe());
                    Assert.That(warnings[0], Does.Contain("restore listener " + nameof(AlphaTieListener) + " has Order 20"));
                    Assert.That(warnings[0], Does.Contain(nameof(LateListener)));
                    Assert.That(warnings[1], Does.Contain("deactivation listener " + nameof(CountingDeactivationListener) + " has Order 10"));
                }
                finally
                {
                    context.Service.RemoveRestoreListener(early);
                    context.Service.RemoveRestoreListener(alpha);
                    context.Service.RemoveRestoreListener(late);
                    context.Service.RemoveRestoreListener(zulu);
                    context.Service.RemoveRestoreListener(tie);
                    context.Service.RemoveDeactivationListener(hookA);
                    context.Service.RemoveDeactivationListener(hookB);
                    context.Service.RemoveDeactivationListener(hookTie);
                }
            }
        }

        // 04b B2 order ranges: a derived cache (1000-1999) registered before its source cache (0-999) still sees the refreshed source
        [Test]
        public void Dispatch_DependentCacheWithHigherOrder_SeesRefreshedSource()
        {
            var player = new ProfileSlot();
            using (TestServiceContext context = CreateContext(player))
            {
                var source = new CoinsSourceCache(100, player);
                var derived = new CoinsDerivedCache(1000, source, player);
                context.Service.AddRestoreListener(derived);
                context.Service.AddRestoreListener(source);
                try
                {
                    Assert.That(context.InitializeSync().IsSuccess, Is.True);
                    Assert.That(player.Mutate(data => data.Coins = 5), Is.True);

                    // The outgoing guest is flushed; the new profile loads defaults while the source still holds 5
                    Assert.That(context.ActivateSync(ProfileId.Local("other")).IsSuccess, Is.True);
                    Assert.That(player.Mutate(data => data.Coins = 9), Is.True);

                    // Back to guest: the source still holds 9 until it is refreshed
                    Assert.That(context.ActivateSync(ProfileId.Guest).IsSuccess, Is.True);

                    Assert.That(derived.Seen, Is.EqualTo(new[] { "source=0 slot=0", "source=0 slot=0", "source=5 slot=5" }), "A stale source would show -1, 5 and 9.");
                    Assert.That(derived.DoubledCoins, Is.EqualTo(10));
                    Assert.That(source.Coins, Is.EqualTo(5));
                }
                finally
                {
                    context.Service.RemoveRestoreListener(derived);
                    context.Service.RemoveRestoreListener(source);
                }
            }
        }

        [Test]
        public void DuplicateAdd_IsIgnored()
        {
            using (TestServiceContext context = CreateContext(new ProfileSlot()))
            {
                var listener = new CountingRestoreListener(0);
                context.Service.AddRestoreListener(listener);
                context.Service.AddRestoreListener(listener);
                try
                {
                    Assert.That(context.InitializeSync().IsSuccess, Is.True);
                    Assert.That(listener.CallCount, Is.EqualTo(1), "One dispatch calls a duplicate-added listener once.");

                    Assert.That(context.ActivateSync(ProfileId.Local("next")).IsSuccess, Is.True);
                    Assert.That(listener.CallCount, Is.EqualTo(2));

                    // One Remove fully unregisters it because the duplicate never entered the list
                    context.Service.RemoveRestoreListener(listener);
                    Assert.That(context.ActivateSync(ProfileId.Guest).IsSuccess, Is.True);
                    Assert.That(listener.CallCount, Is.EqualTo(2));
                }
                finally
                {
                    context.Service.RemoveRestoreListener(listener);
                }
            }
        }

        [Test]
        public void AddAndRemoveAfterDispose_AreNoOps()
        {
            TestServiceContext context = CreateContext(new ProfileSlot());
            try
            {
                var listener = new CountingRestoreListener(0);
                var hook = new CountingDeactivationListener(0);
                context.Service.AddRestoreListener(listener);
                context.Service.AddDeactivationListener(hook);

                context.Dispose();

                Assert.DoesNotThrow(() => context.Service.RemoveRestoreListener(listener));
                Assert.DoesNotThrow(() => context.Service.AddRestoreListener(listener));
                Assert.DoesNotThrow(() => context.Service.AddRestoreListener(new CountingRestoreListener(1)));
                Assert.DoesNotThrow(() => context.Service.RemoveDeactivationListener(hook));
                Assert.DoesNotThrow(() => context.Service.AddDeactivationListener(hook));

                InitializeResult initialized = context.InitializeSync();
                Assert.That(initialized.IsSuccess, Is.False);
                Assert.That(initialized.Error.Code, Is.EqualTo(SaveErrorCode.Disposed));
                Assert.That(listener.CallCount, Is.EqualTo(0));
                Assert.That(hook.CallCount, Is.EqualTo(0));
            }
            finally
            {
                context.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator ListenerAddedAfterInit_GetsNoCatchUpCall()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                using (TestServiceContext context = CreateContext(new ProfileSlot()))
                {
                    Assert.That(context.InitializeSync().IsSuccess, Is.True);
                    var listener = new CountingRestoreListener(0);
                    context.Service.AddRestoreListener(listener);
                    try
                    {
                        await AsyncTestUtility.WaitFramesAsync(3);
                        Assert.That(listener.CallCount, Is.EqualTo(0), "No replay of the init dispatch.");

                        Assert.That(context.ActivateSync(ProfileId.Local("next")).IsSuccess, Is.True);
                        Assert.That(listener.CallCount, Is.EqualTo(1));
                        Assert.That(listener.Reports[0].Trigger, Is.EqualTo(RestoreTrigger.ProfileActivated));
                        Assert.That(listener.Reports[0].Profile, Is.EqualTo(ProfileId.Local("next")));
                    }
                    finally
                    {
                        context.Service.RemoveRestoreListener(listener);
                    }
                }
            });
        }

        [UnityTest]
        public IEnumerator RestoreActivateInitializeFromListener_ReturnReentrantCall()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                using (TestServiceContext context = CreateContext(new ProfileSlot()))
                {
                    context.Provider.SignedInAccountId = AccountA;
                    var outcomes = new List<string>();
                    var listener = new DelegateRestoreListener(0, async (report, ct) =>
                    {
                        RestoreReport restore = await context.Service.RestoreAsync(ct);
                        ProfileActivationResult activation = await context.Service.ActivateProfileAsync(ProfileId.Local("other"), ct);
                        InitializeResult initialize = await context.Service.InitializeAsync(ct);
                        outcomes.Add(report.Trigger + " restore=" + restore.Error?.Code + " activate=" + activation.Error?.Code + " initialize=" + initialize.Error?.Code);
                    });
                    context.Service.AddRestoreListener(listener);
                    try
                    {
                        UniTask<InitializeResult> init = context.Service.InitializeAsync(CancellationToken.None).Preserve();
                        await AsyncTestUtility.WaitUntilAsync(() => init.Status.IsCompleted(), MaxFrames, "Initialize");
                        Assert.That((await init).IsSuccess, Is.True);

                        UniTask<ProfileActivationResult> activation = context.Service.ActivateProfileAsync(ProfileA, CancellationToken.None).Preserve();
                        await AsyncTestUtility.WaitUntilAsync(() => activation.Status.IsCompleted(), MaxFrames, "Activation");
                        Assert.That((await activation).IsSuccess, Is.True);

                        UniTask<RestoreReport> restore = context.Service.RestoreAsync(CancellationToken.None).Preserve();
                        await AsyncTestUtility.WaitUntilAsync(() => restore.Status.IsCompleted(), MaxFrames, "Restore");
                        RestoreReport restored = await restore;
                        Assert.That(restored.Status, Is.EqualTo(SaveStatus.Success), restored.ToString());

                        const string refused = " restore=ReentrantCall activate=ReentrantCall initialize=ReentrantCall";
                        Assert.That(outcomes, Is.EqualTo(new[] { "ProfileActivated" + refused, "ProfileActivated" + refused, "CloudRestore" + refused }));
                        Assert.That(context.Service.ActiveProfile, Is.EqualTo(ProfileA), "The refused activation switched nothing.");
                    }
                    finally
                    {
                        context.Service.RemoveRestoreListener(listener);
                    }
                }
            });
        }

        [UnityTest]
        public IEnumerator FlushAsyncFromRestoreListener_CompletesWithoutDeadlock()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                var player = new ProfileSlot();
                using (TestServiceContext context = CreateContext(player))
                {
                    context.Provider.SignedInAccountId = AccountA;
                    Assert.That(context.InitializeSync().IsSuccess, Is.True);
                    var flushes = new List<FlushResult>();
                    var listener = new DelegateRestoreListener(0, async (report, ct) =>
                    {
                        Assert.That(player.Mutate(data => data.Coins++), Is.True);
                        flushes.Add(await context.Service.FlushAsync(ct));
                    });
                    context.Service.AddRestoreListener(listener);
                    try
                    {
                        // Account profile: FlushAsync takes the gate
                        UniTask<ProfileActivationResult> activation = context.Service.ActivateProfileAsync(ProfileA, CancellationToken.None).Preserve();
                        await AsyncTestUtility.WaitUntilAsync(() => activation.Status.IsCompleted(), MaxFrames, "Activation whose listener awaits FlushAsync");
                        ProfileActivationResult activated = await activation;
                        Assert.That(activated.IsSuccess, Is.True, activated.ToString());
                        Assert.That(flushes.Count, Is.EqualTo(1));
                        Assert.That(flushes[0].Status, Is.EqualTo(SaveStatus.Success), flushes[0].ToString());
                        Assert.That(flushes[0].Local.IsComplete, Is.True, flushes[0].ToString());

                        UniTask<RestoreReport> restore = context.Service.RestoreAsync(CancellationToken.None).Preserve();
                        await AsyncTestUtility.WaitUntilAsync(() => restore.Status.IsCompleted(), MaxFrames, "Restore whose listener awaits FlushAsync");
                        RestoreReport report = await restore;
                        Assert.That(report.Status, Is.EqualTo(SaveStatus.Success), report.ToString());
                        Assert.That(report.ListenerFailures.Count, Is.EqualTo(0), report.ToString());
                        Assert.That(flushes.Count, Is.EqualTo(2));
                        Assert.That(flushes[1].Status, Is.EqualTo(SaveStatus.Success), flushes[1].ToString());
                    }
                    finally
                    {
                        context.Service.RemoveRestoreListener(listener);
                    }
                }
            });
        }

        // Flagged risk (hot.md): deletes have no reentrancy check; both dispatch points run after the gate is released
        [UnityTest]
        public IEnumerator DeleteSlotAsyncFromRestoreListener_CompletesWithinBoundedWait()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                var player = new ProfileSlot();
                using (TestServiceContext context = CreateContext(player))
                {
                    context.Provider.SignedInAccountId = AccountA;
                    Assert.That(context.InitializeSync().IsSuccess, Is.True);
                    var deletes = new List<DeleteResult>();
                    var listener = new DelegateRestoreListener(0, async (report, ct) =>
                    {
                        Assert.That(player.Mutate(data => data.Coins = 5), Is.True);
                        deletes.Add(await context.Service.DeleteSlotAsync(player, DeleteTarget.LocalOnly, ct));
                    });
                    context.Service.AddRestoreListener(listener);
                    try
                    {
                        UniTask<ProfileActivationResult> activation = context.Service.ActivateProfileAsync(ProfileA, CancellationToken.None).Preserve();
                        await AsyncTestUtility.WaitUntilAsync(() => activation.Status.IsCompleted(), MaxFrames, "Activation whose listener awaits DeleteSlotAsync");
                        ProfileActivationResult activated = await activation;
                        Assert.That(activated.IsSuccess, Is.True, activated.ToString());
                        Assert.That(deletes.Count, Is.EqualTo(1));
                        Assert.That(deletes[0].Status, Is.EqualTo(SaveStatus.Success), deletes[0].Error?.ToString());
                        Assert.That(player.PeekData().Coins, Is.EqualTo(0), "The delete reset the slot.");

                        UniTask<RestoreReport> restore = context.Service.RestoreAsync(CancellationToken.None).Preserve();
                        await AsyncTestUtility.WaitUntilAsync(() => restore.Status.IsCompleted(), MaxFrames, "Restore whose listener awaits DeleteSlotAsync");
                        RestoreReport report = await restore;
                        Assert.That(report.Status, Is.EqualTo(SaveStatus.Success), report.ToString());
                        Assert.That(report.ListenerFailures.Count, Is.EqualTo(0), report.ToString());
                        Assert.That(deletes.Count, Is.EqualTo(2));
                        Assert.That(deletes[1].Status, Is.EqualTo(SaveStatus.Success), deletes[1].Error?.ToString());
                    }
                    finally
                    {
                        context.Service.RemoveRestoreListener(listener);
                    }
                }
            });
        }

        private static TestServiceContext CreateContext(params SaveSlot[] slots)
        {
            return TestServiceFactory.Create(new TestServiceSetup
            {
                Slots = slots,
                ConfigureOptions = options => options.CloudRetryCount = 0,
            });
        }

        // Verbose order lines of both dispatchers, in log order
        private static List<string> DispatchOrderLogs(TestServiceContext context)
        {
            return context.Logger.GetMessages(TestLogLevel.Verbose)
                .Where(message => message.StartsWith("[SaveSystem] Dispatching ", StringComparison.Ordinal))
                .ToList();
        }

        private static List<string> OrderWarnings(TestServiceContext context)
        {
            return context.Logger.GetMessages(TestLogLevel.Warning)
                .Where(message => message.Contains(" has Order "))
                .ToList();
        }

        /// <summary>Source cache (Order 0-999): copies the slot value on every dispatch.</summary>
        private sealed class CoinsSourceCache : IRestoreListener
        {
            private readonly ProfileSlot _slot;

            public CoinsSourceCache(int order, ProfileSlot slot)
            {
                Order = order;
                _slot = slot;
            }

            public int Order { get; }

            public int Coins { get; private set; } = -1;

            public UniTask OnRestoredAsync(RestoreReport report, CancellationToken ct)
            {
                Coins = _slot.Read(data => data.Coins);
                return UniTask.CompletedTask;
            }
        }

        /// <summary>Derived cache (Order 1000-1999): reads only the source cache; records what it saw next to the slot value.</summary>
        private sealed class CoinsDerivedCache : IRestoreListener
        {
            private readonly CoinsSourceCache _source;
            private readonly ProfileSlot _slot;
            private readonly List<string> _seen = new List<string>();

            public CoinsDerivedCache(int order, CoinsSourceCache source, ProfileSlot slot)
            {
                Order = order;
                _source = source;
                _slot = slot;
            }

            public int Order { get; }

            public int DoubledCoins { get; private set; } = -1;

            public IReadOnlyList<string> Seen => _seen;

            public UniTask OnRestoredAsync(RestoreReport report, CancellationToken ct)
            {
                DoubledCoins = _source.Coins * 2;
                _seen.Add("source=" + _source.Coins + " slot=" + _slot.Read(data => data.Coins));
                return UniTask.CompletedTask;
            }
        }

        /// <summary>Counts dispatches and keeps the reports.</summary>
        private sealed class CountingRestoreListener : IRestoreListener
        {
            private readonly Action _onCalled;
            private readonly List<RestoreReport> _reports = new List<RestoreReport>();

            public CountingRestoreListener(int order, Action onCalled = null)
            {
                Order = order;
                _onCalled = onCalled;
            }

            public int Order { get; }

            public int CallCount => _reports.Count;

            public IReadOnlyList<RestoreReport> Reports => _reports;

            public UniTask OnRestoredAsync(RestoreReport report, CancellationToken ct)
            {
                _reports.Add(report);
                _onCalled?.Invoke();
                return UniTask.CompletedTask;
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

        /// <summary>Appends its type name on every dispatch.</summary>
        private abstract class NamedRestoreListener : IRestoreListener
        {
            private readonly List<string> _calls;

            protected NamedRestoreListener(int order, List<string> calls)
            {
                Order = order;
                _calls = calls;
            }

            public int Order { get; }

            public UniTask OnRestoredAsync(RestoreReport report, CancellationToken ct)
            {
                _calls.Add(GetType().Name);
                return UniTask.CompletedTask;
            }
        }

        private sealed class EarlyListener : NamedRestoreListener
        {
            public EarlyListener(int order, List<string> calls)
                : base(order, calls)
            {
            }
        }

        private sealed class ZuluTieListener : NamedRestoreListener
        {
            public ZuluTieListener(int order, List<string> calls)
                : base(order, calls)
            {
            }
        }

        private sealed class AlphaTieListener : NamedRestoreListener
        {
            public AlphaTieListener(int order, List<string> calls)
                : base(order, calls)
            {
            }
        }

        private sealed class LateListener : NamedRestoreListener
        {
            public LateListener(int order, List<string> calls)
                : base(order, calls)
            {
            }
        }

        private sealed class CountingDeactivationListener : IProfileDeactivatingListener
        {
            public CountingDeactivationListener(int order)
            {
                Order = order;
            }

            public int Order { get; }

            public int CallCount { get; private set; }

            public UniTask OnProfileDeactivatingAsync(ProfileDeactivation context, CancellationToken ct)
            {
                CallCount++;
                return UniTask.CompletedTask;
            }
        }
    }
}
