using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Ecanakli.SaveSystem.Tests
{
    /// <summary>
    /// SaveLifecycleDriver on a real player loop: DontDestroyOnLoad creation, the pause, focus and quit messages on a live
    /// component, deferred destruction of the owner GameObject and re-attaching after dispose (01 5.8, 04a A3 5.8).
    /// </summary>
    [TestFixture]
    public sealed class SaveLifecycleDriverPlayModeTests
    {
        private const string FlushLocalNowCall = "FlushLocalNow";
        private const string FlushAsyncCall = "FlushAsync";
        private const string DontDestroyOnLoadSceneName = "DontDestroyOnLoad";
        private const int MaxFrames = 120;

        private int _mainThreadId;

        [SetUp]
        public void CaptureMainThread()
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        [UnityTearDown]
        public IEnumerator DestroyLeftoverDrivers()
        {
            foreach (SaveLifecycleDriver driver in Resources.FindObjectsOfTypeAll<SaveLifecycleDriver>())
            {
                if (driver != null)
                {
                    UnityEngine.Object.Destroy(driver.gameObject);
                }
            }

            // Deferred destruction lands at the end of the frame
            yield return null;
        }

        [UnityTest]
        public IEnumerator Attach_CreatesDontDestroyOnLoadDriver_DisposeDestroysTheGameObject()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                var service = new RecordingSaveService();
                IDisposable handle = SaveLifecycleDriver.Attach(service);
                SaveLifecycleDriver driver = SaveLifecycleDriver.GetAttached(service);

                Assert.That(driver != null, Is.True, "Attach must create a live driver.");
                GameObject owner = driver.gameObject;
                Assert.That(owner.scene.name, Is.EqualTo(DontDestroyOnLoadSceneName), "The driver must survive scene loads.");
                Assert.That(owner.hideFlags & HideFlags.HideInHierarchy, Is.EqualTo(HideFlags.HideInHierarchy));

                handle.Dispose();

                Assert.That(SaveLifecycleDriver.GetAttached(service), Is.Null, "Detach runs synchronously.");
                Assert.That(service.FlushLocalNowCount, Is.EqualTo(1), "Detaching the last handle flushes once.");

                // Destroy is deferred to the end of the frame in play mode
                await AsyncTestUtility.WaitUntilAsync(() => owner == null, MaxFrames, "Driver GameObject destroyed");
            });
        }

        [UnityTest]
        public IEnumerator Pause_OnLiveDriver_FlushesLocalNowThenStartsUpload_ResumeCancelsTheWait()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                var service = new RecordingSaveService();
                using (SaveLifecycleDriver.Attach(service))
                {
                    SaveLifecycleDriver driver = SaveLifecycleDriver.GetAttached(service);

                    driver.OnApplicationPause(true);

                    Assert.That(service.Calls, Is.EqualTo(new[] { FlushLocalNowCall, FlushAsyncCall }), "Disk first, then the best-effort upload.");
                    Assert.That(driver.HasPendingPauseUpload, Is.True);
                    Assert.That(service.LastFlushToken.CanBeCanceled, Is.False, "The upload itself is never cancellable by the driver.");

                    service.CompletePendingFlush();
                    await AsyncTestUtility.WaitFramesAsync(2);

                    driver.OnApplicationPause(false);

                    Assert.That(driver.HasPendingPauseUpload, Is.False);
                    Assert.That(service.FlushLocalNowCount, Is.EqualTo(1), "Resume does not flush.");
                    Assert.That(service.FlushAsyncCount, Is.EqualTo(1));
                }

                await AsyncTestUtility.WaitFramesAsync(1);
            });
        }

        [UnityTest]
        public IEnumerator FocusLoss_OnLiveDriver_FlushesLocalNowWithoutUpload()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                var service = new RecordingSaveService();
                using (SaveLifecycleDriver.Attach(service))
                {
                    SaveLifecycleDriver driver = SaveLifecycleDriver.GetAttached(service);

                    driver.OnApplicationFocus(false);

                    Assert.That(service.Calls, Is.EqualTo(new[] { FlushLocalNowCall }), "Focus loss is local only; no network.");
                    Assert.That(driver.HasPendingPauseUpload, Is.False);

                    driver.OnApplicationFocus(true);

                    Assert.That(service.FlushLocalNowCount, Is.EqualTo(1), "Focus regain does not flush.");
                }

                await AsyncTestUtility.WaitFramesAsync(1);
            });
        }

        [UnityTest]
        public IEnumerator Quit_OnLiveDriver_FlushesOnce_StopsForwarding()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                var service = new RecordingSaveService();
                IDisposable handle = SaveLifecycleDriver.Attach(service);
                SaveLifecycleDriver driver = SaveLifecycleDriver.GetAttached(service);
                GameObject owner = driver.gameObject;

                driver.OnApplicationQuit();

                Assert.That(service.Calls, Is.EqualTo(new[] { FlushLocalNowCall }));

                driver.OnApplicationQuit();
                driver.OnApplicationPause(true);
                driver.OnApplicationFocus(false);
                handle.Dispose();

                Assert.That(service.Calls, Is.EqualTo(new[] { FlushLocalNowCall }), "Quit, pause, focus and detach share one final flush.");
                await AsyncTestUtility.WaitUntilAsync(() => owner == null, MaxFrames, "Driver GameObject destroyed");
                Assert.That(service.Calls, Is.EqualTo(new[] { FlushLocalNowCall }), "OnDestroy after quit does not flush again.");
            });
        }

        [UnityTest]
        public IEnumerator DisposedHandle_StopsForwarding_SecondAttachCreatesAWorkingDriver()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                var service = new RecordingSaveService();
                IDisposable first = SaveLifecycleDriver.Attach(service);
                SaveLifecycleDriver firstDriver = SaveLifecycleDriver.GetAttached(service);
                GameObject firstOwner = firstDriver.gameObject;

                first.Dispose();
                Assert.That(service.FlushLocalNowCount, Is.EqualTo(1));

                // The component still exists this frame, but it must not forward anything
                firstDriver.OnApplicationPause(true);
                firstDriver.OnApplicationFocus(false);
                Assert.That(service.FlushLocalNowCount, Is.EqualTo(1), "A detached driver stops forwarding.");
                Assert.That(firstDriver.HasPendingPauseUpload, Is.False);

                IDisposable second = SaveLifecycleDriver.Attach(service);
                SaveLifecycleDriver secondDriver = SaveLifecycleDriver.GetAttached(service);
                Assert.That(secondDriver != null, Is.True);
                Assert.That(ReferenceEquals(secondDriver, firstDriver), Is.False, "Re-attaching creates a new driver.");
                Assert.That(secondDriver.gameObject.scene.name, Is.EqualTo(DontDestroyOnLoadSceneName));

                await AsyncTestUtility.WaitUntilAsync(() => firstOwner == null, MaxFrames, "First driver GameObject destroyed");
                Assert.That(service.FlushLocalNowCount, Is.EqualTo(1), "Destroying the detached driver does not flush again.");

                secondDriver.OnApplicationFocus(false);
                Assert.That(service.FlushLocalNowCount, Is.EqualTo(2), "The fresh driver forwards again.");

                GameObject secondOwner = secondDriver.gameObject;
                second.Dispose();
                await AsyncTestUtility.WaitUntilAsync(() => secondOwner == null, MaxFrames, "Second driver GameObject destroyed");
            });
        }

        [UnityTest]
        public IEnumerator Quit_RealService_WritesDirtySlotSynchronouslyOnTheMainThread()
        {
            return AsyncTestUtility.ToCoroutine(async () =>
            {
                var slot = new ProfileSlot();
                using (TestServiceContext context = TestServiceFactory.Create(new TestServiceSetup
                       {
                           Slots = new SaveSlot[] { slot },

                           // The quit path must stay synchronous even when IO is offloaded
                           ConfigureOptions = options => options.OffloadIo = true,
                       }))
                {
                    InitializeResult init = await context.Service.InitializeAsync(CancellationToken.None);
                    Assert.That(init.IsSuccess, Is.True, init.ToString());
                    context.Storage.ResetCounters();

                    string path = TestPaths.ProfileSlot(ProfileId.Guest, TestSlotKeys.Player);
                    Assert.That(slot.Mutate(data => data.Coins = 5), Is.True);

                    using (SaveLifecycleDriver.Attach(context.Service))
                    {
                        SaveLifecycleDriver driver = SaveLifecycleDriver.GetAttached(context.Service);

                        driver.OnApplicationQuit();

                        Assert.That(context.Storage.GetWriteCount(path), Is.EqualTo(1), "FlushLocalNow never offloads; the bytes are on disk when quit returns.");
                        Assert.That(context.Storage.WriteThreadIds, Is.EqualTo(new[] { _mainThreadId }), "The quit flush writes on the main thread.");
                    }

                    Assert.That(context.Logger.Count(TestLogLevel.Error), Is.EqualTo(0), context.Logger.Describe());
                    await AsyncTestUtility.WaitFramesAsync(1);
                }
            });
        }

        // Records the calls the driver makes; FlushAsync stays pending until the test completes it
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
