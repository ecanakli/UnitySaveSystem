using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;

namespace Ecanakli.SaveSystem
{
    /// <summary>
    /// File-backed ISaveStorage: tmp write with fsync, .bak rotation and promote under a per-path lock.
    /// On Windows, rename and delete steps retry transient IOException or UnauthorizedAccessException inside the lock.
    /// </summary>
    public sealed class AtomicFileStorage : ISaveStorage
    {
        /// <summary>Default Windows retry count for rename and delete steps (15, 30, 60 ms).</summary>
        public const int DefaultReplaceRetryCount = 3;

        private const int BaseRetryDelayMilliseconds = 15;
        private const int MaxRetryDelayMilliseconds = 1000;

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN || UNITY_WSA
        private static readonly bool IsWindowsPlatform = true;
#else
        private static readonly bool IsWindowsPlatform = false;
#endif

        // Shared by all instances so two storages over the same root never interleave on a path
        private static readonly ConcurrentDictionary<string, object> PathLocks =
            new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        private readonly IFileOps _fileOps;
        private readonly int _replaceRetryCount;
        private readonly bool _retryTransientFailures;

        /// <summary>Creates storage over rootDirectory (null uses SaveFolders.DefaultRootDirectory, main thread). Retries apply on Windows only.</summary>
        public AtomicFileStorage(string rootDirectory = null, int replaceRetryCount = DefaultReplaceRetryCount)
            : this(rootDirectory ?? SaveFolders.DefaultRootDirectory, replaceRetryCount, SystemFileOps.Instance, IsWindowsPlatform)
        {
        }

        /// <summary>Test constructor: injectable file ops and explicit retry gating.</summary>
        internal AtomicFileStorage(string rootDirectory, int replaceRetryCount, IFileOps fileOps, bool retryTransientFailures)
        {
            if (string.IsNullOrEmpty(rootDirectory))
            {
                throw new ArgumentException("Root directory must not be null or empty.", nameof(rootDirectory));
            }

            if (replaceRetryCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(replaceRetryCount), "Must not be negative.");
            }

            RootDirectory = Path.GetFullPath(rootDirectory);
            _fileOps = fileOps ?? throw new ArgumentNullException(nameof(fileOps));
            _replaceRetryCount = replaceRetryCount;
            _retryTransientFailures = retryTransientFailures;
        }

        private enum FileStep
        {
            DeleteFile,
            MoveFile,
            MoveDirectory,
            DeleteDirectory,
        }

        /// <summary>Absolute root that relative paths resolve against.</summary>
        public string RootDirectory { get; }

        /// <inheritdoc />
        public byte[] ReadAllBytes(string relativePath)
        {
            string path = ToFullPath(relativePath);
            lock (GetPathLock(path))
            {
                if (!_fileOps.FileExists(path))
                {
                    return null;
                }

                try
                {
                    return _fileOps.ReadAllBytes(path);
                }
                catch (FileNotFoundException)
                {
                    return null;
                }
                catch (DirectoryNotFoundException)
                {
                    return null;
                }
            }
        }

        /// <inheritdoc />
        public void WriteAtomic(string relativePath, byte[] bytes)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            string path = ToFullPath(relativePath);
            string tmpPath = SaveLayout.TmpPath(path);
            string bakPath = SaveLayout.BakPath(path);

