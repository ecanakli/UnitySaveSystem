using System;
using System.IO;
using UnityEngine;

namespace Ecanakli.SaveSystem.Tests
{
    /// <summary>
    /// Unique save roots under Application.persistentDataPath for tests that use real files.
    /// Never returns or deletes the default save root; every root lives under one parent directory.
    /// </summary>
    internal static class PersistentDataTestRoot
    {
        /// <summary>Parent of every test root; also the guard for Delete.</summary>
        public const string ParentDirectoryName = "savesystem-playmode-tests";

        /// <summary>{persistentDataPath}/savesystem-playmode-tests/{guid}, created on disk. Main thread only.</summary>
        public static string Create()
        {
            string root = Path.Combine(Application.persistentDataPath, ParentDirectoryName, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }

        /// <summary>Deletes the tree; ignores IO errors so a teardown never masks the test failure. Refuses paths outside the parent.</summary>
        public static void Delete(string root)
        {
            if (string.IsNullOrEmpty(root) || root.IndexOf(ParentDirectoryName, StringComparison.Ordinal) < 0)
            {
                return;
            }

            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        /// <summary>Absolute path of a slot file inside a root, e.g. {root}/profiles/guest/player.json.</summary>
        public static string SlotFilePath(string root, ProfileId profile, string key)
        {
            return ToAbsolute(root, SaveLayout.SlotPath(SaveLayout.ProfileDirectory(profile), key));
        }

        /// <summary>Absolute path of a '/'-separated relative storage path.</summary>
        public static string ToAbsolute(string root, string relativePath)
        {
            return Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        }
    }
}
