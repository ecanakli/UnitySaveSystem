using System;
using System.Collections.Generic;
using System.Linq;

namespace Ecanakli.SaveSystem.EditorTools
{
    /// <summary>Verdict for one slot's member-shape diff against its previous snapshot entry.</summary>
    internal enum SlotChangeKind
    {
        /// <summary>Not present in the previous snapshot.</summary>
        New,

        /// <summary>Same Key, SchemaVersion and members as the previous snapshot.</summary>
        Unchanged,

        /// <summary>Only members added; no version bump required.</summary>
        Additive,

        /// <summary>A breaking member diff, but SchemaVersion was increased since the previous snapshot.</summary>
        Accepted,

        /// <summary>A breaking member diff without a matching SchemaVersion increase.</summary>
        Breaking,
    }

    /// <summary>Same member path present on both sides with a different type.</summary>
    internal readonly struct MemberRetype
    {
        public MemberRetype(string path, string oldTypeName, string newTypeName)
        {
            Path = path;
            OldTypeName = oldTypeName;
            NewTypeName = newTypeName;
        }

        public string Path { get; }

        public string OldTypeName { get; }

        public string NewTypeName { get; }
    }

    /// <summary>Full comparison outcome for one slot type.</summary>
    internal sealed class SlotComparisonResult
    {
        public string SlotTypeName { get; internal set; }

        public SlotChangeKind Kind { get; internal set; }

        public IReadOnlyList<SchemaMember> Added { get; internal set; } = Array.Empty<SchemaMember>();

        public IReadOnlyList<SchemaMember> Removed { get; internal set; } = Array.Empty<SchemaMember>();

        public IReadOnlyList<MemberRetype> Retyped { get; internal set; } = Array.Empty<MemberRetype>();

        public bool KeyChanged { get; internal set; }

        public string OldKey { get; internal set; }

        public string NewKey { get; internal set; }

        /// <summary>Null unless Kind == Breaking.</summary>
        public string BreakingMessage { get; internal set; }

        /// <summary>Null unless KeyChanged.</summary>
        public string KeyChangedMessage { get; internal set; }

        public bool MissingUpgradePayloadWarning { get; internal set; }

        /// <summary>Null unless MissingUpgradePayloadWarning.</summary>
        public string UpgradeWarningMessage { get; internal set; }
    }

    /// <summary>Comparison across every current slot shape plus the snapshot to persist afterward.</summary>
    internal sealed class SchemaComparisonResult
    {
        public IReadOnlyList<SlotComparisonResult> Slots { get; internal set; }

        /// <summary>Breaking-not-accepted slots keep their previous entry untouched; everything else adopts the current shape.</summary>
        public SchemaSnapshot UpdatedSnapshot { get; internal set; }

        /// <summary>Breaking and key-changed console lines, in slot order.</summary>
        public IReadOnlyList<string> Errors { get; internal set; }

        /// <summary>Missing-UpgradePayload console lines, in slot order.</summary>
        public IReadOnlyList<string> Warnings { get; internal set; }
    }

