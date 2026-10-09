using System;
using System.Collections.Generic;
using Ecanakli.SaveSystem.EditorTools;
using NUnit.Framework;
using UnityEditor.Build;

namespace Ecanakli.SaveSystem.Tests
{
    /// <summary>Pure symbol-array math in ScriptingDefineSymbols; no Player Settings writes here.</summary>
    [TestFixture]
    public sealed class ScriptingDefineSymbolsTests
    {
        [Test]
        public void Add_SymbolNotPresent_AppendsIt()
        {
            string[] result = ScriptingDefineSymbols.Add(new[] { "FOO", "BAR" }, "BAZ");

            Assert.That(result, Is.EqualTo(new[] { "FOO", "BAR", "BAZ" }));
        }

        [Test]
        public void Add_SymbolAlreadyPresent_IsIdempotent()
        {
            string[] original = { "FOO", "ECANAKLI_SAVESYSTEM_DI_ZENJECT", "BAR" };

            string[] result = ScriptingDefineSymbols.Add(original, "ECANAKLI_SAVESYSTEM_DI_ZENJECT");

            Assert.That(result, Is.EqualTo(original));
        }

        [Test]
        public void Add_CalledTwice_ProducesOneOccurrence()
        {
            string[] first = ScriptingDefineSymbols.Add(Array.Empty<string>(), "FOO");
            string[] second = ScriptingDefineSymbols.Add(first, "FOO");

            Assert.That(second, Is.EqualTo(new[] { "FOO" }));
        }

        [Test]
        public void Add_EmptyArray_ReturnsSingleSymbol()
        {
            string[] result = ScriptingDefineSymbols.Add(Array.Empty<string>(), "FOO");

            Assert.That(result, Is.EqualTo(new[] { "FOO" }));
        }

        [Test]
        public void Add_NullArray_TreatedAsEmpty()
        {
            string[] result = ScriptingDefineSymbols.Add(null, "FOO");

            Assert.That(result, Is.EqualTo(new[] { "FOO" }));
        }

        [Test]
        public void Remove_KeepsOtherSymbolsAndTheirOrder()
        {
            string[] original = { "FOO", "ECANAKLI_SAVESYSTEM_DI_ZENJECT", "BAR", "BAZ" };

            string[] result = ScriptingDefineSymbols.Remove(original, "ECANAKLI_SAVESYSTEM_DI_ZENJECT");

            Assert.That(result, Is.EqualTo(new[] { "FOO", "BAR", "BAZ" }));
        }

        [Test]
        public void Remove_SymbolNotPresent_ReturnsSameContent()
        {
            string[] original = { "FOO", "BAR" };

            string[] result = ScriptingDefineSymbols.Remove(original, "MISSING");

            Assert.That(result, Is.EqualTo(original));
        }

        [Test]
        public void Remove_EmptyArray_ReturnsEmpty()
        {
            string[] result = ScriptingDefineSymbols.Remove(Array.Empty<string>(), "FOO");

            Assert.That(result, Is.Empty);
        }

        [Test]
        public void Remove_NullArray_ReturnsEmpty()
        {
            string[] result = ScriptingDefineSymbols.Remove(null, "FOO");

            Assert.That(result, Is.Empty);
        }

        [Test]
        public void Contains_FindsExactSymbolOnly()
        {
            string[] symbols = { "FOO", "BAR" };

            Assert.That(ScriptingDefineSymbols.Contains(symbols, "BAR"), Is.True);
            Assert.That(ScriptingDefineSymbols.Contains(symbols, "BA"), Is.False);
            Assert.That(ScriptingDefineSymbols.Contains(null, "BAR"), Is.False);
        }

        [Test]
        public void Parse_EmptyOrNullString_ReturnsEmptyArray()
        {
            Assert.That(ScriptingDefineSymbols.Parse(null), Is.Empty);
            Assert.That(ScriptingDefineSymbols.Parse(string.Empty), Is.Empty);
        }

        [Test]
        public void Parse_SkipsEmptyEntries()
        {
            string[] result = ScriptingDefineSymbols.Parse("FOO;;BAR;");

            Assert.That(result, Is.EqualTo(new[] { "FOO", "BAR" }));
        }

        [Test]
        public void Join_EmptyArray_ReturnsEmptyString()
        {
            Assert.That(ScriptingDefineSymbols.Join(Array.Empty<string>()), Is.EqualTo(string.Empty));
        }

        [Test]
        public void Join_NullArray_ReturnsEmptyString()
        {
            Assert.That(ScriptingDefineSymbols.Join(null), Is.EqualTo(string.Empty));
        }

