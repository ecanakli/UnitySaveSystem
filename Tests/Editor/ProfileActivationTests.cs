using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Ecanakli.SaveSystem.Tests
{
    /// <summary>Init profile resolution, directory switching, guest claim, owner collision, local profile names and listing.</summary>
    [TestFixture]
    public sealed class ProfileActivationTests
    {
        private const string AccountA = "account-a";
        private const string OtherOwner = "account-other";
        private const int MaxFrames = 120;

        private static readonly ProfileId ProfileA = ProfileId.Account(AccountA);

        [Test]
        public void Initialize_FreshStorage_ActivatesGuest()
        {
            var player = new ProfileSlot();
            using (TestServiceContext context = CreateContext(player))
            {
                InitializeResult result = context.InitializeSync();

                Assert.That(result.IsSuccess, Is.True, result.ToString());
                Assert.That(result.Profile, Is.EqualTo(ProfileId.Guest));
                Assert.That(context.Service.ActiveProfile, Is.EqualTo(ProfileId.Guest));
                Assert.That(player.State, Is.EqualTo(SlotState.Ready));
                Assert.That(context.Storage.HasDirectory(TestPaths.ProfileDirectory(ProfileId.Guest)), Is.True);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Initialize_AfterRestart_ResolvesLastActiveProfile(bool local)
        {
            ProfileId target = local ? ProfileId.Local("campaign") : ProfileA;
            TestServiceContext context = CreateContext(new ProfileSlot());
            TestServiceContext restarted = null;
            try
            {
                Assert.That(context.InitializeSync().IsSuccess, Is.True);
                Assert.That(context.ActivateSync(target).IsSuccess, Is.True);

                var player = new ProfileSlot();
                restarted = TestServiceFactory.Restart(context, player);
                InitializeResult result = restarted.InitializeSync();

                Assert.That(result.IsSuccess, Is.True, result.ToString());
                Assert.That(result.Profile, Is.EqualTo(target));
                Assert.That(restarted.Service.ActiveProfile, Is.EqualTo(target));
                Assert.That(player.State, Is.EqualTo(SlotState.Ready));
            }
            finally
            {
                context.Dispose();
                restarted?.Dispose();
            }
        }

        [Test]
        public void Initialize_LastActiveLocalDirectoryMissing_FallsBackToGuest()
        {
            ProfileId gone = ProfileId.Local("gone");
            TestServiceContext context = CreateContext(new ProfileSlot());
            TestServiceContext restarted = null;
            try
            {
                Assert.That(context.InitializeSync().IsSuccess, Is.True);
                Assert.That(context.ActivateSync(gone).IsSuccess, Is.True);

                // Restart disposes the old service (its flush may write), then the directory disappears
                var player = new ProfileSlot();
                restarted = TestServiceFactory.Restart(context, player);
                restarted.Storage.DeleteDirectory(TestPaths.ProfileDirectory(gone));
                Assert.That(restarted.Storage.HasDirectory(TestPaths.ProfileDirectory(gone)), Is.False, "Premise: the local directory is gone.");

                InitializeResult result = restarted.InitializeSync();

                Assert.That(result.IsSuccess, Is.True, result.ToString());
                Assert.That(result.Profile, Is.EqualTo(ProfileId.Guest));
                Assert.That(restarted.Service.ActiveProfile, Is.EqualTo(ProfileId.Guest));
                Assert.That(restarted.Storage.HasDirectory(TestPaths.ProfileDirectory(gone)), Is.False, "The missing local profile is not recreated at init.");
                Assert.That(restarted.Logger.Contains(TestLogLevel.Warning, "falling back to guest"), Is.True, restarted.Logger.Describe());
            }
            finally
            {
                context.Dispose();
                restarted?.Dispose();
            }
        }

        [Test]
        public void Activate_SwitchesDirectory_ReloadsProfileSlots_DeviceSlotsStay()
        {
            var player = new ProfileSlot();
            var settings = new DeviceSettingsSlot();
            using (TestServiceContext context = CreateContext(player, settings))
            {
                Assert.That(context.InitializeSync().IsSuccess, Is.True);
                Assert.That(player.Mutate(data => data.Coins = 4), Is.True);
                Assert.That(settings.Mutate(data => data.MusicVolume = 0.25f), Is.True);
                long settingsRevision = settings.Revision;
                player.ResetCounters();
                settings.ResetCounters();
                ProfileId alpha = ProfileId.Local("alpha");

                ProfileActivationResult result = context.ActivateSync(alpha);

                Assert.That(result.IsSuccess, Is.True, result.ToString());
                Assert.That(result.Profile, Is.EqualTo(alpha));
                Assert.That(result.Previous, Is.EqualTo(ProfileId.Guest));
                Assert.That(result.AlreadyActive, Is.False);
                Assert.That(result.ClaimedGuestData, Is.False);
                Assert.That(context.Service.ActiveProfile, Is.EqualTo(alpha));
                Assert.That(context.Storage.HasFile(TestPaths.ProfileSlot(ProfileId.Guest, TestSlotKeys.Player)), Is.True, "The outgoing profile is flushed before the switch.");
                Assert.That(context.Storage.HasDirectory(TestPaths.ProfileDirectory(alpha)), Is.True);

                Assert.That(player.State, Is.EqualTo(SlotState.Ready));
                Assert.That(player.PeekData().Coins, Is.EqualTo(0), "The incoming profile starts from its own data.");
                Assert.That(player.NormalizeCount, Is.GreaterThanOrEqualTo(1), "Profile slots are reloaded.");

                Assert.That(settings.State, Is.EqualTo(SlotState.Ready));
                Assert.That(settings.NormalizeCount, Is.EqualTo(0), "Device slots are not reloaded.");
                Assert.That(settings.CreateDefaultCount, Is.EqualTo(0), "Device slots are not reset.");
                Assert.That(settings.PeekData().MusicVolume, Is.EqualTo(0.25f));
                Assert.That(settings.Revision, Is.EqualTo(settingsRevision));

                Assert.That(player.Mutate(data => data.Coins = 9), Is.True);
                Assert.That(context.Service.FlushLocalNow().IsComplete, Is.True);
                Assert.That(context.Storage.HasFile(TestPaths.ProfileSlot(alpha, TestSlotKeys.Player)), Is.True, "Writes go to the active profile directory.");

                Assert.That(context.ActivateSync(ProfileId.Guest).IsSuccess, Is.True);
                Assert.That(player.PeekData().Coins, Is.EqualTo(4));
                Assert.That(context.ActivateSync(alpha).IsSuccess, Is.True);
                Assert.That(player.PeekData().Coins, Is.EqualTo(9));
                Assert.That(settings.PeekData().MusicVolume, Is.EqualTo(0.25f));
            }
        }

        [Test]
        public void Activate_AlreadyActive_DoesNothing()
        {
            var player = new ProfileSlot();
            using (TestServiceContext context = CreateContext(player))
            {
                Assert.That(context.InitializeSync().IsSuccess, Is.True);
                Assert.That(player.Mutate(data => data.Coins = 3), Is.True);
                player.ResetCounters();
                context.Storage.ResetCounters();
                var activations = new List<ProfileActivationResult>();
                Action<ProfileActivationResult> onActivated = activations.Add;
                context.Service.ProfileActivated += onActivated;
                try
                {
                    ProfileActivationResult result = context.ActivateSync(ProfileId.Guest);

                    Assert.That(result.IsSuccess, Is.True, result.ToString());
                    Assert.That(result.AlreadyActive, Is.True);
                    Assert.That(result.ClaimedGuestData, Is.False);
                    Assert.That(context.Service.ActiveProfile, Is.EqualTo(ProfileId.Guest));
                    Assert.That(context.Service.IsReady, Is.True);
                    Assert.That(player.State, Is.EqualTo(SlotState.Ready));
                    Assert.That(player.NormalizeCount, Is.EqualTo(0), "Nothing is reloaded.");
                    Assert.That(player.PeekData().Coins, Is.EqualTo(3));
                    Assert.That(context.Storage.CountCalls(StorageOperation.Mutations), Is.EqualTo(0), "Nothing is flushed or written.");
                    Assert.That(activations.Count, Is.EqualTo(0), "ProfileActivated is not raised.");
                }
                finally
                {
                    context.Service.ProfileActivated -= onActivated;
                }
            }
        }

        [Test]
        public void Claim_AccountDirectoryAbsent_MovesGuestDataToAccount()
        {
            var player = new ProfileSlot();
            using (TestServiceContext context = CreateContext(player))
            {
                Assert.That(context.InitializeSync().IsSuccess, Is.True);
                WriteGuestCoins(context, player, 7);
                string guestFile = TestPaths.ProfileSlot(ProfileId.Guest, TestSlotKeys.Player);
                Assert.That(context.Storage.HasDirectory(TestPaths.ProfileDirectory(ProfileA)), Is.False, "Premise: no account directory.");

                ProfileActivationResult result = context.ActivateSync(ProfileA);

                Assert.That(result.IsSuccess, Is.True, result.ToString());
                Assert.That(result.ClaimedGuestData, Is.True);
                Assert.That(player.PeekData().Coins, Is.EqualTo(7));
                Assert.That(context.Storage.HasFile(TestPaths.ProfileSlot(ProfileA, TestSlotKeys.Player)), Is.True);
                Assert.That(context.Storage.HasFile(guestFile), Is.False, "Guest data is moved, not copied.");
                Assert.That(LoadOwner(context, TestPaths.ProfileDirectory(ProfileA)), Is.EqualTo(AccountA));
            }
        }

        [Test]
        public void Claim_AccountDirectoryEmpty_MovesGuestDataToAccount()
        {
            var player = new ProfileSlot();
            using (TestServiceContext context = CreateContext(player))
            {
                Assert.That(context.InitializeSync().IsSuccess, Is.True);
                WriteGuestCoins(context, player, 7);
                context.Storage.CreateDirectory(TestPaths.ProfileDirectory(ProfileA));

                ProfileActivationResult result = context.ActivateSync(ProfileA);

                Assert.That(result.IsSuccess, Is.True, result.ToString());
                Assert.That(result.ClaimedGuestData, Is.True);
                Assert.That(player.PeekData().Coins, Is.EqualTo(7));
                Assert.That(context.Storage.HasFile(TestPaths.ProfileSlot(ProfileA, TestSlotKeys.Player)), Is.True);
                Assert.That(context.Storage.HasFile(TestPaths.ProfileSlot(ProfileId.Guest, TestSlotKeys.Player)), Is.False);
            }
        }

        [Test]
        public void Claim_AccountDirectoryHasData_GuestDataStays()
        {
            var player = new ProfileSlot();
            using (TestServiceContext context = CreateContext(player))
            {
                Assert.That(context.InitializeSync().IsSuccess, Is.True);
                Assert.That(context.ActivateSync(ProfileA).IsSuccess, Is.True);
                Assert.That(player.Mutate(data => data.Coins = 3), Is.True);
                Assert.That(context.ActivateSync(ProfileId.Guest).IsSuccess, Is.True);
                WriteGuestCoins(context, player, 7);

                ProfileActivationResult result = context.ActivateSync(ProfileA);

                Assert.That(result.IsSuccess, Is.True, result.ToString());
                Assert.That(result.ClaimedGuestData, Is.False);
                Assert.That(player.PeekData().Coins, Is.EqualTo(3), "The account keeps its own data.");
                Assert.That(context.Storage.HasFile(TestPaths.ProfileSlot(ProfileId.Guest, TestSlotKeys.Player)), Is.True);

                Assert.That(context.ActivateSync(ProfileId.Guest).IsSuccess, Is.True);
                Assert.That(player.PeekData().Coins, Is.EqualTo(7), "Guest data is untouched.");
            }
        }

        [Test]
        public void Claim_NeverFromLocalProfile()
        {
            var player = new ProfileSlot();
            ProfileId local = ProfileId.Local("offline");
            using (TestServiceContext context = CreateContext(player))
            {
                Assert.That(context.InitializeSync().IsSuccess, Is.True);
                Assert.That(context.ActivateSync(local).IsSuccess, Is.True);
                Assert.That(player.Mutate(data => data.Coins = 5), Is.True);

                ProfileActivationResult result = context.ActivateSync(ProfileA);

                Assert.That(result.IsSuccess, Is.True, result.ToString());
                Assert.That(context.Storage.HasFile(TestPaths.ProfileSlot(ProfileId.Guest, TestSlotKeys.Player)), Is.False, "Premise: guest holds no slot files.");
                Assert.That(result.ClaimedGuestData, Is.False);
                Assert.That(player.PeekData().Coins, Is.EqualTo(0), "Local data is never claimed.");
                Assert.That(context.Storage.HasFile(TestPaths.ProfileSlot(local, TestSlotKeys.Player)), Is.True);
                Assert.That(context.Storage.HasFile(TestPaths.ProfileSlot(ProfileA, TestSlotKeys.Player)), Is.False);

                Assert.That(context.ActivateSync(local).IsSuccess, Is.True);
                Assert.That(player.PeekData().Coins, Is.EqualTo(5));
            }
        }

        [Test]
        public void Claim_NeverIntoLocalProfile()
        {
            var player = new ProfileSlot();
            ProfileId local = ProfileId.Local("fresh");
            using (TestServiceContext context = CreateContext(player))
            {
                Assert.That(context.InitializeSync().IsSuccess, Is.True);
                WriteGuestCoins(context, player, 7);

                ProfileActivationResult result = context.ActivateSync(local);

                Assert.That(result.IsSuccess, Is.True, result.ToString());
                Assert.That(result.ClaimedGuestData, Is.False);
                Assert.That(player.PeekData().Coins, Is.EqualTo(0));
                Assert.That(context.Storage.HasFile(TestPaths.ProfileSlot(ProfileId.Guest, TestSlotKeys.Player)), Is.True, "Guest data stays in place.");

                Assert.That(context.ActivateSync(ProfileId.Guest).IsSuccess, Is.True);
                Assert.That(player.PeekData().Coins, Is.EqualTo(7));
            }
        }

        [Test]
        public void OwnerCollision_ClaimAndWritesUseTheSuffixedDirectory()
        {
            var player = new ProfileSlot();
            using (TestServiceContext context = CreateContext(player))
            {
                Assert.That(context.InitializeSync().IsSuccess, Is.True);
                string taken = SaveLayout.AccountDirectory(AccountA, 0);
                string suffixed = SaveLayout.AccountDirectory(AccountA, 1);
                var store = new SyncStateStore(context.Storage, new TestSaveLogger(), context.Clock);
                Assert.That(store.Save(taken, new ProfileSyncState { OwnerAccountId = OtherOwner }).IsSuccess, Is.True, "Premise: another owner holds the base directory.");
                WriteGuestCoins(context, player, 8);

                ProfileActivationResult result = context.ActivateSync(ProfileA);

                Assert.That(result.IsSuccess, Is.True, result.ToString());
                Assert.That(result.ClaimedGuestData, Is.True, "The suffixed directory is absent, so the guest data is claimed into it.");
                Assert.That(player.PeekData().Coins, Is.EqualTo(8));
                Assert.That(context.Storage.HasFile(SaveLayout.SlotPath(suffixed, TestSlotKeys.Player)), Is.True, string.Join(", ", context.Storage.AllFilePaths));
                Assert.That(context.Storage.HasFile(SaveLayout.SlotPath(taken, TestSlotKeys.Player)), Is.False);

                Assert.That(player.Mutate(data => data.Coins = 9), Is.True);
                Assert.That(context.Service.FlushLocalNow().IsComplete, Is.True);
                Assert.That(context.Storage.HasFile(SaveLayout.SlotPath(taken, TestSlotKeys.Player)), Is.False, "Writes never land in the other owner's directory.");

                Assert.That(LoadOwner(context, taken), Is.EqualTo(OtherOwner));
                Assert.That(LoadOwner(context, suffixed), Is.EqualTo(AccountA));
                Assert.That(context.Logger.Contains(TestLogLevel.Warning, "belongs to another account"), Is.True, context.Logger.Describe());
            }
        }

        [TestCase("a")]
        [TestCase("save_1")]
        [TestCase("slot-2")]
        [TestCase("0123456789abcdefghijklmnopqrstuv")]
        public void LocalName_Valid_CreatesLocalProfile(string name)
        {
            Assert.That(ProfileId.IsValidLocalName(name), Is.True);

            ProfileId profile = ProfileId.Local(name);

            Assert.That(profile.Kind, Is.EqualTo(ProfileKind.Local));
            Assert.That(profile.LocalName, Is.EqualTo(name));
            Assert.That(profile.AccountId, Is.Null);
            Assert.That(profile.IsCloudBacked, Is.False);
            Assert.That(profile, Is.EqualTo(ProfileId.Local(name)));
            Assert.That(profile, Is.Not.EqualTo(ProfileId.Guest));
            Assert.That(TestPaths.ProfileDirectory(profile), Is.EqualTo("profiles/local-" + name));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("Save")]
        [TestCase("has space")]
        [TestCase("dot.name")]
        [TestCase("slash/name")]
        [TestCase("0123456789abcdefghijklmnopqrstuvw")]
        [TestCase("café")]
        public void LocalName_Invalid_Throws(string name)
        {
            Assert.That(ProfileId.IsValidLocalName(name), Is.False);
            Assert.Throws<ArgumentException>(() => ProfileId.Local(name));
        }

        [Test]
        public void GetLocalProfiles_ListsLocalDirectoriesSortedByName_WritesNothing()
        {
            using (TestServiceContext context = CreateContext(new ProfileSlot()))
            {
                Assert.That(context.InitializeSync().IsSuccess, Is.True);
                Assert.That(context.Service.GetLocalProfiles(), Is.Empty);

                Assert.That(context.ActivateSync(ProfileId.Local("beta")).IsSuccess, Is.True);
                Assert.That(context.ActivateSync(ProfileId.Local("alpha")).IsSuccess, Is.True);
                Assert.That(context.ActivateSync(ProfileA).IsSuccess, Is.True);
                Assert.That(context.ActivateSync(ProfileId.Guest).IsSuccess, Is.True);
                context.Storage.CreateDirectory(SaveLayout.Combine(SaveLayout.ProfilesDirectory, "local-Invalid"));
                context.Storage.CreateDirectory(SaveLayout.Combine(SaveLayout.ProfilesDirectory, "unrelated"));
                context.Storage.ResetCounters();

                IReadOnlyList<ProfileId> profiles = context.Service.GetLocalProfiles();

                Assert.That(profiles, Is.EqualTo(new[] { ProfileId.Local("alpha"), ProfileId.Local("beta") }));
                Assert.That(context.Storage.CountCalls(StorageOperation.Mutations), Is.EqualTo(0), "Listing is a query only.");
                Assert.That(context.Storage.WriteAttemptCount, Is.EqualTo(0));
                Assert.That(context.Service.ActiveProfile, Is.EqualTo(ProfileId.Guest));
            }
        }

        [UnityTest]
        public IEnumerator LocalProfile_NeverUploads()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                var player = new ProfileSlot();
                ProfileId local = ProfileId.Local("offline");
                using (TestServiceContext context = CreateContext(player))
                {
                    context.Provider.SignedInAccountId = AccountA;
                    Assert.That(context.InitializeSync().IsSuccess, Is.True);
                    Assert.That(context.ActivateSync(local).IsSuccess, Is.True);
                    context.Provider.ClearCalls();

                    Assert.That(player.Mutate(data => data.Coins = 5), Is.True);
                    FlushResult flushed = await context.Service.FlushAsync(CancellationToken.None);

                    Assert.That(flushed.Local.IsComplete, Is.True, flushed.ToString());
                    Assert.That(flushed.IsComplete, Is.False, "A local profile is never cloud complete.");
                    Assert.That(flushed.Cloud.Count, Is.EqualTo(1), flushed.ToString());
                    Assert.That(flushed.Cloud[0].Status, Is.EqualTo(CloudFlushStatus.Skipped));
                    Assert.That(flushed.Cloud[0].Reason, Is.EqualTo(CloudFlushReason.ProfileNotCloudBacked));

                    // Scheduler deadlines pass without an upload
                    Assert.That(player.Mutate(data => data.Coins = 6), Is.True);
                    for (int i = 0; i < 3; i++)
                    {
                        context.Clock.Advance(TimeSpan.FromSeconds(30));
                        await AsyncTestUtility.WaitFramesAsync(3);
                    }

                    Assert.That(context.Provider.TotalCallCount, Is.EqualTo(0), string.Join(", ", context.Provider.Calls));
                    Assert.That(context.Storage.HasFile(TestPaths.ProfileSlot(local, TestSlotKeys.Player)), Is.True);
                }
            });
        }

        // C2: the entry check and the hook dispatch both run before the gate, so a second call must be refused there
        [UnityTest]
        public IEnumerator ConcurrentActivations_SameProfile_DispatchTheHooksOnce()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                using (TestServiceContext context = CreateContext(new ProfileSlot()))
                {
                    Assert.That(context.InitializeSync().IsSuccess, Is.True);
                    ProfileId next = ProfileId.Local("next");
                    var hook = new HoldingDeactivationListener(0);
                    context.Service.AddDeactivationListener(hook);
                    try
                    {
                        UniTask<ProfileActivationResult> first = context.Service.ActivateProfileAsync(next, CancellationToken.None).Preserve();
                        await AsyncTestUtility.WaitUntilAsync(() => hook.CallCount == 1, MaxFrames, "Deactivation hook");

                        ProfileActivationResult second = await context.Service.ActivateProfileAsync(next, CancellationToken.None);

                        Assert.That(second.IsSuccess, Is.False, second.ToString());
                        Assert.That(second.Error.Code, Is.EqualTo(SaveErrorCode.ReentrantCall), second.ToString());
                        Assert.That(second.AlreadyActive, Is.False, second.ToString());
                        Assert.That(hook.CallCount, Is.EqualTo(1), "The hooks belong to one switch.");
                        Assert.That(context.Service.ActiveProfile, Is.EqualTo(ProfileId.Guest), "Nothing switched while the hook is pending.");

                        hook.Release();
                        await AsyncTestUtility.WaitUntilAsync(() => first.Status.IsCompleted(), MaxFrames, "First activation");

                        ProfileActivationResult result = await first;
                        Assert.That(result.IsSuccess, Is.True, result.ToString());
                        Assert.That(result.Profile, Is.EqualTo(next));
                        Assert.That(result.Previous, Is.EqualTo(ProfileId.Guest));
                        Assert.That(hook.CallCount, Is.EqualTo(1));
                        Assert.That(context.Service.ActiveProfile, Is.EqualTo(next));
                    }
                    finally
                    {
                        hook.Release();
                        context.Service.RemoveDeactivationListener(hook);
                    }
                }
            });
        }

        // F3: the single flight must outlive the activation tail, which raises ProfileActivated before it returns
        [UnityTest]
        public IEnumerator ActivationFromAProfileActivatedHandler_IsRefusedAndChangesNothing()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                using (TestServiceContext context = CreateContext(new ProfileSlot()))
                {
                    Assert.That(context.InitializeSync().IsSuccess, Is.True);

                    ProfileId first = ProfileId.Local("first");
                    ProfileId nested = ProfileId.Local("nested");
                    var raised = new List<ProfileId>();
                    UniTask<ProfileActivationResult> reentrant = default;
                    bool reentrantStarted = false;

                    Action<ProfileActivationResult> onActivated = result =>
                    {
                        raised.Add(result.Profile);
                        if (reentrantStarted)
                        {
                            return;
                        }

                        reentrantStarted = true;
                        reentrant = context.Service.ActivateProfileAsync(nested, CancellationToken.None).Preserve();
                    };

                    context.Service.ProfileActivated += onActivated;
                    try
                    {
                        ProfileActivationResult outer = await context.Service.ActivateProfileAsync(first, CancellationToken.None);

                        Assert.That(outer.IsSuccess, Is.True, outer.ToString());
                        Assert.That(reentrantStarted, Is.True, "Premise: the handler ran and called back in.");

                        ProfileActivationResult refused = await reentrant;
                        Assert.That(refused.IsSuccess, Is.False, refused.ToString());
                        Assert.That(refused.Error.Code, Is.EqualTo(SaveErrorCode.ReentrantCall), refused.ToString());
                        Assert.That(
                            context.Service.ActiveProfile,
                            Is.EqualTo(first),
                            "A handler must not switch the profile out from under the caller that is still returning.");
                        Assert.That(raised, Is.EqualTo(new[] { first }), "A nested activation would have raised its own event too.");
                    }
                    finally
                    {
                        context.Service.ProfileActivated -= onActivated;
                    }
                }
            });
        }

        // C2: the second call names another profile, so a stale Previous would be reported
        [UnityTest]
        public IEnumerator ConcurrentActivations_DifferentProfiles_SecondIsRefusedAndChangesNothing()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                using (TestServiceContext context = CreateContext(new ProfileSlot()))
                {
                    Assert.That(context.InitializeSync().IsSuccess, Is.True);
                    ProfileId next = ProfileId.Local("next");
                    ProfileId other = ProfileId.Local("other");
                    var hook = new HoldingDeactivationListener(0);
                    context.Service.AddDeactivationListener(hook);
                    try
                    {
                        UniTask<ProfileActivationResult> first = context.Service.ActivateProfileAsync(next, CancellationToken.None).Preserve();
                        await AsyncTestUtility.WaitUntilAsync(() => hook.CallCount == 1, MaxFrames, "Deactivation hook");

                        ProfileActivationResult second = await context.Service.ActivateProfileAsync(other, CancellationToken.None);

                        Assert.That(second.IsSuccess, Is.False, second.ToString());
                        Assert.That(second.Error.Code, Is.EqualTo(SaveErrorCode.ReentrantCall), second.ToString());
                        Assert.That(second.Previous, Is.EqualTo(ProfileId.Guest), second.ToString());
                        Assert.That(hook.CallCount, Is.EqualTo(1), "The refused call dispatches no hooks.");
                        Assert.That(hook.Deactivations, Is.EqualTo(new[] { "guest->local:next" }));

                        hook.Release();
                        await AsyncTestUtility.WaitUntilAsync(() => first.Status.IsCompleted(), MaxFrames, "First activation");

                        ProfileActivationResult result = await first;
                        Assert.That(result.IsSuccess, Is.True, result.ToString());
                        Assert.That(result.Profile, Is.EqualTo(next));
                        Assert.That(result.Previous, Is.EqualTo(ProfileId.Guest));
                        Assert.That(context.Service.ActiveProfile, Is.EqualTo(next));
                        Assert.That(context.Storage.HasDirectory(TestPaths.ProfileDirectory(other)), Is.False, "The refused activation created nothing.");
                    }
                    finally
                    {
                        hook.Release();
                        context.Service.RemoveDeactivationListener(hook);
                    }
                }
            });
        }

        // Mutates and flushes so profiles/guest holds a slot file
        private static void WriteGuestCoins(TestServiceContext context, ProfileSlot player, int coins)
        {
            Assert.That(context.Service.ActiveProfile, Is.EqualTo(ProfileId.Guest));
            Assert.That(player.Mutate(data => data.Coins = coins), Is.True);
            Assert.That(context.Service.FlushLocalNow().IsComplete, Is.True);
            Assert.That(context.Storage.HasFile(TestPaths.ProfileSlot(ProfileId.Guest, TestSlotKeys.Player)), Is.True, "Premise: guest holds a slot file.");
        }

        // Reads profile.json directly without repairing anything
        private static string LoadOwner(TestServiceContext context, string directory)
        {
            var store = new SyncStateStore(context.Storage, new TestSaveLogger(), context.Clock);
            SyncStateLoadResult loaded = store.Load(directory, SlotReadMode.ReadOnly);
            Assert.That(loaded.Status, Is.EqualTo(SyncStateLoadStatus.Loaded), loaded.Message);
            return loaded.State.OwnerAccountId;
        }

        private static TestServiceContext CreateContext(params SaveSlot[] slots)
        {
            return TestServiceFactory.Create(new TestServiceSetup
            {
                Slots = slots,
                ConfigureOptions = options => options.CloudRetryCount = 0,
            });
        }

        /// <summary>Records every dispatch and stays pending until Release; ignores its token.</summary>
        private sealed class HoldingDeactivationListener : IProfileDeactivatingListener
        {
            private readonly UniTaskCompletionSource _release = new UniTaskCompletionSource();
            private readonly List<string> _deactivations = new List<string>();

            public HoldingDeactivationListener(int order)
            {
                Order = order;
            }

            public int Order { get; }

            public int CallCount => _deactivations.Count;

            /// <summary>"{outgoing}->{incoming}" per dispatch, in order.</summary>
            public IReadOnlyList<string> Deactivations => _deactivations;

            public UniTask OnProfileDeactivatingAsync(ProfileDeactivation context, CancellationToken ct)
            {
                _deactivations.Add(context.Outgoing + "->" + context.Incoming);
                return _release.Task;
            }

            public void Release()
            {
                _release.TrySetResult();
            }
        }
    }
}
