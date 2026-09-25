using System.Threading;
using Cysharp.Threading.Tasks;

namespace Ecanakli.SaveSystem
{
    // ISaveSlotHost: mutation marks and SaveNowAsync (04a A3 5.3, F4)
    public sealed partial class SaveService
    {
        ISaveLogger ISaveSlotHost.Logger => _logger;

        SaveServiceOptions ISaveSlotHost.Options => _options;

        SaveJson ISaveSlotHost.Json => _json;

        bool ISaveSlotHost.IsSwitchingProfile => _session.IsSwitchingProfile;

        bool ISaveSlotHost.IsDisposed => _disposed;

        void ISaveSlotHost.OnSlotMutated(SaveSlot slot)
        {
            if (_disposed || slot == null)
            {
                return;
            }

            _scheduler.MarkLocalDirty(slot);

            // Guest and Local profiles never upload; reconcile after an account activation queues the upload
            if (TracksContent(slot) && _session.ActiveProfile.IsCloudBacked)
            {
                _scheduler.MarkCloudDirty(slot);
            }
        }

        UniTask<SaveResult> ISaveSlotHost.SaveSlotNowAsync(SaveSlot slot, CancellationToken ct)
        {
            return SaveSlotNowAsync(slot, ct);
        }

        // Durable write of the current revision ignoring backoff; requests an upload but does not await it
        private async UniTask<SaveResult> SaveSlotNowAsync(SaveSlot slot, CancellationToken ct)
        {
            if (TryRefuseSaveNow(slot, out SaveResult refusal))
            {
                return refusal;
            }

            // Caller cancellation is honored only before the write starts
            ct.ThrowIfCancellationRequested();

            LocalWriteJob job = PrepareLocalWrite(slot, _localWriteGeneration, out string error);
            if (job == null)
            {
                return SaveResult.Failure(SaveErrorCode.Unknown, error);
            }

            if (_options.OffloadIo)
            {
                await UniTask.RunOnThreadPool(() => ExecuteLocalWrite(job), true, CancellationToken.None);
            }
            else
            {
                ExecuteLocalWrite(job);
            }

            if (!job.Superseded)
            {
                return CompleteSaveNow(job);
            }

            // A FlushLocalNow or Dispose ran before the write started; retry inline while the slot still belongs to this epoch
            if (_disposed)
            {
                return SaveResult.Failure(SaveErrorCode.Disposed, "The save service was disposed before the write started.");
            }

            if (slot.Scope == SlotScope.Profile && job.Epoch != _session.Epoch)
            {
                return SaveResult.Failure(SaveErrorCode.Superseded, "The active profile changed before the write started.");
            }

            if (TryRefuseSaveNow(slot, out refusal))
            {
                return refusal;
            }

            LocalWriteJob retry = PrepareLocalWrite(slot, _localWriteGeneration, out error);
            if (retry == null)
            {
                return SaveResult.Failure(SaveErrorCode.Unknown, error);
            }

            ExecuteLocalWrite(retry);
            return CompleteSaveNow(retry);
        }

        private SaveResult CompleteSaveNow(LocalWriteJob job)
        {
            SaveSlot slot = job.Slot;
            bool current = !_disposed && (slot.Scope == SlotScope.Device || job.Epoch == _session.Epoch);
            if (current)
            {
                bool healthChanged = false;
                bool persistContentMarker = false;
                ApplyLocalWriteResult(job, ref healthChanged, ref persistContentMarker);
                RaiseHealthIfChanged(healthChanged);
                if (persistContentMarker)
                {
                    PersistContentMarker();
                }
            }

            if (job.Exception != null)
            {
                return SaveResult.Failure(new SaveError(SaveErrorCode.Unknown, "Writing '" + slot.Key + "' threw: " + job.Exception.Message, job.Exception));
            }

            SlotWriteResult result = job.Result;
            if (!result.IsSuccess)
            {
                return SaveResult.Failure(result.ToSaveError());
            }

            if (current && TracksContent(slot) && _session.ActiveProfile.IsCloudBacked)
            {
                _scheduler.RequestImmediateUpload(slot);
            }

            return SaveResult.Success(job.Revision);
        }

        private bool TryRefuseSaveNow(SaveSlot slot, out SaveResult refusal)
        {
            SaveErrorCode code = SaveErrorCode.None;
            string message = null;
            if (HookScope.RefuseIfActive(_logger, "SaveNowAsync on slot '" + slot.Key + "'"))
            {
                code = SaveErrorCode.CalledFromHook;
                message = "SaveNowAsync" + HookRefusalSuffix;
            }
            else if (_disposed)
            {
                code = SaveErrorCode.Disposed;
                message = "SaveNowAsync" + DisposedRefusalSuffix;
            }
            else if (!_registry.Contains(slot))
            {
                code = SaveErrorCode.SlotNotRegistered;
                message = "Slot '" + slot.Key + "' is not registered with this SaveService.";
            }
            else if (!_session.IsInitialized)
            {
                code = SaveErrorCode.NotInitialized;
                message = "SaveNowAsync requires InitializeAsync first.";
            }
            else if (_session.IsSwitchingProfile || slot.State != SlotState.Ready)
            {
                code = SaveErrorCode.SlotNotReady;
                message = "Slot '" + slot.Key + "' is not Ready (" + slot.State + ").";
            }
            else if (slot.SyncMode == SyncMode.CloudReadOnly)
            {
                code = SaveErrorCode.SlotReadOnly;
                message = "Slot '" + slot.Key + "' is CloudReadOnly; its local mirror is written by restore.";
            }

            refusal = code == SaveErrorCode.None ? default : SaveResult.Failure(code, message);
            return code != SaveErrorCode.None;
        }
    }
}