            lock (GetPathLock(path))
            {
                EnsureParentDirectory(path);

                try
                {
                    _fileOps.WriteAllBytesFlushed(tmpPath, bytes);
                }
                catch
                {
                    // A partial tmp is never recoverable
                    TryDeleteQuietly(tmpPath);
                    throw;
                }

                // From here a failure keeps tmp and .bak for load-time recovery
                if (_fileOps.FileExists(path))
                {
                    if (_fileOps.FileExists(bakPath))
                    {
                        Run(FileStep.DeleteFile, bakPath, null);
                    }

                    Run(FileStep.MoveFile, path, bakPath);
                }

                Run(FileStep.MoveFile, tmpPath, path);
            }
        }

        /// <inheritdoc />
        public bool FileExists(string relativePath)
        {
            string path = ToFullPath(relativePath);
            lock (GetPathLock(path))
            {
                return _fileOps.FileExists(path);
            }
        }

        /// <inheritdoc />
        public void DeleteFile(string relativePath)
        {
            string path = ToFullPath(relativePath);
            lock (GetPathLock(path))
            {
                if (_fileOps.FileExists(path))
                {
                    Run(FileStep.DeleteFile, path, null);
                }
            }
        }

        /// <inheritdoc />
        public void MoveFile(string sourceRelativePath, string destinationRelativePath)
        {
            string source = ToFullPath(sourceRelativePath);
            string destination = ToFullPath(destinationRelativePath);

            // Stable lock order avoids deadlocks between opposite moves
            string sourceKey = LockKey(source);
            string destinationKey = LockKey(destination);
            bool sourceFirst = string.Compare(sourceKey, destinationKey, StringComparison.OrdinalIgnoreCase) <= 0;
            object first = GetLockByKey(sourceFirst ? sourceKey : destinationKey);
            object second = GetLockByKey(sourceFirst ? destinationKey : sourceKey);

            lock (first)
            {
                lock (second)
                {
                    if (_fileOps.FileExists(destination) || _fileOps.DirectoryExists(destination))
                    {
                        throw new IOException("Destination already exists: " + destinationRelativePath);
                    }

                    EnsureParentDirectory(destination);
                    Run(FileStep.MoveFile, source, destination);
                }
            }
        }

        /// <inheritdoc />
        public bool DirectoryExists(string relativeDirectory)
        {
            return _fileOps.DirectoryExists(ToFullPath(relativeDirectory));
        }

        /// <inheritdoc />
        public void CreateDirectory(string relativeDirectory)
        {
            _fileOps.CreateDirectory(ToFullPath(relativeDirectory));
        }

        /// <inheritdoc />
        public IReadOnlyList<string> ListFileNames(string relativeDirectory)
        {
            string directory = ToFullPath(relativeDirectory);
            if (!_fileOps.DirectoryExists(directory))
            {
                return Array.Empty<string>();
            }

            string[] names = _fileOps.GetFileNames(directory);
            Array.Sort(names, StringComparer.Ordinal);
            return names;
        }

        /// <inheritdoc />
        public IReadOnlyList<string> ListDirectoryNames(string relativeDirectory)
        {
            string directory = ToFullPath(relativeDirectory);
            if (!_fileOps.DirectoryExists(directory))
            {
                return Array.Empty<string>();
            }

            string[] names = _fileOps.GetDirectoryNames(directory);
            Array.Sort(names, StringComparer.Ordinal);
            return names;
        }

        /// <inheritdoc />
        public void MoveDirectory(string sourceRelativeDirectory, string destinationRelativeDirectory)
        {
            string source = ToFullPath(sourceRelativeDirectory);
            string destination = ToFullPath(destinationRelativeDirectory);
            if (_fileOps.DirectoryExists(destination) || _fileOps.FileExists(destination))
            {
                throw new IOException("Destination already exists: " + destinationRelativeDirectory);
            }

            EnsureParentDirectory(destination);
            Run(FileStep.MoveDirectory, source, destination);
        }

        /// <inheritdoc />
        public void DeleteDirectory(string relativeDirectory)
        {
            string directory = ToFullPath(relativeDirectory);
            if (_fileOps.DirectoryExists(directory))
            {
                Run(FileStep.DeleteDirectory, directory, null);
            }
        }

        /// <summary>Delay before retry attempt (0-based): 15, 30, 60 ms, doubling, capped at 1 s.</summary>
        internal static int GetRetryDelayMilliseconds(int attempt)
        {
            return Math.Min(BaseRetryDelayMilliseconds << Math.Min(Math.Max(attempt, 0), 10), MaxRetryDelayMilliseconds);
        }

        private void Run(FileStep step, string path, string destination)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    Execute(step, path, destination);
                    return;
                }
                catch (Exception exception) when (ShouldRetry(exception, attempt))
                {
                    _fileOps.Sleep(GetRetryDelayMilliseconds(attempt));
                }
            }
        }

        private void Execute(FileStep step, string path, string destination)
        {
            switch (step)
            {
                case FileStep.DeleteFile:
                    _fileOps.DeleteFile(path);
                    break;
                case FileStep.MoveFile:
                    _fileOps.MoveFile(path, destination);
                    break;
                case FileStep.MoveDirectory:
                    _fileOps.MoveDirectory(path, destination);
                    break;
                case FileStep.DeleteDirectory:
                    _fileOps.DeleteDirectory(path);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(step));
            }
        }

        private bool ShouldRetry(Exception exception, int attempt)
        {
            return _retryTransientFailures
                && attempt < _replaceRetryCount
                && StorageErrorClassifier.IsRetryableReplaceFailure(exception);
        }

        private void EnsureParentDirectory(string path)
        {
            string parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent) && !_fileOps.DirectoryExists(parent))
            {
                _fileOps.CreateDirectory(parent);
            }
        }

        private void TryDeleteQuietly(string path)
        {
            try
            {
                if (_fileOps.FileExists(path))
                {
                    _fileOps.DeleteFile(path);
                }
            }
            catch (Exception)
            {
                // Best effort; the original failure is rethrown by the caller
            }
        }

        private string ToFullPath(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath))
            {
                throw new ArgumentException("Relative path must not be null or empty.", nameof(relativePath));
            }

            if (Path.IsPathRooted(relativePath))
            {
                throw new ArgumentException("Path must be relative: " + relativePath, nameof(relativePath));
            }

            string[] segments = relativePath.Split('/', '\\');
            for (int i = 0; i < segments.Length; i++)
            {
                string segment = segments[i];
                if (segment.Length == 0 || segment == "." || segment == "..")
                {
                    throw new ArgumentException("Invalid relative path: " + relativePath, nameof(relativePath));
                }
            }

            return Path.Combine(RootDirectory, string.Join(Path.DirectorySeparatorChar.ToString(), segments));
        }

        private static object GetPathLock(string fullPath)
        {
            return GetLockByKey(LockKey(fullPath));
        }

        private static object GetLockByKey(string key)
        {
            return PathLocks.GetOrAdd(key, _ => new object());
        }

        // path, path.tmp and path.bak share one lock
        private static string LockKey(string fullPath)
        {
            if (fullPath.EndsWith(SaveLayout.TmpSuffix, StringComparison.OrdinalIgnoreCase))
            {
                return fullPath.Substring(0, fullPath.Length - SaveLayout.TmpSuffix.Length);
            }

            if (fullPath.EndsWith(SaveLayout.BakSuffix, StringComparison.OrdinalIgnoreCase))
            {
                return fullPath.Substring(0, fullPath.Length - SaveLayout.BakSuffix.Length);
            }

            return fullPath;
        }
    }
}
