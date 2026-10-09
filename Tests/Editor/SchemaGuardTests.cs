using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ecanakli.SaveSystem;
using Ecanakli.SaveSystem.EditorTools;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ecanakli.SaveSystem.Tests
{
    /// <summary>Pure tests over SchemaShapeReader (reflection) and SchemaSnapshotComparer (diff/classification); no Unity calls.</summary>
    [TestFixture]
    public sealed class SchemaGuardTests
    {
        // ---- Fixtures for SchemaShapeReader.TryReadShape ----

        private sealed class GuardFixtureData
        {
            public int Value { get; set; }
        }

        private sealed class GuardFixtureSlotNoOverride : SaveSlot<GuardFixtureData>
        {
            public GuardFixtureSlotNoOverride()
            {
            }

            public override string Key => "guard_fixture_no_override";
        }

        private sealed class GuardFixtureSlotWithOverride : SaveSlot<GuardFixtureData>
        {
            public GuardFixtureSlotWithOverride()
            {
            }

            public override string Key => "guard_fixture_with_override";

            protected override int SchemaVersion => 3;

            protected override JObject UpgradePayload(JObject payload, int fromSchemaVersion)
            {
                return payload;
            }
        }

        private sealed class GuardFixtureSlotRequiresArgs : SaveSlot<GuardFixtureData>
        {
            public GuardFixtureSlotRequiresArgs(string unused)
            {
            }

            public override string Key => "guard_fixture_requires_args";
        }

        private sealed class GuardFixtureSlotAmbiguousUpgrade : SaveSlot<GuardFixtureData>
        {
            public GuardFixtureSlotAmbiguousUpgrade()
            {
            }

            public override string Key => "guard_fixture_ambiguous_upgrade";

            // Hides (does not override) the base UpgradePayload with the identical signature: GetMethod with an
            // explicit parameter-type filter still finds both candidates and throws AmbiguousMatchException.
#pragma warning disable CS0628 // Protected on purpose: it mirrors the accessibility of the hook it hides.
            protected new JObject UpgradePayload(JObject payload, int fromSchemaVersion) => payload;
#pragma warning restore CS0628
        }

        [Test]
        public void TryReadShape_NoOverrides_UsesDefaults()
        {
            SlotShape shape = SchemaShapeReader.TryReadShape(typeof(GuardFixtureSlotNoOverride), out string reason);

            Assert.That(shape, Is.Not.Null);
            Assert.That(reason, Is.Null);
            Assert.That(shape.Key, Is.EqualTo("guard_fixture_no_override"));
            Assert.That(shape.SchemaVersion, Is.EqualTo(1));
            Assert.That(shape.HasUpgradePayloadOverride, Is.False);
            Assert.That(shape.Members.Select(m => m.Path), Is.EqualTo(new[] { "Value" }));
        }

        [Test]
        public void TryReadShape_WithOverrides_ReadsOverriddenValues()
        {
            SlotShape shape = SchemaShapeReader.TryReadShape(typeof(GuardFixtureSlotWithOverride), out string reason);

            Assert.That(shape, Is.Not.Null);
            Assert.That(shape.SchemaVersion, Is.EqualTo(3));
            Assert.That(shape.HasUpgradePayloadOverride, Is.True);
        }

        [Test]
        public void TryReadShape_NoParameterlessConstructor_ReturnsNullWithReason()
        {
            SlotShape shape = SchemaShapeReader.TryReadShape(typeof(GuardFixtureSlotRequiresArgs), out string reason);

            Assert.That(shape, Is.Null);
            Assert.That(reason, Does.Contain("parameterless constructor"));
        }

        [Test]
        public void TryReadShape_NotASaveSlotSubclass_ReturnsNullWithReason()
        {
            SlotShape shape = SchemaShapeReader.TryReadShape(typeof(string), out string reason);

            Assert.That(shape, Is.Null);
            Assert.That(reason, Does.Contain("SaveSlot<TData>"));
        }

        [Test]
        public void TryReadShape_HiddenUpgradePayload_DoesNotThrow()
        {
            SlotShape shape = null;
            string reason = null;

            // A one-level 'new' hide resolves to the derived method rather than throwing; the guard around
            // HasUpgradePayloadOverride stays as defence for deeper hierarchies that do throw.
            Assert.DoesNotThrow(() => shape = SchemaShapeReader.TryReadShape(typeof(GuardFixtureSlotAmbiguousUpgrade), out reason));

            Assert.That(shape, Is.Not.Null);
            Assert.That(reason, Is.Null);
        }

        [Test]
        public void FindDataType_ReturnsGenericArgument()
        {
            Assert.That(SchemaShapeReader.FindDataType(typeof(GuardFixtureSlotNoOverride)), Is.EqualTo(typeof(GuardFixtureData)));
        }

        [Test]
        public void FindDataType_NonSlotType_ReturnsNull()
        {
            Assert.That(SchemaShapeReader.FindDataType(typeof(string)), Is.Null);
        }

        [Test]
        public void IsTestAssembly_MatchesDesignSubstrings()
        {
            Assert.That(SchemaShapeReader.IsTestAssembly("Ecanakli.SaveSystem.Tests"), Is.True);
            Assert.That(SchemaShapeReader.IsTestAssembly("Ecanakli.SaveSystem.PlayModeTests"), Is.True);
            Assert.That(SchemaShapeReader.IsTestAssembly("Ecanakli.SaveSystem.UnityCloudSave.Tests"), Is.True);
            Assert.That(SchemaShapeReader.IsTestAssembly("Ecanakli.SaveSystem.TestUtilities"), Is.True);
            Assert.That(SchemaShapeReader.IsTestAssembly("Ecanakli.SaveSystem"), Is.False);
            Assert.That(SchemaShapeReader.IsTestAssembly("MyGame.Contests"), Is.False);
            Assert.That(SchemaShapeReader.IsTestAssembly(null), Is.False);
            Assert.That(SchemaShapeReader.IsTestAssembly(string.Empty), Is.False);
        }

        // ---- SchemaGuardMenu.IsProductionAssembly: F14, both directions the name heuristic got wrong ----

        [Test]
        public void IsProductionAssembly_NameLooksLikeTests_ButPlayerSetIncludesIt_IsProduction()
        {
            // False positive fixed: MyGame.ABTests ends with "Tests" ordinally, but CompilationPipeline says it ships.
            var playerAssemblies = new HashSet<string> { "MyGame.ABTests" };
            var editorAssemblies = new HashSet<string> { "MyGame.ABTests" };

            Assert.That(SchemaGuardMenu.IsProductionAssembly("MyGame.ABTests", playerAssemblies, editorAssemblies), Is.True);
        }

        [Test]
        public void IsProductionAssembly_NameLooksLikeProduction_ButOnlyCompilesForEditor_IsExcluded()
        {
            // False negatives fixed: MyGame.Test/Testing/TESTS do not match the old name heuristic at all, but a
            // real Test Assembly (compiled for the Editor domain, never for a player build) is still excluded.
            var playerAssemblies = new HashSet<string>();
            var editorAssemblies = new HashSet<string> { "MyGame.Test", "MyGame.Testing", "MyGame.TESTS" };

            Assert.That(SchemaGuardMenu.IsProductionAssembly("MyGame.Test", playerAssemblies, editorAssemblies), Is.False);
            Assert.That(SchemaGuardMenu.IsProductionAssembly("MyGame.Testing", playerAssemblies, editorAssemblies), Is.False);
            Assert.That(SchemaGuardMenu.IsProductionAssembly("MyGame.TESTS", playerAssemblies, editorAssemblies), Is.False);
        }

        [Test]
        public void IsProductionAssembly_NameNotTrackedByCompilationPipeline_FallsBackToNameHeuristic()
        {
            var playerAssemblies = new HashSet<string>();
            var editorAssemblies = new HashSet<string>();

            Assert.That(SchemaGuardMenu.IsProductionAssembly("MyGame.Runtime", playerAssemblies, editorAssemblies), Is.True);
            Assert.That(SchemaGuardMenu.IsProductionAssembly("MyGame.Tests", playerAssemblies, editorAssemblies), Is.False);
        }

        // ---- SchemaGuardMenu.SnapshotsEqual: F12, the reload write-skip decision ----

        [Test]
        public void SnapshotsEqual_SameContentDifferentOrder_ReturnsTrue()
        {
            SchemaSnapshot a = SnapshotWith(
                Entry("Slot.A", "key_a", 1, ("X", "int"), ("Y", "int")),
                Entry("Slot.B", "key_b", 2, ("Z", "string")));
            SchemaSnapshot b = SnapshotWith(
                Entry("Slot.B", "key_b", 2, ("Z", "string")),
                Entry("Slot.A", "key_a", 1, ("Y", "int"), ("X", "int")));

            Assert.That(SchemaGuardMenu.SnapshotsEqual(a, b), Is.True);
        }

        [Test]
        public void SnapshotsEqual_DifferentMemberType_ReturnsFalse()
        {
            SchemaSnapshot a = SnapshotWith(Entry("Slot.A", "key_a", 1, ("X", "int")));
            SchemaSnapshot b = SnapshotWith(Entry("Slot.A", "key_a", 1, ("X", "long")));

            Assert.That(SchemaGuardMenu.SnapshotsEqual(a, b), Is.False);
        }

        [Test]
        public void SnapshotsEqual_DifferentSlotCount_ReturnsFalse()
        {
            SchemaSnapshot a = SnapshotWith(Entry("Slot.A", "key_a", 1, ("X", "int")));
            SchemaSnapshot b = SnapshotWith(Entry("Slot.A", "key_a", 1, ("X", "int")), Entry("Slot.B", "key_b", 1, ("Y", "int")));

            Assert.That(SchemaGuardMenu.SnapshotsEqual(a, b), Is.False);
        }

        [Test]
        public void SnapshotsEqual_NullSlotTypeNameEntry_NeverComparesEqual()
        {
            var a = new SchemaSnapshot { Slots = new List<SlotSnapshotEntry> { new SlotSnapshotEntry() } };
            var b = new SchemaSnapshot { Slots = new List<SlotSnapshotEntry> { new SlotSnapshotEntry() } };

            Assert.That(SchemaGuardMenu.SnapshotsEqual(a, b), Is.False);
        }

        // ---- Fixtures for SchemaShapeReader.ReadMembers ----

        private sealed class LeafHolder
        {
            public int Number { get; set; }

            public string Text { get; set; }

            [JsonIgnore]
            public string Hidden { get; set; }
        }

        [Test]
        public void ReadMembers_ExcludesJsonIgnore()
        {
            IReadOnlyList<SchemaMember> members = SchemaShapeReader.ReadMembers(typeof(LeafHolder));

            Assert.That(members.Select(m => m.Path), Is.EquivalentTo(new[] { "Number", "Text" }));
        }

        private sealed class NestedChild
        {
            public int Strength { get; set; }

            public int Agility { get; set; }
        }

        private sealed class NestedParent
        {
            public NestedChild Stats { get; set; }
        }

        [Test]
        public void ReadMembers_WalksNestedTypes()
        {
            IReadOnlyList<SchemaMember> members = SchemaShapeReader.ReadMembers(typeof(NestedParent));

            Assert.That(members.Select(m => m.Path), Is.EquivalentTo(new[] { "Stats", "Stats.Strength", "Stats.Agility" }));
        }

        private sealed class ListHolder
        {
            public List<string> Names { get; set; }

            public List<NestedChild> Children { get; set; }
        }

        [Test]
        public void ReadMembers_WalksListsAndRecursesIntoComplexElementTypes()
        {
            IReadOnlyList<SchemaMember> members = SchemaShapeReader.ReadMembers(typeof(ListHolder));
            Dictionary<string, string> byPath = members.ToDictionary(m => m.Path, m => m.TypeName);

            Assert.That(byPath["Names"], Is.EqualTo("List<string>"));
            Assert.That(byPath.ContainsKey("Names[]"), Is.False);
            Assert.That(byPath["Children"], Is.EqualTo("List<NestedChild>"));
            Assert.That(byPath["Children[].Strength"], Is.EqualTo("int"));
            Assert.That(byPath["Children[].Agility"], Is.EqualTo("int"));
        }

        private sealed class DictHolder
        {
            public Dictionary<string, int> Counters { get; set; }

            public Dictionary<string, NestedChild> Named { get; set; }
        }

        [Test]
        public void ReadMembers_WalksDictionariesAndRecursesIntoComplexValueTypes()
        {
            IReadOnlyList<SchemaMember> members = SchemaShapeReader.ReadMembers(typeof(DictHolder));
            Dictionary<string, string> byPath = members.ToDictionary(m => m.Path, m => m.TypeName);

            Assert.That(byPath["Counters"], Is.EqualTo("Dictionary<string,int>"));
            Assert.That(byPath.ContainsKey("Counters[]"), Is.False);
            Assert.That(byPath["Named"], Is.EqualTo("Dictionary<string,NestedChild>"));
            Assert.That(byPath["Named[].Strength"], Is.EqualTo("int"));
        }

        private sealed class NullableHolder
        {
            public int? Optional { get; set; }
        }

        [Test]
        public void ReadMembers_NullableValueType_UsesQuestionMarkSuffix()
        {
            IReadOnlyList<SchemaMember> members = SchemaShapeReader.ReadMembers(typeof(NullableHolder));

            Assert.That(members.Single().TypeName, Is.EqualTo("int?"));
        }

        private sealed class CycleA
        {
            public int Value { get; set; }

            public CycleB Next { get; set; }
        }

        private sealed class CycleB
        {
            public CycleA Back { get; set; }
        }

        [Test]
        public void ReadMembers_CycleTerminates()
        {
            IReadOnlyList<SchemaMember> members = SchemaShapeReader.ReadMembers(typeof(CycleA));
            string[] paths = members.Select(m => m.Path).ToArray();

            Assert.That(paths, Is.EquivalentTo(new[] { "Value", "Next", "Next.Back" }));
        }

        private sealed class OrderA
        {
            public int X { get; set; }

            public int Y { get; set; }
        }

        private sealed class OrderB
        {
            public int Y { get; set; }

            public int X { get; set; }
        }

        [Test]
        public void ReadMembers_DeclarationOrder_DoesNotAffectResult()
        {
            string[] a = SchemaShapeReader.ReadMembers(typeof(OrderA)).Select(m => m.Path + ":" + m.TypeName).ToArray();
            string[] b = SchemaShapeReader.ReadMembers(typeof(OrderB)).Select(m => m.Path + ":" + m.TypeName).ToArray();

            Assert.That(a, Is.EqualTo(b));
        }

        private class HiddenMemberBase
        {
            public int Value { get; set; }
        }

        private sealed class HiddenMemberDerived : HiddenMemberBase
        {
            // GetProperties without DeclaredOnly returns both this and the base Value; only one path must survive.
            public new string Value { get; set; }
        }

        [Test]
        public void ReadMembers_NewHiddenProperty_DeduplicatesByPath_MostDerivedWins()
        {
            IReadOnlyList<SchemaMember> members = SchemaShapeReader.ReadMembers(typeof(HiddenMemberDerived));

            Assert.That(members.Count(m => m.Path == "Value"), Is.EqualTo(1));
            Assert.That(members.Single(m => m.Path == "Value").TypeName, Is.EqualTo("string"));
        }

        // ---- SchemaSnapshotComparer: classification table ----

        private static SchemaSnapshot SnapshotWith(params SlotSnapshotEntry[] entries)
        {
            return new SchemaSnapshot { Slots = entries.ToList() };
        }

        private static SlotSnapshotEntry Entry(string slotType, string key, int schemaVersion, params (string Path, string Type)[] members)
        {
            return new SlotSnapshotEntry
            {
                SlotTypeName = slotType,
                Key = key,
                SchemaVersion = schemaVersion,
                Members = members.Select(m => new SlotMemberEntry { Path = m.Path, TypeName = m.Type }).ToList(),
            };
        }

        private static SlotShape Shape(string slotType, string key, int schemaVersion, bool hasUpgradeOverride, params (string Path, string Type)[] members)
        {
            return new SlotShape(slotType, key, schemaVersion, hasUpgradeOverride, members.Select(m => new SchemaMember(m.Path, m.Type)).ToList());
        }

        [Test]
        public void Compare_AddedMember_IsAdditive_AndSnapshotUpdatedSilently()
        {
            SchemaSnapshot previous = SnapshotWith(Entry("Slot.A", "key_a", 1, ("Coins", "int")));
            SlotShape current = Shape("Slot.A", "key_a", 1, false, ("Coins", "int"), ("Level", "int"));

            SchemaComparisonResult result = SchemaSnapshotComparer.Compare(new[] { current }, previous);

            Assert.That(result.Slots.Single().Kind, Is.EqualTo(SlotChangeKind.Additive));
            Assert.That(result.Errors, Is.Empty);
            Assert.That(result.Warnings, Is.Empty);
            SlotSnapshotEntry updated = result.UpdatedSnapshot.Slots.Single();
            Assert.That(updated.Members.Select(m => m.Path), Is.EquivalentTo(new[] { "Coins", "Level" }));
        }

        [Test]
        public void Compare_RemovedMember_IsBreaking_AndSnapshotKeepsOldShape()
        {
            SchemaSnapshot previous = SnapshotWith(Entry("Slot.B", "key_b", 1, ("Coins", "int"), ("Gems", "int")));
            SlotShape current = Shape("Slot.B", "key_b", 1, false, ("Coins", "int"));

            SchemaComparisonResult result = SchemaSnapshotComparer.Compare(new[] { current }, previous);

            SlotComparisonResult slotResult = result.Slots.Single();
            Assert.That(slotResult.Kind, Is.EqualTo(SlotChangeKind.Breaking));
            Assert.That(slotResult.BreakingMessage, Is.EqualTo(
                "Save System schema guard: slot 'Slot.B' (key 'key_b') has a breaking data shape change: removed 'Gems : int'. " +
                "Bump SchemaVersion to 2 and handle the upgrade in UpgradePayload."));
            Assert.That(result.Errors, Is.EqualTo(new[] { slotResult.BreakingMessage }));
            SlotSnapshotEntry updated = result.UpdatedSnapshot.Slots.Single();
            Assert.That(updated.Members.Select(m => m.Path), Is.EquivalentTo(new[] { "Coins", "Gems" }));
        }

        [Test]
        public void Compare_RenamedMember_ReportsRemovalPlusAddition_WithRenameHint()
        {
            SchemaSnapshot previous = SnapshotWith(Entry("Slot.C", "key_c", 1, ("PlayerName", "string")));
            SlotShape current = Shape("Slot.C", "key_c", 1, false, ("DisplayName", "string"));

            SchemaComparisonResult result = SchemaSnapshotComparer.Compare(new[] { current }, previous);

            SlotComparisonResult slotResult = result.Slots.Single();
            Assert.That(slotResult.Kind, Is.EqualTo(SlotChangeKind.Breaking));
            Assert.That(slotResult.BreakingMessage, Does.Contain("removed 'PlayerName : string'"));
            Assert.That(slotResult.BreakingMessage, Does.Contain("added 'DisplayName : string'"));
            Assert.That(slotResult.BreakingMessage, Does.Contain("This may be a rename"));
        }

        [Test]
        public void Compare_RetypedMember_IsBreaking()
        {
            SchemaSnapshot previous = SnapshotWith(Entry("Slot.D", "key_d", 1, ("Coins", "int")));
            SlotShape current = Shape("Slot.D", "key_d", 1, false, ("Coins", "long"));

            SchemaComparisonResult result = SchemaSnapshotComparer.Compare(new[] { current }, previous);

            SlotComparisonResult slotResult = result.Slots.Single();
            Assert.That(slotResult.Kind, Is.EqualTo(SlotChangeKind.Breaking));
            Assert.That(slotResult.BreakingMessage, Is.EqualTo(
                "Save System schema guard: slot 'Slot.D' (key 'key_d') has a breaking data shape change: retyped 'Coins' from 'int' to 'long'. " +
                "Bump SchemaVersion to 2 and handle the upgrade in UpgradePayload."));
        }

        [Test]
        public void Compare_BreakingDiffWithBumpedVersion_IsAccepted_NoErrorLogged()
        {
            SchemaSnapshot previous = SnapshotWith(Entry("Slot.E", "key_e", 1, ("Coins", "int")));
            SlotShape current = Shape("Slot.E", "key_e", 2, true, ("Coins", "long"));

            SchemaComparisonResult result = SchemaSnapshotComparer.Compare(new[] { current }, previous);

            Assert.That(result.Slots.Single().Kind, Is.EqualTo(SlotChangeKind.Accepted));
            Assert.That(result.Errors, Is.Empty);
            SlotSnapshotEntry updated = result.UpdatedSnapshot.Slots.Single();
            Assert.That(updated.SchemaVersion, Is.EqualTo(2));
            Assert.That(updated.Members.Single().TypeName, Is.EqualTo("long"));
        }

        [Test]
        public void Compare_VersionBumpedWithoutUpgradePayloadOverride_Warns()
        {
            SchemaSnapshot previous = SnapshotWith(Entry("Slot.F", "key_f", 1, ("Coins", "int")));
            SlotShape current = Shape("Slot.F", "key_f", 2, false, ("Coins", "int"));

            SchemaComparisonResult result = SchemaSnapshotComparer.Compare(new[] { current }, previous);

            SlotComparisonResult slotResult = result.Slots.Single();
            Assert.That(slotResult.MissingUpgradePayloadWarning, Is.True);
            Assert.That(slotResult.UpgradeWarningMessage, Is.EqualTo(
                "Save System schema guard: slot 'Slot.F' raised SchemaVersion from 1 to 2 but does not override UpgradePayload; " +
                "saves at schema 1 will fail to upgrade."));
            Assert.That(result.Warnings, Is.EqualTo(new[] { slotResult.UpgradeWarningMessage }));
        }

        [Test]
        public void Compare_VersionBumpedWithUpgradePayloadOverride_NoWarning()
        {
            SchemaSnapshot previous = SnapshotWith(Entry("Slot.G", "key_g", 1, ("Coins", "int")));
            SlotShape current = Shape("Slot.G", "key_g", 2, true, ("Coins", "int"));

            SchemaComparisonResult result = SchemaSnapshotComparer.Compare(new[] { current }, previous);

            Assert.That(result.Slots.Single().MissingUpgradePayloadWarning, Is.False);
            Assert.That(result.Warnings, Is.Empty);
        }

        [Test]
        public void Compare_KeyChanged_IsReportedWithOwnMessage_AndKeepsRepeatingAcrossReloads()
        {
            SchemaSnapshot previous = SnapshotWith(Entry("Slot.H", "old_key", 1, ("Coins", "int")));
            SlotShape current = Shape("Slot.H", "new_key", 1, false, ("Coins", "int"));

            SchemaComparisonResult result = SchemaSnapshotComparer.Compare(new[] { current }, previous);

            SlotComparisonResult slotResult = result.Slots.Single();
            Assert.That(slotResult.KeyChanged, Is.True);
            Assert.That(slotResult.KeyChangedMessage, Is.EqualTo(
                "Save System schema guard: slot 'Slot.H' changed Key from 'old_key' to 'new_key'. " +
                "The save file under the old key will be orphaned for existing players."));
            Assert.That(result.Errors, Is.EqualTo(new[] { slotResult.KeyChangedMessage }));
            // A Key change is never accepted by a SchemaVersion bump, so it is classified like any other
            // unresolved breaking change rather than Unchanged.
            Assert.That(slotResult.Kind, Is.EqualTo(SlotChangeKind.Breaking));

            // The previous entry (old Key) must be the one persisted, or the error goes silent after one reload.
            SlotSnapshotEntry updated = result.UpdatedSnapshot.Slots.Single();
            Assert.That(updated.Key, Is.EqualTo("old_key"));

            // Feeding that snapshot back in (simulating the next reload) must still report the same Key change.
            SchemaComparisonResult secondPass = SchemaSnapshotComparer.Compare(new[] { current }, result.UpdatedSnapshot);
            Assert.That(secondPass.Slots.Single().KeyChanged, Is.True);
            Assert.That(secondPass.Errors, Has.Count.EqualTo(1));
        }

        [Test]
        public void Compare_NewSlotType_IsRecordedWithoutMessage()
        {
            SchemaSnapshot previous = new SchemaSnapshot();
            SlotShape current = Shape("Slot.I", "key_i", 1, false, ("Coins", "int"));

            SchemaComparisonResult result = SchemaSnapshotComparer.Compare(new[] { current }, previous);

            SlotComparisonResult slotResult = result.Slots.Single();
            Assert.That(slotResult.Kind, Is.EqualTo(SlotChangeKind.New));
            Assert.That(result.Errors, Is.Empty);
            Assert.That(result.Warnings, Is.Empty);
            Assert.That(result.UpdatedSnapshot.Slots.Single().SlotTypeName, Is.EqualTo("Slot.I"));
        }

        [Test]
        public void Compare_NoChanges_IsUnchanged()
        {
            SchemaSnapshot previous = SnapshotWith(Entry("Slot.J", "key_j", 1, ("Coins", "int")));
            SlotShape current = Shape("Slot.J", "key_j", 1, false, ("Coins", "int"));

            SchemaComparisonResult result = SchemaSnapshotComparer.Compare(new[] { current }, previous);

            Assert.That(result.Slots.Single().Kind, Is.EqualTo(SlotChangeKind.Unchanged));
            Assert.That(result.Errors, Is.Empty);
            Assert.That(result.Warnings, Is.Empty);
        }

        [Test]
        public void Compare_MemberOrderInSnapshot_IsNotADifference()
        {
            SchemaSnapshot previous = SnapshotWith(Entry("Slot.K", "key_k", 1, ("B", "int"), ("A", "int")));
            SlotShape current = Shape("Slot.K", "key_k", 1, false, ("A", "int"), ("B", "int"));

            SchemaComparisonResult result = SchemaSnapshotComparer.Compare(new[] { current }, previous);

            Assert.That(result.Slots.Single().Kind, Is.EqualTo(SlotChangeKind.Unchanged));
        }

        [Test]
        public void Compare_PreviousEntryNotSeenThisPass_IsCarriedForwardUnchanged()
        {
            // Slot.Missing stands in for a slot type whose assembly failed to compile this pass (or a type
            // reflection could not load); it must not be dropped from the baseline.
            SchemaSnapshot previous = SnapshotWith(
                Entry("Slot.Seen", "key_seen", 1, ("Coins", "int")),
                Entry("Slot.Missing", "key_missing", 2, ("Gems", "int")));
            SlotShape current = Shape("Slot.Seen", "key_seen", 1, false, ("Coins", "int"));

            SchemaComparisonResult result = SchemaSnapshotComparer.Compare(new[] { current }, previous);

            Assert.That(result.Slots.Select(s => s.SlotTypeName), Is.EqualTo(new[] { "Slot.Seen" }));

            SlotSnapshotEntry carried = result.UpdatedSnapshot.Slots.Single(s => s.SlotTypeName == "Slot.Missing");
            Assert.That(carried.Key, Is.EqualTo("key_missing"));
            Assert.That(carried.SchemaVersion, Is.EqualTo(2));
            Assert.That(carried.Members.Select(m => m.Path), Is.EquivalentTo(new[] { "Gems" }));
        }

        [Test]
        public void Compare_PreservesPreviousFormatVersion_InsteadOfResettingToDefault()
        {
            var previous = new SchemaSnapshot { FormatVersion = 5, Slots = new List<SlotSnapshotEntry>() };
            SlotShape current = Shape("Slot.Fmt", "key_fmt", 1, false, ("Coins", "int"));

            SchemaComparisonResult result = SchemaSnapshotComparer.Compare(new[] { current }, previous);

            Assert.That(result.UpdatedSnapshot.FormatVersion, Is.EqualTo(5));
        }

        [Test]
        public void Compare_NoPreviousSnapshot_UsesCurrentFormatVersion()
        {
            SlotShape current = Shape("Slot.Fmt2", "key_fmt2", 1, false, ("Coins", "int"));

            SchemaComparisonResult result = SchemaSnapshotComparer.Compare(new[] { current }, null);

            Assert.That(result.UpdatedSnapshot.FormatVersion, Is.EqualTo(SchemaSnapshot.CurrentFormatVersion));
        }

        [Test]
        public void Compare_PreviousSnapshotHasDuplicateOrMissingPaths_DoesNotThrow_LastPathWins()
        {
            SchemaSnapshot previous = SnapshotWith(new SlotSnapshotEntry
            {
                SlotTypeName = "Slot.Dup",
                Key = "key_dup",
                SchemaVersion = 1,
                Members = new List<SlotMemberEntry>
                {
                    new SlotMemberEntry { Path = "Coins", TypeName = "int" },
                    new SlotMemberEntry { Path = "Coins", TypeName = "long" },
                    new SlotMemberEntry { Path = null, TypeName = "string" },
                },
            });
            SlotShape current = Shape("Slot.Dup", "key_dup", 1, false, ("Coins", "long"));

            SchemaComparisonResult result = null;
            Assert.DoesNotThrow(() => result = SchemaSnapshotComparer.Compare(new[] { current }, previous));

            Assert.That(result.Slots.Single().Kind, Is.EqualTo(SlotChangeKind.Unchanged));
        }

        [Test]
        public void Compare_CurrentShapeHasDuplicatePaths_DoesNotThrow()
        {
            SchemaSnapshot previous = SnapshotWith(Entry("Slot.Dup2", "key_dup2", 1, ("Coins", "int")));
            var current = new SlotShape("Slot.Dup2", "key_dup2", 1, false, new[]
            {
                new SchemaMember("Coins", "int"),
                new SchemaMember("Coins", "long"),
            });

            Assert.DoesNotThrow(() => SchemaSnapshotComparer.Compare(new[] { current }, previous));
        }

        // ---- SchemaSnapshot: round trip ----

        [Test]
        public void Snapshot_SaveThenLoad_RoundTrips()
        {
            string tempPath = Path.Combine(Path.GetTempPath(), "SaveSystemSchemaGuardTests_" + Guid.NewGuid().ToString("N") + ".json");
            string previousOverride = SchemaSnapshot.PathOverride;
            SchemaSnapshot.PathOverride = tempPath;
            try
            {
                SchemaSnapshot snapshot = SnapshotWith(Entry("Slot.RoundTrip", "key_rt", 2, ("B", "int"), ("A", "string")));

                SchemaSnapshot.Save(snapshot);
                SchemaSnapshot loaded = SchemaSnapshot.Load();

                Assert.That(loaded.Slots.Count, Is.EqualTo(1));
                Assert.That(loaded.Slots[0].SlotTypeName, Is.EqualTo("Slot.RoundTrip"));
                Assert.That(loaded.Slots[0].Key, Is.EqualTo("key_rt"));
                Assert.That(loaded.Slots[0].SchemaVersion, Is.EqualTo(2));
                Assert.That(loaded.Slots[0].Members.Select(m => m.Path), Is.EqualTo(new[] { "A", "B" }));
            }
            finally
            {
                SchemaSnapshot.PathOverride = previousOverride;
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }

        [Test]
        public void Snapshot_Load_MissingFile_ReturnsEmptySnapshot()
        {
            string tempPath = Path.Combine(Path.GetTempPath(), "SaveSystemSchemaGuardTests_missing_" + Guid.NewGuid().ToString("N") + ".json");
            string previousOverride = SchemaSnapshot.PathOverride;
            SchemaSnapshot.PathOverride = tempPath;
            try
            {
                SchemaSnapshot loaded = SchemaSnapshot.Load();

                Assert.That(loaded.Slots, Is.Empty);
            }
            finally
            {
                SchemaSnapshot.PathOverride = previousOverride;
            }
        }

        [Test]
        public void Snapshot_Load_CorruptJson_ReturnsEmptySnapshot_RefusesOverwrite_LeavesFileUntouched()
        {
            string tempPath = Path.Combine(Path.GetTempPath(), "SaveSystemSchemaGuardTests_corrupt_" + Guid.NewGuid().ToString("N") + ".json");
            string previousOverride = SchemaSnapshot.PathOverride;
            const string corruptContent = "<<<<<<< HEAD\n{\"formatVersion\":1,\"slots\":[]}\n=======\n{\"formatVersion\":1,\"slots\":[";
            File.WriteAllText(tempPath, corruptContent);
            SchemaSnapshot.PathOverride = tempPath;
            try
            {
                SchemaSnapshot loaded = null;
                bool refuseOverwrite = false;
                Assert.DoesNotThrow(() => loaded = SchemaSnapshot.Load(out refuseOverwrite));

                Assert.That(refuseOverwrite, Is.True);
                Assert.That(loaded.Slots, Is.Empty);
                Assert.That(File.ReadAllText(tempPath), Is.EqualTo(corruptContent));
            }
            finally
            {
                SchemaSnapshot.PathOverride = previousOverride;
                File.Delete(tempPath);
            }
        }

        [Test]
        public void Snapshot_Load_FormatVersionNewerThanSupported_ReturnsEmptySnapshot_RefusesOverwrite()
        {
            string tempPath = Path.Combine(Path.GetTempPath(), "SaveSystemSchemaGuardTests_futurefmt_" + Guid.NewGuid().ToString("N") + ".json");
            string previousOverride = SchemaSnapshot.PathOverride;
            File.WriteAllText(tempPath, "{\"formatVersion\":" + (SchemaSnapshot.CurrentFormatVersion + 1) + ",\"slots\":[]}");
            SchemaSnapshot.PathOverride = tempPath;
            try
            {
                SchemaSnapshot loaded = SchemaSnapshot.Load(out bool refuseOverwrite);

                Assert.That(refuseOverwrite, Is.True);
                Assert.That(loaded.Slots, Is.Empty);
            }
            finally
            {
                SchemaSnapshot.PathOverride = previousOverride;
                File.Delete(tempPath);
            }
        }

        // ---- SaveSchemaGuard: public, dialog-free CI entry point ----

        [Test]
        public void SaveSchemaGuard_Validate_AgainstEmptyBaseline_ReportsNothingAndNeverWritesTheSnapshot()
        {
            string tempPath = Path.Combine(Path.GetTempPath(), "SaveSystemSchemaGuardTests_ci_" + Guid.NewGuid().ToString("N") + ".json");
            string previousOverride = SchemaSnapshot.PathOverride;
            SchemaSnapshot.PathOverride = tempPath;
            try
            {
                SaveSchemaValidationResult result = SaveSchemaGuard.Validate();

                Assert.That(result, Is.Not.Null);
                Assert.That(result.ErrorCount, Is.EqualTo(result.Errors.Count));
                Assert.That(result.WarningCount, Is.EqualTo(result.Warnings.Count));
                // Against an empty baseline, every discovered slot type is classified New, which never reports.
                Assert.That(result.HasErrors, Is.False);
                Assert.That(result.WarningCount, Is.EqualTo(0));
                Assert.That(File.Exists(tempPath), Is.False);
            }
            finally
            {
                SchemaSnapshot.PathOverride = previousOverride;
            }
        }

        [Test]
        public void SaveSchemaGuard_Validate_BaselineUnreadable_ReportsAnErrorInsteadOfAnAllClear()
        {
            string tempPath = Path.Combine(Path.GetTempPath(), "SaveSystemSchemaGuardTests_ci_bad_" + Guid.NewGuid().ToString("N") + ".json");
            string previousOverride = SchemaSnapshot.PathOverride;
            SchemaSnapshot.PathOverride = tempPath;
            try
            {
                File.WriteAllText(tempPath, "<<<<<<< HEAD\n{ \"slots\": [] }\n=======");

                SaveSchemaValidationResult result = SaveSchemaGuard.Validate();

                Assert.That(result.BaselineUnreadable, Is.True);
                Assert.That(result.HasErrors, Is.True, "An unreadable baseline compares every slot as New, so silence would be a false all-clear.");
                Assert.That(result.Errors.First(), Is.EqualTo(SaveSchemaGuard.BaselineUnreadableMessage));
            }
            finally
            {
                SchemaSnapshot.PathOverride = previousOverride;
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }

        // F13: an unmatched duplicate slotType must be carried forward once, not once per duplicate on every pass.
        [Test]
        public void Compare_UnmatchedDuplicateSlotType_IsCarriedForwardOnlyOnce()
        {
            SchemaSnapshot previous = SnapshotWith(
                Entry("Slot.Ghost", "key_ghost", 1, ("A", "int")),
                Entry("Slot.Ghost", "key_ghost", 2, ("B", "int")));
            SlotShape current = Shape("Slot.Seen", "key_seen", 1, false, ("Coins", "int"));

            SchemaComparisonResult result = SchemaSnapshotComparer.Compare(new[] { current }, previous);

            Assert.That(result.UpdatedSnapshot.Slots.Count(e => e.SlotTypeName == "Slot.Ghost"), Is.EqualTo(1));
            // De-duplication keeps the last entry, matching previousByType's own last-wins rule (F2).
            Assert.That(result.UpdatedSnapshot.Slots.Single(e => e.SlotTypeName == "Slot.Ghost").SchemaVersion, Is.EqualTo(2));
        }

        [Test]
        public void Compare_PreviousEntryHasNoSlotTypeName_DoesNotThrowAndIsDropped()
        {
            var previous = new SchemaSnapshot { Slots = new List<SlotSnapshotEntry> { new SlotSnapshotEntry() } };
            var current = new SlotShape("Slot.NoPrevious", "key_no_previous", 1, false, new[] { new SchemaMember("Coins", "int") });

            SchemaComparisonResult result = null;
            Assert.DoesNotThrow(() => result = SchemaSnapshotComparer.Compare(new[] { current }, previous));

            Assert.That(result.Slots.Single().Kind, Is.EqualTo(SlotChangeKind.New));
            Assert.That(result.UpdatedSnapshot.Slots.Any(e => string.IsNullOrEmpty(e.SlotTypeName)), Is.False);
        }

        [Test]
        public void Snapshot_SaveOverAReadOnlyFile_Succeeds()
        {
            string tempPath = Path.Combine(Path.GetTempPath(), "SaveSystemSchemaGuardTests_ro_" + Guid.NewGuid().ToString("N") + ".json");
            string previousOverride = SchemaSnapshot.PathOverride;
            SchemaSnapshot.PathOverride = tempPath;
            try
            {
                SchemaSnapshot.Save(SnapshotWith(Entry("Slot.Keep", "key_keep", 1, ("Coins", "int"))));
                File.SetAttributes(tempPath, FileAttributes.ReadOnly);

                bool saved = false;
                Assert.DoesNotThrow(() => saved = SchemaSnapshot.Save(SnapshotWith(Entry("Slot.Other", "key_other", 9, ("Gems", "long")))));

                // Replacing needs write permission on the directory, not on the file, so the read-only
                // ProjectSettings case now succeeds where the old direct write threw on every reload.
                Assert.That(saved, Is.True);
                Assert.That(SchemaSnapshot.Load().Slots.Single().SlotTypeName, Is.EqualTo("Slot.Other"));
                Assert.That(File.Exists(tempPath + ".tmp"), Is.False);
            }
            finally
            {
                SchemaSnapshot.PathOverride = previousOverride;
                if (File.Exists(tempPath))
                {
                    File.SetAttributes(tempPath, FileAttributes.Normal);
                    File.Delete(tempPath);
                }
            }
        }

        [Test]
        public void Snapshot_SaveIntoAMissingDirectory_ReturnsFalseWithoutThrowing()
        {
            string missingDirectory = Path.Combine(Path.GetTempPath(), "SaveSystemSchemaGuardTests_gone_" + Guid.NewGuid().ToString("N"));
            string previousOverride = SchemaSnapshot.PathOverride;
            SchemaSnapshot.PathOverride = Path.Combine(missingDirectory, "SaveSystemSchemas.json");
            try
            {
                bool saved = true;

                // A throw here would escape [DidReloadScripts] and fire after every compile
                Assert.DoesNotThrow(() => saved = SchemaSnapshot.Save(SnapshotWith(Entry("Slot.Any", "key_any", 1, ("Coins", "int")))));

                Assert.That(saved, Is.False);
                Assert.That(Directory.Exists(missingDirectory), Is.False);
            }
            finally
            {
                SchemaSnapshot.PathOverride = previousOverride;
            }
        }
    }
}
