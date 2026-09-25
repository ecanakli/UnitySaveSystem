using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;

namespace Ecanakli.SaveSystem.EditorTools
{
    /// <summary>One recorded slot in the snapshot file: its Key, SchemaVersion and member list at the last accepted state.</summary>
    internal sealed class SlotSnapshotEntry
    {
        [JsonProperty("slotType")]
        public string SlotTypeName { get; set; }

        [JsonProperty("key")]
        public string Key { get; set; }

        [JsonProperty("schemaVersion")]
        public int SchemaVersion { get; set; }

        [JsonProperty("members")]
        public List<SlotMemberEntry> Members { get; set; } = new List<SlotMemberEntry>();
    }

    /// <summary>One member entry inside a snapshot slot.</summary>
    internal sealed class SlotMemberEntry
    {
        [JsonProperty("path")]
        public string Path { get; set; }

        [JsonProperty("type")]
        public string TypeName { get; set; }
    }

    /// <summary>
    /// On-disk model at ProjectSettings/SaveSystemSchemas.json: the last accepted shape of every known slot.
    /// Committed to source control; a consuming project reviews its diff in code review.
    /// </summary>
    internal sealed class SchemaSnapshot
    {
        /// <summary>Fixed project path; never write anywhere else. Tests use PathOverride instead of this constant.</summary>
        internal const string DefaultPath = "ProjectSettings/SaveSystemSchemas.json";

        /// <summary>Highest formatVersion this package version understands; bump together with a migration when it changes.</summary>
        internal const int CurrentFormatVersion = 1;

        /// <summary>Test-only override of the read/write path; null means DefaultPath.</summary>
        internal static string PathOverride { get; set; }

        [JsonProperty("formatVersion")]
        public int FormatVersion { get; set; } = CurrentFormatVersion;

        [JsonProperty("slots")]
        public List<SlotSnapshotEntry> Slots { get; set; } = new List<SlotSnapshotEntry>();

        internal static string ResolvePath()
        {
            return string.IsNullOrEmpty(PathOverride) ? DefaultPath : PathOverride;
        }

        /// <summary>Loads the snapshot from ResolvePath(); an empty snapshot when the file does not exist yet.</summary>
        internal static SchemaSnapshot Load()
        {
            return Load(out _);
        }

        /// <summary>
        /// Loads the snapshot from ResolvePath(). refuseOverwrite is true when the file exists but could not be
        /// trusted (corrupt/merge-conflicted JSON, or a formatVersion newer than this package understands); callers
        /// must not persist a new snapshot in that case, or a recoverable conflict becomes a silent baseline reset.
        /// </summary>
        internal static SchemaSnapshot Load(out bool refuseOverwrite)
        {
            refuseOverwrite = false;
            string path = ResolvePath();
            if (!File.Exists(path))
            {
                return new SchemaSnapshot();
            }

            SchemaSnapshot snapshot;
            try
            {
                string json = File.ReadAllText(path, Encoding.UTF8);
                snapshot = JsonConvert.DeserializeObject<SchemaSnapshot>(json) ?? new SchemaSnapshot();
                Normalize(snapshot);
            }
            catch (Exception exception)
            {
                refuseOverwrite = true;
                Debug.LogWarning("Save System schema guard: could not read '" + path + "' (" + exception.Message +
                    "). Treating it as empty for this pass only; resolve the merge conflict or corruption in that " +
                    "file, then run Tools/Save System/Update Save Schema Snapshot to write a clean baseline.");
                return new SchemaSnapshot();
            }

            if (snapshot.FormatVersion > CurrentFormatVersion)
            {
                refuseOverwrite = true;
                Debug.LogWarning("Save System schema guard: '" + path + "' has formatVersion " + snapshot.FormatVersion +
                    ", newer than this package version supports (" + CurrentFormatVersion + "). Update the package " +
                    "before touching this project's slots; the snapshot is treated as empty for this pass and will not be overwritten.");
                return new SchemaSnapshot();
            }

            return snapshot;
        }

        /// <summary>Saves snapshot to ResolvePath() with stable ordering, so the committed diff is small and deterministic.</summary>
        internal static bool Save(SchemaSnapshot snapshot)
        {
            Normalize(snapshot);
            string json = JsonConvert.SerializeObject(snapshot, Formatting.Indented);
            string path = ResolvePath();
            string temp = path + ".tmp";
            try
            {
                // Temp then replace: a kill mid-write must not truncate the baseline the guard compares against
                File.WriteAllText(temp, json, new UTF8Encoding(false));
                if (File.Exists(path))
                {
                    File.Replace(temp, path, null);
                }
                else
                {
                    File.Move(temp, path);
                }

                return true;
            }
            catch (Exception exception)
            {
                Debug.LogWarning("Save System schema guard: could not write '" + path + "' (" + exception.Message +
                    "). The previous baseline is unchanged.");
                TryDeleteTemp(temp);
                return false;
            }
        }

        private static void TryDeleteTemp(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // A leftover temp file is harmless: the next save overwrites it
            }
        }

        private static void Normalize(SchemaSnapshot snapshot)
        {
            snapshot.Slots ??= new List<SlotSnapshotEntry>();
            snapshot.Slots.Sort((a, b) => string.CompareOrdinal(a.SlotTypeName, b.SlotTypeName));
            foreach (SlotSnapshotEntry entry in snapshot.Slots)
            {
                entry.Members ??= new List<SlotMemberEntry>();
                entry.Members.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
            }
        }
    }
}
