using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Ecanakli.SaveSystem.Tests
{
    /// <summary>SaveLifecycleDriver in EditMode: handle sharing and the pause, focus, quit and destroy callbacks invoked directly.</summary>
    [TestFixture]
    public sealed class SaveLifecycleDriverTests
    {
        private const string AccountA = "account-a";
        private const int MaxFrames = 120;
        private const string FlushLocalNowCall = "FlushLocalNow";
        private const string FlushAsyncCall = "FlushAsync";

        [TearDown]
        public void DestroyLeftoverDrivers()
        {
            foreach (SaveLifecycleDriver driver in Resources.FindObjectsOfTypeAll<SaveLifecycleDriver>())
            {
                if (driver != null)
                {
                    UnityEngine.Object.DestroyImmediate(driver.gameObject);
                }
            }
        }

        [Test]
        public void Attach_Null_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => SaveLifecycleDriver.Attach(null));
        }

        [Test]
        public void AttachTwice_SharesOneDriver_LastHandleDisposeDestroysIt_DoubleDisposeIsNoOp()
        {
            var service = new RecordingSaveService();
            IDisposable first = SaveLifecycleDriver.Attach(service);
            IDisposable second = SaveLifecycleDriver.Attach(service);
            SaveLifecycleDriver driver = SaveLifecycleDriver.GetAttached(service);

            Assert.That(driver != null, Is.True);
            Assert.That(Resources.FindObjectsOfTypeAll<SaveLifecycleDriver>().Length, Is.EqualTo(1), "Both handles share one driver.");

            first.Dispose();
            first.Dispose();

            Assert.That(driver != null, Is.True, "A repeated dispose of the same handle must not release the other handle.");
            Assert.That(SaveLifecycleDriver.GetAttached(service), Is.SameAs(driver));
            Assert.That(service.FlushLocalNowCount, Is.EqualTo(0));

            second.Dispose();

            Assert.That(driver == null, Is.True, "The last handle destroys the driver.");
            Assert.That(SaveLifecycleDriver.GetAttached(service), Is.Null);
            Assert.That(service.FlushLocalNowCount, Is.EqualTo(1), "Detaching the last handle flushes once.");

            second.Dispose();
            Assert.That(service.FlushLocalNowCount, Is.EqualTo(1));

            using (SaveLifecycleDriver.Attach(service))
            {
                SaveLifecycleDriver fresh = SaveLifecycleDriver.GetAttached(service);
                Assert.That(fresh != null, Is.True);
                Assert.That(ReferenceEquals(fresh, driver), Is.False, "Re-attaching creates a new driver.");
            }
        }

        [Test]
        public void PauseTrue_FlushesLocallyFirst_StartsBackgroundUploadWithoutToken()
        {
            var service = new RecordingSaveService();
            using (SaveLifecycleDriver.Attach(service))
            {
                SaveLifecycleDriver driver = SaveLifecycleDriver.GetAttached(service);

                driver.OnApplicationPause(true);

                Assert.That(service.Calls, Is.EqualTo(new[] { FlushLocalNowCall, FlushAsyncCall }));
                Assert.That(service.LastFlushToken.CanBeCanceled, Is.False, "The upload itself is never cancellable by the driver.");
                Assert.That(driver.HasPendingPauseUpload, Is.True);
                Assert.That(service.PendingFlushCompleted, Is.False, "The driver does not wait for the upload.");

                service.CompletePendingFlush();
            }
        }

        [Test]
        public void PauseTrue_BeforeInitialize_FlushesLocallyOnly()
        {
            var service = new RecordingSaveService { IsInitialized = false };
            using (SaveLifecycleDriver.Attach(service))
            {
                SaveLifecycleDriver driver = SaveLifecycleDriver.GetAttached(service);

                driver.OnApplicationPause(true);

                Assert.That(service.Calls, Is.EqualTo(new[] { FlushLocalNowCall }));
                Assert.That(driver.HasPendingPauseUpload, Is.False);
            }
        }

        [Test]
        public void PauseFalse_CancelsPendingWait_UploadKeepsRunning()
        {
            var service = new RecordingSaveService();
            using (SaveLifecycleDriver.Attach(service))
            {
                SaveLifecycleDriver driver = SaveLifecycleDriver.GetAttached(service);
                driver.OnApplicationPause(true);
                Assert.That(driver.HasPendingPauseUpload, Is.True);

                driver.OnApplicationPause(false);

                Assert.That(driver.HasPendingPauseUpload, Is.False);
                Assert.That(service.PendingFlushCompleted, Is.False, "Resuming abandons the wait, not the upload.");
                Assert.That(service.FlushLocalNowCount, Is.EqualTo(1), "Resume does not flush.");
                Assert.That(service.FlushAsyncCount, Is.EqualTo(1));

                // A late completion after the wait was abandoned is harmless
                service.CompletePendingFlush();
                Assert.That(service.FlushAsyncCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void SecondPause_ReplacesPendingWait()
        {
            var service = new RecordingSaveService();
            using (SaveLifecycleDriver.Attach(service))
            {
                SaveLifecycleDriver driver = SaveLifecycleDriver.GetAttached(service);
                driver.OnApplicationPause(true);
                service.CompletePendingFlush();

                driver.OnApplicationPause(true);

                Assert.That(service.FlushLocalNowCount, Is.EqualTo(2));
                Assert.That(service.FlushAsyncCount, Is.EqualTo(2));
                Assert.That(driver.HasPendingPauseUpload, Is.True);
                service.CompletePendingFlush();
            }
        }

        [Test]
        public void FocusLoss_FlushesLocallyOnly_FocusRegainCancelsPendingWait()
        {
            var service = new RecordingSaveService();
            using (SaveLifecycleDriver.Attach(service))
            {
                SaveLifecycleDriver driver = SaveLifecycleDriver.GetAttached(service);

                driver.OnApplicationFocus(false);

                Assert.That(service.Calls, Is.EqualTo(new[] { FlushLocalNowCall }));
                Assert.That(driver.HasPendingPauseUpload, Is.False);

                driver.OnApplicationPause(true);
                driver.OnApplicationFocus(true);

                Assert.That(driver.HasPendingPauseUpload, Is.False);
                Assert.That(service.FlushLocalNowCount, Is.EqualTo(2), "Focus regain does not flush.");
                service.CompletePendingFlush();
            }
        }

        [Test]
        public void Quit_FlushesOnce_LaterCallbacksAndDetachDoNotFlushAgain()
        {
            var service = new RecordingSaveService();
            IDisposable handle = SaveLifecycleDriver.Attach(service);
            SaveLifecycleDriver driver = SaveLifecycleDriver.GetAttached(service);

            driver.OnApplicationQuit();
            driver.OnApplicationQuit();

            Assert.That(service.Calls, Is.EqualTo(new[] { FlushLocalNowCall }));

            driver.OnApplicationPause(true);
            driver.OnApplicationFocus(false);
            handle.Dispose();

            Assert.That(service.Calls, Is.EqualTo(new[] { FlushLocalNowCall }), "Quit, pause, focus and detach share one flush.");
            Assert.That(driver == null, Is.True);
        }

        [Test]
        public void Destroy_FlushesOnce_CancelsPendingWait_DetachesFromService()
        {
            var service = new RecordingSaveService();
            IDisposable handle = SaveLifecycleDriver.Attach(service);
            SaveLifecycleDriver driver = SaveLifecycleDriver.GetAttached(service);
            GameObject owner = driver.gameObject;
            try
            {
                driver.OnApplicationPause(true);
                Assert.That(driver.HasPendingPauseUpload, Is.True);

                // Unity skips OnDestroy for edit-mode destruction; invoke the message directly
                InvokeOnDestroy(driver);
                InvokeOnDestroy(driver);
                driver.OnApplicationQuit();
                handle.Dispose();

                Assert.That(service.FlushLocalNowCount, Is.EqualTo(2), "One flush for the pause and one for destroy.");
                Assert.That(service.FlushAsyncCount, Is.EqualTo(1));
                Assert.That(driver.HasPendingPauseUpload, Is.False);
                Assert.That(SaveLifecycleDriver.GetAttached(service), Is.Null);
                service.CompletePendingFlush();
            }
            finally
            {
                if (owner != null)
                {
                    UnityEngine.Object.DestroyImmediate(owner);
                }
            }
        }

        [UnityTest]
        public IEnumerator PauseTrue_RealService_WritesLocalBeforeUpload_NoErrors()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                var slot = new ProfileSlot();
                using (TestServiceContext context = TestServiceFactory.Create(new TestServiceSetup
                       {
                           Slots = new SaveSlot[] { slot },
                           ConfigureOptions = options => options.CloudRetryCount = 0,
                       }))
                {
                    ProfileId profile = ProfileId.Account(AccountA);
                    context.Provider.SignedInAccountId = AccountA;
                    Assert.That(context.InitializeSync().IsSuccess, Is.True);
                    Assert.That(context.ActivateSync(profile).IsSuccess, Is.True);
                    RestoreReport report = AsyncTestUtility.RunSync(context.Service.RestoreAsync(CancellationToken.None), nameof(SaveService.RestoreAsync));
                    Assert.That(report.Status, Is.EqualTo(SaveStatus.Success), report.ToString());
                    context.Provider.ClearCalls();
                    context.Storage.ResetCounters();

                    string path = TestPaths.ProfileSlot(profile, TestSlotKeys.Player);
                    int localWritesAtUpload = -1;
                    context.Provider.AfterWrite = (provider, requests) => localWritesAtUpload = context.Storage.GetWriteCount(path);
                    Assert.That(slot.Mutate(data => data.Coins = 8), Is.True);

                    using (SaveLifecycleDriver.Attach(context.Service))
                    {
                        SaveLifecycleDriver driver = SaveLifecycleDriver.GetAttached(context.Service);

                        driver.OnApplicationPause(true);
                        Assert.That(context.Storage.GetWriteCount(path), Is.GreaterThanOrEqualTo(1), "The local flush is synchronous.");

                        await AsyncTestUtility.WaitUntilAsync(() => context.Provider.Store.Contains(AccountA, TestSlotKeys.Player), MaxFrames, "Pause upload");
                        await AsyncTestUtility.WaitFramesAsync(2);
                        driver.OnApplicationPause(false);
                    }

                    Assert.That(context.Provider.WriteCallCount, Is.EqualTo(1));
                    Assert.That(localWritesAtUpload, Is.GreaterThanOrEqualTo(1), "Disk first: the file existed before the provider call.");
                    Assert.That(context.Logger.Count(TestLogLevel.Error), Is.EqualTo(0), context.Logger.Describe());
                    Assert.That(context.Logger.Count(TestLogLevel.Warning, "pause"), Is.EqualTo(0), context.Logger.Describe());
                }
            });
        }

        private static void InvokeOnDestroy(SaveLifecycleDriver driver)
        {
            MethodInfo onDestroy = typeof(SaveLifecycleDriver).GetMethod("OnDestroy", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(onDestroy, Is.Not.Null);
            onDestroy.Invoke(driver, null);
        }

        // Records the calls the driver makes; FlushAsync stays pending until completed by the test
        private sealed class RecordingSaveService : ISaveService
        {
            private readonly List<string> _calls = new List<string>();
            private UniTaskCompletionSource<FlushResult> _pendingFlush;

            public event Action<ProfileActivationResult> ProfileActivated
            {
                add { }
                remove { }
            }

            public event Action<RestoreReport> RestoreCompleted
            {
                add { }
                remove { }
            }

            public event Action<SlotLoadIssue> SlotLoadIssueDetected
            {
                add { }
                remove { }
            }

            public event Action<UploadFailure> UploadFailed
            {
                add { }
                remove { }
            }

            public event Action<LocalWriteHealth> LocalWriteHealthChanged
            {
                add { }
                remove { }
            }

            public event Action<UpdateRequiredInfo> UpdateRequired
            {
                add { }
                remove { }
            }

            public bool IsInitialized { get; set; } = true;

            public bool IsReady => IsInitialized;

            public ProfileId ActiveProfile => ProfileId.Guest;

            public IReadOnlyList<string> Calls => _calls.ToArray();

            public int FlushLocalNowCount => _calls.FindAll(call => call == FlushLocalNowCall).Count;

            public int FlushAsyncCount => _calls.FindAll(call => call == FlushAsyncCall).Count;

            public CancellationToken LastFlushToken { get; private set; }

            public bool PendingFlushCompleted => _pendingFlush == null || _pendingFlush.Task.Status.IsCompleted();

            public void CompletePendingFlush()
            {
                _pendingFlush?.TrySetResult(new FlushResult(SaveStatus.Success, null, LocalFlushResult.Complete, null));
            }

            public LocalFlushResult FlushLocalNow()
            {
                _calls.Add(FlushLocalNowCall);
                return LocalFlushResult.Complete;
            }

            public UniTask<FlushResult> FlushAsync(CancellationToken ct)
            {
                _calls.Add(FlushAsyncCall);
                LastFlushToken = ct;
                _pendingFlush = new UniTaskCompletionSource<FlushResult>();
                return _pendingFlush.Task;
            }

            public UniTask<bool> WhenReadyAsync(CancellationToken ct)
            {
                throw new NotSupportedException();
            }

            public UniTask<InitializeResult> InitializeAsync(CancellationToken ct)
            {
                throw new NotSupportedException();
            }

            public UniTask<ProfileActivationResult> ActivateProfileAsync(ProfileId profile, CancellationToken ct)
            {
                throw new NotSupportedException();
            }

            public IReadOnlyList<ProfileId> GetLocalProfiles()
            {
                throw new NotSupportedException();
            }

            public UniTask<RestoreReport> RestoreAsync(CancellationToken ct)
            {
                throw new NotSupportedException();
            }

            public UniTask<DeleteResult> DeleteSlotAsync(SaveSlot slot, DeleteTarget target, CancellationToken ct)
            {
                throw new NotSupportedException();
            }

            public UniTask<DeleteResult> DeleteProfileAsync(ProfileId profile, ProfileDeleteMode mode, CancellationToken ct)
            {
                throw new NotSupportedException();
            }

            public UniTask<AccountDataDeleteResult> DeleteAccountDataAsync(string accountId, CancellationToken ct)
            {
                throw new NotSupportedException();
            }

            public LocalPresence ProbeLocalPresence(ProfileId profile)
            {
                throw new NotSupportedException();
            }

            public void AddRestoreListener(IRestoreListener listener)
            {
                throw new NotSupportedException();
            }

            public void RemoveRestoreListener(IRestoreListener listener)
            {
                throw new NotSupportedException();
            }

            public void AddDeactivationListener(IProfileDeactivatingListener listener)
            {
                throw new NotSupportedException();
            }

            public void RemoveDeactivationListener(IProfileDeactivatingListener listener)
            {
                throw new NotSupportedException();
            }

            public void Dispose()
            {
            }
        }
    }
}
