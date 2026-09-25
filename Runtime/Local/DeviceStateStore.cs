using System;
using Newtonsoft.Json.Linq;

namespace Ecanakli.SaveSystem
{
    /// <summary>device.json model: diagnostic device id and the last active profile pointer. Main thread only.</summary>
    internal sealed class DeviceState
    {
        /// <summary>Document version written by this build.</summary>
        public const int CurrentVersion = 1;

        internal const string DeviceIdProperty = "deviceId";
        internal const string LastActiveProfileProperty = "lastActiveProfile";
        internal const string KindProperty = "kind";
        internal const string AccountIdProperty = "accountId";
        internal const string LocalNameProperty = "localName";
        internal const string GuestKind = "guest";
        internal const string AccountKind = "account";
        internal const string LocalKind = "local";

        /// <summary>Diagnostics only; null until the service assigns one.</summary>
        public string DeviceId { get; set; }

        /// <summary>Profile to activate at init; null means Guest.</summary>
        public ProfileId? LastActiveProfile { get; set; }

        /// <summary>Version found on disk; CurrentVersion for new states.</summary>
        public int LoadedVersion { get; private set; } = CurrentVersion;

        internal JObject ToJson()
        {
            var root = new JObject();
            root[StateFileReader.VersionProperty] = CurrentVersion;
            StateJson.WriteString(root, DeviceIdProperty, DeviceId);
            if (LastActiveProfile.HasValue)
            {
                root[LastActiveProfileProperty] = ProfileToJson(LastActiveProfile.Value);
            }

            return root;
        }

        /// <summary>Strict for known versions (throws FormatException); newer versions load known fields leniently.</summary>
        internal static DeviceState FromJson(JObject root, int version)
        {
            bool strict = version <= CurrentVersion;
            var state = new DeviceState
            {
                LoadedVersion = version,
                DeviceId = StateJson.ReadString(root, DeviceIdProperty, strict),
            };

            JObject lastActive = StateJson.ReadObject(root, LastActiveProfileProperty, strict);
            if (lastActive != null)
            {
                state.LastActiveProfile = ProfileFromJson(lastActive, strict);
            }

            return state;
        }

        private static JObject ProfileToJson(ProfileId profile)
        {
            var obj = new JObject();
            switch (profile.Kind)
            {
                case ProfileKind.Account:
                    obj[KindProperty] = AccountKind;
                    obj[AccountIdProperty] = profile.AccountId;
                    break;
                case ProfileKind.Local:
                    obj[KindProperty] = LocalKind;
                    obj[LocalNameProperty] = profile.LocalName;
                    break;
                default:
                    obj[KindProperty] = GuestKind;
                    break;
            }

            return obj;
        }

        private static ProfileId? ProfileFromJson(JObject obj, bool strict)
        {
            string kind = StateJson.ReadString(obj, KindProperty, strict);
            switch (kind)
            {
                case GuestKind:
                    return ProfileId.Guest;
                case AccountKind:
                    string accountId = StateJson.ReadString(obj, AccountIdProperty, strict);
                    if (!string.IsNullOrEmpty(accountId))
                    {
                        return ProfileId.Account(accountId);
                    }

                    break;
                case LocalKind:
                    string name = StateJson.ReadString(obj, LocalNameProperty, strict);
                    if (ProfileId.IsValidLocalName(name))
                    {
                        return ProfileId.Local(name);
                    }

                    break;
            }

            if (strict)
            {
                throw new FormatException(LastActiveProfileProperty + " is invalid.");
            }

            return null;
        }
    }

    internal enum DeviceStateLoadStatus
    {
        Missing = 0,
        Loaded = 1,

        /// <summary>Written by a newer build; known fields loaded.</summary>
        NewerVersion = 2,

        /// <summary>Primary missing or corrupt; .tmp or .bak used.</summary>
        Recovered = 3,

        /// <summary>No readable copy; default state.</summary>
        Corrupt = 4,

        /// <summary>Read failed; default state. Do not overwrite until a read succeeds.</summary>
        IoError = 5,
    }

