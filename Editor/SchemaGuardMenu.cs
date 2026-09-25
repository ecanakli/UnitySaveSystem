using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.Compilation;
using UnityEngine;

namespace Ecanakli.SaveSystem.EditorTools
{
    /// <summary>
    /// Menu surface and reload hook for the schema change guard. Reflection lives in SchemaShapeReader, diff and
    /// classification live in SchemaSnapshotComparer; this file only discovers types, logs, shows dialogs and writes the snapshot.
    /// </summary>
    internal static class SchemaGuardMenu
    {
        private const string ValidateMenuPath = "Tools/Save System/Validate Save Schemas";
        internal const string UpdateSnapshotMenuPath = "Tools/Save System/Update Save Schema Snapshot";
        private const string ReloadCheckMenuPath = "Tools/Save System/Schema Check On Reload";
        private const string ReloadCheckPrefKey = "Ecanakli.SaveSystem.SchemaCheckOnReload";

        /// <summary>Read-only: logs (via SaveSchemaGuard.Validate) and shows a summary dialog, never writes the snapshot.</summary>
        [MenuItem(ValidateMenuPath)]
        private static void ValidateSaveSchemas()
        {
            SaveSchemaValidationResult result = SaveSchemaGuard.Validate();
            EditorUtility.DisplayDialog("Validate Save Schemas", Summarize(result), "OK");
        }

        /// <summary>Force-accepts the current live shapes as the new baseline; used after bumping SchemaVersion and writing UpgradePayload.</summary>
        [MenuItem(UpdateSnapshotMenuPath)]
        private static void UpdateSaveSchemaSnapshot()
        {
            List<SlotShape> shapes = ReadCurrentShapes(out List<string> skippedMessages);
            foreach (string skipped in skippedMessages)
            {
                Debug.LogWarning(skipped);
            }

            var snapshot = new SchemaSnapshot { Slots = shapes.Select(SchemaSnapshotComparer.ToSnapshotEntry).ToList() };
            if (!SchemaSnapshot.Save(snapshot))
            {
                EditorUtility.DisplayDialog("Update Save Schema Snapshot",
                    "The snapshot could not be written. See the console for the reason.", "OK");
                return;
            }

            string message = "Save System schema guard: snapshot updated with " + shapes.Count + " slot(s).";
            Debug.Log(message);
            EditorUtility.DisplayDialog("Update Save Schema Snapshot", message, "OK");
        }

        [MenuItem(ReloadCheckMenuPath)]
        private static void ToggleReloadCheck()
        {
            SetReloadCheckEnabled(!IsReloadCheckEnabled());
        }

        [MenuItem(ReloadCheckMenuPath, true)]
        private static bool ToggleReloadCheckValidate()
        {
            Menu.SetChecked(ReloadCheckMenuPath, IsReloadCheckEnabled());
            return true;
        }

        /// <summary>Log-only, no dialog, gated by the Schema Check On Reload toggle. Persists the snapshot for non-breaking slots.</summary>
        [DidReloadScripts]
        private static void OnScriptsReloaded()
        {
            if (!IsReloadCheckEnabled())
            {
                return;
            }

            SchemaComparisonResult result = RunComparison(out bool refuseOverwrite, out SchemaSnapshot previous);
            LogResult(result);

            if (refuseOverwrite)
            {
                // Load() already logged why (corrupt file or a formatVersion too new); do not turn a recoverable
                // conflict into a silent baseline reset by writing over it.
                return;
            }

            if (EditorUtility.scriptCompilationFailed)
            {
                Debug.LogWarning("Save System schema guard: script compilation failed this reload; snapshot not written, " +
                    "so slots invisible to this pass are not dropped from the baseline.");
                return;
            }

            if (SnapshotsEqual(previous, result.UpdatedSnapshot))
            {
                // F12: nothing changed, so skip the write; a no-op reload must not touch the file on disk.
                return;
            }

            SchemaSnapshot.Save(result.UpdatedSnapshot);
        }

        /// <summary>Shared orchestration for every entry point: discover shapes, load the snapshot, compare. No writes.</summary>
        internal static SchemaComparisonResult RunComparison(out bool refuseOverwrite)
        {
            return RunComparison(out refuseOverwrite, out _);
        }

        private static SchemaComparisonResult RunComparison(out bool refuseOverwrite, out SchemaSnapshot previous)
        {
            List<SlotShape> shapes = ReadCurrentShapes(out List<string> skippedMessages);
            foreach (string skipped in skippedMessages)
            {
                Debug.LogWarning(skipped);
            }

            previous = SchemaSnapshot.Load(out refuseOverwrite);
            return SchemaSnapshotComparer.Compare(shapes, previous);
        }

