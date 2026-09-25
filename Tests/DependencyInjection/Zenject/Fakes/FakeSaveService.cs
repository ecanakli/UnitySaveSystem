using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Ecanakli.SaveSystem.DependencyInjection.Tests
{
    /// <summary>
    /// Minimal ISaveService used only to unit-test SaveSignalBridge wiring in isolation from real save/restore logic
    /// (already covered by the core test suite). Every Raise* method is a direct, synchronous event invocation.
    /// </summary>
    internal sealed class FakeSaveService : ISaveService
    {
        public int FlushAsyncCallCount { get; private set; }

        public CancellationToken LastFlushToken { get; private set; }

        public bool IsInitialized => true;

        public bool IsReady => true;

        public ProfileId ActiveProfile => ProfileId.Guest;

        public event Action<ProfileActivationResult> ProfileActivated;

        public event Action<RestoreReport> RestoreCompleted;

        public event Action<SlotLoadIssue> SlotLoadIssueDetected;

        public event Action<UploadFailure> UploadFailed;

        public event Action<LocalWriteHealth> LocalWriteHealthChanged;

        public event Action<UpdateRequiredInfo> UpdateRequired;

        public void RaiseProfileActivated(ProfileActivationResult result) => ProfileActivated?.Invoke(result);

        public void RaiseRestoreCompleted(RestoreReport report) => RestoreCompleted?.Invoke(report);

        public void RaiseSlotLoadIssueDetected(SlotLoadIssue issue) => SlotLoadIssueDetected?.Invoke(issue);

        public void RaiseUploadFailed(UploadFailure failure) => UploadFailed?.Invoke(failure);

        public void RaiseLocalWriteHealthChanged(LocalWriteHealth health) => LocalWriteHealthChanged?.Invoke(health);

        public void RaiseUpdateRequired(UpdateRequiredInfo info) => UpdateRequired?.Invoke(info);

        public UniTask<bool> WhenReadyAsync(CancellationToken ct) => UniTask.FromResult(true);

        public UniTask<InitializeResult> InitializeAsync(CancellationToken ct) => throw new NotSupportedException("Not used by signal bridge tests.");

        public UniTask<ProfileActivationResult> ActivateProfileAsync(ProfileId profile, CancellationToken ct) =>
            throw new NotSupportedException("Not used by signal bridge tests.");

        public IReadOnlyList<ProfileId> GetLocalProfiles() => Array.Empty<ProfileId>();

        public UniTask<RestoreReport> RestoreAsync(CancellationToken ct) => throw new NotSupportedException("Not used by signal bridge tests.");

        public UniTask<FlushResult> FlushAsync(CancellationToken ct)
        {
            FlushAsyncCallCount++;
            LastFlushToken = ct;
            return UniTask.FromResult(new FlushResult(SaveStatus.Success, null, LocalFlushResult.Complete, Array.Empty<CloudFlushSlotResult>()));
        }

        public LocalFlushResult FlushLocalNow() => LocalFlushResult.Complete;

        public UniTask<DeleteResult> DeleteSlotAsync(SaveSlot slot, DeleteTarget target, CancellationToken ct) =>
            throw new NotSupportedException("Not used by signal bridge tests.");

        public UniTask<DeleteResult> DeleteProfileAsync(ProfileId profile, ProfileDeleteMode mode, CancellationToken ct) =>
            throw new NotSupportedException("Not used by signal bridge tests.");

        public UniTask<AccountDataDeleteResult> DeleteAccountDataAsync(string accountId, CancellationToken ct) =>
            throw new NotSupportedException("Not used by signal bridge tests.");

        public LocalPresence ProbeLocalPresence(ProfileId profile) => LocalPresence.Absent;

        public void AddRestoreListener(IRestoreListener listener)
        {
        }

        public void RemoveRestoreListener(IRestoreListener listener)
        {
        }

        public void AddDeactivationListener(IProfileDeactivatingListener listener)
        {
        }

        public void RemoveDeactivationListener(IProfileDeactivatingListener listener)
        {
        }

        public void Dispose()
        {
        }
    }
}
