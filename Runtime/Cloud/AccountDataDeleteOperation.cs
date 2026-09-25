using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Ecanakli.SaveSystem
{
    /// <summary>
    /// DeleteAccountDataAsync (04a item 9, A3 5.5 with 04 R1 tombstones).
    /// Main thread only; runs under the OperationGate; dispatches no listeners. OperationCanceledException only before the gate is held.
    /// </summary>
    internal sealed class AccountDataDeleteOperation
    {
        private const string OperationName = "DeleteAccountDataAsync";

        private readonly SaveContext _context;
        private readonly ProfileSession _session;

        public AccountDataDeleteOperation(SaveContext context, ProfileSession session)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _session = session ?? throw new ArgumentNullException(nameof(session));
        }

        /// <summary>
        /// Tombstones every registered Profile CloudSync slot of a non-active account, deletes each cloud key unconditionally,
        /// then deletes the local directory only when every key is Deleted or AlreadyAbsent. Incomplete runs keep everything for a retry.
        /// The signed-in account is re-checked before every delete attempt, gateway retries included; a mismatch aborts the remaining keys with AccountMismatch.
        /// </summary>
        public async UniTask<AccountDataDeleteResult> DeleteAccountDataAsync(string accountId, CancellationToken ct)
        {
            // Throws ArgumentException on a null or empty id
            ProfileId profile = ProfileId.Account(accountId);

            AccountDataDeleteResult refusal = CheckEntry(accountId);
            if (refusal != null)
            {
                return refusal;
            }

            ct.ThrowIfCancellationRequested();
            OperationGate.Releaser releaser = default;
            bool gateHeld = false;
            try
            {
                using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _context.LifetimeToken))
                {
                    try
                    {
                        releaser = await _context.Gate.AcquireAsync(linked.Token);
                        gateHeld = true;
                    }
                    catch (OperationCanceledException) when (linked.IsCancellationRequested)
                    {
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }

                if (!gateHeld)
                {
                    ct.ThrowIfCancellationRequested();
                    return Refuse(accountId, SaveErrorCode.Disposed, "The save service was disposed during the account data delete.");
                }

                if (IsDisposed())
                {
                    return Refuse(accountId, SaveErrorCode.Disposed, "The save service was disposed during the account data delete.");
                }

                return await DeleteUnderGateAsync(profile, ct);
            }
            finally
            {
                if (gateHeld)
                {
                    releaser.Dispose();
                }
            }
        }

        private async UniTask<AccountDataDeleteResult> DeleteUnderGateAsync(ProfileId profile, CancellationToken ct)
        {
            string accountId = profile.AccountId;
            if (profile == _session.ActiveProfile)
            {
                return Refuse(accountId, SaveErrorCode.ProfileActive, profile + " is active; activate another profile first.");
            }

            string signedIn = ReadSignedInAccountId();
            if (string.IsNullOrEmpty(signedIn))
            {
                return Refuse(accountId, SaveErrorCode.NotSignedIn, "The cloud provider is not signed in.");
            }

            if (!string.Equals(signedIn, accountId, StringComparison.Ordinal))
            {
                return Refuse(accountId, SaveErrorCode.AccountMismatch, "The signed-in account does not match " + profile + ".");
            }

            string directory;
            try
            {
                directory = _session.FindProfileDirectory(profile);
            }
            catch (Exception exception)
            {
                _context.Logger.Error("[SaveSystem] Locating the directory of " + profile + " failed.", exception);
                return Failed(accountId, SaveError.FromLocalWriteKind(StorageErrorClassifier.Classify(exception), exception.Message, exception), null);
            }

            IReadOnlyList<SaveSlot> slots = _context.Registry.Select(SlotScope.Profile, SyncMode.CloudSync);
            if (directory != null)
            {
                AccountDataDeleteResult tombstoneFailure = PersistTombstones(accountId, directory, slots);
                if (tombstoneFailure != null)
                {
                    return tombstoneFailure;
                }
            }

            var cloud = new List<CloudDeleteSlotResult>(slots.Count);
            CloudDeletePhase phase = await DeleteCloudKeysAsync(accountId, slots, cloud, ct);
            if (IsDisposed())
            {
                return Failed(accountId, new SaveError(SaveErrorCode.Disposed, "The save service was disposed during the account data delete."), cloud);
            }

            if (phase.AccountChanged)
            {
                _context.Logger.Warning(
                    "[SaveSystem] Account data delete of " + profile + " stopped because the signed-in account changed; local data and tombstones are kept.");
                return Failed(
                    accountId,
                    new SaveError(
                        SaveErrorCode.AccountMismatch,
                        "The signed-in account changed during the account data delete of " + profile + "; the remaining keys were not deleted."),
                    cloud);
            }

            CloudError firstError = phase.FirstError;
            if (firstError != null)
            {
                _context.Logger.Warning("[SaveSystem] Account data delete of " + profile + " is incomplete (" + firstError + "); local data and tombstones are kept.");
                return Failed(accountId, new SaveError(SaveErrorCode.CloudError, firstError.Message, firstError.Exception, firstError), cloud);
            }

            if (directory != null)
            {
                // Slot deletes drop in-memory too-new knowledge for these paths
                IReadOnlyList<SaveSlot> profileSlots = _context.Registry.Select(SlotScope.Profile);
                for (int i = 0; i < profileSlots.Count; i++)
                {
                    _context.SlotStore.DeleteSlotFiles(directory, profileSlots[i].Key);
                }

                try
                {
                    _context.Storage.DeleteDirectory(directory);
                }
                catch (Exception exception)
                {
                    _context.Logger.Error("[SaveSystem] Deleting " + directory + " failed after the cloud delete.", exception);
                    return Failed(accountId, SaveError.FromLocalWriteKind(StorageErrorClassifier.Classify(exception), exception.Message, exception), cloud);
                }
            }

            if (!_session.ClearLastActiveProfileIf(profile))
            {
                _context.Logger.Warning("[SaveSystem] The last-active pointer to " + profile + " could not be cleared; init falls back when the directory is missing.");
            }

            _context.Logger.Info("[SaveSystem] Account data of " + profile + " was deleted locally and in the cloud.");
            return new AccountDataDeleteResult(SaveStatus.Success, null, accountId, cloud, true);
        }

        // Null on success; an unreadable profile.json is never overwritten
        private AccountDataDeleteResult PersistTombstones(string accountId, string directory, IReadOnlyList<SaveSlot> slots)
        {
            if (slots.Count == 0)
            {
                return null;
            }

            SyncStateLoadResult loaded = _context.SyncStateStore.Load(directory);
            if (!loaded.CanOverwrite)
            {
                return Failed(accountId, SaveError.FromLocalWriteKind(loaded.ErrorKind, loaded.Message, loaded.Exception), null);
            }

            ProfileSyncState state = loaded.State;
            if (state.OwnerAccountId == null)
            {
                state.OwnerAccountId = accountId;
            }

            for (int i = 0; i < slots.Count; i++)
            {
                SlotSyncState sync = state.GetOrCreateSlot(slots[i].Key);

                // An unconfirmed write may be the cloud value and is newer than the last confirmed one, so row 5 must target it
                string deletedWriteId = sync.PendingWriteId ?? sync.LastSyncedWriteId;

                // The version of an unconfirmed write is unknown; never pair it with the confirmed one
                string deletedVersion = sync.PendingWriteId != null ? null : sync.LastSyncedProviderVersion;
                if (deletedWriteId == null && sync.PendingDelete)
                {
                    // Retry keeps the earlier tombstone's target
                    deletedWriteId = sync.DeletedWriteId;
                    deletedVersion = sync.DeletedProviderVersion;
                }

                sync.SetTombstone(deletedWriteId, deletedVersion);
            }

            StateWriteResult persisted = _context.SyncStateStore.Save(directory, state);
            if (persisted.IsSuccess)
            {
                return null;
            }

            _context.Logger.Error("[SaveSystem] Tombstones for account data delete could not be persisted in " + directory + "; no cloud delete was attempted.", persisted.Exception);
            return Failed(accountId, persisted.ToSaveError(), null);
        }

        // Fills one entry per slot in order; the deletes are unconditional, so every key is checked against the live session first
        private async UniTask<CloudDeletePhase> DeleteCloudKeysAsync(
            string accountId, IReadOnlyList<SaveSlot> slots, List<CloudDeleteSlotResult> cloud, CancellationToken ct)
        {
            CloudError firstError = null;
            bool accountChanged = false;
            var guard = new SignedInAccountGuard(this, accountId);
            using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _context.LifetimeToken))
            {
                for (int i = 0; i < slots.Count; i++)
                {
                    string key = slots[i].Key;
                    CloudDeleteSlotResult entry;
                    if (linked.IsCancellationRequested)
                    {
                        entry = new CloudDeleteSlotResult(key, CloudDeleteStatus.Failed, AbortedError());
                    }
                    else if (accountChanged || !IsStillSignedInAs(accountId))
                    {
                        // The provider resolves the delete against whoever is signed in at call time
                        accountChanged = true;
                        entry = new CloudDeleteSlotResult(key, CloudDeleteStatus.Failed, AccountChangedError());
                    }
                    else
                    {
                        entry = await DeleteKeyAsync(key, guard, linked.Token);

                        // The guard also fires inside the gateway's retry loop
                        accountChanged |= guard.AccountChanged;
                    }

                    cloud.Add(entry);
                    if (firstError == null && entry.Status == CloudDeleteStatus.Failed)
                    {
                        firstError = entry.Error;
                    }
                }
            }

            return new CloudDeletePhase(firstError, accountChanged);
        }

        // Re-read before every delete: the session can change across the awaits of the loop
        private bool IsStillSignedInAs(string accountId)
        {
            return string.Equals(ReadSignedInAccountId(), accountId, StringComparison.Ordinal);
        }

        private async UniTask<CloudDeleteSlotResult> DeleteKeyAsync(string key, ICloudCallGuard guard, CancellationToken token)
        {
            CloudDeleteResult result;
            try
            {
                result = await _context.CloudGateway.DeleteAsync(key, null, guard, token);
            }
            catch (OperationCanceledException)
            {
                return new CloudDeleteSlotResult(key, CloudDeleteStatus.Failed, AbortedError());
            }
            catch (Exception exception)
            {
                _context.Logger.Error("[SaveSystem] Cloud delete of '" + key + "' threw.", exception);
                return new CloudDeleteSlotResult(
                    key, CloudDeleteStatus.Failed, new CloudError(CloudErrorKind.Permanent, "Cloud delete threw: " + exception.Message, null, exception));
            }

            if (result.IsSuccess)
            {
                return new CloudDeleteSlotResult(key, CloudDeleteStatus.Deleted);
            }

            if (result.IsNotFound)
            {
                return new CloudDeleteSlotResult(key, CloudDeleteStatus.AlreadyAbsent);
            }

            return new CloudDeleteSlotResult(key, CloudDeleteStatus.Failed, result.Error ?? new CloudError(CloudErrorKind.Permanent, "The cloud delete failed."));
        }

        private string ReadSignedInAccountId()
        {
            try
            {
                return _context.CloudGateway.SignedInAccountId;
            }
            catch (Exception exception)
            {
                _context.Logger.Error("[SaveSystem] ICloudSaveProvider.SignedInAccountId threw; treated as signed out.", exception);
                return null;
            }
        }

        private AccountDataDeleteResult CheckEntry(string accountId)
        {
            if (IsDisposed())
            {
                return Refuse(accountId, SaveErrorCode.Disposed, OperationName + " was called after Dispose.");
            }

            if (HookScope.RefuseIfActive(_context.Logger, OperationName))
            {
                return Refuse(accountId, SaveErrorCode.CalledFromHook, OperationName + " was called from a slot hook.");
            }

            if (!_session.IsInitialized)
            {
                return Refuse(accountId, SaveErrorCode.NotInitialized, OperationName + " requires InitializeAsync first.");
            }

            return null;
        }

        private bool IsDisposed()
        {
            return _session.IsDisposed || _context.LifetimeToken.IsCancellationRequested;
        }

        private static CloudError AbortedError()
        {
            return new CloudError(CloudErrorKind.Transient, "The cloud delete was aborted; call again to retry.");
        }

        private static CloudError AccountChangedError()
        {
            return new CloudError(CloudErrorKind.Transient, "The cloud delete was aborted because the signed-in account changed; sign in again and retry.");
        }

        private static AccountDataDeleteResult Refuse(string accountId, SaveErrorCode code, string message)
        {
            return new AccountDataDeleteResult(SaveStatus.Failed, new SaveError(code, message), accountId, null, false);
        }

        private static AccountDataDeleteResult Failed(string accountId, SaveError error, IEnumerable<CloudDeleteSlotResult> cloud)
        {
            return new AccountDataDeleteResult(SaveStatus.Failed, error, accountId, cloud, false);
        }

        /// <summary>Stops a gateway attempt once the signed-in account stops matching; keeps account concepts out of CloudGateway.</summary>
        private sealed class SignedInAccountGuard : ICloudCallGuard
        {
            private readonly AccountDataDeleteOperation _operation;
            private readonly string _accountId;

            public SignedInAccountGuard(AccountDataDeleteOperation operation, string accountId)
            {
                _operation = operation;
                _accountId = accountId;
            }

            /// <summary>True once an attempt was refused; stays true for the rest of the run.</summary>
            public bool AccountChanged { get; private set; }

            public CloudError CheckBeforeAttempt()
            {
                if (!AccountChanged && _operation.IsStillSignedInAs(_accountId))
                {
                    return null;
                }

                AccountChanged = true;
                return AccountChangedError();
            }
        }

        /// <summary>Outcome of the cloud delete loop.</summary>
        private readonly struct CloudDeletePhase
        {
            public CloudDeletePhase(CloudError firstError, bool accountChanged)
            {
                FirstError = firstError;
                AccountChanged = accountChanged;
            }

            /// <summary>Error of the first failed entry; null when every key was deleted or already absent.</summary>
            public CloudError FirstError { get; }

            /// <summary>The signed-in account stopped matching, so the remaining keys were left alone.</summary>
            public bool AccountChanged { get; }
        }
    }
}