    /// <summary>Pure diff and classification: no UnityEditor calls, no file IO, so it is unit testable and CI-safe.</summary>
    internal static class SchemaSnapshotComparer
    {
        internal static SchemaComparisonResult Compare(IReadOnlyList<SlotShape> currentShapes, SchemaSnapshot previous)
        {
            if (currentShapes == null)
            {
                throw new ArgumentNullException(nameof(currentShapes));
            }

            var previousByType = new Dictionary<string, SlotSnapshotEntry>(StringComparer.Ordinal);
            if (previous?.Slots != null)
            {
                foreach (SlotSnapshotEntry entry in previous.Slots)
                {
                    // A hand-edited or merged snapshot can carry an entry with no slotType; a null key would throw here
                    if (string.IsNullOrEmpty(entry?.SlotTypeName))
                    {
                        continue;
                    }

                    previousByType[entry.SlotTypeName] = entry;
                }
            }

            var results = new List<SlotComparisonResult>();
            var errors = new List<string>();
            var warnings = new List<string>();
            var updatedEntries = new List<SlotSnapshotEntry>();
            var matchedPreviousTypes = new HashSet<string>(StringComparer.Ordinal);

            foreach (SlotShape current in currentShapes.OrderBy(s => s.SlotTypeName, StringComparer.Ordinal))
            {
                var result = new SlotComparisonResult { SlotTypeName = current.SlotTypeName };

                if (!previousByType.TryGetValue(current.SlotTypeName, out SlotSnapshotEntry previousEntry))
                {
                    result.Kind = SlotChangeKind.New;
                    updatedEntries.Add(ToSnapshotEntry(current));
                    results.Add(result);
                    continue;
                }

                matchedPreviousTypes.Add(current.SlotTypeName);

                bool keyChanged = !string.Equals(previousEntry.Key, current.Key, StringComparison.Ordinal);
                if (keyChanged)
                {
                    result.KeyChanged = true;
                    result.OldKey = previousEntry.Key;
                    result.NewKey = current.Key;
                    result.KeyChangedMessage = FormatKeyChangedMessage(current.SlotTypeName, previousEntry.Key, current.Key);
                    errors.Add(result.KeyChangedMessage);
                }

                DiffMembers(previousEntry, current, out List<SchemaMember> added, out List<SchemaMember> removed, out List<MemberRetype> retyped);
                result.Added = added;
                result.Removed = removed;
                result.Retyped = retyped;

                bool hasBreakingDiff = removed.Count > 0 || retyped.Count > 0;
                bool versionIncreased = current.SchemaVersion > previousEntry.SchemaVersion;

                if (keyChanged)
                {
                    // A Key change is never resolved by a SchemaVersion bump: it orphans the old save file
                    // regardless of the member diff. Keep the previous entry (old Key) so the error keeps
                    // firing every reload until the change is reverted or the snapshot is force-accepted.
                    result.Kind = SlotChangeKind.Breaking;
                    updatedEntries.Add(previousEntry);
                }
                else if (hasBreakingDiff && !versionIncreased)
                {
                    result.Kind = SlotChangeKind.Breaking;
                    int suggestedVersion = previousEntry.SchemaVersion + 1;
                    result.BreakingMessage = FormatBreakingMessage(current.SlotTypeName, current.Key, removed, retyped, added, suggestedVersion);
                    errors.Add(result.BreakingMessage);

                    // Keep the old entry so the error keeps firing until the developer bumps SchemaVersion or reverts the change.
                    updatedEntries.Add(previousEntry);
                }
                else if (hasBreakingDiff)
                {
                    result.Kind = SlotChangeKind.Accepted;
                    updatedEntries.Add(ToSnapshotEntry(current));
                }
                else if (added.Count > 0)
                {
                    result.Kind = SlotChangeKind.Additive;
                    updatedEntries.Add(ToSnapshotEntry(current));
                }
                else
                {
                    result.Kind = SlotChangeKind.Unchanged;
                    updatedEntries.Add(ToSnapshotEntry(current));
                }

                if (versionIncreased && !current.HasUpgradePayloadOverride)
                {
                    result.MissingUpgradePayloadWarning = true;
                    result.UpgradeWarningMessage = FormatMissingUpgradeWarning(current.SlotTypeName, previousEntry.SchemaVersion, current.SchemaVersion);
                    warnings.Add(result.UpgradeWarningMessage);
                }

                results.Add(result);
            }

            // Carry forward any previous entry this pass never saw (a slot assembly that failed to compile, or a
            // type reflection could not load) so the baseline is not erased; only the explicit "Update Save Schema
            // Snapshot" command intentionally drops a slot that was truly removed from the code.
            // Iterating previousByType (not previous.Slots) is what fixes F13: previousByType already collapsed a
            // duplicate slotType to its last entry, so a duplicate for a type no longer in the code is carried
            // forward once instead of accumulating on every pass forever.
            foreach (KeyValuePair<string, SlotSnapshotEntry> pair in previousByType)
            {
                if (!matchedPreviousTypes.Contains(pair.Key))
                {
                    updatedEntries.Add(pair.Value);
                }
            }

            return new SchemaComparisonResult
            {
                Slots = results,
                UpdatedSnapshot = new SchemaSnapshot
                {
                    FormatVersion = previous?.FormatVersion ?? SchemaSnapshot.CurrentFormatVersion,
                    Slots = updatedEntries,
                },
                Errors = errors,
                Warnings = warnings,
            };
        }

        internal static SlotSnapshotEntry ToSnapshotEntry(SlotShape shape)
        {
            return new SlotSnapshotEntry
            {
                SlotTypeName = shape.SlotTypeName,
                Key = shape.Key,
                SchemaVersion = shape.SchemaVersion,
                Members = shape.Members.Select(m => new SlotMemberEntry { Path = m.Path, TypeName = m.TypeName }).ToList(),
            };
        }

