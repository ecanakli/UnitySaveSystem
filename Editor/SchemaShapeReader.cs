using System;
using System.Collections.Generic;
using System.Reflection;
using Ecanakli.SaveSystem;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Ecanakli.SaveSystem.EditorTools
{
    /// <summary>One member of a data shape: dotted path (with "[]" segments for collection/dictionary elements) and a friendly type name.</summary>
    internal readonly struct SchemaMember
    {
        public SchemaMember(string path, string typeName)
        {
            Path = path;
            TypeName = typeName;
        }

        public string Path { get; }

        public string TypeName { get; }
    }

    /// <summary>Reflected shape of one concrete SaveSlot&lt;TData&gt; subclass at the current build.</summary>
    internal sealed class SlotShape
    {
        public SlotShape(string slotTypeName, string key, int schemaVersion, bool hasUpgradePayloadOverride, IReadOnlyList<SchemaMember> members)
        {
            SlotTypeName = slotTypeName;
            Key = key;
            SchemaVersion = schemaVersion;
            HasUpgradePayloadOverride = hasUpgradePayloadOverride;
            Members = members;
        }

        public string SlotTypeName { get; }

        public string Key { get; }

        public int SchemaVersion { get; }

        public bool HasUpgradePayloadOverride { get; }

        /// <summary>Sorted by Path, ordinal; deterministic regardless of reflection declaration order.</summary>
        public IReadOnlyList<SchemaMember> Members { get; }
    }

    /// <summary>
    /// Pure reflection over SaveSlot&lt;TData&gt; subclasses and their data shape. No UnityEditor calls; unit testable.
    /// Construction of a slot instance is unavoidable to read Key (abstract) and an overridden SchemaVersion (protected virtual).
    /// </summary>
    internal static class SchemaShapeReader
    {
        /// <summary>Guards against pathological nesting even without a cycle.</summary>
        internal const int MaxDepth = 8;

        private static readonly HashSet<Type> LeafTypes = new HashSet<Type>
        {
            typeof(bool), typeof(byte), typeof(sbyte), typeof(short), typeof(ushort),
            typeof(int), typeof(uint), typeof(long), typeof(ulong),
            typeof(float), typeof(double), typeof(decimal), typeof(char),
            typeof(string), typeof(DateTime), typeof(DateTimeOffset), typeof(TimeSpan),
            typeof(Guid), typeof(Uri), typeof(object),
        };

        /// <summary>Finds concrete, non-generic-definition subclasses of SaveSlot&lt;TData&gt; across every loaded assembly.</summary>
        internal static IReadOnlyList<Type> DiscoverSlotTypes()
        {
            var result = new List<Type>();
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException loadException)
                {
                    types = loadException.Types;
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (Type type in types)
                {
                    if (type == null || type.IsAbstract || type.IsGenericTypeDefinition || type.ContainsGenericParameters)
                    {
                        continue;
                    }

                    if (FindDataType(type) != null)
                    {
                        result.Add(type);
                    }
                }
            }

            result.Sort((a, b) => string.CompareOrdinal(a.FullName, b.FullName));
            return result;
        }

        /// <summary>True when assemblyName looks like a test assembly (".Tests" or "TestUtilities"); the guard's own test slots match this.</summary>
        internal static bool IsTestAssembly(string assemblyName)
        {
            if (string.IsNullOrEmpty(assemblyName))
            {
                return false;
            }

            // Covers Foo.Tests, Foo.PlayModeTests and shared test utility assemblies; case-sensitive so "Contests" does not match
            return assemblyName.EndsWith("Tests", StringComparison.Ordinal)
                   || assemblyName.Contains(".Tests")
                   || assemblyName.Contains("TestUtilities");
        }

        /// <summary>Walks base types to find the TData closing SaveSlot&lt;TData&gt;; null when slotType does not derive from it.</summary>
        internal static Type FindDataType(Type slotType)
        {
            for (Type current = slotType; current != null && current != typeof(object); current = current.BaseType)
            {
                if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(SaveSlot<>))
                {
                    return current.GetGenericArguments()[0];
                }
            }

            return null;
        }

        /// <summary>True when UpgradePayload is declared below the SaveSlot&lt;TData&gt; base (an intermediate base class counts).</summary>
        internal static bool HasUpgradePayloadOverride(Type slotType, Type dataType)
        {
            Type closedBase = typeof(SaveSlot<>).MakeGenericType(dataType);
            MethodInfo method = slotType.GetMethod(
                "UpgradePayload",
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(JObject), typeof(int) },
                null);

            return method != null && method.DeclaringType != closedBase;
        }

        /// <summary>
        /// Reads the full shape of one slot type: constructs an instance (parameterless, public or non-public) to read Key and
        /// SchemaVersion, then walks TData's serializable members. Never throws; returns null with a reason on failure.
        /// </summary>
        internal static SlotShape TryReadShape(Type slotType, out string failureReason)
        {
            failureReason = null;
            if (slotType == null)
            {
                failureReason = "Slot type is null.";
                return null;
            }

            Type dataType = FindDataType(slotType);
            if (dataType == null)
            {
                failureReason = "Type does not derive from SaveSlot<TData>.";
                return null;
            }

            object instance;
            try
            {
                instance = Activator.CreateInstance(slotType, nonPublic: true);
            }
            catch (Exception exception)
            {
                failureReason = "Could not construct an instance (needs a parameterless constructor): " + exception.Message;
                return null;
            }

            string key;
            try
            {
                key = ((SaveSlot)instance).Key;
            }
            catch (Exception exception)
            {
                failureReason = "Reading Key threw: " + exception.Message;
                return null;
            }

            int schemaVersion;
            try
            {
                Type closedBase = typeof(SaveSlot<>).MakeGenericType(dataType);
                PropertyInfo schemaVersionProperty = closedBase.GetProperty("SchemaVersion", BindingFlags.NonPublic | BindingFlags.Instance);
                schemaVersion = (int)schemaVersionProperty.GetValue(instance);
            }
            catch (Exception exception)
            {
                failureReason = "Reading SchemaVersion threw: " + exception.Message;
                return null;
            }

            bool hasUpgradeOverride;
            try
            {
                hasUpgradeOverride = HasUpgradePayloadOverride(slotType, dataType);
            }
            catch (Exception exception)
            {
                // AmbiguousMatchException from a `new`-hidden UpgradePayload lands here.
                failureReason = "Checking the UpgradePayload override threw: " + exception.Message;
                return null;
            }

            IReadOnlyList<SchemaMember> members;
            try
            {
                members = ReadMembers(dataType);
            }
            catch (Exception exception)
            {
                // TypeLoadException from a member type in an assembly that failed to load lands here.
                failureReason = "Reading the data shape threw: " + exception.Message;
                return null;
            }

            return new SlotShape(slotType.FullName, key, schemaVersion, hasUpgradeOverride, members);
        }

        /// <summary>Builds the deterministic (sorted by Path) member list Newtonsoft would write for dataType.</summary>
        internal static IReadOnlyList<SchemaMember> ReadMembers(Type dataType)
        {
            var results = new List<SchemaMember>();
            var visiting = new HashSet<Type>();
            CollectMembers(dataType, string.Empty, 0, visiting, results);
            results.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
            return results;
        }

        /// <summary>Friendly type name used in both the member list and console messages: C# aliases, "List&lt;T&gt;", "T[]", "T?".</summary>
        internal static string FriendlyTypeName(Type type)
        {
            Type underlying = Nullable.GetUnderlyingType(type);
            if (underlying != null)
            {
                return FriendlyTypeName(underlying) + "?";
            }

            if (type.IsArray)
            {
                return FriendlyTypeName(type.GetElementType()) + "[]";
            }

            if (type.IsGenericType)
            {
                string name = type.Name;
                int tick = name.IndexOf('`');
                if (tick >= 0)
                {
                    name = name.Substring(0, tick);
                }

                Type[] args = type.GetGenericArguments();
                var argNames = new string[args.Length];
                for (int i = 0; i < args.Length; i++)
                {
                    argNames[i] = FriendlyTypeName(args[i]);
                }

                return name + "<" + string.Join(",", argNames) + ">";
            }

            return AliasOrName(type);
        }

        private static void CollectMembers(Type type, string pathPrefix, int depth, HashSet<Type> visiting, List<SchemaMember> results)
        {
            if (depth > MaxDepth)
            {
                return;
            }

            // Cycle guard: only blocks a type reappearing on its own recursion path, not legitimate sibling reuse.
            if (!visiting.Add(type))
            {
                return;
            }

            try
            {
                foreach ((string name, Type memberType) in GetSerializableMembers(type))
                {
                    string path = pathPrefix.Length == 0 ? name : pathPrefix + "." + name;
                    AppendMember(memberType, path, depth, visiting, results);
                }
            }
            finally
            {
                visiting.Remove(type);
            }
        }

        private static void AppendMember(Type memberType, string path, int depth, HashSet<Type> visiting, List<SchemaMember> results)
        {
            results.Add(new SchemaMember(path, FriendlyTypeName(memberType)));

            Type unwrapped = Nullable.GetUnderlyingType(memberType) ?? memberType;
            if (IsLeafType(unwrapped))
            {
                return;
            }

            if (TryGetDictionaryValueType(unwrapped, out Type valueType))
            {
                Type unwrappedValue = Nullable.GetUnderlyingType(valueType) ?? valueType;
                if (!IsLeafType(unwrappedValue))
                {
                    CollectMembers(unwrappedValue, path + "[]", depth + 1, visiting, results);
                }

                return;
            }

            if (TryGetCollectionElementType(unwrapped, out Type elementType))
            {
                Type unwrappedElement = Nullable.GetUnderlyingType(elementType) ?? elementType;
                if (!IsLeafType(unwrappedElement))
                {
                    CollectMembers(unwrappedElement, path + "[]", depth + 1, visiting, results);
                }

                return;
            }

            // Plain complex type: walk its members under the same dotted path.
            CollectMembers(unwrapped, path, depth + 1, visiting, results);
        }

        private static IEnumerable<(string Name, Type MemberType)> GetSerializableMembers(Type type)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;

            // Name -> (member type, declaring type). GetProperties/GetFields without DeclaredOnly returns both
            // sides of a `new`-hidden member; keep the most-derived declaration, matching Newtonsoft's own resolution.
            var byName = new Dictionary<string, (Type MemberType, Type DeclaringType)>(StringComparer.Ordinal);

            foreach (PropertyInfo property in type.GetProperties(flags))
            {
                if (property.GetIndexParameters().Length > 0)
                {
                    continue;
                }

                if (!property.CanRead || !property.CanWrite)
                {
                    continue;
                }

                if (Attribute.IsDefined(property, typeof(JsonIgnoreAttribute)))
                {
                    continue;
                }

                KeepMostDerived(byName, property.Name, property.PropertyType, property.DeclaringType);
            }

            foreach (FieldInfo field in type.GetFields(flags))
            {
                if (field.IsStatic)
                {
                    continue;
                }

                if (Attribute.IsDefined(field, typeof(JsonIgnoreAttribute)))
                {
                    continue;
                }

                KeepMostDerived(byName, field.Name, field.FieldType, field.DeclaringType);
            }

            foreach (KeyValuePair<string, (Type MemberType, Type DeclaringType)> entry in byName)
            {
                yield return (entry.Key, entry.Value.MemberType);
            }
        }

        private static void KeepMostDerived(
            Dictionary<string, (Type MemberType, Type DeclaringType)> byName,
            string name,
            Type memberType,
            Type declaringType)
        {
            if (byName.TryGetValue(name, out (Type MemberType, Type DeclaringType) existing) && !IsAtOrBelow(declaringType, existing.DeclaringType))
            {
                return;
            }

            byName[name] = (memberType, declaringType);
        }

        /// <summary>True when candidate is the same type as current or a subclass of it (i.e. at least as derived).</summary>
        private static bool IsAtOrBelow(Type candidate, Type current)
        {
            return candidate == current || candidate.IsSubclassOf(current);
        }

        private static bool IsLeafType(Type type)
        {
            if (type.IsEnum)
            {
                return true;
            }

            if (LeafTypes.Contains(type))
            {
                return true;
            }

            // JToken/JObject/JArray/JValue are already-serialized opaque JSON; walking their internals is meaningless here.
            return typeof(JToken).IsAssignableFrom(type);
        }

        private static bool TryGetDictionaryValueType(Type type, out Type valueType)
        {
            foreach (Type candidate in GetInterfacesIncludingSelf(type))
            {
                if (candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IDictionary<,>))
                {
                    valueType = candidate.GetGenericArguments()[1];
                    return true;
                }
            }

            valueType = null;
            return false;
        }

        private static bool TryGetCollectionElementType(Type type, out Type elementType)
        {
            if (type.IsArray)
            {
                elementType = type.GetElementType();
                return true;
            }

            foreach (Type candidate in GetInterfacesIncludingSelf(type))
            {
                if (candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                {
                    elementType = candidate.GetGenericArguments()[0];
                    return true;
                }
            }

            elementType = null;
            return false;
        }

        private static IEnumerable<Type> GetInterfacesIncludingSelf(Type type)
        {
            if (type.IsInterface)
            {
                yield return type;
            }

            foreach (Type iface in type.GetInterfaces())
            {
                yield return iface;
            }
        }

        private static string AliasOrName(Type type)
        {
            if (type == typeof(bool)) return "bool";
            if (type == typeof(byte)) return "byte";
            if (type == typeof(sbyte)) return "sbyte";
            if (type == typeof(short)) return "short";
            if (type == typeof(ushort)) return "ushort";
            if (type == typeof(int)) return "int";
            if (type == typeof(uint)) return "uint";
            if (type == typeof(long)) return "long";
            if (type == typeof(ulong)) return "ulong";
            if (type == typeof(float)) return "float";
            if (type == typeof(double)) return "double";
            if (type == typeof(decimal)) return "decimal";
            if (type == typeof(char)) return "char";
            if (type == typeof(string)) return "string";
            if (type == typeof(object)) return "object";
            return type.Name;
        }
    }
}
