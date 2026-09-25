using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Ecanakli.SaveSystem
{
    // Public entry points: hook refusal first (04 R5), then Disposed, then delegation to the operation
    public sealed partial class SaveService
    {
        private const string HookRefusalSuffix = " was called from a slot hook.";
        private const string DisposedRefusalSuffix = " was called after Dispose.";

        public UniTask<bool> WhenReadyAsync(CancellationToken ct)
        {
            if (HookScope.RefuseIfActive(_logger, nameof(WhenReadyAsync)))
            {
                return UniTask.FromResult(false);
            }

            // False after Dispose; caller cancellation throws
            return _readiness.WhenReadyAsync(ct);
        }

        public async UniTask<InitializeResult> InitializeAsync(CancellationToken ct)
        {
            if (HookScope.RefuseIfActive(_logger, nameof(InitializeAsync)))
            {
                return new InitializeResult(
                    SaveStatus.Failed, new SaveError(SaveErrorCode.CalledFromHook, nameof(InitializeAsync) + HookRefusalSuffix), _session.ActiveProfile, null);
            }

            if (_disposed)
            {
                return new InitializeResult(
                    SaveStatus.Failed, new SaveError(SaveErrorCode.Disposed, nameof(InitializeAsync) + DisposedRefusalSuffix), ProfileId.Guest, null);
            }

            ProfileActivationResult activation = await _session.InitializeAsync(ct);
            return new InitializeResult(activation.Status, activation.Error, activation.Profile, activation.Issues);
        }

        public UniTask<ProfileActivationResult> ActivateProfileAsync(ProfileId profile, CancellationToken ct)
        {
            if (HookScope.RefuseIfActive(_logger, nameof(ActivateProfileAsync)))
            {
                return UniTask.FromResult(CreateActivationRefusal(profile, SaveErrorCode.CalledFromHook, nameof(ActivateProfileAsync) + HookRefusalSuffix));
            }

            if (_disposed)
            {
                return UniTask.FromResult(CreateActivationRefusal(profile, SaveErrorCode.Disposed, nameof(ActivateProfileAsync) + DisposedRefusalSuffix));
            }

            return _session.ActivateProfileAsync(profile, ct);
        }

        public IReadOnlyList<ProfileId> GetLocalProfiles()
        {
            if (HookScope.RefuseIfActive(_logger, nameof(GetLocalProfiles)) || _disposed)
            {
                return Array.Empty<ProfileId>();
            }

            return _session.GetLocalProfiles();
        }

        public UniTask<RestoreReport> RestoreAsync(CancellationToken ct)
        {
            if (HookScope.RefuseIfActive(_logger, nameof(RestoreAsync)))
            {
                return UniTask.FromResult(CreateRestoreRefusal(SaveErrorCode.CalledFromHook, nameof(RestoreAsync) + HookRefusalSuffix));
            }

            if (_disposed)
            {
                return UniTask.FromResult(CreateRestoreRefusal(SaveErrorCode.Disposed, nameof(RestoreAsync) + DisposedRefusalSuffix));
            }

            return _restore.RestoreAsync(ct);
        }

        public UniTask<DeleteResult> DeleteSlotAsync(SaveSlot slot, DeleteTarget target, CancellationToken ct)
        {
            if (slot == null)
            {
                throw new ArgumentNullException(nameof(slot));
            }

            if (HookScope.RefuseIfActive(_logger, nameof(DeleteSlotAsync)))
            {
                return UniTask.FromResult(CreateDeleteRefusal(SaveErrorCode.CalledFromHook, nameof(DeleteSlotAsync) + HookRefusalSuffix));
            }

            if (_disposed)
            {
                return UniTask.FromResult(CreateDeleteRefusal(SaveErrorCode.Disposed, nameof(DeleteSlotAsync) + DisposedRefusalSuffix));
            }

            return _profileDelete.DeleteSlotAsync(slot, target, ct);
        }

        public UniTask<DeleteResult> DeleteProfileAsync(ProfileId profile, ProfileDeleteMode mode, CancellationToken ct)
        {
            if (HookScope.RefuseIfActive(_logger, nameof(DeleteProfileAsync)))
            {
                return UniTask.FromResult(CreateDeleteRefusal(SaveErrorCode.CalledFromHook, nameof(DeleteProfileAsync) + HookRefusalSuffix));
            }

            if (_disposed)
            {
                return UniTask.FromResult(CreateDeleteRefusal(SaveErrorCode.Disposed, nameof(DeleteProfileAsync) + DisposedRefusalSuffix));
            }

            return _profileDelete.DeleteProfileAsync(profile, mode, ct);
        }

        public UniTask<AccountDataDeleteResult> DeleteAccountDataAsync(string accountId, CancellationToken ct)
        {
            if (HookScope.RefuseIfActive(_logger, nameof(DeleteAccountDataAsync)))
            {
                return UniTask.FromResult(new AccountDataDeleteResult(
                    SaveStatus.Failed, new SaveError(SaveErrorCode.CalledFromHook, nameof(DeleteAccountDataAsync) + HookRefusalSuffix), accountId, null, false));
            }

            if (_disposed)
            {
                return UniTask.FromResult(new AccountDataDeleteResult(
                    SaveStatus.Failed, new SaveError(SaveErrorCode.Disposed, nameof(DeleteAccountDataAsync) + DisposedRefusalSuffix), accountId, null, false));
            }

            return _accountDelete.DeleteAccountDataAsync(accountId, ct);
        }

        /// <summary>Present when any registered Profile slot has a readable local file; header peek only, never writes or repairs.</summary>
        public LocalPresence ProbeLocalPresence(ProfileId profile)
        {
            if (HookScope.RefuseIfActive(_logger, nameof(ProbeLocalPresence)) || _disposed)
            {
                return LocalPresence.Undetermined;
            }

            string directory;
            try
            {
                directory = _session.FindProfileDirectory(profile);
            }
            catch (Exception exception)
            {
                _logger.Error("[SaveSystem] Probing the directory of " + profile + " failed; presence is undetermined.", exception);
                return LocalPresence.Undetermined;
            }

            if (directory == null)
            {
                return LocalPresence.Absent;
            }

            bool undetermined = false;
            IReadOnlyList<SaveSlot> slots = _registry.Select(SlotScope.Profile);
            for (int i = 0; i < slots.Count; i++)
            {
                SaveSlot slot = slots[i];
                LocalPresence presence;
                try
                {
                    presence = _slotStore.PeekHeader(directory, slot.Key, slot.SupportedSchema).Presence;
                }
                catch (Exception exception)
                {
                    _logger.Error("[SaveSystem] Probing '" + slot.Key + "' in " + directory + " failed; presence is undetermined.", exception);
                    presence = LocalPresence.Undetermined;
                }

                if (presence == LocalPresence.Present)
                {
                    return LocalPresence.Present;
                }

                undetermined |= presence == LocalPresence.Undetermined;
            }

            return undetermined ? LocalPresence.Undetermined : LocalPresence.Absent;
        }

        public void AddRestoreListener(IRestoreListener listener)
        {
            if (HookScope.RefuseIfActive(_logger, nameof(AddRestoreListener)) || _disposed)
            {
                return;
            }

            if (listener == null)
            {
                throw new ArgumentNullException(nameof(listener));
            }

            _restoreListeners.Add(listener);
        }

        public void RemoveRestoreListener(IRestoreListener listener)
        {
            if (HookScope.RefuseIfActive(_logger, nameof(RemoveRestoreListener)) || _disposed)
            {
                return;
            }

            _restoreListeners.Remove(listener);
        }

        public void AddDeactivationListener(IProfileDeactivatingListener listener)
        {
            if (HookScope.RefuseIfActive(_logger, nameof(AddDeactivationListener)) || _disposed)
            {
                return;
            }

            if (listener == null)
            {
                throw new ArgumentNullException(nameof(listener));
            }

            _deactivationListeners.Add(listener);
        }

        public void RemoveDeactivationListener(IProfileDeactivatingListener listener)
        {
            if (HookScope.RefuseIfActive(_logger, nameof(RemoveDeactivationListener)) || _disposed)
            {
                return;
            }

            _deactivationListeners.Remove(listener);
        }

        private ProfileActivationResult CreateActivationRefusal(ProfileId profile, SaveErrorCode code, string message)
        {
            ProfileId? previous = !_disposed && _session.IsInitialized ? _session.ActiveProfile : (ProfileId?)null;
            return new ProfileActivationResult(SaveStatus.Failed, new SaveError(code, message), profile, previous, false, false, null, null, false);
        }

        private RestoreReport CreateRestoreRefusal(SaveErrorCode code, string message)
        {
            return new RestoreReport(
                RestoreTrigger.CloudRestore, SaveStatus.Failed, new SaveError(code, message), _session.ActiveProfile, RestoreCompleteness.Failed, null, null, false);
        }

        private static DeleteResult CreateDeleteRefusal(SaveErrorCode code, string message)
        {
            return new DeleteResult(SaveStatus.Failed, new SaveError(code, message), false, CloudDeleteStatus.NotRequested);
        }
    }
}
