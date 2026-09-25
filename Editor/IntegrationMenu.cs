using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Compilation;

namespace Ecanakli.SaveSystem.EditorTools
{
    /// <summary>Checkable toggle for ECANAKLI_SAVESYSTEM_DI_ZENJECT, applied across every build target.</summary>
    internal static class IntegrationMenu
    {
        private const string MenuPath = "Tools/Save System/Zenject Integration";
        private const string ZenjectSymbol = "ECANAKLI_SAVESYSTEM_DI_ZENJECT";

        // Must match DependencyInjection/Zenject/Ecanakli.SaveSystem.DependencyInjection.Zenject.asmdef's versionDefines entry.
        private const string ZenjectUpmPackageId = "com.svermeulen.extenject";
        private const string ZenjectUpmMinVersion = "9.0.0";

        [MenuItem(MenuPath)]
        private static void ToggleZenjectIntegration()
        {
            if (IsZenjectActiveViaUpmPackage(out string upmVersion))
            {
                EditorUtility.DisplayDialog(
                    "Save System",
                    "Extenject " + upmVersion + " is installed via UPM/OpenUPM, so " + ZenjectSymbol + " is already " +
                    "active through the package's own versionDefines. This menu only manages the Player Settings " +
                    "define for an Assets-folder install; there is nothing to toggle here.",
                    "OK");
                return;
            }

            if (IsSymbolSetForActiveTarget())
            {
                ScriptingDefineSymbols.ApplyToAllBuildTargets(symbols => ScriptingDefineSymbols.Remove(symbols, ZenjectSymbol));
                return;
            }

            if (!HasZenjectAssembly())
            {
                EditorUtility.DisplayDialog(
                    "Save System",
                    "No assembly named \"Zenject\" was found in this project. Install Zenject or Extenject first, then enable this integration.",
                    "OK");
                return;
            }

            ScriptingDefineSymbols.ApplyToAllBuildTargets(symbols => ScriptingDefineSymbols.Add(symbols, ZenjectSymbol));
        }

        [MenuItem(MenuPath, true)]
        private static bool ToggleZenjectIntegrationValidate()
        {
            bool active = IsZenjectActiveViaUpmPackage(out _) || IsSymbolSetForActiveTarget();
            Menu.SetChecked(MenuPath, active);
            return true;
        }

        private static bool HasZenjectAssembly()
        {
            return CompilationPipeline.GetAssemblies()
                .Any(assembly => string.Equals(assembly.name, "Zenject", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsSymbolSetForActiveTarget()
        {
            try
            {
                BuildTargetGroup activeGroup = BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget);
                NamedBuildTarget activeTarget = NamedBuildTarget.FromBuildTargetGroup(activeGroup);
                string[] symbols = ScriptingDefineSymbols.Parse(PlayerSettings.GetScriptingDefineSymbols(activeTarget));
                return ScriptingDefineSymbols.Contains(symbols, ZenjectSymbol);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// True when Extenject is installed via UPM/OpenUPM at a version that grants ECANAKLI_SAVESYSTEM_DI_ZENJECT
        /// through the DI assembly's versionDefines, regardless of what Player Settings has recorded.
        /// </summary>
        private static bool IsZenjectActiveViaUpmPackage(out string version)
        {
            version = null;
            try
            {
                foreach (UnityEditor.PackageManager.PackageInfo package in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
                {
                    if (string.Equals(package.name, ZenjectUpmPackageId, StringComparison.OrdinalIgnoreCase) &&
                        ScriptingDefineSymbols.IsPackageVersionAtLeast(package.version, ZenjectUpmMinVersion))
                    {
                        version = package.version;
                        return true;
                    }
                }
            }
            catch (Exception)
            {
                return false;
            }

            return false;
        }
    }
}