        [Test]
        public void RoundTrip_ParseThenJoin_ReturnsEquivalentString()
        {
            const string original = "FOO;BAR;BAZ";

            string[] parsed = ScriptingDefineSymbols.Parse(original);
            string joined = ScriptingDefineSymbols.Join(parsed);

            Assert.That(joined, Is.EqualTo(original));
        }

        [Test]
        public void RoundTrip_JoinThenParse_ReturnsSameArray()
        {
            string[] original = { "FOO", "BAR", "BAZ" };

            string joined = ScriptingDefineSymbols.Join(original);
            string[] parsed = ScriptingDefineSymbols.Parse(joined);

            Assert.That(parsed, Is.EqualTo(original));
        }

        // ---- IsPackageVersionAtLeast: used to detect a UPM/OpenUPM Extenject install (F8) ----

        [Test]
        public void IsPackageVersionAtLeast_HigherVersion_ReturnsTrue()
        {
            Assert.That(ScriptingDefineSymbols.IsPackageVersionAtLeast("9.1.0", "9.0.0"), Is.True);
        }

        [Test]
        public void IsPackageVersionAtLeast_ExactVersion_ReturnsTrue()
        {
            Assert.That(ScriptingDefineSymbols.IsPackageVersionAtLeast("9.0.0", "9.0.0"), Is.True);
        }

        [Test]
        public void IsPackageVersionAtLeast_LowerVersion_ReturnsFalse()
        {
            Assert.That(ScriptingDefineSymbols.IsPackageVersionAtLeast("8.9.9", "9.0.0"), Is.False);
        }

        [Test]
        public void IsPackageVersionAtLeast_PreReleaseSuffix_ComparesTheNumericPartOnly()
        {
            Assert.That(ScriptingDefineSymbols.IsPackageVersionAtLeast("9.0.0-preview.1", "9.0.0"), Is.True);
            Assert.That(ScriptingDefineSymbols.IsPackageVersionAtLeast("8.9.9-preview.1", "9.0.0"), Is.False);
        }

        [Test]
        public void IsPackageVersionAtLeast_UnparseableVersion_ReturnsFalse()
        {
            Assert.That(ScriptingDefineSymbols.IsPackageVersionAtLeast("not-a-version", "9.0.0"), Is.False);
        }

        [Test]
        public void IsPackageVersionAtLeast_NullOrEmpty_ReturnsFalse()
        {
            Assert.That(ScriptingDefineSymbols.IsPackageVersionAtLeast(null, "9.0.0"), Is.False);
            Assert.That(ScriptingDefineSymbols.IsPackageVersionAtLeast(string.Empty, "9.0.0"), Is.False);
            Assert.That(ScriptingDefineSymbols.IsPackageVersionAtLeast("9.0.0", null), Is.False);
        }

        // ---- GetNamedBuildTargets: the targets the integration menu writes its define to ----

        [Test]
        public void GetNamedBuildTargets_IOSSharesItsValueWithAnObsoleteAlias_IncludesIOS()
        {
            List<string> actual = NamedBuildTargetNames();

            Assert.That(actual, Does.Contain(NamedBuildTarget.iOS.TargetName));
        }

        [Test]
        public void GetNamedBuildTargets_WindowsStoreAppsSharesItsValueWithAnObsoleteAlias_IncludesWindowsStoreApps()
        {
            List<string> actual = NamedBuildTargetNames();

            Assert.That(actual, Does.Contain(NamedBuildTarget.WindowsStoreApps.TargetName));
        }

        [Test]
        public void GetNamedBuildTargets_Always_IncludesStandaloneAndroidAndServer()
        {
            List<string> actual = NamedBuildTargetNames();

            Assert.That(actual, Does.Contain(NamedBuildTarget.Standalone.TargetName));
            Assert.That(actual, Does.Contain(NamedBuildTarget.Android.TargetName));
            Assert.That(actual, Does.Contain(NamedBuildTarget.Server.TargetName));
        }

        [Test]
        public void GetNamedBuildTargets_Always_ListsEachTargetOnce()
        {
            List<string> actual = NamedBuildTargetNames();

            Assert.That(actual, Is.Unique);
        }

        // Names, so a failure lists the targets instead of the type name NamedBuildTarget prints.
        private static List<string> NamedBuildTargetNames()
        {
            var names = new List<string>();
            foreach (NamedBuildTarget target in ScriptingDefineSymbols.GetNamedBuildTargets())
            {
                names.Add(target.TargetName);
            }

            return names;
        }
    }
}
