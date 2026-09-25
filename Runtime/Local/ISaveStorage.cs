using System.Collections.Generic;

namespace Ecanakli.SaveSystem
{
    /// <summary>
    /// Synchronous byte storage addressed by '/'-separated relative paths. Members may run on the thread pool,
    /// must be safe for concurrent calls on different paths, and throw on failure (IOException or SaveStorageException).
    /// </summary>
    public interface ISaveStorage
    {
        /// <summary>Reads the whole file; returns null when it does not exist.</summary>
        byte[] ReadAllBytes(string relativePath);

        /// <summary>Crash-safe replace: writes path.tmp, flushes, moves the old file to path.bak, then promotes path.tmp.</summary>
        void WriteAtomic(string relativePath, byte[] bytes);

        /// <summary>True when the file exists.</summary>
        bool FileExists(string relativePath);

        /// <summary>Deletes the file; no-op when it does not exist.</summary>
        void DeleteFile(string relativePath);

        /// <summary>Renames a file (used for quarantine); throws when the destination exists.</summary>
        void MoveFile(string sourceRelativePath, string destinationRelativePath);

        /// <summary>True when the directory exists.</summary>
        bool DirectoryExists(string relativeDirectory);

        /// <summary>Creates the directory and its parents; no-op when it exists.</summary>
        void CreateDirectory(string relativeDirectory);

        /// <summary>File names (not paths) directly inside the directory, ordinal sorted; empty when it does not exist.</summary>
        IReadOnlyList<string> ListFileNames(string relativeDirectory);

        /// <summary>Directory names (not paths) directly inside the directory, ordinal sorted; empty when it does not exist.</summary>
        IReadOnlyList<string> ListDirectoryNames(string relativeDirectory);

        /// <summary>Renames a directory (guest claim); throws when the destination exists.</summary>
        void MoveDirectory(string sourceRelativeDirectory, string destinationRelativeDirectory);

        /// <summary>Deletes the directory recursively; no-op when it does not exist.</summary>
        void DeleteDirectory(string relativeDirectory);
    }
}