    /// <summary>Result of loading device.json; State is never null.</summary>
    internal readonly struct DeviceStateLoadResult
    {
        internal DeviceStateLoadResult(
            DeviceStateLoadStatus status,
            DeviceState state,
            string quarantineFileName,
            bool newerVersionUnbacked,
            LocalWriteErrorKind errorKind,
            string message,
            Exception exception)
        {
            Status = status;
            State = state;
            QuarantineFileName = quarantineFileName;
            NewerVersionUnbacked = newerVersionUnbacked;
            ErrorKind = errorKind;
            Message = message;
            Exception = exception;
        }

        public DeviceStateLoadStatus Status { get; }

        public DeviceState State { get; }

        public string QuarantineFileName { get; }

        /// <summary>A newer-version device.json could not be quarantined; overwriting it would destroy it without a copy.</summary>
        public bool NewerVersionUnbacked { get; }

        public LocalWriteErrorKind ErrorKind { get; }

        public string Message { get; }

        public Exception Exception { get; }

        /// <summary>False after an IoError or a failed newer-version backup: writing now could replace data that has no copy.</summary>
        public bool CanOverwrite => Status != DeviceStateLoadStatus.IoError && !NewerVersionUnbacked;
    }

    /// <summary>Reads and atomically writes device/device.json.</summary>
    internal sealed class DeviceStateStore
    {
        private const string BackupKey = "device";

        private readonly ISaveStorage _storage;
        private readonly ISaveLogger _logger;
        private readonly ISaveClock _clock;

        public DeviceStateStore(ISaveStorage storage, ISaveLogger logger, ISaveClock clock)
        {
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        /// <summary>Encodes a state to compact UTF-8 (main thread).</summary>
        public static byte[] Encode(DeviceState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            return SaveJson.ToUtf8Bytes(state.ToJson());
        }

        /// <summary>Loads device.json; Recover quarantines a corrupt primary, ReadOnly never touches files.</summary>
        public DeviceStateLoadResult Load(SlotReadMode mode = SlotReadMode.Recover)
        {
            StateFileReadResult<DeviceState> read = StateFileReader.Read<DeviceState>(
                _storage, SaveLayout.DeviceDirectory, SaveLayout.DeviceStateFileName, BackupKey, mode == SlotReadMode.Recover, DeviceState.FromJson,
                _clock, _logger, DeviceState.CurrentVersion);

            switch (read.Status)
            {
                case StateFileReadStatus.Missing:
                    return Create(DeviceStateLoadStatus.Missing, new DeviceState(), in read);
                case StateFileReadStatus.IoError:
                    _logger.Error("device.json could not be read. " + read.Message, read.Exception);
                    return Create(DeviceStateLoadStatus.IoError, new DeviceState(), in read);
                case StateFileReadStatus.Corrupt:
                    _logger.Warning("device.json is corrupt; using a new device state. " + read.Message);
                    return Create(DeviceStateLoadStatus.Corrupt, new DeviceState(), in read);
                case StateFileReadStatus.RecoveredFromTmp:
                case StateFileReadStatus.RecoveredFromBackup:
                    // A recovered copy from a newer build is classified by its version, not by the copy it came from
                    return Create(IsNewerVersion(in read) ? DeviceStateLoadStatus.NewerVersion : DeviceStateLoadStatus.Recovered, read.Model, in read);
                default:
                    return Create(IsNewerVersion(in read) ? DeviceStateLoadStatus.NewerVersion : DeviceStateLoadStatus.Loaded, read.Model, in read);
            }
        }

        /// <summary>Encodes and writes atomically; never throws on storage failure.</summary>
        public StateWriteResult Save(DeviceState state)
        {
            return SaveEncoded(Encode(state));
        }

        /// <summary>Writes encoded bytes atomically (any thread).</summary>
        public StateWriteResult SaveEncoded(byte[] bytes)
        {
            return StateFileReader.Write(_storage, SaveLayout.DeviceStatePath, bytes);
        }

        private static bool IsNewerVersion(in StateFileReadResult<DeviceState> read)
        {
            return read.Version > DeviceState.CurrentVersion;
        }

        private static DeviceStateLoadResult Create(DeviceStateLoadStatus status, DeviceState state, in StateFileReadResult<DeviceState> read)
        {
            return new DeviceStateLoadResult(
                status, state, read.QuarantineFileName, read.NewerVersionUnbacked, read.ErrorKind, read.Message, read.Exception);
        }
    }
}
