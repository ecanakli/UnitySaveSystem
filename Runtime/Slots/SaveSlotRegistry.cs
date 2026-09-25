using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Ecanakli.SaveSystem
{
    /// <summary>Validated, freezable slot list; throws at composition time on invalid registrations.</summary>
    internal sealed class SaveSlotRegistry
    {
        /// <summary>Longest valid slot key.</summary>
        public const int MaxKeyLength = 64;

        private const int AnyFilter = -1;
        private const int ProfileCloudCacheKey = int.MaxValue;

        private readonly List<SaveSlot> _slots = new List<SaveSlot>();
        private readonly ReadOnlyCollection<SaveSlot> _readOnlySlots;
        private readonly Dictionary<string, SaveSlot> _byKey = new Dictionary<string, SaveSlot>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, SaveSlot[]> _selectionCache = new Dictionary<int, SaveSlot[]>();

        public SaveSlotRegistry()
        {
            _readOnlySlots = _slots.AsReadOnly();
        }

        /// <summary>Registers every slot in order; throws on the first invalid one.</summary>
        public SaveSlotRegistry(IEnumerable<SaveSlot> slots)
            : this()
        {
            if (slots == null)
            {
                throw new ArgumentNullException(nameof(slots));
            }

            foreach (SaveSlot slot in slots)
            {
                Register(slot);
            }
        }

        public bool IsFrozen { get; private set; }

        public int Count => _slots.Count;

        /// <summary>All slots in registration order.</summary>
        public IReadOnlyList<SaveSlot> Slots => _readOnlySlots;

        /// <summary>True for keys matching ^[A-Za-z0-9_-]{1,64}$.</summary>
        public static bool IsValidKey(string key)
        {
            if (string.IsNullOrEmpty(key) || key.Length > MaxKeyLength)
            {
                return false;
            }

            for (int i = 0; i < key.Length; i++)
            {
                char c = key[i];
                bool valid = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-';
                if (!valid)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Validates a single slot's declaration; throws ArgumentException.</summary>
        public static void Validate(SaveSlot slot)
        {
            if (slot == null)
            {
                throw new ArgumentNullException(nameof(slot));
            }

            string typeName = slot.GetType().FullName;
            string key = slot.Key;
            if (!IsValidKey(key))
            {
                throw new ArgumentException("Slot key '" + key + "' of " + typeName + " is invalid; it must match ^[A-Za-z0-9_-]{1,64}$.");
            }

            if (SaveLayout.IsReservedSlotKey(key))
            {
                throw new ArgumentException(
                    "Slot key '" + key + "' of " + typeName
                    + " is reserved (device, profile and the Windows device names con, prn, aux, nul, com1-com9, lpt1-lpt9).");
            }

            SyncMode mode = slot.SyncMode;
            SlotScope scope = slot.Scope;
            if (!Enum.IsDefined(typeof(SyncMode), mode))
            {
                throw new ArgumentException("Slot '" + key + "' of " + typeName + " has an unknown SyncMode " + (int)mode + ".");
            }

            if (!Enum.IsDefined(typeof(SlotScope), scope))
            {
                throw new ArgumentException("Slot '" + key + "' of " + typeName + " has an unknown Scope " + (int)scope + ".");
            }

            if (scope == SlotScope.Device && mode != SyncMode.LocalOnly)
            {
                throw new ArgumentException("Slot '" + key + "' of " + typeName + " is Device scope and must be LocalOnly (was " + mode + ").");
            }

            if (mode == SyncMode.CloudReadOnly && scope != SlotScope.Profile)
            {
                throw new ArgumentException("Slot '" + key + "' of " + typeName + " is CloudReadOnly and must be Profile scope.");
            }

            if (slot.SupportedSchema < 1)
            {
                throw new ArgumentException("Slot '" + key + "' of " + typeName + " has SchemaVersion " + slot.SupportedSchema + "; it must be at least 1.");
            }
        }

        /// <summary>Validates and adds a slot; throws when frozen, invalid or a duplicate key (case-insensitive).</summary>
        public void Register(SaveSlot slot)
        {
            if (slot == null)
            {
                throw new ArgumentNullException(nameof(slot));
            }

            if (IsFrozen)
            {
                throw new InvalidOperationException("Slot '" + slot.Key + "' cannot be registered after the save service was initialized.");
            }

            Validate(slot);
            string key = slot.Key;
            if (_byKey.TryGetValue(key, out SaveSlot existing))
            {
                if (ReferenceEquals(existing, slot))
                {
                    throw new ArgumentException("Slot '" + key + "' (" + slot.GetType().FullName + ") is registered twice.");
                }

                throw new ArgumentException(
                    "Slot key '" + key + "' of " + slot.GetType().FullName + " duplicates '" + existing.Key + "' of "
                    + existing.GetType().FullName + " (keys are compared case-insensitively).");
            }

            _byKey.Add(key, slot);
            _slots.Add(slot);
            _selectionCache.Clear();
        }

        /// <summary>Blocks further registrations.</summary>
        public void Freeze()
        {
            IsFrozen = true;
        }

        /// <summary>True when this exact instance is registered.</summary>
        public bool Contains(SaveSlot slot)
        {
            return slot != null && _byKey.TryGetValue(slot.Key, out SaveSlot existing) && ReferenceEquals(existing, slot);
        }

        /// <summary>Case-insensitive key lookup.</summary>
        public bool TryGet(string key, out SaveSlot slot)
        {
            if (key == null)
            {
                slot = null;
                return false;
            }

            return _byKey.TryGetValue(key, out slot);
        }

        public IReadOnlyList<SaveSlot> Select(SlotScope scope)
        {
            return Select((int)scope, AnyFilter);
        }

        public IReadOnlyList<SaveSlot> Select(SyncMode mode)
        {
            return Select(AnyFilter, (int)mode);
        }

        public IReadOnlyList<SaveSlot> Select(SlotScope scope, SyncMode mode)
        {
            return Select((int)scope, (int)mode);
        }

        /// <summary>Profile-scope slots that are CloudSync or CloudReadOnly (restore candidates).</summary>
        public IReadOnlyList<SaveSlot> SelectProfileCloudSlots()
        {
            if (_selectionCache.TryGetValue(ProfileCloudCacheKey, out SaveSlot[] cached))
            {
                return cached;
            }

            var result = new List<SaveSlot>();
            for (int i = 0; i < _slots.Count; i++)
            {
                SaveSlot slot = _slots[i];
                if (slot.Scope == SlotScope.Profile && slot.SyncMode != SyncMode.LocalOnly)
                {
                    result.Add(slot);
                }
            }

            SaveSlot[] array = result.ToArray();
            _selectionCache[ProfileCloudCacheKey] = array;
            return array;
        }

        private IReadOnlyList<SaveSlot> Select(int scopeFilter, int modeFilter)
        {
            int cacheKey = ((scopeFilter + 1) * 16) + modeFilter + 1;
            if (_selectionCache.TryGetValue(cacheKey, out SaveSlot[] cached))
            {
                return cached;
            }

            var result = new List<SaveSlot>();
            for (int i = 0; i < _slots.Count; i++)
            {
                SaveSlot slot = _slots[i];
                if ((scopeFilter == AnyFilter || (int)slot.Scope == scopeFilter) && (modeFilter == AnyFilter || (int)slot.SyncMode == modeFilter))
                {
                    result.Add(slot);
                }
            }

            SaveSlot[] array = result.ToArray();
            _selectionCache[cacheKey] = array;
            return array;
        }
    }
}
