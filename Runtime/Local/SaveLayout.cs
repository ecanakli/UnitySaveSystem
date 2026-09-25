using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Ecanakli.SaveSystem
{
    /// <summary>Separate capped backup families next to slot files.</summary>
    internal enum BackupFileFamily
    {
        /// <summary>{key}.corrupt-{UTC}.json, local quarantine.</summary>
        LocalCorrupt = 0,

        /// <summary>{key}.cloud-corrupt-{UTC}.json, raw corrupt cloud bytes.</summary>
        CloudCorrupt = 1,

        /// <summary>{key}.newer-{UTC}.json, a document written by a newer build of this package.</summary>
        NewerVersion = 2,
    }

    /// <summary>All on-disk path rules; every path is relative to the save root and '/'-separated.</summary>
    internal static class SaveLayout
    {
        public const string DeviceDirectory = "device";
        public const string ProfilesDirectory = "profiles";
        public const string GuestDirectoryName = "guest";
        public const string AccountDirectoryPrefix = "acc-";
        public const string LocalDirectoryPrefix = "local-";
        public const string DeviceStateFileName = "device.json";
        public const string ProfileStateFileName = "profile.json";
        public const string DeviceStatePath = DeviceDirectory + "/" + DeviceStateFileName;
        public const string JsonExtension = ".json";
        public const string TmpSuffix = ".tmp";
        public const string BakSuffix = ".bak";
        public const string ConflictSuffix = ".conflict.json";
        public const string CorruptMarker = ".corrupt-";
        public const string CloudCorruptMarker = ".cloud-corrupt-";

        public const string NewerVersionMarker = ".newer-";

        /// <summary>Newest local quarantine files kept per slot.</summary>
        public const int QuarantineCap = 3;

        /// <summary>Newest cloud-corrupt backups kept per slot.</summary>
        public const int CloudCorruptCap = 3;

        /// <summary>Kept separately from QuarantineCap: a newer build's document must not evict corruption evidence.</summary>
        public const int NewerVersionCap = 3;

        /// <summary>Longest account id used verbatim in a directory name.</summary>
        public const int MaxRawAccountIdLength = 64;

        /// <summary>Hex characters of SHA-256 used for unsafe account ids.</summary>
        public const int HashedAccountIdLength = 32;

        private const string TimestampFormat = "yyyyMMdd'T'HHmmss'Z'";
        private const int TimestampLength = 16;

        /// <summary>com1-com9 and lpt1-lpt9.</summary>
        private const int WindowsNumberedDeviceLength = 4;

        private static readonly string[] WindowsDeviceNames = { "con", "prn", "aux", "nul" };

        /// <summary>Joins a relative directory and a name with '/'.</summary>
        public static string Combine(string directory, string name)
        {
            return string.IsNullOrEmpty(directory) ? name : directory + "/" + name;
        }

        /// <summary>profiles/guest, profiles/acc-{id} (collision index 0) or profiles/local-{name}.</summary>
        public static string ProfileDirectory(ProfileId profile)
        {
            return Combine(ProfilesDirectory, ProfileDirectoryName(profile));
        }

        /// <summary>guest, acc-{id} (collision index 0) or local-{name}.</summary>
        public static string ProfileDirectoryName(ProfileId profile)
        {
            switch (profile.Kind)
            {
                case ProfileKind.Account:
                    return AccountDirectoryName(profile.AccountId, 0);
                case ProfileKind.Local:
                    return LocalDirectoryPrefix + profile.LocalName;
                default:
                    return GuestDirectoryName;
            }
        }

        /// <summary>profiles/acc-{sanitized}[.{collisionIndex}]; used when profile.json names a different owner.</summary>
        public static string AccountDirectory(string accountId, int collisionIndex)
        {
            return Combine(ProfilesDirectory, AccountDirectoryName(accountId, collisionIndex));
        }

        /// <summary>acc-{id} when the id is safe, else acc-{32 hex of SHA-256}; ".{n}" appended for n &gt; 0 ('.' never occurs in the base name).</summary>
        public static string AccountDirectoryName(string accountId, int collisionIndex)
        {
            if (string.IsNullOrEmpty(accountId))
            {
                throw new ArgumentException("Account id must not be null or empty.", nameof(accountId));
            }

            if (collisionIndex < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(collisionIndex), "Must not be negative.");
            }

            string sanitized = IsSafeAccountId(accountId)
                ? accountId
                : PayloadChecksum.Compute(Encoding.UTF8.GetBytes(accountId)).Substring(0, HashedAccountIdLength);
            string name = AccountDirectoryPrefix + sanitized;
            return collisionIndex == 0 ? name : name + "." + collisionIndex.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>True when the id matches ^[A-Za-z0-9_-]{1,64}$ and can be used verbatim.</summary>
        public static bool IsSafeAccountId(string accountId)
        {
            if (string.IsNullOrEmpty(accountId) || accountId.Length > MaxRawAccountIdLength)
            {
                return false;
            }

            for (int i = 0; i < accountId.Length; i++)
            {
                char c = accountId[i];
                bool safe = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-';
                if (!safe)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>device for Device scope, otherwise the given profile directory.</summary>
        public static string SlotDirectory(SlotScope scope, string profileDirectory)
        {
            return scope == SlotScope.Device ? DeviceDirectory : profileDirectory;
        }

        /// <summary>{profileDirectory}/profile.json.</summary>
        public static string ProfileStatePath(string profileDirectory)
        {
            return Combine(profileDirectory, ProfileStateFileName);
        }

        /// <summary>{directory}/{key}.json.</summary>
        public static string SlotPath(string directory, string key)
        {
            return Combine(directory, key + JsonExtension);
        }

        /// <summary>{path}.tmp; matches AtomicFileStorage.</summary>
        public static string TmpPath(string path)
        {
            return path + TmpSuffix;
        }

        /// <summary>{path}.bak; matches AtomicFileStorage.</summary>
        public static string BakPath(string path)
        {
            return path + BakSuffix;
        }

        /// <summary>{directory}/{key}.conflict.json.</summary>
        public static string ConflictPath(string directory, string key)
        {
            return Combine(directory, key + ConflictSuffix);
        }

        /// <summary>Files removed by a slot delete: json, tmp, bak and conflict. Quarantine files are kept.</summary>
        public static string[] SlotFilePaths(string directory, string key)
        {
            string path = SlotPath(directory, key);
            return new[] { path, TmpPath(path), BakPath(path), ConflictPath(directory, key) };
        }

        /// <summary>
        /// True for keys whose file would collide with device.json or profile.json, or whose file name Win32 resolves to a
        /// device (con.json is the console, not a file). Case-insensitive.
        /// </summary>
        public static bool IsReservedSlotKey(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return false;
            }

            return string.Equals(key, "device", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "profile", StringComparison.OrdinalIgnoreCase)
                || IsWindowsDeviceName(key);
        }

        /// <summary>True for con, prn, aux, nul, com1-com9 and lpt1-lpt9; Win32 resolves them whatever the extension is.</summary>
        public static bool IsWindowsDeviceName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            for (int i = 0; i < WindowsDeviceNames.Length; i++)
            {
                if (string.Equals(name, WindowsDeviceNames[i], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            if (name.Length != WindowsNumberedDeviceLength)
            {
                return false;
            }

            char digit = name[WindowsNumberedDeviceLength - 1];
            if (digit < '1' || digit > '9')
            {
                return false;
            }

            return name.StartsWith("com", StringComparison.OrdinalIgnoreCase) || name.StartsWith("lpt", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>{key}.corrupt-{yyyyMMddTHHmmssZ}[-{attempt}].json; attempt &gt; 0 resolves a same-second name clash.</summary>
        public static string QuarantineFileName(string key, DateTime utcNow, int attempt = 0)
        {
            return BackupFileName(BackupFileFamily.LocalCorrupt, key, utcNow, attempt);
        }

        /// <summary>{key}.newer-{yyyyMMddTHHmmssZ}[-{attempt}].json, kept apart from the corruption quota.</summary>
        public static string NewerVersionFileName(string key, DateTime utcNow, int attempt = 0)
        {
            return BackupFileName(BackupFileFamily.NewerVersion, key, utcNow, attempt);
        }

        /// <summary>{key}.cloud-corrupt-{yyyyMMddTHHmmssZ}[-{attempt}].json.</summary>
        public static string CloudCorruptFileName(string key, DateTime utcNow, int attempt = 0)
        {
            return BackupFileName(BackupFileFamily.CloudCorrupt, key, utcNow, attempt);
        }

        /// <summary>Backup file name for a family.</summary>
        public static string BackupFileName(BackupFileFamily family, string key, DateTime utcNow, int attempt)
        {
            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentException("Key must not be null or empty.", nameof(key));
            }

            if (attempt < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(attempt), "Must not be negative.");
            }

            DateTime utc = utcNow.Kind == DateTimeKind.Local ? utcNow.ToUniversalTime() : utcNow;
            string name = key + Marker(family) + utc.ToString(TimestampFormat, CultureInfo.InvariantCulture);
            if (attempt > 0)
            {
                name += "-" + attempt.ToString(CultureInfo.InvariantCulture);
            }

            return name + JsonExtension;
        }

        /// <summary>True when fileName is a backup of that family for key.</summary>
        public static bool IsBackupFileName(BackupFileFamily family, string key, string fileName)
        {
            return TryParseBackupFileName(family, key, fileName, out _, out _);
        }

        /// <summary>Oldest backup names beyond keep, oldest first. Unrecognized names and keepFileName are never selected.</summary>
        public static IReadOnlyList<string> SelectBackupsToPrune(BackupFileFamily family, string key, IEnumerable<string> fileNames, int keep, string keepFileName)
        {
            if (keep < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(keep), "Must not be negative.");
            }

            if (fileNames == null)
            {
                return Array.Empty<string>();
            }

            var backups = new List<BackupName>();
            bool keptPresent = false;
            foreach (string fileName in fileNames)
            {
                if (TryParseBackupFileName(family, key, fileName, out string timestamp, out int attempt))
                {
                    // The copy just written survives even when a clock set back makes it sort oldest
                    if (string.Equals(fileName, keepFileName, StringComparison.Ordinal))
                    {
                        keptPresent = true;
                        continue;
                    }

                    backups.Add(new BackupName(fileName, timestamp, attempt));
                }
            }

            int keepOthers = keptPresent ? Math.Max(keep - 1, 0) : keep;
            if (backups.Count <= keepOthers)
            {
                return Array.Empty<string>();
            }

            backups.Sort(CompareBackupNames);
            var prune = new string[backups.Count - keepOthers];
            for (int i = 0; i < prune.Length; i++)
            {
                prune[i] = backups[i].FileName;
            }

            return prune;
        }

        /// <summary>Parses local-{name} with a valid local name.</summary>
        public static bool TryParseLocalDirectoryName(string directoryName, out ProfileId profile)
        {
            profile = ProfileId.Guest;
            if (directoryName == null || !directoryName.StartsWith(LocalDirectoryPrefix, StringComparison.Ordinal))
            {
                return false;
            }

            string name = directoryName.Substring(LocalDirectoryPrefix.Length);
            if (!ProfileId.IsValidLocalName(name))
            {
                return false;
            }

            profile = ProfileId.Local(name);
            return true;
        }

        /// <summary>Local profiles found under profiles/, sorted by name. Read-only; throws storage exceptions.</summary>
        public static IReadOnlyList<ProfileId> EnumerateLocalProfiles(ISaveStorage storage)
        {
            if (storage == null)
            {
                throw new ArgumentNullException(nameof(storage));
            }

            IReadOnlyList<string> names = storage.ListDirectoryNames(ProfilesDirectory);
            var profiles = new List<ProfileId>();
            for (int i = 0; i < names.Count; i++)
            {
                if (TryParseLocalDirectoryName(names[i], out ProfileId profile))
                {
                    profiles.Add(profile);
                }
            }

            if (profiles.Count == 0)
            {
                return Array.Empty<ProfileId>();
            }

            profiles.Sort((left, right) => string.CompareOrdinal(left.LocalName, right.LocalName));
            return profiles.AsReadOnly();
        }

        private static string Marker(BackupFileFamily family)
        {
            switch (family)
            {
                case BackupFileFamily.CloudCorrupt:
                    return CloudCorruptMarker;
                case BackupFileFamily.NewerVersion:
                    return NewerVersionMarker;
                default:
                    return CorruptMarker;
            }
        }

        private static bool TryParseBackupFileName(BackupFileFamily family, string key, string fileName, out string timestamp, out int attempt)
        {
            timestamp = null;
            attempt = 0;
            if (string.IsNullOrEmpty(key) || fileName == null)
            {
                return false;
            }

            string prefix = key + Marker(family);
            if (!fileName.StartsWith(prefix, StringComparison.Ordinal) || !fileName.EndsWith(JsonExtension, StringComparison.Ordinal))
            {
                return false;
            }

            int bodyLength = fileName.Length - prefix.Length - JsonExtension.Length;
            if (bodyLength < TimestampLength)
            {
                return false;
            }

            string body = fileName.Substring(prefix.Length, bodyLength);
            string stamp = body.Substring(0, TimestampLength);
            if (!DateTime.TryParseExact(stamp, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _))
            {
                return false;
            }

            if (body.Length == TimestampLength)
            {
                timestamp = stamp;
                return true;
            }

            if (body[TimestampLength] != '-'
                || !int.TryParse(body.Substring(TimestampLength + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
                || parsed <= 0)
            {
                return false;
            }

            timestamp = stamp;
            attempt = parsed;
            return true;
        }

        private static int CompareBackupNames(BackupName left, BackupName right)
        {
            int byTime = string.CompareOrdinal(left.Timestamp, right.Timestamp);
            return byTime != 0 ? byTime : left.Attempt.CompareTo(right.Attempt);
        }

        private readonly struct BackupName
        {
            public BackupName(string fileName, string timestamp, int attempt)
            {
                FileName = fileName;
                Timestamp = timestamp;
                Attempt = attempt;
            }

            public string FileName { get; }

            public string Timestamp { get; }

            public int Attempt { get; }
        }
    }
}