        private static void DiffMembers(
            SlotSnapshotEntry previousEntry,
            SlotShape current,
            out List<SchemaMember> added,
            out List<SchemaMember> removed,
            out List<MemberRetype> retyped)
        {
            Dictionary<string, string> previousMembers = ToPathMap(previousEntry.Members);
            Dictionary<string, string> currentMembers = ToPathMap(current.Members);

            added = new List<SchemaMember>();
            removed = new List<SchemaMember>();
            retyped = new List<MemberRetype>();

            foreach (KeyValuePair<string, string> pair in currentMembers)
            {
                if (!previousMembers.TryGetValue(pair.Key, out string oldType))
                {
                    added.Add(new SchemaMember(pair.Key, pair.Value));
                }
                else if (!string.Equals(oldType, pair.Value, StringComparison.Ordinal))
                {
                    retyped.Add(new MemberRetype(pair.Key, oldType, pair.Value));
                }
            }

            foreach (KeyValuePair<string, string> pair in previousMembers)
            {
                if (!currentMembers.ContainsKey(pair.Key))
                {
                    removed.Add(new SchemaMember(pair.Key, pair.Value));
                }
            }

            added.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
            removed.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
            retyped.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        }

        /// <summary>Path to type-name map, tolerant of a hand-edited or merge-conflicted file: a missing/null path is
        /// skipped rather than throwing, and a duplicate path keeps the last one instead of a Dictionary.Add throw.</summary>
        private static Dictionary<string, string> ToPathMap(List<SlotMemberEntry> members)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (members == null)
            {
                return result;
            }

            foreach (SlotMemberEntry member in members)
            {
                if (member == null || string.IsNullOrEmpty(member.Path))
                {
                    continue;
                }

                result[member.Path] = member.TypeName;
            }

            return result;
        }

        /// <summary>Same tolerance as the snapshot overload, for the current-shape side (a `new`-hidden data member
        /// is deduplicated in SchemaShapeReader, but this stays defensive against a hand-built SlotShape too).</summary>
        private static Dictionary<string, string> ToPathMap(IReadOnlyList<SchemaMember> members)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (SchemaMember member in members)
            {
                if (string.IsNullOrEmpty(member.Path))
                {
                    continue;
                }

                result[member.Path] = member.TypeName;
            }

            return result;
        }

        private static string FormatBreakingMessage(
            string slotTypeName,
            string key,
            List<SchemaMember> removed,
            List<MemberRetype> retyped,
            List<SchemaMember> added,
            int suggestedVersion)
        {
            var parts = new List<string>();
            if (removed.Count > 0)
            {
                parts.Add("removed " + string.Join(", ", removed.Select(m => "'" + m.Path + " : " + m.TypeName + "'")));
            }

            if (retyped.Count > 0)
            {
                parts.Add("retyped " + string.Join(", ", retyped.Select(m => "'" + m.Path + "' from '" + m.OldTypeName + "' to '" + m.NewTypeName + "'")));
            }

            if (added.Count > 0)
            {
                parts.Add("added " + string.Join(", ", added.Select(m => "'" + m.Path + " : " + m.TypeName + "'")));
            }

            string details = string.Join("; ", parts);
            string message = "Save System schema guard: slot '" + slotTypeName + "' (key '" + key + "') has a breaking data shape change: " + details + ".";
            if (removed.Count > 0 && added.Count > 0)
            {
                message += " This may be a rename; it is reported as a removal plus an addition because intent cannot be known.";
            }

            message += " Bump SchemaVersion to " + suggestedVersion + " and handle the upgrade in UpgradePayload.";
            return message;
        }

        private static string FormatKeyChangedMessage(string slotTypeName, string oldKey, string newKey)
        {
            return "Save System schema guard: slot '" + slotTypeName + "' changed Key from '" + oldKey + "' to '" + newKey +
                "'. The save file under the old key will be orphaned for existing players.";
        }

        private static string FormatMissingUpgradeWarning(string slotTypeName, int oldVersion, int newVersion)
        {
            return "Save System schema guard: slot '" + slotTypeName + "' raised SchemaVersion from " + oldVersion + " to " + newVersion +
                " but does not override UpgradePayload; saves at schema " + oldVersion + " will fail to upgrade.";
        }
    }
}
