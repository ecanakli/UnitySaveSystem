using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace Ecanakli.SaveSystem
{
    /// <summary>
    /// Forwards pause, focus loss and quit to an ISaveService (01 5.8, 04a A3 5.8). Holds no save logic. Main thread only.
    /// Create it with Attach; dispose the returned handle to flush and destroy it.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class SaveLifecycleDriver : MonoBehaviour
    {
        private const string GameObjectName = "[SaveLifecycleDriver]";

        // One driver per service; handles share it through a reference count
        private static readonly List<SaveLifecycleDriver> s_drivers = new List<SaveLifecycleDriver>();

        private ISaveService _service;
        private ISaveLogger _logger;
        private int _handleCount;
        private bool _finalFlushDone;
        private bool _shutDown;

        // Owned by the driver: created on pause, cancelled and disposed on resume, focus regain, the next pause or shutdown
        private CancellationTokenSource _pauseUploadCts;

        /// <summary>True while a pause upload is still awaited by the driver.</summary>
        internal bool HasPendingPauseUpload => _pauseUploadCts != null;

        /// <summary>Attaches a hidden DontDestroyOnLoad driver for the service, reusing a live one. Main thread only.</summary>
        public static IDisposable Attach(ISaveService service)
        {
            if (service == null)
            {
                throw new ArgumentNullException(nameof(service));
            }

            SaveLifecycleDriver driver = FindDriver(service);
            if (driver == null)
            {
                driver = CreateDriver(service);
            }

            driver._handleCount++;
            return new Handle(driver);
        }

        /// <summary>Live driver attached to the service, or null; tests invoke the callbacks on it directly.</summary>
        internal static SaveLifecycleDriver GetAttached(ISaveService service)
        {
            return service == null ? null : FindDriver(service);
        }

        internal void OnApplicationPause(bool pauseStatus)
        {
            if (!IsForwarding)
            {
                return;
            }

            try
            {
                if (pauseStatus)
                {
                    FlushLocal("pause");
                    StartPauseUpload();
                }
                else
                {
                    CancelPauseUpload();
                    ResumeRetries();
                }
            }
            catch (Exception exception)
            {
                LogError(_logger, "[SaveSystem] Lifecycle driver failed handling pause=" + pauseStatus + ".", exception);
            }
        }

        internal void OnApplicationFocus(bool hasFocus)
        {
            if (!IsForwarding)
            {
                return;
            }

            try
            {
                if (hasFocus)
                {
                    CancelPauseUpload();
                }
                else
                {
                    // No network: system dialogs cause frequent focus loss
                    FlushLocal("focus loss");
                }
            }
            catch (Exception exception)
            {
                LogError(_logger, "[SaveSystem] Lifecycle driver failed handling focus=" + hasFocus + ".", exception);
            }
        }

        internal void OnApplicationQuit()
        {
            FinalFlush("quit");
        }

        private void OnDestroy()
        {
            Shutdown("destroy");
        }

        private bool IsForwarding => _service != null && !_shutDown && !_finalFlushDone;

        // Only the package's own service exposes the transient-retry resume
        private void ResumeRetries()
        {
            if (_service is SaveService saveService)
            {
                saveService.ResumeTransientRetries();
            }
        }

        private static SaveLifecycleDriver FindDriver(ISaveService service)
        {
            for (int i = s_drivers.Count - 1; i >= 0; i--)
            {
                SaveLifecycleDriver driver = s_drivers[i];

                // Destroyed without OnDestroy (edit mode) or already shut down
                if (driver == null || driver._shutDown)
                {
                    s_drivers.RemoveAt(i);
                    continue;
                }

                if (ReferenceEquals(driver._service, service))
                {
                    return driver;
                }
            }

            return null;
        }

        private static SaveLifecycleDriver CreateDriver(ISaveService service)
        {
            var owner = new GameObject(GameObjectName);
            owner.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSaveInEditor;
            try
            {
                // DontDestroyOnLoad throws outside play mode (EditMode tests)
                if (Application.isPlaying)
                {
                    DontDestroyOnLoad(owner);
                }

                SaveLifecycleDriver driver = owner.AddComponent<SaveLifecycleDriver>();
                driver._service = service;
                driver._logger = ResolveLogger(service);
                s_drivers.Add(driver);
                return driver;
            }
            catch
            {
                DestroyOwner(owner);
                throw;
            }
        }

        private static ISaveLogger ResolveLogger(ISaveService service)
        {
            ISaveLogger logger = service is ISaveSlotHost host ? host.Logger : null;
            return logger ?? UnityDebugSaveLogger.Fallback;
        }

        private static void DestroyOwner(GameObject owner)
        {
            if (owner == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                Destroy(owner);
            }
            else
            {
                DestroyImmediate(owner);
            }
        }

        // Enter Play Mode without domain reload keeps statics alive
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            s_drivers.Clear();
        }

        private void Release()
        {
            if (_shutDown)
            {
                return;
            }

            _handleCount--;
            if (_handleCount > 0)
            {
                return;
            }

            Shutdown("detach");

            // Shutdown runs synchronously so a re-Attach in the same frame gets a fresh driver
            if (this != null)
            {
                DestroyOwner(gameObject);
            }
        }

        private void Shutdown(string reason)
        {
            if (_shutDown)
            {
                return;
            }

            FinalFlush(reason);
            CancelPauseUpload();
            _shutDown = true;
            s_drivers.Remove(this);
        }

        // Quit, destroy and detach share one flush
        private void FinalFlush(string reason)
        {
            if (_service == null || _finalFlushDone)
            {
                return;
            }

            _finalFlushDone = true;
            FlushLocal(reason);
        }

        // Health changes are reported by the service event; only exceptions are logged here
        private void FlushLocal(string reason)
        {
            try
            {
                _service.FlushLocalNow();
            }
            catch (Exception exception)
            {
                LogError(_logger, "[SaveSystem] FlushLocalNow on " + reason + " threw.", exception);
            }
        }

        private void StartPauseUpload()
        {
            CancelPauseUpload();

            // Avoids a NotInitialized refusal before init and after Dispose
            if (!_service.IsInitialized)
            {
                return;
            }

            var cts = new CancellationTokenSource();
            _pauseUploadCts = cts;
            AwaitPauseUploadAsync(_service, _logger, cts.Token).Forget();
        }

        private void CancelPauseUpload()
        {
            CancellationTokenSource cts = _pauseUploadCts;
            if (cts == null)
            {
                return;
            }

            _pauseUploadCts = null;
            try
            {
                cts.Cancel();
            }
            catch (Exception exception)
            {
                LogError(_logger, "[SaveSystem] Cancelling the pause upload wait threw.", exception);
            }
            finally
            {
                cts.Dispose();
            }
        }

        // The token only stops the driver awaiting; the upload itself is never cancelled
        private static async UniTaskVoid AwaitPauseUploadAsync(ISaveService service, ISaveLogger logger, CancellationToken stopAwaiting)
        {
            try
            {
                await UploadAndLogAsync(service, logger).AttachExternalCancellation(stopAwaiting);
            }
            catch (OperationCanceledException) when (stopAwaiting.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                LogError(logger, "[SaveSystem] Awaiting the pause upload threw.", exception);
            }
        }

        // Never throws and logs its own outcome, because an abandoned await swallows late exceptions
        private static async UniTask UploadAndLogAsync(ISaveService service, ISaveLogger logger)
        {
            try
            {
                // No token: resuming must not abort an upload already holding the gate
                FlushResult result = await service.FlushAsync(CancellationToken.None);
                LogUploadResult(logger, result);
            }
            catch (Exception exception)
            {
                LogError(logger, "[SaveSystem] Best-effort cloud flush after pause threw.", exception);
            }
        }

        private static void LogUploadResult(ISaveLogger logger, FlushResult result)
        {
            if (result == null || result.Status != SaveStatus.Failed)
            {
                return;
            }

            SaveErrorCode code = result.Error != null ? result.Error.Code : SaveErrorCode.Unknown;
            string message = "[SaveSystem] Best-effort cloud flush after pause failed (" + code + "): "
                             + (result.Error != null ? result.Error.Message : result.ToString());
            try
            {
                // A profile switch or Dispose during the pause upload is expected
                if (code == SaveErrorCode.Superseded || code == SaveErrorCode.Disposed)
                {
                    if (logger.IsVerboseEnabled)
                    {
                        logger.Verbose(message);
                    }
                }
                else
                {
                    logger.Warning(message);
                }
            }
            catch (Exception loggerException)
            {
                Debug.LogWarning(message);
                Debug.LogException(loggerException);
            }
        }

        private static void LogError(ISaveLogger logger, string message, Exception exception)
        {
            try
            {
                logger.Error(message, exception);
            }
            catch (Exception loggerException)
            {
                Debug.LogException(exception);
                Debug.LogException(loggerException);
            }
        }

        // Disposing twice is a no-op; the driver is destroyed when its last handle is disposed
        private sealed class Handle : IDisposable
        {
            private SaveLifecycleDriver _driver;

            public Handle(SaveLifecycleDriver driver)
            {
                _driver = driver;
            }

            public void Dispose()
            {
                SaveLifecycleDriver driver = _driver;
                if (ReferenceEquals(driver, null))
                {
                    return;
                }

                _driver = null;
                driver.Release();
            }
        }
    }
}
