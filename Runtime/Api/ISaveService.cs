using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Ecanakli.SaveSystem
{
    /// <summary>
    /// Save service facade. Main thread only. Every member called from inside a slot hook is refused (CalledFromHook).
    /// OperationCanceledException surfaces only when the caller's own token is cancelled before the operation starts its work.
    /// </summary>
    public interface ISaveService : IDisposable
    {
        /// <summary>True after a successful InitializeAsync and before Dispose.</summary>
        bool IsInitialized { get; }

        /// <summary>Initialized, no activation in progress, every slot Ready or Failed.</summary>
        bool IsReady { get; }

        /// <summary>Active profile; Guest before initialization.</summary>
        ProfileId ActiveProfile { get; }

        /// <summary>After slots load and restore listeners finish, including the initial profile at InitializeAsync.</summary>
        event Action<ProfileActivationResult> ProfileActivated;

        /// <summary>End of every RestoreAsync and every single-slot conflict reconcile.</summary>
        event Action<RestoreReport> RestoreCompleted;

        /// <summary>A local or cloud payload problem found during a load.</summary>
        event Action<SlotLoadIssue> SlotLoadIssueDetected;

        /// <summary>Permanent upload failure or refusal; never a transient retry.</summary>
        event Action<UploadFailure> UploadFailed;

        /// <summary>Local write health transition (healthy to failing, failing to healthy, or kind change).</summary>
        event Action<LocalWriteHealth> LocalWriteHealthChanged;

        /// <summary>A local or cloud payload was written by a newer build; raised once per slot, source and epoch.</summary>
        event Action<UpdateRequiredInfo> UpdateRequired;

        /// <summary>True when ready, false after Dispose; caller cancellation throws OperationCanceledException.</summary>
        UniTask<bool> WhenReadyAsync(CancellationToken ct);

        /// <summary>Loads device state, Device slots and the last active profile; dispatches restore listeners (ProfileActivated).</summary>
        UniTask<InitializeResult> InitializeAsync(CancellationToken ct);

        /// <summary>Switches the active profile (Guest, Account or Local); an Account activation may claim guest data.</summary>
        UniTask<ProfileActivationResult> ActivateProfileAsync(ProfileId profile, CancellationToken ct);

        /// <summary>Local profiles on disk. Query only; never persists.</summary>
        IReadOnlyList<ProfileId> GetLocalProfiles();

        /// <summary>Reconciles the active account's cloud-backed Profile slots with the cloud; joins a restore in flight.</summary>
        UniTask<RestoreReport> RestoreAsync(CancellationToken ct);

        /// <summary>Strict flush: local first, then every Profile CloudSync slot uploaded or confirmed in sync.</summary>
        UniTask<FlushResult> FlushAsync(CancellationToken ct);

        /// <summary>Synchronous local flush of every dirty slot, ignoring write backoff. Pause and quit path.</summary>
        LocalFlushResult FlushLocalNow();

        /// <summary>Deletes a slot's local files (and its cloud value for LocalAndCloud) and resets memory to defaults.</summary>
        UniTask<DeleteResult> DeleteSlotAsync(SaveSlot slot, DeleteTarget target, CancellationToken ct);

        /// <summary>Deletes a non-active profile's local directory; the cloud is untouched.</summary>
        UniTask<DeleteResult> DeleteProfileAsync(ProfileId profile, ProfileDeleteMode mode, CancellationToken ct);

        /// <summary>Deletes a non-active signed-in account's cloud values, then its local directory when every delete succeeded.</summary>
        UniTask<AccountDataDeleteResult> DeleteAccountDataAsync(string accountId, CancellationToken ct);

        /// <summary>Whether any registered Profile slot has a local file for the profile. Query only; never writes or repairs.</summary>
        LocalPresence ProbeLocalPresence(ProfileId profile);

        /// <summary>Duplicate ignored; no-op after Dispose. A listener added after InitializeAsync gets no catch-up call.</summary>
        void AddRestoreListener(IRestoreListener listener);

        /// <summary>No-op when not registered or after Dispose.</summary>
        void RemoveRestoreListener(IRestoreListener listener);

        /// <summary>Duplicate ignored; no-op after Dispose.</summary>
        void AddDeactivationListener(IProfileDeactivatingListener listener);

        /// <summary>No-op when not registered or after Dispose.</summary>
        void RemoveDeactivationListener(IProfileDeactivatingListener listener);
    }
}
