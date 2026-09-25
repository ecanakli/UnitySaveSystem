using System.Collections.Generic;

namespace Ecanakli.SaveSystem.EditorTools
{
    /// <summary>
    /// Read-only result of one schema validation pass: message lists plus counts. No dialog, no snapshot write.
    /// </summary>
    public sealed class SaveSchemaValidationResult
    {
        internal SaveSchemaValidationResult(IReadOnlyList<string> errors, IReadOnlyList<string> warnings, bool baselineUnreadable)
        {
            Errors = errors;
            Warnings = warnings;
            BaselineUnreadable = baselineUnreadable;
        }

        /// <summary>Breaking-change and Key-changed console lines in slot order, preceded by the baseline line when one applies.</summary>
        public IReadOnlyList<string> Errors { get; }

        /// <summary>Missing-UpgradePayload console lines, in slot order.</summary>
        public IReadOnlyList<string> Warnings { get; }

        /// <summary>
        /// True when the committed snapshot could not be read. Every slot then compares as new, so this pass cannot
        /// detect a breaking change and an otherwise empty result means nothing.
        /// </summary>
        public bool BaselineUnreadable { get; }

        public int ErrorCount => Errors.Count;

        public int WarningCount => Warnings.Count;

        /// <summary>True when a consuming project's CI build should fail on this result.</summary>
        public bool HasErrors => Errors.Count > 0;
    }

    /// <summary>
    /// Public, dialog-free entry point for the schema change guard: the surface a consuming project calls from its
    /// own EditMode test to fail CI on a breaking schema change. Never writes the snapshot.
    /// </summary>
    public static class SaveSchemaGuard
    {
        internal const string BaselineUnreadableMessage =
            "Save System schema guard: the committed snapshot could not be read, so every slot compares as new and " +
            "no breaking change can be detected. Resolve the conflict or corruption in " +
            SchemaSnapshot.DefaultPath + ", then run " + SchemaGuardMenu.UpdateSnapshotMenuPath + ".";

        /// <summary>Compares the current slot shapes against the committed snapshot and returns the outcome. Read-only.</summary>
        public static SaveSchemaValidationResult Validate()
        {
            SchemaComparisonResult result = SchemaGuardMenu.RunComparison(out bool baselineUnreadable);
            SchemaGuardMenu.LogResult(result);

            if (!baselineUnreadable)
            {
                return new SaveSchemaValidationResult(result.Errors, result.Warnings, false);
            }

            // An unreadable baseline compares every slot as new, so staying silent here would be a false all-clear
            var errors = new List<string>(result.Errors.Count + 1) { BaselineUnreadableMessage };
            errors.AddRange(result.Errors);
            return new SaveSchemaValidationResult(errors, result.Warnings, true);
        }
    }
}