        /// <summary>
        /// F12: content equality independent of slot/member order, so the reload path can skip writing the
        /// snapshot when nothing actually changed. A missing SlotTypeName or Path never compares equal, so an
        /// ambiguous entry always causes a write rather than a silently skipped one.
        /// </summary>
        internal static bool SnapshotsEqual(SchemaSnapshot a, SchemaSnapshot b)
        {
            List<SlotSnapshotEntry> slotsA = a?.Slots ?? new List<SlotSnapshotEntry>();
            List<SlotSnapshotEntry> slotsB = b?.Slots ?? new List<SlotSnapshotEntry>();
            if (slotsA.Count != slotsB.Count)
            {
                return false;
            }

            var byType = new Dictionary<string, SlotSnapshotEntry>(StringComparer.Ordinal);
            foreach (SlotSnapshotEntry entry in slotsA)
            {
                if (entry?.SlotTypeName == null)
                {
                    return false;
                }

                byType[entry.SlotTypeName] = entry;
            }

            foreach (SlotSnapshotEntry entryB in slotsB)
            {
                if (entryB?.SlotTypeName == null || !byType.TryGetValue(entryB.SlotTypeName, out SlotSnapshotEntry entryA) || !SlotEntriesEqual(entryA, entryB))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool SlotEntriesEqual(SlotSnapshotEntry a, SlotSnapshotEntry b)
        {
            if (!string.Equals(a.Key, b.Key, StringComparison.Ordinal) || a.SchemaVersion != b.SchemaVersion)
            {
                return false;
            }

            List<SlotMemberEntry> membersA = a.Members ?? new List<SlotMemberEntry>();
            List<SlotMemberEntry> membersB = b.Members ?? new List<SlotMemberEntry>();
            if (membersA.Count != membersB.Count)
            {
                return false;
            }

            var pathsA = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (SlotMemberEntry member in membersA)
            {
                if (member?.Path == null)
                {
                    return false;
                }

                pathsA[member.Path] = member.TypeName;
            }

            foreach (SlotMemberEntry member in membersB)
            {
                if (member?.Path == null || !pathsA.TryGetValue(member.Path, out string typeName) || !string.Equals(typeName, member.TypeName, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static List<SlotShape> ReadCurrentShapes(out List<string> skippedMessages)
        {
            skippedMessages = new List<string>();
            var shapes = new List<SlotShape>();

            var playerAssemblies = new HashSet<string>(
                CompilationPipeline.GetAssemblies(AssembliesType.PlayerWithoutTestAssemblies).Select(a => a.name), StringComparer.Ordinal);
            var editorAssemblies = new HashSet<string>(
                CompilationPipeline.GetAssemblies(AssembliesType.Editor).Select(a => a.name), StringComparer.Ordinal);

            foreach (Type slotType in SchemaShapeReader.DiscoverSlotTypes())
            {
                if (!IsProductionAssembly(slotType.Assembly.GetName().Name, playerAssemblies, editorAssemblies))
                {
                    continue;
                }

                SlotShape shape = SchemaShapeReader.TryReadShape(slotType, out string failureReason);
                if (shape == null)
                {
                    skippedMessages.Add("Save System schema guard: could not inspect slot '" + slotType.FullName + "': " + failureReason);
                    continue;
                }

                shapes.Add(shape);
            }

            return shapes;
        }

        /// <summary>
        /// F14: name-based detection alone had both false positives (a production "...Tests"-suffixed assembly,
        /// e.g. MyGame.ABTests, wrongly excluded) and false negatives (MyGame.Test/Testing/TESTS wrongly included).
        /// playerAssemblies (AssembliesType.PlayerWithoutTestAssemblies) is the real "ships to a player" answer,
        /// independent of naming. editorAssemblies (AssembliesType.Editor) additionally covers a real Test
        /// Assembly, which compiles for the Editor domain but never for PlayerWithoutTestAssemblies: tracked
        /// there but absent from the player set means "excluded", regardless of name. SchemaShapeReader.IsTestAssembly
        /// is only the fallback for a name neither set tracks (e.g. a precompiled DLL).
        /// </summary>
        internal static bool IsProductionAssembly(string assemblyName, HashSet<string> playerAssemblies, HashSet<string> editorAssemblies)
        {
            if (playerAssemblies.Contains(assemblyName))
            {
                return true;
            }

            if (editorAssemblies.Contains(assemblyName))
            {
                // Tracked, but only for the Editor domain, not for a player build: excluded, independent of its name.
                return false;
            }

            return !SchemaShapeReader.IsTestAssembly(assemblyName);
        }

        internal static void LogResult(SchemaComparisonResult result)
        {
            foreach (string error in result.Errors)
            {
                Debug.LogError(error);
            }

            foreach (string warning in result.Warnings)
            {
                Debug.LogWarning(warning);
            }
        }

        private static string Summarize(SaveSchemaValidationResult result)
        {
            if (!result.HasErrors && result.WarningCount == 0)
            {
                return "No breaking schema changes. See the Console for the per-slot detail.";
            }

            return result.ErrorCount + " breaking change(s) and " + result.WarningCount + " warning(s) found. See the Console for details.";
        }

        private static bool IsReloadCheckEnabled()
        {
            return EditorPrefs.GetBool(ReloadCheckPrefKey, true);
        }

        private static void SetReloadCheckEnabled(bool enabled)
        {
            EditorPrefs.SetBool(ReloadCheckPrefKey, enabled);
        }
    }
}
