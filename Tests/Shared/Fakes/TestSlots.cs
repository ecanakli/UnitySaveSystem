using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Ecanakli.SaveSystem.Tests
{
    /// <summary>Delegate form of SaveSlot.ResolveConflict for test slots.</summary>
    internal delegate ConflictResolution<TData> TestConflictResolver<TData>(in ConflictContext<TData> context) where TData : class;

    /// <summary>Default keys of the test slots.</summary>
    internal static class TestSlotKeys
    {
        public const string Player = "player";
        public const string Settings = "settings";
        public const string ServerRewards = "server_rewards";
        public const string NormalizeThrows = "normalize_throws";
        public const string Inventory = "inventory";
        public const string Schema = "schema_slot";
        public const string Initializers = "initializers";
        public const string HookCalls = "hook_calls";
    }

    internal sealed class ProfileData
    {
        public int Coins { get; set; }

        public int Level { get; set; }

        public List<string> Items { get; set; } = new List<string>();
    }

    internal sealed class SettingsData
    {
        public float MusicVolume { get; set; } = 1f;

        public bool Vibration { get; set; } = true;

        public string Language { get; set; } = "en";
    }

    internal sealed class RewardsData
    {
        public List<string> Granted { get; set; } = new List<string>();

        public int ServerVersion { get; set; }
    }

    internal sealed class InventoryData
    {
        public List<string> Items { get; set; } = new List<string>();
    }

    internal sealed class SchemaV1Data
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        public int Coins { get; set; }
    }

    internal sealed class SchemaV2Data
    {
        public string DisplayName { get; set; }

        public int Coins { get; set; }
    }

    internal sealed class SchemaV3Data
    {
        public string DisplayName { get; set; }

        public long Gold { get; set; }
    }

    internal sealed class InitializerStats
    {
        public int Strength { get; set; } = 1;

        public int Agility { get; set; }
    }

    /// <summary>Field initializers that serializer settings must not duplicate or merge.</summary>
    internal sealed class InitializerData
    {
        public static readonly string[] DefaultItems = { "starter_sword", "starter_shield" };

        public List<string> Items { get; set; } = new List<string>(DefaultItems);

        public InitializerStats Stats { get; set; } = new InitializerStats();

        public InitializerStats OptionalStats { get; set; }

        public Dictionary<string, int> Counters { get; set; } = new Dictionary<string, int> { { "wins", 0 } };
    }

    /// <summary>Payload upgrade steps shared by the schema slots (one step per call).</summary>
    internal static class SchemaMigrations
    {
        public static JObject Step(JObject payload, int fromSchemaVersion)
        {
            switch (fromSchemaVersion)
            {
                // v1 -> v2: "name" renamed to "DisplayName"
                case 1:
                    return RenameProperty(payload, "name", "DisplayName");

                // v2 -> v3: "Coins" renamed to "Gold" (int to long)
                case 2:
                    return RenameProperty(payload, "Coins", "Gold");

                default:
                    throw new InvalidOperationException("No migration from schema " + fromSchemaVersion + ".");
            }
        }

        public static JObject RenameProperty(JObject payload, string from, string to)
        {
            if (payload.TryGetValue(from, out JToken value))
            {
                payload.Remove(from);
                payload[to] = value;
            }

            return payload;
        }
    }

    /// <summary>Configurable slot base: constant key/mode/scope, hook call counters and optional hook delegates.</summary>
    internal abstract class TestSlot<TData> : SaveSlot<TData> where TData : class, new()
    {
        private readonly string _key;
        private readonly SyncMode _syncMode;
        private readonly SlotScope _scope;
        private readonly int _schemaVersion;
        private readonly List<int> _upgradeFromVersions = new List<int>();

        protected TestSlot(string key, SyncMode syncMode, SlotScope scope, int schemaVersion)
        {
            _key = key;
            _syncMode = syncMode;
            _scope = scope;
            _schemaVersion = schemaVersion;
        }

        public override string Key => _key;

        public override SyncMode SyncMode => _syncMode;

        public override SlotScope Scope => _scope;

        public int NormalizeCount { get; private set; }

        public int CreateDefaultCount { get; private set; }

        public int IsEmptyCount { get; private set; }

        public int ResolveConflictCount { get; private set; }

        /// <summary>fromSchemaVersion of every UpgradePayload call, in order.</summary>
        public IReadOnlyList<int> UpgradeFromVersions => _upgradeFromVersions;

        /// <summary>Runs inside Normalize after the count.</summary>
        public Action<TData> NormalizeAction { get; set; }

        /// <summary>Replaces CreateDefault when set.</summary>
        public Func<TData> CreateDefaultFactory { get; set; }

        /// <summary>Replaces UpgradePayload when set.</summary>
        public Func<JObject, int, JObject> UpgradeFunc { get; set; }

        /// <summary>Replaces IsEmpty when set.</summary>
        public Func<TData, bool> IsEmptyFunc { get; set; }

        /// <summary>Replaces ResolveConflict when set; default policy otherwise.</summary>
        public TestConflictResolver<TData> ConflictResolver { get; set; }

        protected override int SchemaVersion => _schemaVersion;

        /// <summary>Live data for assertions only; never mutate it.</summary>
        public TData PeekData()
        {
            return Data;
        }

        public void ResetCounters()
        {
            NormalizeCount = 0;
            CreateDefaultCount = 0;
            IsEmptyCount = 0;
            ResolveConflictCount = 0;
            _upgradeFromVersions.Clear();
        }

        protected sealed override TData CreateDefault()
        {
            CreateDefaultCount++;
            Func<TData> factory = CreateDefaultFactory;
            return factory != null ? factory() : base.CreateDefault();
        }

        protected sealed override void Normalize(TData data)
        {
            NormalizeCount++;
            NormalizeAction?.Invoke(data);
        }

        protected sealed override JObject UpgradePayload(JObject payload, int fromSchemaVersion)
        {
            _upgradeFromVersions.Add(fromSchemaVersion);
            Func<JObject, int, JObject> upgrade = UpgradeFunc;
            return upgrade != null ? upgrade(payload, fromSchemaVersion) : base.UpgradePayload(payload, fromSchemaVersion);
        }

        protected sealed override bool IsEmpty(TData data)
        {
            IsEmptyCount++;
            Func<TData, bool> isEmpty = IsEmptyFunc;
            return isEmpty != null ? isEmpty(data) : base.IsEmpty(data);
        }

        protected sealed override ConflictResolution<TData> ResolveConflict(in ConflictContext<TData> context)
        {
            ResolveConflictCount++;
            TestConflictResolver<TData> resolver = ConflictResolver;
            return resolver != null ? resolver(in context) : base.ResolveConflict(in context);
        }
    }

    /// <summary>Basic profile slot; CloudSync by default.</summary>
    internal sealed class ProfileSlot : TestSlot<ProfileData>
    {
        public ProfileSlot(string key = TestSlotKeys.Player, SyncMode syncMode = SyncMode.CloudSync)
            : base(key, syncMode, SlotScope.Profile, 1)
        {
        }
    }

    /// <summary>Device-scoped LocalOnly settings slot.</summary>
    internal sealed class DeviceSettingsSlot : TestSlot<SettingsData>
    {
        public DeviceSettingsSlot(string key = TestSlotKeys.Settings)
            : base(key, SyncMode.LocalOnly, SlotScope.Device, 1)
        {
        }
    }

    /// <summary>Server-owned CloudReadOnly profile slot.</summary>
    internal sealed class CloudReadOnlySlot : TestSlot<RewardsData>
    {
        public CloudReadOnlySlot(string key = TestSlotKeys.ServerRewards)
            : base(key, SyncMode.CloudReadOnly, SlotScope.Profile, 1)
        {
        }
    }

    /// <summary>Normalize throws while ThrowOnNormalize is true.</summary>
    internal sealed class NormalizeThrowsSlot : TestSlot<ProfileData>
    {
        public NormalizeThrowsSlot(string key = TestSlotKeys.NormalizeThrows, SyncMode syncMode = SyncMode.LocalOnly)
            : base(key, syncMode, SlotScope.Profile, 1)
        {
            NormalizeAction = ThrowIfEnabled;
        }

        public bool ThrowOnNormalize { get; set; } = true;

        private void ThrowIfEnabled(ProfileData data)
        {
            if (ThrowOnNormalize)
            {
                throw new InvalidOperationException("Scripted Normalize failure.");
            }
        }
    }

    /// <summary>CloudSync inventory; conflicts merge as an ordinal-sorted union unless a side is empty.</summary>
    internal sealed class UnionMergeSlot : TestSlot<InventoryData>
    {
        public UnionMergeSlot(string key = TestSlotKeys.Inventory)
            : base(key, SyncMode.CloudSync, SlotScope.Profile, 1)
        {
            ConflictResolver = Union;
        }

        private static ConflictResolution<InventoryData> Union(in ConflictContext<InventoryData> context)
        {
            if (context.LocalIsEmpty || context.CloudIsEmpty)
            {
                return DefaultConflictPolicy.Resolve(in context);
            }

            var items = new SortedSet<string>(StringComparer.Ordinal);
            items.UnionWith(context.Local.Items);
            items.UnionWith(context.Cloud.Items);
            return ConflictResolution<InventoryData>.Merged(new InventoryData { Items = new List<string>(items) });
        }
    }

    /// <summary>Schema 1 writer for the shared schema key.</summary>
    internal sealed class SchemaV1Slot : TestSlot<SchemaV1Data>
    {
        public SchemaV1Slot(string key = TestSlotKeys.Schema, SyncMode syncMode = SyncMode.CloudSync)
            : base(key, syncMode, SlotScope.Profile, 1)
        {
        }
    }

    /// <summary>Schema 2 of the shared schema key; upgrades v1 payloads.</summary>
    internal sealed class SchemaV2Slot : TestSlot<SchemaV2Data>
    {
        public SchemaV2Slot(string key = TestSlotKeys.Schema, SyncMode syncMode = SyncMode.CloudSync)
            : base(key, syncMode, SlotScope.Profile, 2)
        {
            UpgradeFunc = SchemaMigrations.Step;
        }
    }

    /// <summary>Schema 3 of the shared schema key; upgrades v1 and v2 payloads step by step.</summary>
    internal sealed class SchemaV3Slot : TestSlot<SchemaV3Data>
    {
        public SchemaV3Slot(string key = TestSlotKeys.Schema, SyncMode syncMode = SyncMode.CloudSync)
            : base(key, syncMode, SlotScope.Profile, 3)
        {
            UpgradeFunc = SchemaMigrations.Step;
        }
    }

    /// <summary>List with default items, initialized sub-object, nullable sub-object and dictionary initializer.</summary>
    internal sealed class InitializerSlot : TestSlot<InitializerData>
    {
        public InitializerSlot(string key = TestSlotKeys.Initializers, SyncMode syncMode = SyncMode.CloudSync)
            : base(key, syncMode, SlotScope.Profile, 1)
        {
            NormalizeAction = FillOptionalStats;
        }

        /// <summary>When true, Normalize creates OptionalStats if null.</summary>
        public bool NormalizeFillsOptionalStats { get; set; }

        private void FillOptionalStats(InitializerData data)
        {
            if (NormalizeFillsOptionalStats && data.OptionalStats == null)
            {
                data.OptionalStats = new InitializerStats();
            }
        }
    }

    /// <summary>Hook purity probe: set the hook delegates to call Service or OtherSlot from inside a hook.</summary>
    internal sealed class HookCallsServiceSlot : TestSlot<ProfileData>
    {
        public HookCallsServiceSlot(string key = TestSlotKeys.HookCalls, SyncMode syncMode = SyncMode.CloudSync, int schemaVersion = 1)
            : base(key, syncMode, SlotScope.Profile, schemaVersion)
        {
        }

        public ISaveService Service { get; set; }

        public ProfileSlot OtherSlot { get; set; }
    }

    /// <summary>Slot generators for participation tests.</summary>
    internal static class TestSlots
    {
        /// <summary>Profile slots keyed {prefix}_00, {prefix}_01, ...</summary>
        public static ProfileSlot[] GenerateProfileSlots(int count, SyncMode syncMode = SyncMode.CloudSync, string prefix = "slot")
        {
            var slots = new ProfileSlot[count];
            for (int i = 0; i < count; i++)
            {
                slots[i] = new ProfileSlot(prefix + "_" + i.ToString("00", CultureInfo.InvariantCulture), syncMode);
            }

            return slots;
        }
    }
}
