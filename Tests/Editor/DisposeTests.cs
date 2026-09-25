using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Ecanakli.SaveSystem.Tests
{
    /// <summary>01 s7 and s13 item 14: Dispose flushes locally, stops the scheduler lanes, raises nothing and refuses every operation.</summary>
    [TestFixture]
    public sealed class DisposeTests
    {
        private const string AccountA = "account-a";
        private const string LocalWriteFailureMessage = "Local write of '" + TestSlotKeys.Player + "' failed";

        private static readonly ProfileId ProfileA = ProfileId.Account(AccountA);

        // The dirty slot reaches disk inside the Dispose call, without a single player-loop tick
        [Test]
        public void Dispose_FlushesPendingLocalWrites()
        {
            var player = new ProfileSlot();
            TestServiceContext context = CreateContext(player);
            try
            {
                Assert.That(context.InitializeSync().IsSuccess, Is.True);
                string path = TestPaths.ProfileSlot(context.Service.ActiveProfile, TestSlotKeys.Player);
                Assert.That(player.Mutate(data => data.Coins = 9), Is.True);
                Assert.That(context.Storage.GetWriteCount(path), Is.EqualTo(0), "Premise: the debounced write has not run yet.");
                long revision = player.Revision;

                context.Dispose();

                Assert.That(context.Storage.GetWriteCount(path), Is.EqualTo(1), "Dispose wrote the dirty slot itself.");
                JObject envelope = ReadEnvelope(context.Storage, path);
                Assert.That(envelope.Value<long>(SaveEnvelope.RevisionProperty), Is.EqualTo(revision), envelope.ToString());
                Assert.That(ReadCoins(envelope), Is.EqualTo(9), envelope.ToString());
            }
            finally
            {
                context.Dispose();
            }
        }

        // Both lanes are waiting on the clock (local debounce and a transient upload retry) and neither survives Dispose
        [UnityTest]
        public IEnumerator AfterDispose_ClockAdvance_StopsDebounceAndRetryLoops()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                var player = new ProfileSlot();
                TestServiceContext context = CreateContext(player);
                try
                {
                    context.Provider.SignedInAccountId = AccountA;
                    Assert.That(context.InitializeSync().IsSuccess, Is.True);
                    Assert.That(context.ActivateSync(ProfileA).IsSuccess, Is.True);
                    Assert.That(RestoreSync(context).Status, Is.EqualTo(SaveStatus.Success));

                    Assert.That(player.Mutate(data => data.Coins = 5), Is.True);
                    context.Provider.EnqueueError(CloudOperation.Write, TestSlotKeys.Player, CloudErrorKind.Transient);
                    FlushResult flushed = context.FlushSync();
                    Assert.That(flushed.IsComplete, Is.False, "Premise: the upload failed transiently. " + flushed);

                    // Local dirty again, so the local lane waits too
                    Assert.That(player.Mutate(data => data.Coins = 6), Is.True);
                    Assert.That(context.Clock.PendingDelayCount, Is.GreaterThan(0), "Premise: a scheduler lane is waiting on the clock.");

                    context.Dispose();

                    int writeAttempts = context.Storage.WriteAttemptCount;
                    int mutations = context.Storage.MutationCallCount;
                    int uploads = context.Provider.WriteCallCount;
                    Assert.That(context.Clock.PendingDelayCount, Is.EqualTo(0), "Dispose leaves no scheduler delay waiting on the clock.");

                    context.Clock.Advance(TimeSpan.FromMinutes(10));
                    await AsyncTestUtility.WaitFramesAsync(5);

                    Assert.That(context.Storage.WriteAttemptCount, Is.EqualTo(writeAttempts), "No local write after Dispose.");
                    Assert.That(context.Storage.MutationCallCount, Is.EqualTo(mutations), string.Join("\n", context.Storage.Calls));
                    Assert.That(context.Provider.WriteCallCount, Is.EqualTo(uploads), "No upload retry after Dispose.");
                }
                finally
                {
                    context.Dispose();
                }
            });
        }

        // S4: the dispose flush raises nothing, so a handler cannot re-enter Dispose
        [Test]
        public void Dispose_HealthHandlerCallingDispose_IsNotInvoked()
        {
            var player = new ProfileSlot();
            TestServiceContext context = CreateContext(player);
            var calls = new List<string>();
            Action<LocalWriteHealth> onHealthChanged = health =>
            {
                calls.Add("health " + health.Kind);
                context.Service.Dispose();
            };

            try
            {
                Assert.That(context.InitializeSync().IsSuccess, Is.True);
                Assert.That(player.Mutate(data => data.Coins = 9), Is.True);

                // The write of the dispose flush fails, which is a health transition
                context.Storage.FailWithKind(StorageOperation.Write, GuestPlayerPath, LocalWriteErrorKind.DiskFull);
                context.Service.LocalWriteHealthChanged += onHealthChanged;

                Assert.DoesNotThrow(() => context.Dispose());

                Assert.That(calls, Is.Empty, "No event is raised from inside Dispose.");
                Assert.That(context.Logger.Count(TestLogLevel.Error, "during Dispose"), Is.EqualTo(0), context.Logger.Describe());

                // The dispose flush result stays available to the caller
                LocalFlushResult flushed = context.Service.FlushLocalNow();
                Assert.That(flushed.IsComplete, Is.False, flushed.ToString());
                Assert.That(flushed.Failures[0].Kind, Is.EqualTo(LocalWriteErrorKind.DiskFull), flushed.ToString());
            }
            finally
            {
                context.Service.LocalWriteHealthChanged -= onHealthChanged;
                context.Dispose();
            }
        }

        // S4: the same guard keeps a handler from re-entering the flush that raised it
        [Test]
        public void Dispose_HealthHandlerCallingFlushLocalNow_IsNotInvoked()
        {
            var player = new ProfileSlot();
            TestServiceContext context = CreateContext(player);
            var calls = new List<string>();
            Action<LocalWriteHealth> onHealthChanged = health =>
            {
                calls.Add("health " + health.Kind);
                context.Service.FlushLocalNow();
            };

            try
            {
                Assert.That(context.InitializeSync().IsSuccess, Is.True);
                Assert.That(player.Mutate(data => data.Coins = 9), Is.True);
                context.Storage.FailWithKind(StorageOperation.Write, GuestPlayerPath, LocalWriteErrorKind.DiskFull);
                context.Service.LocalWriteHealthChanged += onHealthChanged;

                Assert.DoesNotThrow(() => context.Dispose());

                Assert.That(calls, Is.Empty, "No event is raised from inside Dispose.");
                Assert.That(context.Logger.Count(TestLogLevel.Error, LocalWriteFailureMessage), Is.EqualTo(1), "The dispose flush runs once. " + context.Logger.Describe());
                Assert.That(context.Logger.Count(TestLogLevel.Error, "during Dispose"), Is.EqualTo(0), context.Logger.Describe());
            }
            finally
            {
                context.Service.LocalWriteHealthChanged -= onHealthChanged;
                context.Dispose();
            }
        }

        // Handlers added before Dispose are dropped, and handlers added afterwards never hear anything either
        [Test]
        public void AfterDispose_NoEventsAreRaised()
        {
            var player = new ProfileSlot();
            TestServiceContext context = CreateContext(player);
            var before = new EventProbe(context.Service);
            EventProbe after = null;
            before.Attach();
            try
            {
                Assert.That(context.InitializeSync().IsSuccess, Is.True);
                Assert.That(before.Events, Is.Not.Empty, "Premise: events reach handlers before Dispose.");
                Assert.That(player.Mutate(data => data.Coins = 3), Is.True);

                context.Dispose();
                before.Clear();
                after = new EventProbe(context.Service);
                after.Attach();

                // Every refused operation, over a disk that would report a health change if anything were written
                context.Storage.FailWithKind(StorageOperation.Write, null, LocalWriteErrorKind.DiskFull);
                Assert.That(context.InitializeSync().IsSuccess, Is.False);
                Assert.That(context.ActivateSync(ProfileId.Local("next")).IsSuccess, Is.False);
                Assert.That(RestoreSync(context).Status, Is.EqualTo(SaveStatus.Failed));
                Assert.That(context.Service.FlushLocalNow().IsComplete, Is.True);
                Assert.That(context.FlushSync().Status, Is.EqualTo(SaveStatus.Failed));
                Assert.That(player.Mutate(data => data.Coins = 4), Is.False);

                Assert.That(before.Events, Is.Empty, "Handlers added before Dispose: " + before.Describe());
                Assert.That(after.Events, Is.Empty, "Handlers added after Dispose: " + after.Describe());
            }
            finally
            {
                before.Detach();
                after?.Detach();
                context.Dispose();
            }
        }

        // 01 s7: every member answers with a result instead of throwing
        [Test]
        public void AfterDispose_EveryOperation_IsRefusedWithDisposed()
        {
            var player = new ProfileSlot();
            TestServiceContext context = CreateContext(player);
            try
            {
                Assert.That(context.InitializeSync().IsSuccess, Is.True);
                Assert.That(player.Mutate(data => data.Coins = 1), Is.True);

                context.Dispose();
                ISaveService service = context.Service;

                Assert.That(service.IsInitialized, Is.False);
                Assert.That(service.IsReady, Is.False);
                Assert.That(AsyncTestUtility.RunSync(service.WhenReadyAsync(CancellationToken.None)), Is.False);
                Assert.DoesNotThrow(() => service.ActiveProfile.ToString());

                InitializeResult initialized = AsyncTestUtility.RunSync(service.InitializeAsync(CancellationToken.None), nameof(ISaveService.InitializeAsync));
                AssertDisposed(initialized.Status, initialized.Error, nameof(ISaveService.InitializeAsync));

                ProfileActivationResult activated = AsyncTestUtility.RunSync(
                    service.ActivateProfileAsync(ProfileId.Local("next"), CancellationToken.None), nameof(ISaveService.ActivateProfileAsync));
                AssertDisposed(activated.Status, activated.Error, nameof(ISaveService.ActivateProfileAsync));

                RestoreReport restored = AsyncTestUtility.RunSync(service.RestoreAsync(CancellationToken.None), nameof(ISaveService.RestoreAsync));
                AssertDisposed(restored.Status, restored.Error, nameof(ISaveService.RestoreAsync));

                FlushResult flushed = AsyncTestUtility.RunSync(service.FlushAsync(CancellationToken.None), nameof(ISaveService.FlushAsync));
                AssertDisposed(flushed.Status, flushed.Error, nameof(ISaveService.FlushAsync));

                DeleteResult slotDeleted = AsyncTestUtility.RunSync(
                    service.DeleteSlotAsync(player, DeleteTarget.LocalOnly, CancellationToken.None), nameof(ISaveService.DeleteSlotAsync));
                AssertDisposed(slotDeleted.Status, slotDeleted.Error, nameof(ISaveService.DeleteSlotAsync));

                DeleteResult profileDeleted = AsyncTestUtility.RunSync(
                    service.DeleteProfileAsync(ProfileId.Local("next"), ProfileDeleteMode.DiscardUnsynced, CancellationToken.None),
                    nameof(ISaveService.DeleteProfileAsync));
                AssertDisposed(profileDeleted.Status, profileDeleted.Error, nameof(ISaveService.DeleteProfileAsync));

                AccountDataDeleteResult accountDeleted = AsyncTestUtility.RunSync(
                    service.DeleteAccountDataAsync(AccountA, CancellationToken.None), nameof(ISaveService.DeleteAccountDataAsync));
                AssertDisposed(accountDeleted.Status, accountDeleted.Error, nameof(ISaveService.DeleteAccountDataAsync));

                Assert.That(service.GetLocalProfiles(), Is.Empty);
                Assert.That(service.ProbeLocalPresence(ProfileId.Guest), Is.EqualTo(LocalPresence.Undetermined));

                // FlushLocalNow reports the outcome of the flush Dispose already ran
                LocalFlushResult localFlush = service.FlushLocalNow();
                Assert.That(localFlush.IsComplete, Is.True, localFlush.ToString());

                // Mutate is refused; SaveNowAsync sees a slot that Dispose detached from its service
                Assert.That(player.Mutate(data => data.Coins = 2), Is.False);
                SaveResult saved = AsyncTestUtility.RunSync(player.SaveNowAsync(CancellationToken.None), "SaveNowAsync");
                Assert.That(saved.IsSuccess, Is.False, saved.ToString());
                Assert.That(saved.Error.Code, Is.EqualTo(SaveErrorCode.SlotNotRegistered), saved.ToString());

                Assert.DoesNotThrow(() => service.Dispose());
            }
            finally
            {
                context.Dispose();
            }
        }

        private static string GuestPlayerPath => TestPaths.ProfileSlot(ProfileId.Guest, TestSlotKeys.Player);

        private static void AssertDisposed(SaveStatus status, SaveError error, string member)
        {
            Assert.That(status, Is.EqualTo(SaveStatus.Failed), member);
            Assert.That(error, Is.Not.Null, member);
            Assert.That(error.Code, Is.EqualTo(SaveErrorCode.Disposed), member + ": " + error);
        }

        private static RestoreReport RestoreSync(TestServiceContext context)
        {
            return AsyncTestUtility.RunSync(context.Service.RestoreAsync(CancellationToken.None), nameof(ISaveService.RestoreAsync));
        }

        private static JObject ReadEnvelope(InMemorySaveStorage storage, string path)
        {
            byte[] bytes = storage.GetBytes(path);
            Assert.That(bytes, Is.Not.Null, "No local file " + path + ".");
            var envelope = SaveJson.Parse(bytes) as JObject;
            Assert.That(envelope, Is.Not.Null, "The local file is an envelope object.");
            return envelope;
        }

        // Property names depend on the serializer settings, so match case-insensitively
        private static int ReadCoins(JObject envelope)
        {
            JToken data = envelope[SaveEnvelope.DataProperty];
            Assert.That(data, Is.InstanceOf<JObject>(), envelope.ToString());
            JToken coins = ((JObject)data).GetValue("Coins", StringComparison.OrdinalIgnoreCase);
            Assert.That(coins, Is.Not.Null, envelope.ToString());
            return coins.Value<int>();
        }

        private static TestServiceContext CreateContext(params SaveSlot[] slots)
        {
            return TestServiceFactory.Create(new TestServiceSetup
            {
                Slots = slots,
                ConfigureOptions = options => options.CloudRetryCount = 0,
            });
        }

        /// <summary>Records every core event by name; Detach removes exactly what Attach added.</summary>
        private sealed class EventProbe
        {
            private readonly ISaveService _service;
            private readonly List<string> _events = new List<string>();
            private readonly Action<ProfileActivationResult> _onProfileActivated;
            private readonly Action<RestoreReport> _onRestoreCompleted;
            private readonly Action<SlotLoadIssue> _onSlotLoadIssue;
            private readonly Action<UploadFailure> _onUploadFailed;
            private readonly Action<LocalWriteHealth> _onHealthChanged;
            private readonly Action<UpdateRequiredInfo> _onUpdateRequired;

            public EventProbe(ISaveService service)
            {
                _service = service;
                _onProfileActivated = _ => _events.Add(nameof(ISaveService.ProfileActivated));
                _onRestoreCompleted = _ => _events.Add(nameof(ISaveService.RestoreCompleted));
                _onSlotLoadIssue = _ => _events.Add(nameof(ISaveService.SlotLoadIssueDetected));
                _onUploadFailed = _ => _events.Add(nameof(ISaveService.UploadFailed));
                _onHealthChanged = _ => _events.Add(nameof(ISaveService.LocalWriteHealthChanged));
                _onUpdateRequired = _ => _events.Add(nameof(ISaveService.UpdateRequired));
            }

            public IReadOnlyList<string> Events => _events;

            public string Describe()
            {
                return _events.Count == 0 ? "none" : string.Join(", ", _events);
            }

            public void Clear()
            {
                _events.Clear();
            }

            public void Attach()
            {
                _service.ProfileActivated += _onProfileActivated;
                _service.RestoreCompleted += _onRestoreCompleted;
                _service.SlotLoadIssueDetected += _onSlotLoadIssue;
                _service.UploadFailed += _onUploadFailed;
                _service.LocalWriteHealthChanged += _onHealthChanged;
                _service.UpdateRequired += _onUpdateRequired;
            }

            public void Detach()
            {
                _service.ProfileActivated -= _onProfileActivated;
                _service.RestoreCompleted -= _onRestoreCompleted;
                _service.SlotLoadIssueDetected -= _onSlotLoadIssue;
                _service.UploadFailed -= _onUploadFailed;
                _service.LocalWriteHealthChanged -= _onHealthChanged;
                _service.UpdateRequired -= _onUpdateRequired;
            }
        }
    }
}
