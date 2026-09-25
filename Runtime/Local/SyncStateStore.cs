using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Ecanakli.SaveSystem
{
    /// <summary>Sync metadata of one slot inside profile.json. Main thread only.</summary>
    internal sealed class SlotSyncState
    {
        /// <summary>Unconfirmed write ids kept per slot; two uploads in a row can both be unconfirmed.</summary>
        internal const int MaxPendingWriteIds = 3;

        internal const string HadContentProperty = "hadContent";
        internal const string LastSyncedRevisionProperty = "lastSyncedRevision";
        internal const string LastSyncedWriteIdProperty = "lastSyncedWriteId";
        internal const string LastSyncedProviderVersionProperty = "lastSyncedProviderVersion";
        internal const string PendingWriteIdProperty = "pendingWriteId";
        internal const string PendingWriteIdsProperty = "pendingWriteIds";
        internal const string TombstoneProperty = "tombstone";
        internal const string PendingDeleteProperty = "pendingDelete";
        internal const string DeletedWriteIdProperty = "deletedWriteId";
        internal const string DeletedProviderVersionProperty = "deletedProviderVersion";
        internal const string NeedsCloudRecoveryProperty = "needsCloudRecovery";
        internal const string LastUploadedBytesProperty = "lastUploadedBytes";

        private static readonly string[] NoPendingWriteIds = new string[0];

        // Newest first, capped at MaxPendingWriteIds; null while nothing is unconfirmed
        private List<string> _pendingWriteIds;

        /// <summary>Set only after a local write of non-empty data landed; blocks empty uploads over content.</summary>
        public bool HadContent { get; set; }

        public long LastSyncedRevision { get; set; }

        public string LastSyncedWriteId { get; set; }

        public string LastSyncedProviderVersion { get; set; }

        /// <summary>
        /// Newest unconfirmed write id, persisted before the provider call; recognizes a write whose response was lost.
        /// Assigning pushes onto PendingWriteIds; assigning null clears every unconfirmed id.
        /// </summary>
        public string PendingWriteId
        {
            get => _pendingWriteIds != null && _pendingWriteIds.Count > 0 ? _pendingWriteIds[0] : null;

            set
            {
                if (value == null)
                {
                    ClearPendingWriteIds();
                    return;
                }

                PushPendingWriteId(value);
            }
        }

        /// <summary>Every unconfirmed write id, newest first; a reconcile matches any of them.</summary>
        public IReadOnlyList<string> PendingWriteIds => (IReadOnlyList<string>)_pendingWriteIds ?? NoPendingWriteIds;

        /// <summary>Cloud delete requested but not confirmed.</summary>
        public bool PendingDelete { get; private set; }

        /// <summary>Write id the tombstone deletes; replay only when the cloud still holds it.</summary>
        public string DeletedWriteId { get; private set; }

        public string DeletedProviderVersion { get; private set; }

        /// <summary>Local copy was lost to corruption; the next restore takes cloud.</summary>
        public bool NeedsCloudRecovery { get; set; }

        public long LastUploadedBytes { get; set; }

        /// <summary>True when writeId is still unconfirmed.</summary>
        public bool IsPendingWriteId(string writeId)
        {
            return writeId != null && _pendingWriteIds != null && _pendingWriteIds.Contains(writeId);
        }

        /// <summary>Confirmed write: it and every older unconfirmed id are settled. True when something was removed.</summary>
        public bool ConfirmPendingWriteId(string writeId)
        {
            if (writeId == null || _pendingWriteIds == null)
            {
                return false;
            }

            int index = _pendingWriteIds.IndexOf(writeId);
            if (index < 0)
            {
                return false;
            }

            _pendingWriteIds.RemoveRange(index, _pendingWriteIds.Count - index);
            if (_pendingWriteIds.Count == 0)
            {
                _pendingWriteIds = null;
            }

            return true;
        }

        /// <summary>The provider refused this write, so it can never appear in the cloud. True when it was removed.</summary>
        public bool DropPendingWriteId(string writeId)
        {
            if (writeId == null || _pendingWriteIds == null || !_pendingWriteIds.Remove(writeId))
            {
                return false;
            }

            if (_pendingWriteIds.Count == 0)
            {
                _pendingWriteIds = null;
            }

            return true;
        }

        public void ClearPendingWriteIds()
        {
            _pendingWriteIds = null;
        }

        /// <summary>Replaces the unconfirmed ids (newest first); used to roll back a failed persist and when loading.</summary>
        public void SetPendingWriteIds(IReadOnlyList<string> writeIds)
        {
            _pendingWriteIds = null;
            if (writeIds == null)
            {
                return;
            }

            for (int i = writeIds.Count - 1; i >= 0; i--)
            {
                if (!string.IsNullOrEmpty(writeIds[i]))
                {
                    PushPendingWriteId(writeIds[i]);
                }
            }
        }

        public void SetTombstone(string deletedWriteId, string deletedProviderVersion)
        {
            PendingDelete = true;
            DeletedWriteId = deletedWriteId;
            DeletedProviderVersion = deletedProviderVersion;
        }

        public void ClearTombstone()
        {
            PendingDelete = false;
            DeletedWriteId = null;
            DeletedProviderVersion = null;
        }

        /// <summary>Clears content and sync history after a slot delete; the tombstone is kept.</summary>
        public void ClearSyncHistory()
        {
            HadContent = false;
            LastSyncedRevision = 0;
            LastSyncedWriteId = null;
            LastSyncedProviderVersion = null;
            PendingWriteId = null;
            NeedsCloudRecovery = false;
            LastUploadedBytes = 0;
        }

        /// <summary>Unknown provenance: HadContent true, LastSyncedWriteId null (worst case is a conflict, never data loss).</summary>
        public void ApplySafeFallback()
        {
            HadContent = true;
            LastSyncedWriteId = null;
        }

        public SlotSyncState Clone()
        {
            var clone = (SlotSyncState)MemberwiseClone();
            clone._pendingWriteIds = _pendingWriteIds == null ? null : new List<string>(_pendingWriteIds);
            return clone;
        }

        // Newest first; a repeated id moves to the front and the oldest entry beyond the cap is dropped
        private void PushPendingWriteId(string writeId)
        {
            _pendingWriteIds = _pendingWriteIds ?? new List<string>(MaxPendingWriteIds);
            _pendingWriteIds.Remove(writeId);
            _pendingWriteIds.Insert(0, writeId);
            if (_pendingWriteIds.Count > MaxPendingWriteIds)
            {
                _pendingWriteIds.RemoveRange(MaxPendingWriteIds, _pendingWriteIds.Count - MaxPendingWriteIds);
            }
        }

        internal JObject ToJson()
        {
            var obj = new JObject();
            obj[HadContentProperty] = HadContent;
            obj[LastSyncedRevisionProperty] = LastSyncedRevision;
            StateJson.WriteString(obj, LastSyncedWriteIdProperty, LastSyncedWriteId);
            StateJson.WriteString(obj, LastSyncedProviderVersionProperty, LastSyncedProviderVersion);

            // Older builds read pendingWriteId only, so the newest id keeps its own property
            StateJson.WriteString(obj, PendingWriteIdProperty, PendingWriteId);
            if (_pendingWriteIds != null && _pendingWriteIds.Count > 1)
            {
                var ids = new JArray();
                for (int i = 0; i < _pendingWriteIds.Count; i++)
                {
                    ids.Add(_pendingWriteIds[i]);
                }

                obj[PendingWriteIdsProperty] = ids;
            }

            if (PendingDelete)
            {
                var tombstone = new JObject();
                tombstone[PendingDeleteProperty] = true;
                StateJson.WriteString(tombstone, DeletedWriteIdProperty, DeletedWriteId);
                StateJson.WriteString(tombstone, DeletedProviderVersionProperty, DeletedProviderVersion);
                obj[TombstoneProperty] = tombstone;
            }

            obj[NeedsCloudRecoveryProperty] = NeedsCloudRecovery;
            obj[LastUploadedBytesProperty] = LastUploadedBytes;
            return obj;
        }

        internal static SlotSyncState FromJson(JObject obj, bool strict)
        {
            var state = new SlotSyncState
            {
                HadContent = StateJson.ReadBool(obj, HadContentProperty, strict),
                LastSyncedRevision = StateJson.ReadNonNegativeLong(obj, LastSyncedRevisionProperty, strict),
                LastSyncedWriteId = StateJson.ReadString(obj, LastSyncedWriteIdProperty, strict),
                LastSyncedProviderVersion = StateJson.ReadString(obj, LastSyncedProviderVersionProperty, strict),
                NeedsCloudRecovery = StateJson.ReadBool(obj, NeedsCloudRecoveryProperty, strict),
                LastUploadedBytes = StateJson.ReadNonNegativeLong(obj, LastUploadedBytesProperty, strict),
            };

            // Read first so a wrong type still fails strictly, applied last so the newest id ends up at index 0
            string newestWriteId = StateJson.ReadString(obj, PendingWriteIdProperty, strict);

            // A file written before the ring existed carries the single pendingWriteId only; an empty array must not erase it
            List<string> pendingWriteIds = StateJson.ReadStringArray(obj, PendingWriteIdsProperty, strict, MaxPendingWriteIds);
            if (pendingWriteIds != null && pendingWriteIds.Count > 0)
            {
                state.SetPendingWriteIds(pendingWriteIds);
            }

            if (!string.IsNullOrEmpty(newestWriteId))
            {
                state.PendingWriteId = newestWriteId;
            }

            JObject tombstone = StateJson.ReadObject(obj, TombstoneProperty, strict);
            if (tombstone != null && StateJson.ReadBool(tombstone, PendingDeleteProperty, strict))
            {
                state.SetTombstone(
                    StateJson.ReadString(tombstone, DeletedWriteIdProperty, strict),
                    StateJson.ReadString(tombstone, DeletedProviderVersionProperty, strict));
            }

            return state;
        }
    }

    /// <summary>profile.json model: owner account and per-slot sync state. Main thread only.</summary>
    internal sealed class ProfileSyncState
    {
        /// <summary>Document version written by this build.</summary>
        public const int CurrentVersion = 1;

        internal const string OwnerAccountIdProperty = "ownerAccountId";
        internal const string UnknownSlotsHadContentProperty = "unknownSlotsHadContent";
        internal const string SlotsProperty = "slots";

        private readonly Dictionary<string, SlotSyncState> _slots = new Dictionary<string, SlotSyncState>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Account that owns the directory; null for guest and local profiles.</summary>
        public string OwnerAccountId { get; set; }

        /// <summary>HadContent for slots without an entry; true after a safe fallback and persisted so it survives restarts.</summary>
        public bool UnknownSlotsHadContent { get; set; }

        /// <summary>Version found on disk; CurrentVersion for new or fallback states.</summary>
        public int LoadedVersion { get; private set; } = CurrentVersion;

        public IReadOnlyDictionary<string, SlotSyncState> Slots => _slots;

        public static ProfileSyncState CreateSafeFallback()
        {
            return new ProfileSyncState { UnknownSlotsHadContent = true };
        }

        public bool TryGetSlot(string key, out SlotSyncState state)
        {
            if (key == null)
            {
                state = null;
                return false;
            }

            return _slots.TryGetValue(key, out state);
        }

        /// <summary>Existing entry, or a new one added with HadContent = UnknownSlotsHadContent.</summary>
        public SlotSyncState GetOrCreateSlot(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentException("Key must not be null or empty.", nameof(key));
            }

            if (!_slots.TryGetValue(key, out SlotSyncState state))
            {
                state = new SlotSyncState { HadContent = UnknownSlotsHadContent };
                _slots.Add(key, state);
            }

            return state;
        }

        /// <summary>Existing entry, or a detached default that is not added (for read-only queries).</summary>
        public SlotSyncState PeekSlot(string key)
        {
            return TryGetSlot(key, out SlotSyncState state) ? state : new SlotSyncState { HadContent = UnknownSlotsHadContent };
        }

        public bool RemoveSlot(string key)
        {
            return key != null && _slots.Remove(key);
        }

        /// <summary>Unknown provenance for every entry and for slots without one.</summary>
        public void ApplySafeFallback()
        {
            UnknownSlotsHadContent = true;
            foreach (SlotSyncState state in _slots.Values)
            {
                state.ApplySafeFallback();
            }
        }

        internal JObject ToJson()
        {
            var root = new JObject();
            root[StateFileReader.VersionProperty] = CurrentVersion;
            StateJson.WriteString(root, OwnerAccountIdProperty, OwnerAccountId);
            if (UnknownSlotsHadContent)
            {
                root[UnknownSlotsHadContentProperty] = true;
            }

            var keys = new List<string>(_slots.Keys);
            keys.Sort(StringComparer.Ordinal);
            var slots = new JObject();
            for (int i = 0; i < keys.Count; i++)
            {
                slots[keys[i]] = _slots[keys[i]].ToJson();
            }

            root[SlotsProperty] = slots;
            return root;
        }

        /// <summary>Strict for known versions (throws FormatException); newer versions load known fields leniently with safe overrides.</summary>
        internal static ProfileSyncState FromJson(JObject root, int version)
        {
            bool strict = version <= CurrentVersion;
            var state = new ProfileSyncState
            {
                LoadedVersion = version,
                OwnerAccountId = StateJson.ReadString(root, OwnerAccountIdProperty, strict),
                UnknownSlotsHadContent = StateJson.ReadBool(root, UnknownSlotsHadContentProperty, strict),
            };

            JObject slots = StateJson.ReadObject(root, SlotsProperty, strict);
            if (slots != null)
            {
                foreach (JProperty property in slots.Properties())
                {
                    if (!SaveSlotRegistry.IsValidKey(property.Name) || state._slots.ContainsKey(property.Name))
                    {
                        if (strict)
                        {
                            throw new FormatException("Invalid or duplicate slot key '" + property.Name + "'.");
                        }

                        continue;
                    }

                    if (!(property.Value is JObject slotObject))
                    {
                        if (strict)
                        {
                            throw new FormatException("Slot entry '" + property.Name + "' must be an object.");
                        }

                        continue;
                    }

                    state._slots.Add(property.Name, SlotSyncState.FromJson(slotObject, strict));
                }
            }

            if (!strict)
            {
                state.ApplySafeFallback();
            }

            return state;
        }
    }

    internal enum SyncStateLoadStatus
    {
        /// <summary>No profile.json and no leftovers; a fresh state.</summary>
        Missing = 0,

        Loaded = 1,

        /// <summary>Written by a newer build; known fields loaded with safe overrides.</summary>
        NewerVersion = 2,

        /// <summary>Primary missing, crash-written .tmp used as is.</summary>
        RecoveredFromTmp = 3,

        /// <summary>A .tmp after a corrupt primary or a .bak was used, with safe overrides.</summary>
        RecoveredFromBackup = 4,

        /// <summary>No readable copy; safe fallback state.</summary>
        Corrupt = 5,

        /// <summary>Read failed; safe fallback state. Do not overwrite the file until a read succeeds.</summary>
        IoError = 6,
    }

    /// <summary>Result of loading profile.json; State is never null.</summary>
    internal readonly struct SyncStateLoadResult
    {
        internal SyncStateLoadResult(
            SyncStateLoadStatus status,
            ProfileSyncState state,
            bool usedSafeFallback,
            bool primaryWasCorrupt,
            int foundVersion,
            string quarantineFileName,
            bool newerVersionUnbacked,
            LocalWriteErrorKind errorKind,
            string message,
            Exception exception)
        {
            Status = status;
            State = state;
            UsedSafeFallback = usedSafeFallback;
            PrimaryWasCorrupt = primaryWasCorrupt;
            FoundVersion = foundVersion;
            QuarantineFileName = quarantineFileName;
            NewerVersionUnbacked = newerVersionUnbacked;
            ErrorKind = errorKind;
            Message = message;
            Exception = exception;
        }

        public SyncStateLoadStatus Status { get; }

        public ProfileSyncState State { get; }

        /// <summary>HadContent forced true and LastSyncedWriteId null.</summary>
        public bool UsedSafeFallback { get; }

        public bool PrimaryWasCorrupt { get; }

        /// <summary>Document version read; 0 when nothing was read.</summary>
        public int FoundVersion { get; }

        public string QuarantineFileName { get; }

        /// <summary>A newer-version profile.json could not be quarantined; overwriting it would destroy it without a copy.</summary>
        public bool NewerVersionUnbacked { get; }

        public LocalWriteErrorKind ErrorKind { get; }

        public string Message { get; }

        public Exception Exception { get; }

        /// <summary>False after an IoError or a failed newer-version backup: writing now could replace data that has no copy.</summary>
        public bool CanOverwrite => Status != SyncStateLoadStatus.IoError && !NewerVersionUnbacked;

        /// <summary>SyncStateCorrupt issue for corrupt or unreadable profile.json; null otherwise.</summary>
        public SlotLoadIssue CreateIssue(ProfileId profile)
        {
            if (Status != SyncStateLoadStatus.Corrupt && Status != SyncStateLoadStatus.IoError && !PrimaryWasCorrupt)
            {
                return null;
            }

            CorruptionCause cause = Status == SyncStateLoadStatus.IoError ? CorruptionCause.None : CorruptionCause.ParseFailed;
            return new SlotLoadIssue(profile, null, SlotLoadIssueKind.SyncStateCorrupt, cause, QuarantineFileName, Message, Exception);
        }
    }

    /// <summary>Reads and atomically writes profile.json. Thread-safe file access; the state object itself is main thread only.</summary>
    internal sealed class SyncStateStore
    {
        private const string BackupKey = "profile";

        private readonly ISaveStorage _storage;
        private readonly ISaveLogger _logger;
        private readonly ISaveClock _clock;

        public SyncStateStore(ISaveStorage storage, ISaveLogger logger, ISaveClock clock)
        {
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        /// <summary>Encodes a state to compact UTF-8 (main thread); pair with SaveEncoded for thread-pool writes.</summary>
        public static byte[] Encode(ProfileSyncState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            return SaveJson.ToUtf8Bytes(state.ToJson());
        }

        /// <summary>Loads profile.json; Recover quarantines a corrupt primary, ReadOnly never touches files.</summary>
        public SyncStateLoadResult Load(string profileDirectory, SlotReadMode mode = SlotReadMode.Recover)
        {
            if (string.IsNullOrEmpty(profileDirectory))
            {
                throw new ArgumentException("Profile directory must not be null or empty.", nameof(profileDirectory));
            }

            StateFileReadResult<ProfileSyncState> read = StateFileReader.Read<ProfileSyncState>(
                _storage, profileDirectory, SaveLayout.ProfileStateFileName, BackupKey, mode == SlotReadMode.Recover, ProfileSyncState.FromJson,
                _clock, _logger, ProfileSyncState.CurrentVersion);

            switch (read.Status)
            {
                case StateFileReadStatus.Missing:
                    return Create(SyncStateLoadStatus.Missing, new ProfileSyncState(), false, in read);
                case StateFileReadStatus.IoError:
                    _logger.Error("profile.json in " + profileDirectory + " could not be read; using safe sync-state fallbacks. " + read.Message, read.Exception);
                    return Create(SyncStateLoadStatus.IoError, ProfileSyncState.CreateSafeFallback(), true, in read);
                case StateFileReadStatus.Corrupt:
                    _logger.Error("profile.json in " + profileDirectory + " is corrupt; using safe sync-state fallbacks. " + read.Message, read.Exception);
                    return Create(SyncStateLoadStatus.Corrupt, ProfileSyncState.CreateSafeFallback(), true, in read);
                case StateFileReadStatus.RecoveredFromTmp when !read.PrimaryWasCorrupt && read.Version <= ProfileSyncState.CurrentVersion:
                    return Create(SyncStateLoadStatus.RecoveredFromTmp, read.Model, false, in read);
                case StateFileReadStatus.RecoveredFromTmp:
                case StateFileReadStatus.RecoveredFromBackup:
                    // A recovered copy from a newer build is classified by its version, not by the copy it came from
                    read.Model.ApplySafeFallback();
                    if (read.Version > ProfileSyncState.CurrentVersion)
                    {
                        return CreateNewerVersion(profileDirectory, in read);
                    }

                    _logger.Warning("profile.json in " + profileDirectory + " was recovered from an older copy; safe sync-state fallbacks applied.");
                    return Create(SyncStateLoadStatus.RecoveredFromBackup, read.Model, true, in read);
                default:
                    if (read.Version > ProfileSyncState.CurrentVersion)
                    {
                        return CreateNewerVersion(profileDirectory, in read);
                    }

                    return Create(SyncStateLoadStatus.Loaded, read.Model, false, in read);
            }
        }

        /// <summary>Encodes and writes atomically; never throws on storage failure.</summary>
        public StateWriteResult Save(string profileDirectory, ProfileSyncState state)
        {
            return SaveEncoded(profileDirectory, Encode(state));
        }

        /// <summary>Writes encoded bytes atomically (any thread).</summary>
        public StateWriteResult SaveEncoded(string profileDirectory, byte[] bytes)
        {
            if (string.IsNullOrEmpty(profileDirectory))
            {
                throw new ArgumentException("Profile directory must not be null or empty.", nameof(profileDirectory));
            }

            return StateFileReader.Write(_storage, SaveLayout.ProfileStatePath(profileDirectory), bytes);
        }

        private SyncStateLoadResult CreateNewerVersion(string profileDirectory, in StateFileReadResult<ProfileSyncState> read)
        {
            string suffix = read.NewerVersionUnbacked
                ? "); it could not be backed up, so it is left untouched."
                : "); known fields loaded with safe fallbacks.";
            _logger.Warning(
                "profile.json in " + profileDirectory + " has version " + read.Version + " (supported " + ProfileSyncState.CurrentVersion + suffix);
            return Create(SyncStateLoadStatus.NewerVersion, read.Model, true, in read);
        }

        private static SyncStateLoadResult Create(SyncStateLoadStatus status, ProfileSyncState state, bool usedSafeFallback, in StateFileReadResult<ProfileSyncState> read)
        {
            return new SyncStateLoadResult(
                status, state, usedSafeFallback, read.PrimaryWasCorrupt, read.Version, read.QuarantineFileName, read.NewerVersionUnbacked,
                read.ErrorKind, read.Message, read.Exception);
        }
    }

    internal enum StateFileReadStatus
    {
        Missing = 0,
        Loaded = 1,
        RecoveredFromTmp = 2,
        RecoveredFromBackup = 3,

        /// <summary>Bytes existed but no copy parsed.</summary>
        Corrupt = 4,

        IoError = 5,
    }

    /// <summary>Parses a versioned state document; throws (for example FormatException) when malformed.</summary>
    internal delegate TModel StateDocumentParser<TModel>(JObject root, int version);

    /// <summary>Result of reading a versioned state file (profile.json, device.json).</summary>
    internal readonly struct StateFileReadResult<TModel> where TModel : class
    {
        internal StateFileReadResult(
            StateFileReadStatus status,
            TModel model,
            int version,
            bool primaryWasCorrupt,
            string quarantineFileName,
            bool newerVersionUnbacked,
            LocalWriteErrorKind errorKind,
            string message,
            Exception exception)
        {
            Status = status;
            Model = model;
            Version = version;
            PrimaryWasCorrupt = primaryWasCorrupt;
            QuarantineFileName = quarantineFileName;
            NewerVersionUnbacked = newerVersionUnbacked;
            ErrorKind = errorKind;
            Message = message;
            Exception = exception;
        }

        public StateFileReadStatus Status { get; }

        /// <summary>Parsed model for Loaded and Recovered statuses; null otherwise.</summary>
        public TModel Model { get; }

        public int Version { get; }

        public bool PrimaryWasCorrupt { get; }

        public string QuarantineFileName { get; }

        /// <summary>A newer-version document could not be quarantined; the caller must not overwrite it.</summary>
        public bool NewerVersionUnbacked { get; }

        public LocalWriteErrorKind ErrorKind { get; }

        public string Message { get; }

        public Exception Exception { get; }
    }

    /// <summary>Result of writing a state file; never thrown.</summary>
    internal readonly struct StateWriteResult
    {
        private StateWriteResult(bool isSuccess, LocalWriteErrorKind errorKind, string message, Exception exception)
        {
            IsSuccess = isSuccess;
            ErrorKind = errorKind;
            Message = message;
            Exception = exception;
        }

        public bool IsSuccess { get; }

        public LocalWriteErrorKind ErrorKind { get; }

        public string Message { get; }

        public Exception Exception { get; }

        public SaveError ToSaveError()
        {
            return IsSuccess ? null : SaveError.FromLocalWriteKind(ErrorKind, Message, Exception);
        }

        internal static StateWriteResult Success()
        {
            return new StateWriteResult(true, LocalWriteErrorKind.IoError, null, null);
        }

        internal static StateWriteResult Failed(Exception exception)
        {
            return new StateWriteResult(false, StorageErrorClassifier.Classify(exception), exception.Message, exception);
        }
    }

    /// <summary>Shared read path for versioned state files: primary, then .tmp, then .bak; corrupt primary quarantined when repair is allowed.</summary>
    internal static class StateFileReader
    {
        public const string VersionProperty = "version";

        public static StateFileReadResult<TModel> Read<TModel>(
            ISaveStorage storage,
            string directory,
            string fileName,
            string backupKey,
            bool allowRepair,
            StateDocumentParser<TModel> parser,
            ISaveClock clock,
            ISaveLogger logger,
            int currentVersion = int.MaxValue)
            where TModel : class
        {
            if (storage == null)
            {
                throw new ArgumentNullException(nameof(storage));
            }

            if (parser == null)
            {
                throw new ArgumentNullException(nameof(parser));
            }

            string path = SaveLayout.Combine(directory, fileName);
            if (!TryReadBytes(storage, path, out byte[] primaryBytes, out Exception readError))
            {
                return IoError<TModel>(readError, path, false, null);
            }

            bool primaryWasCorrupt = false;
            string quarantine = null;
            string corruptMessage = null;
            Exception corruptException = null;
            if (primaryBytes != null)
            {
                if (TryParse(primaryBytes, parser, out TModel model, out int version, out corruptMessage, out corruptException))
                {
                    // A document from a newer build is kept verbatim in quarantine: the next write rewrites it in this build's format
                    string newerBackup = BackupNewerVersion(
                        storage, directory, backupKey, primaryBytes, version, currentVersion, allowRepair, clock, logger, path, out bool unbacked);
                    return new StateFileReadResult<TModel>(
                        StateFileReadStatus.Loaded, model, version, false, newerBackup, unbacked, LocalWriteErrorKind.IoError, null, null);
                }

                primaryWasCorrupt = true;
                logger?.Warning(path + " is corrupt: " + corruptMessage);
                if (allowRepair)
                {
                    try
                    {
                        quarantine = LocalBackupFiles.MoveToBackup(storage, directory, backupKey, path, BackupFileFamily.LocalCorrupt, clock.UtcNow);
                    }
                    catch (Exception exception)
                    {
                        return IoError<TModel>(exception, path, true, null);
                    }

                    LocalBackupFiles.Prune(storage, directory, backupKey, BackupFileFamily.LocalCorrupt, SaveLayout.QuarantineCap, quarantine, logger);
                }
            }

            bool sawBytes = primaryBytes != null;
            string tmpPath = SaveLayout.TmpPath(path);
            if (!TryReadBytes(storage, tmpPath, out byte[] tmpBytes, out readError))
            {
                return IoError<TModel>(readError, tmpPath, primaryWasCorrupt, quarantine);
            }

            if (tmpBytes != null)
            {
                sawBytes = true;
                if (TryParse(tmpBytes, parser, out TModel model, out int version, out _, out _))
                {
                    // A recovered copy can be from a newer build too, and this build is about to rewrite it
                    string newerBackup = BackupNewerVersion(
                        storage, directory, backupKey, tmpBytes, version, currentVersion, allowRepair, clock, logger, tmpPath, out bool unbacked);
                    return new StateFileReadResult<TModel>(
                        StateFileReadStatus.RecoveredFromTmp, model, version, primaryWasCorrupt, quarantine ?? newerBackup, unbacked,
                        LocalWriteErrorKind.IoError, null, null);
                }
            }

            string bakPath = SaveLayout.BakPath(path);
            if (!TryReadBytes(storage, bakPath, out byte[] bakBytes, out readError))
            {
                return IoError<TModel>(readError, bakPath, primaryWasCorrupt, quarantine);
            }

            if (bakBytes != null)
            {
                sawBytes = true;
                if (TryParse(bakBytes, parser, out TModel model, out int version, out _, out _))
                {
                    string newerBackup = BackupNewerVersion(
                        storage, directory, backupKey, bakBytes, version, currentVersion, allowRepair, clock, logger, bakPath, out bool unbacked);
                    return new StateFileReadResult<TModel>(
                        StateFileReadStatus.RecoveredFromBackup, model, version, primaryWasCorrupt, quarantine ?? newerBackup, unbacked,
                        LocalWriteErrorKind.IoError, null, null);
                }
            }

            if (sawBytes)
            {
                return new StateFileReadResult<TModel>(
                    StateFileReadStatus.Corrupt, null, 0, primaryWasCorrupt, quarantine, false, LocalWriteErrorKind.IoError,
                    corruptMessage ?? "No readable copy of " + path + ".", corruptException);
            }

            return new StateFileReadResult<TModel>(StateFileReadStatus.Missing, null, 0, false, null, false, LocalWriteErrorKind.IoError, null, null);
        }

        /// <summary>Atomic write; failures are classified and returned.</summary>
        public static StateWriteResult Write(ISaveStorage storage, string path, byte[] bytes)
        {
            if (storage == null)
            {
                throw new ArgumentNullException(nameof(storage));
            }

            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            try
            {
                storage.WriteAtomic(path, bytes);
                return StateWriteResult.Success();
            }
            catch (Exception exception)
            {
                return StateWriteResult.Failed(exception);
            }
        }

        // The load still succeeds on a failed backup, but failed is reported so the caller refuses to overwrite the file
        private static string BackupNewerVersion(
            ISaveStorage storage,
            string directory,
            string backupKey,
            byte[] bytes,
            int version,
            int currentVersion,
            bool allowRepair,
            ISaveClock clock,
            ISaveLogger logger,
            string path,
            out bool failed)
        {
            failed = false;
            if (version <= currentVersion)
            {
                return null;
            }

            // ReadOnly cannot quarantine, so nothing may overwrite this document either
            if (!allowRepair)
            {
                failed = true;
                return null;
            }

            try
            {
                string existing = FindIdenticalBackup(storage, directory, backupKey, bytes);
                if (existing != null)
                {
                    return existing;
                }

                string quarantine = LocalBackupFiles.WriteBackup(storage, directory, backupKey, bytes, BackupFileFamily.NewerVersion, clock.UtcNow);
                LocalBackupFiles.Prune(storage, directory, backupKey, BackupFileFamily.NewerVersion, SaveLayout.NewerVersionCap, quarantine, logger);
                return quarantine;
            }
            catch (Exception exception)
            {
                failed = true;
                logger?.Error("[SaveSystem] Backing up the newer-version " + path + " failed; the file is left untouched.", exception);
                return null;
            }
        }

        // Every Recover-mode load of the same newer document would otherwise write another identical copy
        private static string FindIdenticalBackup(ISaveStorage storage, string directory, string backupKey, byte[] bytes)
        {
            IReadOnlyList<string> names = storage.ListFileNames(directory);
            for (int i = 0; i < names.Count; i++)
            {
                if (!SaveLayout.IsBackupFileName(BackupFileFamily.NewerVersion, backupKey, names[i]))
                {
                    continue;
                }

                byte[] stored = storage.ReadAllBytes(SaveLayout.Combine(directory, names[i]));
                if (stored != null && BytesEqual(stored, bytes))
                {
                    return names[i];
                }
            }

            return null;
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (left.Length != right.Length)
            {
                return false;
            }

            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryParse<TModel>(
            byte[] bytes,
            StateDocumentParser<TModel> parser,
            out TModel model,
            out int version,
            out string error,
            out Exception exception)
            where TModel : class
        {
            model = null;
            version = 0;
            error = null;
            exception = null;
            try
            {
                if (!(SaveJson.Parse(bytes) is JObject root))
                {
                    error = "Root is not a JSON object.";
                    return false;
                }

                JToken versionToken = root[VersionProperty];
                if (!(versionToken is JValue versionValue) || versionValue.Type != JTokenType.Integer || !(versionValue.Value is long number)
                    || number < 1 || number > int.MaxValue)
                {
                    error = "version is missing or invalid.";
                    return false;
                }

                version = (int)number;
                model = parser(root, version);
                if (model == null)
                {
                    error = "Document could not be parsed.";
                    return false;
                }

                return true;
            }
            catch (Exception caught)
            {
                model = null;
                error = caught.Message;
                exception = caught;
                return false;
            }
        }

        private static bool TryReadBytes(ISaveStorage storage, string path, out byte[] bytes, out Exception exception)
        {
            try
            {
                bytes = storage.ReadAllBytes(path);
                exception = null;
                return true;
            }
            catch (Exception caught)
            {
                bytes = null;
                exception = caught;
                return false;
            }
        }

        private static StateFileReadResult<TModel> IoError<TModel>(Exception exception, string path, bool primaryWasCorrupt, string quarantine)
            where TModel : class
        {
            return new StateFileReadResult<TModel>(
                StateFileReadStatus.IoError, null, 0, primaryWasCorrupt, quarantine, false, StorageErrorClassifier.Classify(exception),
                "Accessing " + path + " failed: " + exception.Message, exception);
        }
    }

    /// <summary>Typed field access for state documents: strict throws FormatException on wrong types, lenient ignores them.</summary>
    internal static class StateJson
    {
        public static string ReadString(JObject obj, string name, bool strict)
        {
            JToken token = obj[name];
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            if (token.Type == JTokenType.String)
            {
                return (string)token;
            }

            return Invalid<string>(name, "a string", strict, null);
        }

        public static bool ReadBool(JObject obj, string name, bool strict)
        {
            JToken token = obj[name];
            if (token == null || token.Type == JTokenType.Null)
            {
                return false;
            }

            if (token.Type == JTokenType.Boolean)
            {
                return (bool)token;
            }

            return Invalid(name, "a boolean", strict, false);
        }

        public static long ReadNonNegativeLong(JObject obj, string name, bool strict)
        {
            JToken token = obj[name];
            if (token == null || token.Type == JTokenType.Null)
            {
                return 0;
            }

            if (token is JValue value && value.Type == JTokenType.Integer && value.Value is long number && number >= 0)
            {
                return number;
            }

            return Invalid(name, "a non-negative integer", strict, 0L);
        }

        /// <summary>Non-empty strings of an array property, capped at max; null when the property is absent.</summary>
        public static List<string> ReadStringArray(JObject obj, string name, bool strict, int max)
        {
            JToken token = obj[name];
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            if (!(token is JArray array))
            {
                return Invalid<List<string>>(name, "an array", strict, null);
            }

            var values = new List<string>(array.Count);
            for (int i = 0; i < array.Count && values.Count < max; i++)
            {
                JToken entry = array[i];
                if (entry == null || entry.Type != JTokenType.String || string.IsNullOrEmpty((string)entry))
                {
                    Invalid<string>(name, "an array of non-empty strings", strict, null);
                    continue;
                }

                values.Add((string)entry);
            }

            return values;
        }

        public static JObject ReadObject(JObject obj, string name, bool strict)
        {
            JToken token = obj[name];
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            if (token is JObject child)
            {
                return child;
            }

            return Invalid<JObject>(name, "an object", strict, null);
        }

        /// <summary>Adds the property only when value is not null.</summary>
        public static void WriteString(JObject obj, string name, string value)
        {
            if (value != null)
            {
                obj[name] = value;
            }
        }

        private static T Invalid<T>(string name, string expected, bool strict, T fallback)
        {
            if (strict)
            {
                throw new FormatException(name + " must be " + expected + ".");
            }

            return fallback;
        }
    }
}
