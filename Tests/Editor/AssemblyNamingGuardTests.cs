using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace Ecanakli.SaveSystem.Tests
{
    /// <summary>
    /// Guards the naming rules that let multiple DI integrations compile side by side (02 addendum-1 C.5, I):
    /// no namespace segment or public type name reveals a framework, and no public type is duplicated across assemblies.
    /// Runs over whatever Ecanakli.SaveSystem* assemblies are loaded in this domain; it does not reference them directly.
    /// </summary>
    [TestFixture]
    public sealed class AssemblyNamingGuardTests
    {
        private static readonly string[] ForbiddenNamespaceSegments = { "Zenject", "VContainer", "Editor" };

        private static readonly string[] ForbiddenTypeNameFragments = { "Zenject", "VContainer" };

        [Test]
        public void NoNamespaceSegment_IsZenjectOrVContainerOrEditor()
        {
            foreach (Assembly assembly in LoadedPackageAssemblies())
            {
                foreach (Type type in GetExportedTypesSafe(assembly))
                {
                    string ns = type.Namespace;
                    if (string.IsNullOrEmpty(ns))
                    {
                        continue;
                    }

                    foreach (string segment in ns.Split('.'))
                    {
                        Assert.That(
                            ForbiddenNamespaceSegments,
                            Has.No.Member(segment),
                            assembly.GetName().Name + ": " + type.FullName + " has forbidden namespace segment '" + segment + "'.");
                    }
                }
            }
        }

        [Test]
        public void NoPublicTypeName_ContainsFrameworkName()
        {
            foreach (Assembly assembly in LoadedPackageAssemblies())
            {
                foreach (Type type in GetExportedTypesSafe(assembly))
                {
                    foreach (string fragment in ForbiddenTypeNameFragments)
                    {
                        Assert.That(
                            type.Name,
                            Does.Not.Contain(fragment),
                            assembly.GetName().Name + ": " + type.FullName + " contains forbidden fragment '" + fragment + "'.");
                    }
                }
            }
        }

        [Test]
        public void NoPublicFullTypeName_AppearsInTwoAssemblies()
        {
            var owners = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Assembly assembly in LoadedPackageAssemblies())
            {
                foreach (Type type in GetExportedTypesSafe(assembly))
                {
                    string fullName = type.FullName;
                    string assemblyName = assembly.GetName().Name;
                    if (owners.TryGetValue(fullName, out string owner) && !string.Equals(owner, assemblyName, StringComparison.Ordinal))
                    {
                        Assert.Fail(fullName + " is public in both " + owner + " and " + assemblyName + ".");
                    }

                    owners[fullName] = assemblyName;
                }
            }
        }

        private static IEnumerable<Assembly> LoadedPackageAssemblies()
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => assembly.GetName().Name.StartsWith("Ecanakli.SaveSystem", StringComparison.Ordinal));
        }

        private static IEnumerable<Type> GetExportedTypesSafe(Assembly assembly)
        {
            try
            {
                return assembly.GetExportedTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                return exception.Types.Where(type => type != null && type.IsPublic);
            }
        }
    }
}
