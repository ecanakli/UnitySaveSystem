using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Ecanakli.SaveSystem.Tests
{
    /// <summary>N3 pre-deactivation hooks: order, outgoing-profile writes, flush from a hook, shared budget, caller cancel, reentrancy.</summary>
    [TestFixture]
    public sealed class DeactivationListenerTests
    {
        private const string AccountA = "account-a";
        private const int MaxFrames = 120;

        private static readonly ProfileId ProfileA = ProfileId.Account(AccountA);

        [Test]
        public void Hooks_RunInOrderBeforeUnload_MutateLandsInOutgoingProfile()
        {
            var player = new ProfileSlot();
            using (TestServiceContext context = CreateContext(null, player))
            {
                Assert.That(context.InitializeSync().IsSuccess, Is.True);
                ProfileId next = ProfileId.Local("next");
                var observations = new List<string>();
                var late = new DelegateDeactivationListener(20, (deactivation, ct) =>
                {
                    observations.Add("late " + Describe(context, player, deactivation));
                    return UniTask.CompletedTask;
                });
                var early = new DelegateDeactivationListener(10, (deactivation, ct) =>
                {
                    string state = Describe(context, player, deactivation);
                    observations.Add("early " + state + " mutated=" + player.Mutate(data => data.Coins = 11));
                    return UniTask.CompletedTask;
                });

                // Registration order is the reverse of Order
                context.Service.AddDeactivationListener(late);
                context.Service.AddDeactivationListener(early);
                ProfileActivationResult result;
                try
                {
                    result = context.ActivateSync(next);
                }
                finally
                {
                    context.Service.RemoveDeactivationListener(late);
                    context.Service.RemoveDeactivationListener(early);
                }

                Assert.That(result.IsSuccess, Is.True, result.ToString());
                Assert.That(result.DeactivationTimedOut, Is.False);
                Assert.That(result.DeactivationFailures.Count, Is.EqualTo(0));
                Assert.That(observations, Is.EqualTo(new[]
                {
                    "early guest->local:next active=guest slot=Ready ready=False mutated=True",
                    "late guest->local:next active=guest slot=Ready ready=False",
                }));
                Assert.That(context.Service.ActiveProfile, Is.EqualTo(next));
                Assert.That(player.PeekData().Coins, Is.EqualTo(0), "The hook's change belongs to the outgoing profile.");
                Assert.That(context.Storage.HasFile(TestPaths.ProfileSlot(ProfileId.Guest, TestSlotKeys.Player)), Is.True);

                Assert.That(context.ActivateSync(ProfileId.Guest).IsSuccess, Is.True);
                Assert.That(player.PeekData().Coins, Is.EqualTo(11), "The hook's Mutate was flushed into the outgoing profile.");
            }
        }

        // 04a A5: a hook writing during a Guest -> Account switch writes into the guest directory, which the claim then moves
        [Test]
        public void GuestToAccountClaim_HookWritesMovedWithGuestDirectory()
        {
            var player = new ProfileSlot();
            using (TestServiceContext context = CreateContext(null, player))
            {
                context.Provider.SignedInAccountId = AccountA;
                Assert.That(context.InitializeSync().IsSuccess, Is.True);
                Assert.That(context.Service.ActiveProfile, Is.EqualTo(ProfileId.Guest), "Premise: playing as guest.");
                Assert.That(context.Storage.HasFile(GuestPlayerPath), Is.False, "Premise: the guest slot has no file yet.");

                var observations = new List<string>();
                var hook = new DelegateDeactivationListener(0, (deactivation, ct) =>
                {
                    bool mutated = player.Mutate(data => data.Coins = 11);
                    LocalFlushResult flushed = context.Service.FlushLocalNow();
                    observations.Add(
                        deactivation.Outgoing + "->" + deactivation.Incoming + " mutated=" + mutated + " flushed=" + flushed.IsComplete
                        + " guestFile=" + context.Storage.HasFile(GuestPlayerPath) + " accountFile=" + context.Storage.HasFile(AccountPlayerPath));
                    return UniTask.CompletedTask;
                });

                context.Service.AddDeactivationListener(hook);
                ProfileActivationResult result;
                try
                {
                    result = context.ActivateSync(ProfileA);
                }
                finally
                {
                    context.Service.RemoveDeactivationListener(hook);
                }

                Assert.That(result.IsSuccess, Is.True, result.ToString());
                Assert.That(result.DeactivationFailures.Count, Is.EqualTo(0), string.Join(", ", result.DeactivationFailures));
                Assert.That(observations, Is.EqualTo(new[]
                {
                    "guest->" + ProfileA + " mutated=True flushed=True guestFile=True accountFile=False",
                }));

                Assert.That(result.ClaimedGuestData, Is.True, "The hook's write made the guest directory claimable.");
                Assert.That(context.Storage.HasDirectory(TestPaths.ProfileDirectory(ProfileId.Guest)), Is.False, "The guest directory moved into the account.");
                Assert.That(context.Storage.HasFile(GuestPlayerPath), Is.False);
                Assert.That(context.Storage.HasFile(AccountPlayerPath), Is.True, string.Join(", ", context.Storage.AllFilePaths));
                Assert.That(context.Service.ActiveProfile, Is.EqualTo(ProfileA));
                Assert.That(player.PeekData().Coins, Is.EqualTo(11), "The hook's write moved with the claim.");
            }
        }

        [UnityTest]
        public IEnumerator FlushAsyncFromHook_DoesNotDeadlock()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                var player = new ProfileSlot();
                using (TestServiceContext context = CreateContext(null, player))
                {
                    context.Provider.SignedInAccountId = AccountA;
                    Assert.That(context.InitializeSync().IsSuccess, Is.True);
                    Assert.That(context.ActivateSync(ProfileA).IsSuccess, Is.True);

                    var mutated = new List<bool>();
                    var flushes = new List<FlushResult>();
                    var hook = new DelegateDeactivationListener(0, async (deactivation, ct) =>
                    {
                        mutated.Add(player.Mutate(data => data.Coins = 12));

                        // Account profile: FlushAsync takes the gate, which the activation has not acquired yet
                        flushes.Add(await context.Service.FlushAsync(ct));
                    });
                    context.Service.AddDeactivationListener(hook);
                    try
                    {
                        UniTask<ProfileActivationResult> activation = context.Service.ActivateProfileAsync(ProfileId.Guest, CancellationToken.None).Preserve();
                        await AsyncTestUtility.WaitUntilAsync(() => activation.Status.IsCompleted(), MaxFrames, "Activation whose hook awaits FlushAsync");

                        ProfileActivationResult result = await activation;
                        Assert.That(result.IsSuccess, Is.True, result.ToString());
                        Assert.That(result.DeactivationFailures.Count, Is.EqualTo(0));
                        Assert.That(mutated, Is.EqualTo(new[] { true }));
                        Assert.That(flushes.Count, Is.EqualTo(1));
                        Assert.That(flushes[0].Status, Is.EqualTo(SaveStatus.Success), flushes[0].ToString());
                        Assert.That(flushes[0].Local.IsComplete, Is.True, flushes[0].ToString());
                        Assert.That(context.Storage.HasFile(TestPaths.ProfileSlot(ProfileA, TestSlotKeys.Player)), Is.True);
                    }
                    finally
                    {
                        context.Service.RemoveDeactivationListener(hook);
                    }

                    Assert.That(context.ActivateSync(ProfileA).IsSuccess, Is.True);
                    Assert.That(player.PeekData().Coins, Is.EqualTo(12));
                }
            });
        }

        [UnityTest]
        public IEnumerator TimeoutBudget_SkipsRemainingHooks_RecordsTimedOut_SwitchProceeds()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                TimeSpan timeout = TimeSpan.FromSeconds(2);
                using (TestServiceContext context = CreateContext(options => options.DeactivationTimeout = timeout, new ProfileSlot()))
                {
                    Assert.That(context.InitializeSync().IsSuccess, Is.True);
                    ProfileId next = ProfileId.Local("next");
                    var stuck = new HoldingDeactivationListener(0);
                    var skipped = new CountingDeactivationListener(1);
                    context.Service.AddDeactivationListener(stuck);
                    context.Service.AddDeactivationListener(skipped);
                    try
                    {
                        UniTask<ProfileActivationResult> activation = context.Service.ActivateProfileAsync(next, CancellationToken.None).Preserve();
                        await AsyncTestUtility.WaitUntilAsync(() => stuck.CallCount == 1, MaxFrames, "Stuck hook");

                        context.Clock.Advance(timeout - TimeSpan.FromMilliseconds(1));
                        await AsyncTestUtility.WaitFramesAsync(3);
                        Assert.That(activation.Status.IsCompleted(), Is.False, "The budget has not run out yet.");
                        Assert.That(context.Service.ActiveProfile, Is.EqualTo(ProfileId.Guest));

                        context.Clock.Advance(TimeSpan.FromMilliseconds(1));
                        await AsyncTestUtility.WaitUntilAsync(() => activation.Status.IsCompleted(), MaxFrames, "Activation after the budget ran out");

                        ProfileActivationResult result = await activation;
                        Assert.That(result.IsSuccess, Is.True, result.ToString());
                        Assert.That(result.Profile, Is.EqualTo(next));
                        Assert.That(context.Service.ActiveProfile, Is.EqualTo(next), "The switch proceeds after a timeout.");
                        Assert.That(context.Service.IsReady, Is.True);
                        Assert.That(result.DeactivationTimedOut, Is.True);
                        Assert.That(result.DeactivationFailures.Count, Is.EqualTo(2), string.Join(", ", result.DeactivationFailures));
                        Assert.That(result.DeactivationFailures[0].ListenerType, Is.EqualTo(typeof(HoldingDeactivationListener)));
                        Assert.That(result.DeactivationFailures[0].Order, Is.EqualTo(0));
                        Assert.That(result.DeactivationFailures[0].Kind, Is.EqualTo(ListenerFailureKind.TimedOut));
                        Assert.That(result.DeactivationFailures[1].ListenerType, Is.EqualTo(typeof(CountingDeactivationListener)));
                        Assert.That(result.DeactivationFailures[1].Order, Is.EqualTo(1));
                        Assert.That(result.DeactivationFailures[1].Kind, Is.EqualTo(ListenerFailureKind.Skipped));
                        Assert.That(skipped.CallCount, Is.EqualTo(0));
                        Assert.That(context.Logger.Contains(TestLogLevel.Warning, "exceeded the shared budget"), Is.True, context.Logger.Describe());
                    }
                    finally
                    {
                        stuck.Release();
                        context.Service.RemoveDeactivationListener(stuck);
                        context.Service.RemoveDeactivationListener(skipped);
                    }
                }
            });
        }

        [UnityTest]
        public IEnumerator CallerCancellationDuringHooks_AbortsWithNothingSwitched()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                var player = new ProfileSlot();
                using (TestServiceContext context = CreateContext(null, player))
                using (var cts = new CancellationTokenSource())
                {
                    Assert.That(context.InitializeSync().IsSuccess, Is.True);
                    Assert.That(player.Mutate(data => data.Coins = 4), Is.True);
                    ProfileId next = ProfileId.Local("next");
                    var stuck = new HoldingDeactivationListener(0);
                    var after = new CountingDeactivationListener(1);
                    var activations = new List<ProfileActivationResult>();
                    Action<ProfileActivationResult> onActivated = activations.Add;
                    context.Service.AddDeactivationListener(stuck);
                    context.Service.AddDeactivationListener(after);
                    context.Service.ProfileActivated += onActivated;
                    try
                    {
                        UniTask<ProfileActivationResult> activation = context.Service.ActivateProfileAsync(next, cts.Token).Preserve();
                        await AsyncTestUtility.WaitUntilAsync(() => stuck.CallCount == 1, MaxFrames, "Stuck hook");
                        Assert.That(context.Service.IsReady, Is.False);

                        cts.Cancel();
                        await AsyncTestUtility.WaitUntilAsync(() => activation.Status.IsCompleted(), MaxFrames, "Cancelled activation");

                        Assert.That(activation.Status, Is.EqualTo(UniTaskStatus.Canceled));
                        bool canceled = false;
                        try
                        {
                            await activation;
                        }
                        catch (OperationCanceledException)
                        {
                            canceled = true;
                        }

                        Assert.That(canceled, Is.True, "Caller cancellation during the hooks throws.");
                        Assert.That(context.Service.ActiveProfile, Is.EqualTo(ProfileId.Guest), "Nothing switched.");
                        Assert.That(context.Service.IsReady, Is.True, "Readiness returns after the abort.");
                        Assert.That(context.Storage.HasDirectory(TestPaths.ProfileDirectory(next)), Is.False);
                        Assert.That(after.CallCount, Is.EqualTo(0), "Remaining hooks are skipped.");
                        Assert.That(activations.Count, Is.EqualTo(0));
                        Assert.That(player.State, Is.EqualTo(SlotState.Ready));
                        Assert.That(player.PeekData().Coins, Is.EqualTo(4));
                        Assert.That(player.Mutate(data => data.Coins = 5), Is.True, "Mutations still work in the outgoing profile.");
                    }
                    finally
                    {
                        stuck.Release();
                        context.Service.ProfileActivated -= onActivated;
                        context.Service.RemoveDeactivationListener(stuck);
                        context.Service.RemoveDeactivationListener(after);
                    }
                }
            });
        }

        [UnityTest]
        public IEnumerator ActivateRestoreInitializeFromHook_ReturnReentrantCall()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                using (TestServiceContext context = CreateContext(null, new ProfileSlot()))
                {
                    context.Provider.SignedInAccountId = AccountA;
                    Assert.That(context.InitializeSync().IsSuccess, Is.True);
                    Assert.That(context.ActivateSync(ProfileA).IsSuccess, Is.True);
                    ProfileId other = ProfileId.Local("other");
                    ProfileId next = ProfileId.Local("next");
                    var outcomes = new List<string>();
                    var hook = new DelegateDeactivationListener(0, async (deactivation, ct) =>
                    {
                        ProfileActivationResult activation = await context.Service.ActivateProfileAsync(other, ct);
                        RestoreReport restore = await context.Service.RestoreAsync(ct);
                        InitializeResult initialize = await context.Service.InitializeAsync(ct);
                        outcomes.Add("activate=" + activation.Error?.Code + " restore=" + restore.Error?.Code + " initialize=" + initialize.Error?.Code);
                    });
                    context.Service.AddDeactivationListener(hook);
                    try
                    {
                        UniTask<ProfileActivationResult> activation = context.Service.ActivateProfileAsync(next, CancellationToken.None).Preserve();
                        await AsyncTestUtility.WaitUntilAsync(() => activation.Status.IsCompleted(), MaxFrames, "Activation whose hook calls reentrant operations");

                        ProfileActivationResult result = await activation;
                        Assert.That(result.IsSuccess, Is.True, result.ToString());
                        Assert.That(result.DeactivationFailures.Count, Is.EqualTo(0), string.Join(", ", result.DeactivationFailures));
                        Assert.That(outcomes, Is.EqualTo(new[] { "activate=ReentrantCall restore=ReentrantCall initialize=ReentrantCall" }));
                        Assert.That(context.Service.ActiveProfile, Is.EqualTo(next));
                        Assert.That(context.Storage.HasDirectory(TestPaths.ProfileDirectory(other)), Is.False, "The refused activation created nothing.");
                    }
                    finally
                    {
                        context.Service.RemoveDeactivationListener(hook);
                    }
                }
            });
        }

        [Test]
        public void AlreadyActiveAndInit_RunNoHooks()
        {
            using (TestServiceContext context = CreateContext(null, new ProfileSlot()))
            {
                var hook = new CountingDeactivationListener(0);
                context.Service.AddDeactivationListener(hook);
                try
                {
                    Assert.That(context.InitializeSync().IsSuccess, Is.True);
                    Assert.That(hook.CallCount, Is.EqualTo(0), "Init has no outgoing profile.");

                    ProfileActivationResult result = context.ActivateSync(ProfileId.Guest);

                    Assert.That(result.IsSuccess, Is.True, result.ToString());
                    Assert.That(result.AlreadyActive, Is.True);
                    Assert.That(result.DeactivationFailures.Count, Is.EqualTo(0));
                    Assert.That(hook.CallCount, Is.EqualTo(0));
                    Assert.That(context.Service.IsReady, Is.True);
                }
                finally
                {
                    context.Service.RemoveDeactivationListener(hook);
                }
            }
        }

        private static string GuestPlayerPath => TestPaths.ProfileSlot(ProfileId.Guest, TestSlotKeys.Player);

        private static string AccountPlayerPath => TestPaths.ProfileSlot(ProfileA, TestSlotKeys.Player);

        private static string Describe(TestServiceContext context, SaveSlot slot, ProfileDeactivation deactivation)
        {
            return deactivation.Outgoing + "->" + deactivation.Incoming + " active=" + context.Service.ActiveProfile + " slot=" + slot.State + " ready=" + context.Service.IsReady;
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

        /// <summary>Deactivation hook backed by a delegate.</summary>
        private sealed class DelegateDeactivationListener : IProfileDeactivatingListener
        {
            private readonly Func<ProfileDeactivation, CancellationToken, UniTask> _onDeactivating;

            public DelegateDeactivationListener(int order, Func<ProfileDeactivation, CancellationToken, UniTask> onDeactivating)
            {
                Order = order;
                _onDeactivating = onDeactivating;
            }

            public int Order { get; }

            public UniTask OnProfileDeactivatingAsync(ProfileDeactivation context, CancellationToken ct)
            {
                return _onDeactivating(context, ct);
            }
        }

        /// <summary>Stays pending until Release; ignores its token so only the dispatcher can abandon it.</summary>
        private sealed class HoldingDeactivationListener : IProfileDeactivatingListener
        {
            private readonly UniTaskCompletionSource _release = new UniTaskCompletionSource();

            public HoldingDeactivationListener(int order)
            {
                Order = order;
            }

            public int Order { get; }

            public int CallCount { get; private set; }

            public UniTask OnProfileDeactivatingAsync(ProfileDeactivation context, CancellationToken ct)
            {
                CallCount++;
                return _release.Task;
            }

            public void Release()
            {
                _release.TrySetResult();
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
