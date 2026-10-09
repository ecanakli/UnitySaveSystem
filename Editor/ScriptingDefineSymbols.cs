using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build;

namespace Ecanakli.SaveSystem.EditorTools
{
    /// <summary>Pure symbol-array math, kept separate from the Unity write step so it stays unit testable.</summary>
    internal static class ScriptingDefineSymbols
    {
        private const char Separator = ';';

        /// <summary>Splits a Player Settings define string into symbols; null or empty gives an empty array.</summary>
        internal static string[] Parse(string joinedSymbols)
        {
            if (string.IsNullOrEmpty(joinedSymbols))
            {
                return Array.Empty<string>();
            }

            return joinedSymbols.Split(new[] { Separator }, StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary>Joins symbols back into the Player Settings define string form.</summary>
        internal static string Join(string[] symbols)
        {
            return symbols == null ? string.Empty : string.Join(Separator.ToString(), symbols);
        }

        /// <summary>True when symbol is present, exact match.</summary>
        internal static bool Contains(string[] symbols, string symbol)
        {
            return symbols != null && Array.IndexOf(symbols, symbol) >= 0;
        }

        /// <summary>Appends symbol if missing; returns the same content when it is already present.</summary>
        internal static string[] Add(string[] symbols, string symbol)
        {
            symbols ??= Array.Empty<string>();
            if (Contains(symbols, symbol))
            {
                return symbols;
            }

            var result = new string[symbols.Length + 1];
            Array.Copy(symbols, result, symbols.Length);
            result[symbols.Length] = symbol;
            return result;
        }

        /// <summary>Removes symbol, keeping every other symbol and its order.</summary>
        internal static string[] Remove(string[] symbols, string symbol)
        {
            symbols ??= Array.Empty<string>();
            if (!Contains(symbols, symbol))
            {
                return symbols;
            }

            var result = new List<string>(symbols.Length - 1);
            foreach (string existing in symbols)
            {
                if (existing != symbol)
                {
                    result.Add(existing);
                }
            }

            return result.ToArray();
        }

        /// <summary>Applies transform to the defines of every target GetNamedBuildTargets returns.</summary>
        internal static void ApplyToAllBuildTargets(Func<string[], string[]> transform)
        {
            // About twenty sequential writes; lock reload assemblies so a compile is not triggered mid-loop by a
            // partial write, and always unlock even if a write throws.
            EditorApplication.LockReloadAssemblies();
            try
            {
                foreach (NamedBuildTarget namedTarget in GetNamedBuildTargets())
                {
                    ApplyToTarget(namedTarget, transform);
                }
            }
            finally
            {
                EditorApplication.UnlockReloadAssemblies();
            }
        }

        /// <summary>The NamedBuildTarget of every non-obsolete BuildTargetGroup that has one, plus NamedBuildTarget.Server; each target once.</summary>
        internal static IReadOnlyList<NamedBuildTarget> GetNamedBuildTargets()
        {
            var targets = new List<NamedBuildTarget>();
            var targetNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (BuildTargetGroup group in GetNonObsoleteBuildTargetGroups())
            {
                NamedBuildTarget namedTarget;
                try
                {
                    namedTarget = NamedBuildTarget.FromBuildTargetGroup(group);
                }
                catch (Exception)
                {
                    // Some groups (unsupported or module-less on this machine) reject the conversion; skip them.
                    continue;
                }

                if (targetNames.Add(namedTarget.TargetName))
                {
                    targets.Add(namedTarget);
                }
            }

            if (targetNames.Add(NamedBuildTarget.Server.TargetName))
            {
                targets.Add(NamedBuildTarget.Server);
            }

            return targets;
        }

        /// <summary>True when candidate's version is greater than or equal to minimum; non-numeric suffixes (e.g. "-preview") are ignored.</summary>
        internal static bool IsPackageVersionAtLeast(string candidate, string minimum)
        {
            if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(minimum))
            {
                return false;
            }

            return TryParseVersion(candidate, out Version candidateVersion) &&
                   TryParseVersion(minimum, out Version minimumVersion) &&
                   candidateVersion >= minimumVersion;
        }

        private static bool TryParseVersion(string raw, out Version version)
        {
            int dashIndex = raw.IndexOf('-');
            string numeric = dashIndex >= 0 ? raw.Substring(0, dashIndex) : raw;
            return Version.TryParse(numeric, out version);
        }

        private static void ApplyToTarget(NamedBuildTarget namedTarget, Func<string[], string[]> transform)
        {
            string[] current = Parse(PlayerSettings.GetScriptingDefineSymbols(namedTarget));
            string[] updated = transform(current);
            PlayerSettings.SetScriptingDefineSymbols(namedTarget, Join(updated));
        }

        // Obsolescence is read per field: iOS shares its value with the obsolete iPhone, whose name ToString() returns.
        private static IEnumerable<BuildTargetGroup> GetNonObsoleteBuildTargetGroups()
        {
            var seenGroups = new HashSet<BuildTargetGroup>();
            foreach (FieldInfo field in typeof(BuildTargetGroup).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (Attribute.IsDefined(field, typeof(ObsoleteAttribute)))
                {
                    continue;
                }

                var group = (BuildTargetGroup)field.GetValue(null);
                if (group == BuildTargetGroup.Unknown || !seenGroups.Add(group))
                {
                    continue;
                }

                yield return group;
            }
        }
    }
}
