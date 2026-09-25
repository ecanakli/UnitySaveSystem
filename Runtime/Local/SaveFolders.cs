using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Ecanakli.SaveSystem
{
    /// <summary>Outcome of SaveFolders.DeleteAllLocalData.</summary>
    public enum LocalWipeStatus
    {
        /// <summary>device/ and/or profiles/ were deleted.</summary>
        Deleted = 0,

        /// <summary>Neither directory existed.</summary>
        NothingToDelete = 1,

        /// <summary>A live SaveService owns the root; nothing was deleted.</summary>
        InUse = 2,

        /// <summary>An IO error stopped the delete; data may be partially removed.</summary>
        Failed = 3,
    }

    /// <summary>Save root helpers for tools and games, plus the live-root registry.</summary>
    public static class SaveFolders
    {
        /// <summary>Directory name of the save root under persistentDataPath.</summary>
        public const string SavesDirectoryName = "saves";

        private static readonly object LiveRootsLock = new object();
        private static readonly Dictionary<string, int> LiveRoots = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static string _defaultRootDirectory;

        /// <summary>{Application.persistentDataPath}/saves; first read must happen on the main thread.</summary>
        public static string DefaultRootDirectory
        {
            get
            {
                if (_defaultRootDirectory == null)
                {
                    _defaultRootDirectory = Path.Combine(Application.persistentDataPath, SavesDirectoryName);
                }

                return _defaultRootDirectory;
            }
        }

        /// <summary>Deletes only {root}/device and {root}/profiles; InUse when a live service owns the root. Throws on a null or empty root.</summary>
        public static LocalWipeStatus DeleteAllLocalData(string rootDirectory)
        {
            if (string.IsNullOrEmpty(rootDirectory))
            {
                throw new ArgumentException("Root directory must not be null or empty.", nameof(rootDirectory));
            }

            string root;
            try
            {
                root = NormalizeRoot(rootDirectory);
            }
            catch (Exception)
            {
                return LocalWipeStatus.Failed;
            }

            // Held during the delete so no service can register this root mid-wipe
            lock (LiveRootsLock)
            {
                if (LiveRoots.ContainsKey(root))
                {
                    return LocalWipeStatus.InUse;
                }

                try
                {
                    var storage = new AtomicFileStorage(root);
                    bool hasDevice = storage.DirectoryExists(SaveLayout.DeviceDirectory);
                    bool hasProfiles = storage.DirectoryExists(SaveLayout.ProfilesDirectory);
                    if (!hasDevice && !hasProfiles)
                    {
                        return LocalWipeStatus.NothingToDelete;
                    }

                    if (hasDevice)
                    {
                        storage.DeleteDirectory(SaveLayout.DeviceDirectory);
                    }

                    if (hasProfiles)
                    {
                        storage.DeleteDirectory(SaveLayout.ProfilesDirectory);
                    }

                    return LocalWipeStatus.Deleted;
                }
                catch (Exception)
                {
                    return LocalWipeStatus.Failed;
                }
            }
        }

        /// <summary>Marks a root as owned by a live service (reference counted); false when it was already live.</summary>
        internal static bool RegisterLiveRoot(string rootDirectory)
        {
            string root = NormalizeRoot(rootDirectory);
            lock (LiveRootsLock)
            {
                LiveRoots.TryGetValue(root, out int count);
                LiveRoots[root] = count + 1;
                return count == 0;
            }
        }

        /// <summary>Releases one registration; no-op when the root is not registered.</summary>
        internal static void UnregisterLiveRoot(string rootDirectory)
        {
            string root = NormalizeRoot(rootDirectory);
            lock (LiveRootsLock)
            {
                if (!LiveRoots.TryGetValue(root, out int count))
                {
                    return;
                }

                if (count <= 1)
                {
                    LiveRoots.Remove(root);
                }
                else
                {
                    LiveRoots[root] = count - 1;
                }
            }
        }

        /// <summary>True when a live service owns the root.</summary>
        internal static bool IsLiveRoot(string rootDirectory)
        {
            string root = NormalizeRoot(rootDirectory);
            lock (LiveRootsLock)
            {
                return LiveRoots.ContainsKey(root);
            }
        }

        /// <summary>Full path without trailing separators; registry key.</summary>
        internal static string NormalizeRoot(string rootDirectory)
        {
            if (string.IsNullOrEmpty(rootDirectory))
            {
                throw new ArgumentException("Root directory must not be null or empty.", nameof(rootDirectory));
            }

            string fullPath = Path.GetFullPath(rootDirectory);
            string trimmed = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return trimmed.Length == 0 ? fullPath : trimmed;
        }
    }
}
