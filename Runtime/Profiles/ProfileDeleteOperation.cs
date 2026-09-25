using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Ecanakli.SaveSystem
{
    /// <summary>
    /// DeleteSlotAsync (01 5.5 with 04 R1 tombstones) and DeleteProfileAsync (04a A3 5.5 with ProfileDeleteMode).
    /// Main thread only; runs under the OperationGate; dispatches no listeners. OperationCanceledException only before the gate is held.
    /// </summary>
    internal sealed class ProfileDeleteOperation
    {
        private readonly SaveContext _context;
        private readonly ProfileSession _session;

        public ProfileDeleteOperation(SaveContext context, ProfileSession session)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _session = session ?? throw new ArgumentNullException(nameof(session));
        }

        /// <summary>
        /// Deletes the slot's local files and resets memory to the normalized default. LocalAndCloud on a cloud-backed profile persists a
        /// tombstone {DeletedWriteId, DeletedProviderVersion} first, then deletes the cloud value conditionally; a failure keeps the tombstone.
        /// </summary>
        public async UniTask<DeleteResult> DeleteSlotAsync(SaveSlot slot, DeleteTarget target, CancellationToken ct)
        {
            if (slot == null)
            {
                throw new ArgumentNullException(nameof(slot));
            }

            DeleteResult refusal = CheckEntry("DeleteSlotAsync");
            if (refusal != null)
            {
                return refusal;
            }

            if (!_context.Registry.Contains(slot))
            {
                return Refuse(SaveErrorCode.SlotNotRegistered, "Slot '" + slot.Key + "' is not registered.");
            }

            bool cloudRequested = target == DeleteTarget.LocalAndCloud && slot.Scope == SlotScope.Profile && slot.SyncMode != SyncMode.LocalOnly;
            if (cloudRequested && slot.SyncMode == SyncMode.CloudReadOnly)
            {
                return Refuse(SaveErrorCode.SlotReadOnly, "Slot '" + slot.Key + "' is CloudReadOnly; its cloud value belongs to the server.");
            }

            ct.ThrowIfCancellationRequested();
            long epoch = _session.Epoch;
            CancellationToken epochToken = _session.EpochToken;
            OperationGate.Releaser releaser = default;
            bool gateHeld = false;
            try
            {
                using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, epochToken))
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
                    return IsDisposed()
                        ? Refuse(SaveErrorCode.Disposed, "The save service was disposed during the delete.")
                        : Refuse(SaveErrorCode.Superseded, "The active profile changed before the delete started.");
                }

                if (IsDisposed())
                {
                    return Refuse(SaveErrorCode.Disposed, "The save service was disposed during the delete.");
                }

                if (_session.Epoch != epoch)
                {
                    return Refuse(SaveErrorCode.Superseded, "The active profile changed before the delete started.");
                }

                return await DeleteSlotUnderGateAsync(slot, cloudRequested, epoch, epochToken);
            }
            finally
            {
                if (gateHeld)
                {
                    releaser.Dispose();
                }
            }
        }

        /// <summary>
        /// Deletes a non-active profile's directory and clears the last-active pointer; the cloud is untouched.
        /// RequireSynced refuses with UnsyncedChanges (with keys) when a CloudSync slot is unsynced or undetermined.
        /// </summary>
        public async UniTask<DeleteResult> DeleteProfileAsync(ProfileId profile, ProfileDeleteMode mode, CancellationToken ct)
        {
            DeleteResult refusal = CheckEntry("DeleteProfileAsync");
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
                    return Refuse(SaveErrorCode.Disposed, "The save service was disposed during the delete.");
                }

                if (IsDisposed())
                {
                    return Refuse(SaveErrorCode.Disposed, "The save service was disposed during the delete.");
                }

                if (profile == _session.ActiveProfile)
                {
                    return Refuse(SaveErrorCode.ProfileActive, profile + " is active; activate another profile first.");
                }

                return DeleteProfileUnderGate(profile, mode);
            }
            finally
            {
                if (gateHeld)
                {
                    releaser.Dispose();
                }
            }
        }

        private async UniTask<DeleteResult> DeleteSlotUnderGateAsync(SaveSlot slot, bool cloudRequested, long epoch, CancellationToken epochToken)
        {
            ProfileId profile = _session.ActiveProfile;
            string key = slot.Key;
            string directory = _session.GetSlotDirectory(slot);

            // Unsaved changes land first and in-flight writes are waited out; then no batch or debounce holds the slot
            FlushLocal();
            _context.Scheduler.Remove(slot);

            SlotFileOpResult deleted = _context.SlotStore.DeleteSlotFiles(directory, key);
            if (!deleted.IsSuccess)
            {
                _context.Logger.Error("[SaveSystem] " + deleted.Message, deleted.Exception);
                CloudError notAttempted = cloudRequested ? new CloudError(CloudErrorKind.Permanent, "Not attempted because the local delete failed.") : null;
                return new DeleteResult(
                    SaveStatus.Failed, SaveError.FromLocalWriteKind(deleted.ErrorKind, deleted.Message, deleted.Exception), false,
                    cloudRequested ? CloudDeleteStatus.Failed : CloudDeleteStatus.NotRequested, notAttempted);
            }

            ResetMemory(slot);
            bool synced = slot.Scope == SlotScope.Profile && slot.SyncMode != SyncMode.LocalOnly;
            if (!synced)
            {
                return Deleted(CloudDeleteStatus.NotRequested);
            }

            SlotSyncState sync = _session.GetSyncState(slot);
            string deletedWriteId = sync.PendingWriteId ?? sync.LastSyncedWriteId;
            string deletedVersion = sync.LastSyncedProviderVersion;
            if (deletedWriteId == null && sync.PendingDelete)
            {
                // Repeated delete keeps the earlier tombstone's target
                deletedWriteId = sync.DeletedWriteId;
                deletedVersion = sync.DeletedProviderVersion;
            }

            sync.ClearSyncHistory();
            if (!cloudRequested || !profile.IsCloudBacked)
            {
                // Cloud copy stays; uploads wait for the next restore so the default never overwrites it
                _session.MarkNeedsReconcile(slot);
                PersistOrWarn("slot delete");
                return Deleted(CloudDeleteStatus.NotRequested);
            }

            sync.SetTombstone(deletedWriteId, deletedVersion);

            // Local default counts as synced with the deleted cloud value, so no empty upload follows
            sync.LastSyncedRevision = slot.Revision;
            StateWriteResult persisted = _session.PersistSyncState();
            if (!persisted.IsSuccess)
            {
                return new DeleteResult(
                    SaveStatus.Failed, persisted.ToSaveError(), true, CloudDeleteStatus.Failed,
                    new CloudError(CloudErrorKind.Permanent, "The tombstone could not be persisted; the cloud delete was not attempted."));
            }

            string signedIn = ReadSignedInAccountId();
            if (string.IsNullOrEmpty(signedIn))
            {
                return CloudFailed(SaveErrorCode.NotSignedIn, new CloudError(CloudErrorKind.NotSignedIn, "The cloud provider is not signed in; the tombstone is kept."));
            }

            if (!string.Equals(signedIn, profile.AccountId, StringComparison.Ordinal))
            {
                return CloudFailed(
                    SaveErrorCode.AccountMismatch, new CloudError(CloudErrorKind.Permanent, "The signed-in account does not match " + profile + "; the tombstone is kept."));
            }

            CloudDeleteResult result;
            try
            {
                result = await _context.CloudGateway.DeleteAsync(key, deletedVersion, epochToken);
            }
            catch (OperationCanceledException)
            {
                return CloudFailed(SaveErrorCode.Superseded, new CloudError(CloudErrorKind.Transient, "The cloud delete was aborted because the profile changed."));
            }
            catch (Exception exception)
            {
                _context.Logger.Error("[SaveSystem] Cloud delete of '" + key + "' threw.", exception);
                return CloudFailed(SaveErrorCode.CloudError, new CloudError(CloudErrorKind.Permanent, "Cloud delete threw: " + exception.Message, null, exception));
            }

            if (!result.IsSuccess && !result.IsNotFound)
            {
                CloudError error = result.Error ?? new CloudError(CloudErrorKind.Permanent, "The cloud delete failed.");
                _context.Logger.Warning("[SaveSystem] Cloud delete of '" + key + "' failed (" + error + "); the tombstone is kept for the next restore.");
                return CloudFailed(SaveErrorCode.CloudError, error);
            }

            if (!IsDisposed() && _session.Epoch == epoch)
            {
                _session.GetSyncState(slot).ClearTombstone();
                PersistOrWarn("tombstone clear");
            }

            return Deleted(result.IsSuccess ? CloudDeleteStatus.Deleted : CloudDeleteStatus.AlreadyAbsent);
        }

        private DeleteResult DeleteProfileUnderGate(ProfileId profile, ProfileDeleteMode mode)
        {
            string directory;
            try
            {
                directory = _session.FindProfileDirectory(profile);
            }
            catch (Exception exception)
            {
                _context.Logger.Error("[SaveSystem] Locating the directory of " + profile + " failed.", exception);
                return new DeleteResult(SaveStatus.Failed, SaveError.FromLocalWriteKind(StorageErrorClassifier.Classify(exception), exception.Message, exception), false,
                    CloudDeleteStatus.NotRequested);
            }

            if (directory != null)
            {
                if (mode == ProfileDeleteMode.RequireSynced)
                {
                    List<string> unsynced = FindUnsyncedSlotKeys(profile, directory);
                    if (unsynced.Count > 0)
                    {
                        return new DeleteResult(
                            SaveStatus.Failed, new SaveError(SaveErrorCode.UnsyncedChanges, profile + " has unsynced data in " + unsynced.Count + " slot(s)."), false,
                            CloudDeleteStatus.NotRequested, null, unsynced);
                    }
                }

                // Slot deletes drop in-memory too-new knowledge for these paths
                IReadOnlyList<SaveSlot> slots = _context.Registry.Select(SlotScope.Profile);
                for (int i = 0; i < slots.Count; i++)
                {
                    _context.SlotStore.DeleteSlotFiles(directory, slots[i].Key);
                }

                try
                {
                    _context.Storage.DeleteDirectory(directory);
                }
                catch (Exception exception)
                {
                    _context.Logger.Error("[SaveSystem] Deleting " + directory + " failed.", exception);
                    return new DeleteResult(SaveStatus.Failed, SaveError.FromLocalWriteKind(StorageErrorClassifier.Classify(exception), exception.Message, exception),
                        false, CloudDeleteStatus.NotRequested);
                }
            }

            if (!_session.ClearLastActiveProfileIf(profile))
            {
                _context.Logger.Warning("[SaveSystem] The last-active pointer to " + profile + " could not be cleared; init falls back when the directory is missing.");
            }

            _context.Logger.Info("[SaveSystem] Profile " + profile + " was deleted locally.");
            return Deleted(CloudDeleteStatus.NotRequested);
        }

        // N2: rev > LastSyncedRevision, a PendingWriteId or undetermined presence; Guest and Local refuse any present CloudSync file
        private List<string> FindUnsyncedSlotKeys(ProfileId profile, string directory)
        {
            var unsynced = new List<string>();
            ProfileSyncState state = _context.SyncStateStore.Load(directory, SlotReadMode.ReadOnly).State;
            IReadOnlyList<SaveSlot> slots = _context.Registry.Select(SlotScope.Profile, SyncMode.CloudSync);
            for (int i = 0; i < slots.Count; i++)
            {
                SaveSlot slot = slots[i];
                LocalPresence presence;
                long revision;
                try
                {
                    SlotHeaderPeekResult peek = _context.SlotStore.PeekHeader(directory, slot.Key, slot.SupportedSchema);
                    presence = peek.Presence;
                    revision = peek.Revision;
                }
                catch (Exception exception)
                {
                    _context.Logger.Error("[SaveSystem] Peeking '" + slot.Key + "' in " + directory + " failed; presence is undetermined.", exception);
                    presence = LocalPresence.Undetermined;
                    revision = 0;
                }

                SlotSyncState sync = state.PeekSlot(slot.Key);
                bool isUnsynced;
                if (presence == LocalPresence.Undetermined)
                {
                    isUnsynced = true;
                }
                else if (!profile.IsCloudBacked)
                {
                    isUnsynced = presence == LocalPresence.Present;
                }
                else
                {
                    isUnsynced = sync.PendingWriteId != null || (presence == LocalPresence.Present && revision > sync.LastSyncedRevision);
                }

                if (isUnsynced)
                {
                    unsynced.Add(slot.Key);
                }
            }

            return unsynced;
        }

        // Normalized default in memory; the revision stays monotonic so a stale in-flight snapshot is older
        private void ResetMemory(SaveSlot slot)
        {
            SlotMaterializeResult created = slot.CreateNormalizedDefault();
            if (created.IsSuccess)
            {
                slot.ReplaceData(created.Data);
                slot.MarkReady();
            }
            else
            {
                _context.Logger.Error("[SaveSystem] Default of slot '" + slot.Key + "' failed after delete (" + created.FailedStage + ").", created.Exception);
                slot.ResetToDefault();
                slot.MarkFailed(SlotFailure.NormalizeFailed);
            }

            slot.SetRevision(slot.Revision + 1);
            if (slot.State == SlotState.Ready)
            {
                _context.MutationDetector.CaptureBaseline(slot);
            }
        }

        private void FlushLocal()
        {
            try
            {
                LocalFlushResult flushed = _context.FlushLocalNow();
                if (flushed != null && !flushed.IsComplete)
                {
                    _context.Logger.Warning("[SaveSystem] Local flush before the slot delete was incomplete: " + flushed + ".");
                }
            }
            catch (Exception exception)
            {
                _context.Logger.Error("[SaveSystem] Local flush before the slot delete threw.", exception);
            }
        }

        private void PersistOrWarn(string reason)
        {
            StateWriteResult persisted = _session.PersistSyncState();
            if (!persisted.IsSuccess)
            {
                _context.Logger.Warning("[SaveSystem] profile.json after " + reason + " is recorded in memory only: " + persisted.Message);
            }
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

        private DeleteResult CheckEntry(string operation)
        {
            if (IsDisposed())
            {
                return Refuse(SaveErrorCode.Disposed, operation + " was called after Dispose.");
            }

            if (HookScope.RefuseIfActive(_context.Logger, operation))
            {
                return Refuse(SaveErrorCode.CalledFromHook, operation + " was called from a slot hook.");
            }

            if (!_session.IsInitialized)
            {
                return Refuse(SaveErrorCode.NotInitialized, operation + " requires InitializeAsync first.");
            }

            return null;
        }

        private bool IsDisposed()
        {
            return _session.IsDisposed || _context.LifetimeToken.IsCancellationRequested;
        }

        private static DeleteResult Refuse(SaveErrorCode code, string message)
        {
            return new DeleteResult(SaveStatus.Failed, new SaveError(code, message), false, CloudDeleteStatus.NotRequested);
        }

        private static DeleteResult Deleted(CloudDeleteStatus cloud)
        {
            return new DeleteResult(SaveStatus.Success, null, true, cloud);
        }

        // Local data is gone; the cloud side failed and the tombstone is kept
        private static DeleteResult CloudFailed(SaveErrorCode code, CloudError error)
        {
            return new DeleteResult(SaveStatus.Failed, new SaveError(code, error.Message, error.Exception, error), true, CloudDeleteStatus.Failed, error);
        }
    }
}
