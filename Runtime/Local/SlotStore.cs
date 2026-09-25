using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace Ecanakli.SaveSystem
{
    /// <summary>Whether a local read may repair files (promote, quarantine, rewrite) or must stay read-only.</summary>
    internal enum SlotReadMode
    {
        Recover = 0,

        /// <summary>Queries (probe, RequireSynced): never persists, renames, quarantines or promotes.</summary>
        ReadOnly = 1,
    }

    /// <summary>File a load result came from.</summary>
    internal enum SlotFileSource
    {
        None = 0,
        Primary = 1,
        Tmp = 2,
        Backup = 3,
    }

    /// <summary>Outcome of the file step of a local load.</summary>
    internal enum SlotFileReadStatus
    {
        Found = 0,
        Absent = 1,

        /// <summary>Bytes existed but no valid copy was found; defaults and cloud recovery apply.</summary>
        CorruptNoValidCopy = 2,

        SchemaTooNew = 3,

        /// <summary>A read or quarantine rename failed; nothing was written.</summary>
        IoError = 4,
    }

    /// <summary>File recovery that happened during a load.</summary>
    internal enum SlotRecovery
    {
        None = 0,

        /// <summary>A valid newer .tmp replaced a missing or older primary (crash recovery, not corruption).</summary>
        PromotedTmp = 1,

        /// <summary>.bak was used because the primary was missing.</summary>
        PromotedBackup = 2,

        CorruptRecoveredFromTmp = 3,
        CorruptRecoveredFromBackup = 4,

        /// <summary>No valid copy; defaults loaded, HadContent kept, NeedsCloudRecovery required.</summary>
        CorruptReset = 5,
    }

    internal enum SlotWriteStatus
    {
        Written = 0,
        Failed = 1,

        /// <summary>The file on disk is newer than this build; it is never overwritten.</summary>
        RefusedSchemaTooNew = 2,

        /// <summary>A newer revision of this slot is already durable; the stale bytes are never written.</summary>
        RefusedStaleRevision = 3,
    }

    internal enum SlotFileOpStatus
    {
        Done = 0,
        Skipped = 1,
        Failed = 2,
    }

    /// <summary>File step of a load: which copy was chosen and what was repaired. Thread-safe to produce.</summary>
    internal readonly struct SlotFileReadResult
    {
        private SlotFileReadResult(
            SlotFileReadStatus status,
            SlotFileSource source,
            SaveEnvelope envelope,
            ChecksumStatus checksum,
            bool primaryWasCorrupt,
            CorruptionCause cause,
            string quarantineFileName,
            bool repairFailed,
            LocalWriteErrorKind errorKind,
            string message,
            Exception exception)
        {
            Status = status;
            Source = source;
            Envelope = envelope;
            Checksum = checksum;
            PrimaryWasCorrupt = primaryWasCorrupt;
            Cause = cause;
            QuarantineFileName = quarantineFileName;
            RepairFailed = repairFailed;
            ErrorKind = errorKind;
            Message = message;
            Exception = exception;
        }

        public SlotFileReadStatus Status { get; }

        public SlotFileSource Source { get; }

        /// <summary>Found: header plus Data (null for header-only reads). SchemaTooNew: Format and Schema. Otherwise null.</summary>
        public SaveEnvelope Envelope { get; }

        public ChecksumStatus Checksum { get; }

        /// <summary>The primary file existed but was corrupt.</summary>
        public bool PrimaryWasCorrupt { get; }

        public CorruptionCause Cause { get; }

        /// <summary>Name of the quarantine file written during this read, if any.</summary>
        public string QuarantineFileName { get; }

        /// <summary>The chosen copy was loaded but promoting or rewriting it as primary failed; a later write repairs it.</summary>
        public bool RepairFailed { get; }

        /// <summary>Classified error when Status is IoError.</summary>
        public LocalWriteErrorKind ErrorKind { get; }

        public string Message { get; }

        public Exception Exception { get; }

        public int FoundFormat => Envelope?.Format ?? 0;

        public int FoundSchema => Envelope?.Schema ?? 0;

        /// <summary>Envelope revision when Found; 0 otherwise.</summary>
        public long Revision => Status == SlotFileReadStatus.Found && Envelope != null ? Envelope.Revision : 0;

        internal static SlotFileReadResult CreateFound(
            SlotFileSource source,
            in EnvelopeDecodeResult decoded,
            bool primaryWasCorrupt,
            CorruptionCause cause,
            string quarantineFileName,
            string message,
            bool repairFailed)
        {
            return new SlotFileReadResult(
                SlotFileReadStatus.Found, source, decoded.Envelope, decoded.Checksum, primaryWasCorrupt, cause, quarantineFileName, repairFailed,
                LocalWriteErrorKind.IoError, message, null);
        }

        internal static SlotFileReadResult CreateAbsent()
        {
            return new SlotFileReadResult(
                SlotFileReadStatus.Absent, SlotFileSource.None, null, ChecksumStatus.NotChecked, false, CorruptionCause.None, null, false,
                LocalWriteErrorKind.IoError, null, null);
        }

        internal static SlotFileReadResult CreateCorruptNoValidCopy(bool primaryWasCorrupt, CorruptionCause cause, string quarantineFileName, string message)
        {
            return new SlotFileReadResult(
                SlotFileReadStatus.CorruptNoValidCopy, SlotFileSource.None, null, ChecksumStatus.NotChecked, primaryWasCorrupt, cause,
                quarantineFileName, false, LocalWriteErrorKind.IoError, message, null);
        }

        internal static SlotFileReadResult CreateTooNew(
            SlotFileSource source,
            in EnvelopeDecodeResult decoded,
            bool primaryWasCorrupt,
            CorruptionCause cause,
            string quarantineFileName)
        {
            return new SlotFileReadResult(
                SlotFileReadStatus.SchemaTooNew, source, decoded.Envelope, ChecksumStatus.NotChecked, primaryWasCorrupt, cause, quarantineFileName,
                false, LocalWriteErrorKind.IoError, decoded.Message, null);
        }

        internal static SlotFileReadResult CreateIoError(
            Exception exception,
            string message,
            bool primaryWasCorrupt,
            CorruptionCause cause,
            string quarantineFileName)
        {
            return new SlotFileReadResult(
                SlotFileReadStatus.IoError, SlotFileSource.None, null, ChecksumStatus.NotChecked, primaryWasCorrupt, cause, quarantineFileName, false,
                StorageErrorClassifier.Classify(exception), message, exception);
        }
    }

    /// <summary>Completed local load; apply with SaveSlot.ApplyLoadResult. The service decides events from CreateIssues.</summary>
    internal readonly struct SlotLoadResult
    {
        internal SlotLoadResult(
            in SlotFileReadResult file,
            SlotFailure failure,
            SlotRecovery recovery,
            object data,
            long revision,
            SlotMaterializeStage failedStage,
            string message,
            Exception exception)
        {
            File = file;
            Failure = failure;
            Recovery = recovery;
            Data = data;
            Revision = revision;
            FailedStage = failedStage;
            Message = message;
            Exception = exception;
        }

        public SlotFileReadResult File { get; }

        /// <summary>None means the slot becomes Ready.</summary>
        public SlotFailure Failure { get; }

        public SlotRecovery Recovery { get; }

        /// <summary>Normalized data to apply; the fallback default for failures and resets.</summary>
        public object Data { get; }

        public long Revision { get; }

        /// <summary>Upgrade, Materialize, CreateDefault or Normalize when Failure is NormalizeFailed.</summary>
        public SlotMaterializeStage FailedStage { get; }

        public string Message { get; }

        public Exception Exception { get; }

        public bool IsReady => Failure == SlotFailure.None;

        /// <summary>True after CorruptReset: persist NeedsCloudRecovery and keep HadContent as is.</summary>
        public bool NeedsCloudRecovery => Recovery == SlotRecovery.CorruptReset;

        /// <summary>Write id of the loaded envelope; null when nothing was found.</summary>
        public string WriteId => File.Status == SlotFileReadStatus.Found ? File.Envelope?.WriteId : null;

        public LocalPresence Presence
        {
            get
            {
                if (Failure == SlotFailure.IoError || Failure == SlotFailure.SchemaTooNew || Recovery == SlotRecovery.CorruptReset)
                {
                    return LocalPresence.Undetermined;
                }

                return File.Status == SlotFileReadStatus.Found ? LocalPresence.Present : LocalPresence.Absent;
            }
        }

        /// <summary>Zero to two issues: a corruption recovery and a failure.</summary>
        public IReadOnlyList<SlotLoadIssue> CreateIssues(ProfileId? profile, string slotKey)
        {
            List<SlotLoadIssue> issues = null;
            switch (Recovery)
            {
                case SlotRecovery.CorruptRecoveredFromTmp:
                    Add(ref issues, new SlotLoadIssue(profile, slotKey, SlotLoadIssueKind.CorruptRecoveredFromTmp, File.Cause, File.QuarantineFileName, File.Message));
                    break;
                case SlotRecovery.CorruptRecoveredFromBackup:
                    Add(ref issues, new SlotLoadIssue(profile, slotKey, SlotLoadIssueKind.CorruptRecoveredFromBackup, File.Cause, File.QuarantineFileName, File.Message));
                    break;
                case SlotRecovery.CorruptReset:
                    Add(ref issues, new SlotLoadIssue(profile, slotKey, SlotLoadIssueKind.CorruptReset, File.Cause, File.QuarantineFileName, File.Message));
                    break;
            }

            switch (Failure)
            {
                case SlotFailure.IoError:
                    Add(ref issues, new SlotLoadIssue(profile, slotKey, SlotLoadIssueKind.IoError, File.Cause, File.QuarantineFileName, Message, Exception));
                    break;
                case SlotFailure.SchemaTooNew:
                    Add(ref issues, new SlotLoadIssue(profile, slotKey, SlotLoadIssueKind.SchemaTooNew, CorruptionCause.None, null, Message));
                    break;
                case SlotFailure.NormalizeFailed:
                    Add(ref issues, new SlotLoadIssue(profile, slotKey, SlotLoadIssueKind.NormalizeFailed, CorruptionCause.None, null, FailedStage + ": " + Message, Exception));
                    break;
            }

            return issues ?? (IReadOnlyList<SlotLoadIssue>)Array.Empty<SlotLoadIssue>();
        }

        private static void Add(ref List<SlotLoadIssue> issues, SlotLoadIssue issue)
        {
            if (issues == null)
            {
                issues = new List<SlotLoadIssue>(2);
            }

            issues.Add(issue);
        }
    }

    /// <summary>Read-only header view of a slot's local files (RequireSynced check).</summary>
    internal readonly struct SlotHeaderPeekResult
    {
        internal SlotHeaderPeekResult(in SlotFileReadResult file, LocalPresence presence)
        {
            File = file;
            Presence = presence;
        }

        public SlotFileReadResult File { get; }

        /// <summary>Undetermined for IO errors, too-new files and any corrupt primary.</summary>
        public LocalPresence Presence { get; }

        public long Revision => Presence == LocalPresence.Present ? File.Envelope.Revision : 0;

        public string WriteId => Presence == LocalPresence.Present ? File.Envelope.WriteId : null;
    }

    /// <summary>Result of writing a slot envelope; never thrown.</summary>
    internal readonly struct SlotWriteResult
    {
        private SlotWriteResult(
            SlotWriteStatus status,
            long revision,
            long durableRevision,
            string dataSha256,
            int byteCount,
            LocalWriteErrorKind errorKind,
            string message,
            Exception exception)
        {
            Status = status;
            Revision = revision;
            DurableRevision = durableRevision;
            DataSha256 = dataSha256;
            ByteCount = byteCount;
            ErrorKind = errorKind;
            Message = message;
            Exception = exception;
        }

        public SlotWriteStatus Status { get; }

        public long Revision { get; }

        /// <summary>Newest revision this store wrote or attempted to write for the file; 0 when the write failed or was refused as too new.</summary>
        public long DurableRevision { get; }

        /// <summary>Checksum of the written data; feeds the mutation detector baseline.</summary>
        public string DataSha256 { get; }

        public int ByteCount { get; }

        public LocalWriteErrorKind ErrorKind { get; }

        public string Message { get; }

        public Exception Exception { get; }

        public bool IsSuccess => Status == SlotWriteStatus.Written;

        /// <summary>True when nothing was written because the file already holds equal or better data.</summary>
        public bool IsRefused => Status == SlotWriteStatus.RefusedSchemaTooNew || Status == SlotWriteStatus.RefusedStaleRevision;

        /// <summary>Null on success; DiskFull, AccessDenied or IoError on failure; SlotNotReady or Superseded when refused.</summary>
        public SaveError ToSaveError()
        {
            switch (Status)
            {
                case SlotWriteStatus.Written:
                    return null;
                case SlotWriteStatus.RefusedSchemaTooNew:
                    return new SaveError(SaveErrorCode.SlotNotReady, Message);
                case SlotWriteStatus.RefusedStaleRevision:
                    return new SaveError(SaveErrorCode.Superseded, Message);
                default:
                    return SaveError.FromLocalWriteKind(ErrorKind, Message, Exception);
            }
        }

        internal static SlotWriteResult Written(long revision, string dataSha256, int byteCount)
        {
            return new SlotWriteResult(SlotWriteStatus.Written, revision, revision, dataSha256, byteCount, LocalWriteErrorKind.IoError, null, null);
        }

        internal static SlotWriteResult Failed(long revision, Exception exception)
        {
            return new SlotWriteResult(
                SlotWriteStatus.Failed, revision, 0, null, 0, StorageErrorClassifier.Classify(exception), exception.Message, exception);
        }

        internal static SlotWriteResult Refused(long revision, string message)
        {
            return new SlotWriteResult(SlotWriteStatus.RefusedSchemaTooNew, revision, 0, null, 0, LocalWriteErrorKind.IoError, message, null);
        }

        internal static SlotWriteResult RefusedStale(long revision, long durableRevision, string message)
        {
            return new SlotWriteResult(
                SlotWriteStatus.RefusedStaleRevision, revision, durableRevision, null, 0, LocalWriteErrorKind.IoError, message, null);
        }
    }

    /// <summary>Result of a delete, quarantine, backup or conflict file operation; never thrown.</summary>
    internal readonly struct SlotFileOpResult
    {
        private SlotFileOpResult(SlotFileOpStatus status, string fileName, LocalWriteErrorKind errorKind, string message, Exception exception)
        {
            Status = status;
            FileName = fileName;
            ErrorKind = errorKind;
            Message = message;
            Exception = exception;
        }

        public SlotFileOpStatus Status { get; }

        /// <summary>Written or matched file name (not path), when any.</summary>
        public string FileName { get; }

        public LocalWriteErrorKind ErrorKind { get; }

        public string Message { get; }

        public Exception Exception { get; }

        public bool IsSuccess => Status != SlotFileOpStatus.Failed;

        internal static SlotFileOpResult Done(string fileName)
        {
            return new SlotFileOpResult(SlotFileOpStatus.Done, fileName, LocalWriteErrorKind.IoError, null, null);
        }

        internal static SlotFileOpResult Skipped(string fileName, string message)
        {
            return new SlotFileOpResult(SlotFileOpStatus.Skipped, fileName, LocalWriteErrorKind.IoError, message, null);
        }

        internal static SlotFileOpResult Failed(Exception exception, string message)
        {
            return new SlotFileOpResult(SlotFileOpStatus.Failed, null, StorageErrorClassifier.Classify(exception), message, exception);
        }
    }

    /// <summary>
    /// Per-slot local persistence over ISaveStorage and EnvelopeCodec: recovery, quarantine, writes, backups and conflict files.
    /// File methods are thread-safe; CompleteLoad and Load call slot hooks and belong on the main thread.
    /// Writes to one path are serialized by a per-path gate, so a caller waits out another thread's write of the same slot.
    /// </summary>
    internal sealed class SlotStore
    {
        private const string ConflictTimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
        private const string ResolutionProperty = "resolution";
        private const string SavedAtUtcProperty = "savedAtUtc";
        private const string LocalProperty = "local";
        private const string CloudProperty = "cloud";

        private readonly ISaveStorage _storage;
        private readonly ISaveLogger _logger;
        private readonly ISaveClock _clock;

        // Paths whose primary is newer than this build; writes to them are refused
        private readonly HashSet<string> _tooNewPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly object _tooNewLock = new object();

        // Per-path write gate plus the newest revision written through it; every caller passes through it
        private readonly Dictionary<string, PathWriteState> _writeStates = new Dictionary<string, PathWriteState>(StringComparer.OrdinalIgnoreCase);
        private readonly object _writeStatesLock = new object();

        public SlotStore(ISaveStorage storage, ISaveLogger logger, ISaveClock clock)
        {
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public ISaveStorage Storage => _storage;

        /// <summary>File step plus materialize in one call (main thread).</summary>
        public SlotLoadResult Load(SaveSlot slot, string directory, SlotReadMode mode = SlotReadMode.Recover)
        {
            if (slot == null)
            {
                throw new ArgumentNullException(nameof(slot));
            }

            SlotFileReadResult file = ReadFiles(directory, slot.Key, slot.SupportedSchema, mode);
            return CompleteLoad(slot, in file);
        }

        /// <summary>
        /// File step (any thread): primary, newer valid .tmp, then .bak; quarantines corrupt copies in Recover mode.
        /// IO errors return IoError and nothing is written; a too-new file is never touched.
        /// </summary>
        public SlotFileReadResult ReadFiles(string directory, string key, int supportedSchema, SlotReadMode mode)
        {
            if (mode != SlotReadMode.Recover)
            {
                return ReadCore(directory, key, supportedSchema, mode, false);
            }

            // Sampled before the reads: a write that lands while they run must not be rolled back in the guard
            PathWriteState state = GetWriteState(SaveLayout.SlotPath(directory, key));
            long sequence = state.ReadSequence();
            SlotFileReadResult file = ReadCore(directory, key, supportedSchema, mode, false);

            // A recovering read re-baselines the write guard to what the files really hold
            ObserveDurableRevision(state, sequence, in file);
            return file;
        }

        /// <summary>Materialize step (main thread): upgrade dispatch, then ToObject and Normalize exactly once inside try.</summary>
        public SlotLoadResult CompleteLoad(SaveSlot slot, in SlotFileReadResult file)
        {
            if (slot == null)
            {
                throw new ArgumentNullException(nameof(slot));
            }

            string key = slot.Key;
            switch (file.Status)
            {
                case SlotFileReadStatus.IoError:
                    _logger.Error("Slot '" + key + "' could not be read; it stays Failed and nothing is written. " + file.Message, file.Exception);
                    return CreateFailed(slot, in file, SlotFailure.IoError, SlotRecovery.None, 0, SlotMaterializeStage.None, file.Message, file.Exception);
                case SlotFileReadStatus.SchemaTooNew:
                    _logger.Warning(
                        "Slot '" + key + "' was saved by a newer build (fmt " + file.FoundFormat + ", schema " + file.FoundSchema
                        + ", supported schema " + slot.SupportedSchema + "); the file is left untouched.");
                    return CreateFailed(slot, in file, SlotFailure.SchemaTooNew, SlotRecovery.None, 0, SlotMaterializeStage.None, file.Message, null);
                case SlotFileReadStatus.Absent:
                    return FromDefault(slot, in file, SlotRecovery.None);
                case SlotFileReadStatus.CorruptNoValidCopy:
                    _logger.Error("Slot '" + key + "' has no valid local copy; defaults loaded and cloud recovery is required. " + file.Message);
                    return FromDefault(slot, in file, SlotRecovery.CorruptReset);
            }

            SaveEnvelope envelope = file.Envelope;
            if (envelope == null || envelope.Data == null)
            {
                throw new ArgumentException("A header-only read cannot be materialized.", nameof(file));
            }

            SlotRecovery recovery = GetRecovery(in file);
            int supportedSchema = slot.SupportedSchema;
            if (envelope.Schema > supportedSchema)
            {
                return CreateFailed(
                    slot, in file, SlotFailure.SchemaTooNew, SlotRecovery.None, envelope.Revision, SlotMaterializeStage.None,
                    "schema " + envelope.Schema + " > " + supportedSchema + ".", null);
            }

            JToken payload = envelope.Data;
            if (envelope.Schema < supportedSchema)
            {
                PayloadUpgradeResult upgrade = slot.RunUpgrade(payload, envelope.Schema);
                if (!upgrade.IsSuccess)
                {
                    _logger.Error("Slot '" + key + "' upgrade failed at schema " + upgrade.FailedFromSchema + ": " + upgrade.Message, upgrade.Exception);
                    return CreateFailed(
                        slot, in file, SlotFailure.NormalizeFailed, recovery, envelope.Revision, SlotMaterializeStage.Upgrade, upgrade.Message,
                        upgrade.Exception);
                }

                payload = upgrade.Payload;
            }

            SlotMaterializeResult materialized = slot.MaterializeNormalized(payload);
            if (!materialized.IsSuccess)
            {
                _logger.Error("Slot '" + key + "' failed to load (" + materialized.FailedStage + "): " + materialized.Message, materialized.Exception);
                return CreateFailed(
                    slot, in file, SlotFailure.NormalizeFailed, recovery, envelope.Revision, materialized.FailedStage, materialized.Message,
                    materialized.Exception);
            }

            if (recovery != SlotRecovery.None)
            {
                _logger.Warning("Slot '" + key + "' loaded with recovery " + recovery + " from " + file.Source + ".");
            }

            return new SlotLoadResult(in file, SlotFailure.None, recovery, materialized.Data, envelope.Revision, SlotMaterializeStage.None, null, null);
        }

        /// <summary>Read-only header peek for RequireSynced: presence, effective revision and write id. Never repairs.</summary>
        public SlotHeaderPeekResult PeekHeader(string directory, string key, int supportedSchema)
        {
            SlotFileReadResult file = ReadCore(directory, key, supportedSchema, SlotReadMode.ReadOnly, true);
            LocalPresence presence;
            switch (file.Status)
            {
                case SlotFileReadStatus.Found:
                    presence = file.PrimaryWasCorrupt ? LocalPresence.Undetermined : LocalPresence.Present;
                    break;
                case SlotFileReadStatus.Absent:
                    presence = LocalPresence.Absent;
                    break;
                default:
                    presence = LocalPresence.Undetermined;
                    break;
            }

            return new SlotHeaderPeekResult(in file, presence);
        }

        /// <summary>Encodes and writes atomically. Throws only on a null envelope or data (programmer error).</summary>
        public SlotWriteResult WriteEnvelope(string directory, string key, SaveEnvelope envelope)
        {
            if (envelope == null)
            {
                throw new ArgumentNullException(nameof(envelope));
            }

            EncodedEnvelope encoded = EnvelopeCodec.Encode(envelope);
            return WriteEncoded(directory, key, in encoded, envelope.Revision);
        }

        /// <summary>
        /// Writes already encoded envelope bytes atomically. Refused when the file on disk is too new for this build, or when
        /// a newer revision of the slot is already durable: the check and the write are one step per path, for every caller.
        /// Blocking: the calling thread holds the path gate across the file write and waits for another thread that holds it.
        /// </summary>
        public SlotWriteResult WriteEncoded(string directory, string key, in EncodedEnvelope encoded, long revision)
        {
            ValidateLocation(directory, key);
            if (encoded.Bytes == null)
            {
                throw new ArgumentException("Encoded bytes must not be null.", nameof(encoded));
            }

            string path = SaveLayout.SlotPath(directory, key);
            if (IsTooNewPath(path))
            {
                string message = "Write to '" + key + "' refused: the file on disk was saved by a newer build.";
                _logger.Warning(message);
                return SlotWriteResult.Refused(revision, message);
            }

            PathWriteState state = GetWriteState(path);
            lock (state.Gate)
            {
                // The attempted revision counts too: a write that failed while promoting leaves that revision in a valid .tmp
                long durable = state.GuardRevision;
                if (revision < durable)
                {
                    string message = "Write to '" + key + "' refused: revision " + revision + " is older than the durable revision " + durable + ".";
                    _logger.Warning(message);
                    return SlotWriteResult.RefusedStale(revision, durable, message);
                }

                state.BeginWrite(revision);
                try
                {
                    _storage.WriteAtomic(path, encoded.Bytes);
                }
                catch (Exception exception)
                {
                    state.AbortWrite();
                    return SlotWriteResult.Failed(revision, exception);
                }

                state.CompleteWrite(revision);
            }

            return SlotWriteResult.Written(revision, encoded.DataSha256, encoded.Bytes.Length);
        }

        /// <summary>Deletes .tmp, .bak, the primary, then the conflict file; stops at the first failure so .bak never resurrects data. Quarantine files are kept.</summary>
        public SlotFileOpResult DeleteSlotFiles(string directory, string key)
        {
            ValidateLocation(directory, key);
            string path = SaveLayout.SlotPath(directory, key);
            string[] ordered = { SaveLayout.TmpPath(path), SaveLayout.BakPath(path), path, SaveLayout.ConflictPath(directory, key) };
            for (int i = 0; i < ordered.Length; i++)
            {
                try
                {
                    _storage.DeleteFile(ordered[i]);
                }
                catch (Exception exception)
                {
                    return SlotFileOpResult.Failed(exception, "Deleting " + ordered[i] + " failed: " + exception.Message);
                }
            }

            ForgetTooNew(path);
            ForgetWrittenRevision(path);
            return SlotFileOpResult.Done(null);
        }

        /// <summary>Renames the primary to {key}.corrupt-{UTC}.json and prunes to the cap (row 9 local NormalizeFailed replaced by cloud).</summary>
        public SlotFileOpResult QuarantinePrimary(string directory, string key, CorruptionCause cause)
        {
            ValidateLocation(directory, key);
            string path = SaveLayout.SlotPath(directory, key);
            try
            {
                if (!_storage.FileExists(path))
                {
                    return SlotFileOpResult.Skipped(null, "No primary file.");
                }
            }
            catch (Exception exception)
            {
                return SlotFileOpResult.Failed(exception, "Checking " + path + " failed: " + exception.Message);
            }

            if (!TryQuarantine(directory, key, path, out string fileName, out Exception moveError))
            {
                return SlotFileOpResult.Failed(moveError, "Quarantining " + path + " failed: " + moveError.Message);
            }

            _logger.Warning("Slot '" + key + "' primary quarantined as " + fileName + " (" + cause + ").");
            ForgetTooNew(path);

            // The guard is kept: .tmp and .bak stay on disk and the load path still promotes a newer valid .tmp
            return SlotFileOpResult.Done(fileName);
        }

        /// <summary>Backs up raw corrupt cloud bytes to {key}.cloud-corrupt-{UTC}.json (cap 3); skipped when identical bytes are already backed up.</summary>
        public SlotFileOpResult WriteCloudCorruptBackup(string directory, string key, byte[] rawBytes)
        {
            ValidateLocation(directory, key);
            if (rawBytes == null)
            {
                throw new ArgumentNullException(nameof(rawBytes));
            }

            try
            {
                IReadOnlyList<string> names = _storage.ListFileNames(directory);
                for (int i = 0; i < names.Count; i++)
                {
                    if (!SaveLayout.IsBackupFileName(BackupFileFamily.CloudCorrupt, key, names[i]))
                    {
                        continue;
                    }

                    byte[] existing = _storage.ReadAllBytes(SaveLayout.Combine(directory, names[i]));
                    if (existing != null && BytesEqual(existing, rawBytes))
                    {
                        return SlotFileOpResult.Skipped(names[i], "Identical cloud value already backed up.");
                    }
                }

                string fileName = LocalBackupFiles.WriteBackup(_storage, directory, key, rawBytes, BackupFileFamily.CloudCorrupt, _clock.UtcNow);
                LocalBackupFiles.Prune(_storage, directory, key, BackupFileFamily.CloudCorrupt, SaveLayout.CloudCorruptCap, fileName, _logger);
                _logger.Warning("Slot '" + key + "' corrupt cloud value backed up as " + fileName + ".");
                return SlotFileOpResult.Done(fileName);
            }
            catch (Exception exception)
            {
                return SlotFileOpResult.Failed(exception, "Backing up corrupt cloud value of '" + key + "' failed: " + exception.Message);
            }
        }

        /// <summary>Writes {key}.conflict.json = {resolution, savedAtUtc, local, cloud}; one generation. Write it before swapping data.</summary>
        public SlotFileOpResult WriteConflictFile(string directory, string key, ConflictResolutionKind resolution, JToken local, JToken cloud)
        {
            ValidateLocation(directory, key);
            string path = SaveLayout.ConflictPath(directory, key);
            try
            {
                DateTime now = _clock.UtcNow;
                DateTime utc = now.Kind == DateTimeKind.Local ? now.ToUniversalTime() : now;
                var document = new JObject
                {
                    { ResolutionProperty, resolution.ToString() },
                    { SavedAtUtcProperty, utc.ToString(ConflictTimestampFormat, CultureInfo.InvariantCulture) },
                    { LocalProperty, local ?? JValue.CreateNull() },
                    { CloudProperty, cloud ?? JValue.CreateNull() },
                };

                _storage.WriteAtomic(path, SaveJson.ToUtf8Bytes(document));
            }
            catch (Exception exception)
            {
                return SlotFileOpResult.Failed(exception, "Writing conflict file for '" + key + "' failed: " + exception.Message);
            }

            // One generation: drop the rotated previous copy
            try
            {
                _storage.DeleteFile(SaveLayout.BakPath(path));
            }
            catch (Exception exception)
            {
                _logger.Warning("Removing previous conflict copy of '" + key + "' failed: " + exception.Message);
            }

            return SlotFileOpResult.Done(key + SaveLayout.ConflictSuffix);
        }

        /// <summary>True when a load found this slot's file too new for this build (in-memory knowledge only).</summary>
        public bool IsKnownSchemaTooNew(string directory, string key)
        {
            ValidateLocation(directory, key);
            return IsTooNewPath(SaveLayout.SlotPath(directory, key));
        }

        private SlotFileReadResult ReadCore(string directory, string key, int supportedSchema, SlotReadMode mode, bool headerOnly)
        {
            ValidateLocation(directory, key);
            bool recover = mode == SlotReadMode.Recover;
            string path = SaveLayout.SlotPath(directory, key);
            string tmpPath = SaveLayout.TmpPath(path);
            string bakPath = SaveLayout.BakPath(path);

            if (!TryReadBytes(path, out byte[] primaryBytes, out Exception readError))
            {
                return SlotFileReadResult.CreateIoError(readError, "Reading " + path + " failed: " + readError.Message, false, CorruptionCause.None, null);
            }

            EnvelopeDecodeResult primary = default;
            if (primaryBytes != null)
            {
                primary = DecodeBytes(primaryBytes, supportedSchema, headerOnly);
                if (primary.IsTooNew)
                {
                    RememberTooNew(path);
                    return SlotFileReadResult.CreateTooNew(SlotFileSource.Primary, in primary, false, CorruptionCause.None, null);
                }
            }

            if (!TryReadBytes(tmpPath, out byte[] tmpBytes, out readError))
            {
                return SlotFileReadResult.CreateIoError(readError, "Reading " + tmpPath + " failed: " + readError.Message, false, CorruptionCause.None, null);
            }

            bool primaryDecodeFailed = primaryBytes != null && !primary.IsOk;
            EnvelopeDecodeResult tmp = default;
            bool tmpUsable = false;
            if (tmpBytes != null)
            {
                tmp = DecodeBytes(tmpBytes, supportedSchema, headerOnly);

                // A tmp from a newer build binds the whole path: writing the primary would destroy it
                if (tmp.IsTooNew)
                {
                    RememberTooNew(path);
                    return SlotFileReadResult.CreateTooNew(
                        SlotFileSource.Tmp, in tmp, primaryDecodeFailed, primaryDecodeFailed ? primary.Cause : CorruptionCause.None, null);
                }

                tmpUsable = tmp.IsOk && tmp.Checksum == ChecksumStatus.Verified;
            }

            if (primaryBytes != null && primary.IsOk)
            {
                ForgetTooNew(path);
                if (tmpUsable && tmp.Envelope.Revision > primary.Envelope.Revision)
                {
                    bool promoteFailed = recover && !TryPromoteTmpOverPrimary(key, path, tmpPath, bakPath);
                    return SlotFileReadResult.CreateFound(SlotFileSource.Tmp, in tmp, false, CorruptionCause.None, null, null, promoteFailed);
                }

                WarnUnverifiedChecksum(key, path, primary.Checksum);
                return SlotFileReadResult.CreateFound(SlotFileSource.Primary, in primary, false, CorruptionCause.None, null, null, false);
            }

            bool primaryCorrupt = primaryBytes != null;
            CorruptionCause cause = primaryCorrupt ? primary.Cause : CorruptionCause.None;
            string corruptMessage = primaryCorrupt ? primary.Message : null;
            string quarantine = null;

            // An unverifiable tmp is kept whenever a repair is about to write over it
            bool tmpNeedsQuarantine = recover && tmpBytes != null && !tmpUsable;
            if (primaryCorrupt)
            {
                _logger.Warning("Slot '" + key + "': " + path + " is corrupt (" + cause + "): " + primary.Message);
                if (recover && !TryQuarantine(directory, key, path, out quarantine, out Exception moveError))
                {
                    return SlotFileReadResult.CreateIoError(moveError, "Quarantining " + path + " failed: " + moveError.Message, true, cause, null);
                }

                if (tmpNeedsQuarantine && !TryQuarantineTmp(directory, key, tmpPath, in tmp, ref quarantine, out Exception tmpMoveError))
                {
                    return SlotFileReadResult.CreateIoError(
                        tmpMoveError, "Quarantining " + tmpPath + " failed: " + tmpMoveError.Message, true, cause, quarantine);
                }

                tmpNeedsQuarantine = false;
            }

            if (tmpUsable)
            {
                bool promoteFailed = recover && !TryMove(key, tmpPath, path);
                return SlotFileReadResult.CreateFound(SlotFileSource.Tmp, in tmp, primaryCorrupt, cause, quarantine, corruptMessage, promoteFailed);
            }

            if (!TryReadBytes(bakPath, out byte[] bakBytes, out readError))
            {
                return SlotFileReadResult.CreateIoError(readError, "Reading " + bakPath + " failed: " + readError.Message, primaryCorrupt, cause, quarantine);
            }

            if (bakBytes == null)
            {
                return primaryCorrupt
                    ? SlotFileReadResult.CreateCorruptNoValidCopy(true, cause, quarantine, corruptMessage)
                    : SlotFileReadResult.CreateAbsent();
            }

            EnvelopeDecodeResult bak = DecodeBytes(bakBytes, supportedSchema, headerOnly);
            if (bak.IsTooNew)
            {
                RememberTooNew(path);
                return SlotFileReadResult.CreateTooNew(SlotFileSource.Backup, in bak, primaryCorrupt, cause, quarantine);
            }

            if (bak.IsOk)
            {
                WarnUnverifiedChecksum(key, bakPath, bak.Checksum);

                // The rewrite below writes through {path}.tmp, so the unusable tmp is saved first
                if (tmpNeedsQuarantine && !TryQuarantineTmp(directory, key, tmpPath, in tmp, ref quarantine, out Exception tmpMoveError))
                {
                    return SlotFileReadResult.CreateIoError(
                        tmpMoveError, "Quarantining " + tmpPath + " failed: " + tmpMoveError.Message, primaryCorrupt, cause, quarantine);
                }

                bool rewriteFailed = recover && !TryRewritePrimary(key, path, bakBytes);
                return SlotFileReadResult.CreateFound(SlotFileSource.Backup, in bak, primaryCorrupt, cause, quarantine, corruptMessage, rewriteFailed);
            }

            // A previous generation existed but is unreadable: content may have existed
            _logger.Warning("Slot '" + key + "': " + bakPath + " is corrupt (" + bak.Cause + "): " + bak.Message);
            if (recover)
            {
                if (!TryQuarantine(directory, key, bakPath, out string bakQuarantine, out Exception bakMoveError))
                {
                    return SlotFileReadResult.CreateIoError(
                        bakMoveError, "Quarantining " + bakPath + " failed: " + bakMoveError.Message, primaryCorrupt, primaryCorrupt ? cause : bak.Cause,
                        quarantine);
                }

                quarantine = quarantine ?? bakQuarantine;
            }

            if (!primaryCorrupt)
            {
                cause = bak.Cause;
                corruptMessage = bak.Message;
            }

            return SlotFileReadResult.CreateCorruptNoValidCopy(primaryCorrupt, cause, quarantine, corruptMessage);
        }

        private static EnvelopeDecodeResult DecodeBytes(byte[] bytes, int supportedSchema, bool headerOnly)
        {
            return headerOnly ? EnvelopeCodec.PeekHeader(bytes, supportedSchema, true) : EnvelopeCodec.Decode(bytes, supportedSchema);
        }

        private static SlotRecovery GetRecovery(in SlotFileReadResult file)
        {
            switch (file.Source)
            {
                case SlotFileSource.Tmp:
                    return file.PrimaryWasCorrupt ? SlotRecovery.CorruptRecoveredFromTmp : SlotRecovery.PromotedTmp;
                case SlotFileSource.Backup:
                    return file.PrimaryWasCorrupt ? SlotRecovery.CorruptRecoveredFromBackup : SlotRecovery.PromotedBackup;
                default:
                    return SlotRecovery.None;
            }
        }

        private static SlotLoadResult CreateFailed(
            SaveSlot slot,
            in SlotFileReadResult file,
            SlotFailure failure,
            SlotRecovery recovery,
            long revision,
            SlotMaterializeStage stage,
            string message,
            Exception exception)
        {
            return new SlotLoadResult(in file, failure, recovery, slot.CreateFallbackDefault(), revision, stage, message, exception);
        }

        private static void ValidateLocation(string directory, string key)
        {
            if (string.IsNullOrEmpty(directory))
            {
                throw new ArgumentException("Directory must not be null or empty.", nameof(directory));
            }

            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentException("Key must not be null or empty.", nameof(key));
            }
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

        private SlotLoadResult FromDefault(SaveSlot slot, in SlotFileReadResult file, SlotRecovery recovery)
        {
            SlotMaterializeResult created = slot.CreateNormalizedDefault();
            if (created.IsSuccess)
            {
                return new SlotLoadResult(in file, SlotFailure.None, recovery, created.Data, 0, SlotMaterializeStage.None, file.Message, null);
            }

            _logger.Error("Slot '" + slot.Key + "' default could not be created (" + created.FailedStage + "): " + created.Message, created.Exception);
            return CreateFailed(slot, in file, SlotFailure.NormalizeFailed, recovery, 0, created.FailedStage, created.Message, created.Exception);
        }

        private bool TryReadBytes(string path, out byte[] bytes, out Exception exception)
        {
            try
            {
                bytes = _storage.ReadAllBytes(path);
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

        private bool TryQuarantine(string directory, string key, string path, out string fileName, out Exception exception)
        {
            try
            {
                fileName = LocalBackupFiles.MoveToBackup(_storage, directory, key, path, BackupFileFamily.LocalCorrupt, _clock.UtcNow);
                exception = null;
            }
            catch (Exception caught)
            {
                fileName = null;
                exception = caught;
                return false;
            }

            LocalBackupFiles.Prune(_storage, directory, key, BackupFileFamily.LocalCorrupt, SaveLayout.QuarantineCap, fileName, _logger);
            return true;
        }

        // Keeps an unverifiable .tmp as a quarantine copy; the first name found is the one the load issue reports
        private bool TryQuarantineTmp(string directory, string key, string tmpPath, in EnvelopeDecodeResult tmp, ref string quarantine, out Exception exception)
        {
            if (!TryQuarantine(directory, key, tmpPath, out string fileName, out exception))
            {
                return false;
            }

            string reason = tmp.IsOk ? "checksum " + tmp.Checksum : tmp.Cause.ToString();
            _logger.Warning("Slot '" + key + "': " + tmpPath + " could not be verified (" + reason + "); quarantined as " + fileName + ".");
            quarantine = quarantine ?? fileName;
            return true;
        }

        // Rename sequence keeps every intermediate state recoverable
        private bool TryPromoteTmpOverPrimary(string key, string path, string tmpPath, string bakPath)
        {
            try
            {
                _storage.DeleteFile(bakPath);
                _storage.MoveFile(path, bakPath);
                _storage.MoveFile(tmpPath, path);
                return true;
            }
            catch (Exception exception)
            {
                _logger.Warning("Slot '" + key + "': promoting newer " + tmpPath + " failed; loaded it in memory. " + exception.Message);
                return false;
            }
        }

        private bool TryMove(string key, string sourcePath, string destinationPath)
        {
            try
            {
                _storage.MoveFile(sourcePath, destinationPath);
                return true;
            }
            catch (Exception exception)
            {
                _logger.Warning("Slot '" + key + "': promoting " + sourcePath + " failed; loaded it in memory. " + exception.Message);
                return false;
            }
        }

        private bool TryRewritePrimary(string key, string path, byte[] bytes)
        {
            try
            {
                _storage.WriteAtomic(path, bytes);
                return true;
            }
            catch (Exception exception)
            {
                _logger.Warning("Slot '" + key + "': rewriting " + path + " from .bak failed; loaded it in memory. " + exception.Message);
                return false;
            }
        }

        private void WarnUnverifiedChecksum(string key, string path, ChecksumStatus checksum)
        {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            if (checksum == ChecksumStatus.NotPresent)
            {
                _logger.Warning("Slot '" + key + "': " + path + " has no dataSha256; accepted without an integrity check.");
            }
            else if (checksum == ChecksumStatus.Unverifiable)
            {
                _logger.Warning("Slot '" + key + "': dataSha256 of " + path + " cannot be verified (hand-edited or pretty-printed); accepted.");
            }
#endif
        }

        private bool IsTooNewPath(string path)
        {
            lock (_tooNewLock)
            {
                return _tooNewPaths.Contains(path);
            }
        }

        private void RememberTooNew(string path)
        {
            lock (_tooNewLock)
            {
                _tooNewPaths.Add(path);
            }
        }

        private void ForgetTooNew(string path)
        {
            lock (_tooNewLock)
            {
                _tooNewPaths.Remove(path);
            }
        }

        private PathWriteState GetWriteState(string path)
        {
            lock (_writeStatesLock)
            {
                if (!_writeStates.TryGetValue(path, out PathWriteState state))
                {
                    state = new PathWriteState();
                    _writeStates.Add(path, state);
                }

                return state;
            }
        }

        // Sets the guard to the revision the files really hold; skipped when a write touched the path during the read
        private static void ObserveDurableRevision(PathWriteState state, long sequence, in SlotFileReadResult file)
        {
            long observed;
            switch (file.Status)
            {
                case SlotFileReadStatus.Found:
                    observed = file.Revision;
                    break;
                case SlotFileReadStatus.Absent:
                case SlotFileReadStatus.CorruptNoValidCopy:
                    observed = 0;
                    break;
                default:
                    return;
            }

            lock (state.Gate)
            {
                // No write runs while the gate is held, so an unchanged sequence means none overlapped the read either
                if (state.ReadSequence() == sequence)
                {
                    state.Observe(observed);
                }
            }
        }

        // Every copy of the file is gone: nothing is left to protect from an older revision
        private void ForgetWrittenRevision(string path)
        {
            PathWriteState state = GetWriteState(path);
            lock (state.Gate)
            {
                state.Observe(0);
            }
        }

        /// <summary>Write gate of one slot path plus the newest revision written or attempted through it.</summary>
        private sealed class PathWriteState
        {
            public readonly object Gate = new object();

            // A failed promote leaves the attempted revision in a valid .tmp, so it guards writes just like a written one
            private long _lastWrittenRevision;
            private long _lastAttemptedRevision;

            // Odd while a write runs; bumped on entry and exit so any overlapping read is detectable
            private long _sequence;

            /// <summary>Newest revision this store put on disk or handed to the storage. Read under Gate.</summary>
            public long GuardRevision => Math.Max(_lastWrittenRevision, _lastAttemptedRevision);

            /// <summary>Lock-free sample of the write sequence; odd means a write is in progress.</summary>
            public long ReadSequence()
            {
                return Volatile.Read(ref _sequence);
            }

            /// <summary>Under Gate, before the storage write.</summary>
            public void BeginWrite(long revision)
            {
                if (revision > _lastAttemptedRevision)
                {
                    _lastAttemptedRevision = revision;
                }

                Interlocked.Increment(ref _sequence);
            }

            /// <summary>Under Gate, after the storage write returned.</summary>
            public void CompleteWrite(long revision)
            {
                if (revision > _lastWrittenRevision)
                {
                    _lastWrittenRevision = revision;
                }

                Interlocked.Increment(ref _sequence);
            }

            /// <summary>Under Gate, after the storage write threw.</summary>
            public void AbortWrite()
            {
                Interlocked.Increment(ref _sequence);
            }

            /// <summary>Under Gate: re-baselines both revisions to what the files hold.</summary>
            public void Observe(long revision)
            {
                _lastWrittenRevision = revision;
                _lastAttemptedRevision = revision;
            }
        }
    }

    /// <summary>Capped backup files next to slot and state files (quarantine, cloud-corrupt).</summary>
    internal static class LocalBackupFiles
    {
        private const int MaxNameAttempts = 16;

        /// <summary>Renames sourcePath to a free backup name in directory and returns the file name. Throws storage exceptions.</summary>
        public static string MoveToBackup(ISaveStorage storage, string directory, string key, string sourcePath, BackupFileFamily family, DateTime utcNow)
        {
            for (int attempt = 0; attempt < MaxNameAttempts; attempt++)
            {
                string fileName = SaveLayout.BackupFileName(family, key, utcNow, attempt);
                string destination = SaveLayout.Combine(directory, fileName);
                if (storage.FileExists(destination))
                {
                    continue;
                }

                storage.MoveFile(sourcePath, destination);
                return fileName;
            }

            throw new IOException("No free " + family + " backup name for '" + key + "'.");
        }

        /// <summary>Writes bytes under a free backup name and returns the file name. Throws storage exceptions.</summary>
        public static string WriteBackup(ISaveStorage storage, string directory, string key, byte[] bytes, BackupFileFamily family, DateTime utcNow)
        {
            for (int attempt = 0; attempt < MaxNameAttempts; attempt++)
            {
                string fileName = SaveLayout.BackupFileName(family, key, utcNow, attempt);
                string destination = SaveLayout.Combine(directory, fileName);
                if (storage.FileExists(destination))
                {
                    continue;
                }

                storage.WriteAtomic(destination, bytes);
                return fileName;
            }

            throw new IOException("No free " + family + " backup name for '" + key + "'.");
        }

        /// <summary>Deletes the oldest backups beyond keep, never keepFileName; failures are logged, never thrown.</summary>
        public static void Prune(ISaveStorage storage, string directory, string key, BackupFileFamily family, int keep, string keepFileName, ISaveLogger logger)
        {
            try
            {
                IReadOnlyList<string> prune = SaveLayout.SelectBackupsToPrune(family, key, storage.ListFileNames(directory), keep, keepFileName);
                for (int i = 0; i < prune.Count; i++)
                {
                    storage.DeleteFile(SaveLayout.Combine(directory, prune[i]));
                }
            }
            catch (Exception exception)
            {
                logger?.Warning("Pruning " + family + " backups of '" + key + "' failed: " + exception.Message);
            }
        }
    }
}
