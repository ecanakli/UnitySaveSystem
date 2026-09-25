using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace Ecanakli.SaveSystem.Tests
{
    /// <summary>ISaveStorage operations; used by fault rules and the call log.</summary>
    [Flags]
    internal enum StorageOperation
    {
        None = 0,
        Read = 1,
        Write = 2,
        FileExists = 4,
        DeleteFile = 8,
        MoveFile = 16,
        DirectoryExists = 32,
        CreateDirectory = 64,
        ListFileNames = 128,
        ListDirectoryNames = 256,
        MoveDirectory = 512,
        DeleteDirectory = 1024,
        Mutations = Write | DeleteFile | MoveFile | CreateDirectory | MoveDirectory | DeleteDirectory,
        All = Read | Write | FileExists | DeleteFile | MoveFile | DirectoryExists | CreateDirectory | ListFileNames | ListDirectoryNames | MoveDirectory
              | DeleteDirectory,
    }

    /// <summary>WriteAtomic steps in execution order (mirrors AtomicFileStorage).</summary>
    internal enum AtomicWriteStep
    {
        WriteTmp = 1,
        DeleteBak = 2,
        MovePrimaryToBak = 3,
        PromoteTmp = 4,
    }

    /// <summary>One logged storage call.</summary>
    internal readonly struct StorageCall
    {
        public StorageCall(StorageOperation operation, string path, string destinationPath)
        {
            Operation = operation;
            Path = path;
            DestinationPath = destinationPath;
        }

        public StorageOperation Operation { get; }

        public string Path { get; }

        /// <summary>Set for MoveFile and MoveDirectory.</summary>
        public string DestinationPath { get; }

        public override string ToString()
        {
            return DestinationPath == null ? Operation + " " + Path : Operation + " " + Path + " -> " + DestinationPath;
        }
    }

    /// <summary>Scripted storage failure. Pattern: exact path, prefix ending in '*', or null for any path.</summary>
    internal sealed class StorageFault
    {
        private readonly Func<Exception> _exceptionFactory;
        private int _remaining;

        internal StorageFault(StorageOperation operations, string pathPattern, AtomicWriteStep? writeStep, Func<Exception> exceptionFactory, int times)
        {
            if (times == 0 || times < -1)
            {
                throw new ArgumentOutOfRangeException(nameof(times), "Use a positive count or -1 for unlimited.");
            }

            _exceptionFactory = exceptionFactory ?? throw new ArgumentNullException(nameof(exceptionFactory));
            Operations = operations;
            PathPattern = pathPattern;
            WriteStep = writeStep;
            _remaining = times;
        }

        public StorageOperation Operations { get; }

        public string PathPattern { get; }

        /// <summary>Write step that fails; null means WriteTmp (nothing changed yet).</summary>
        public AtomicWriteStep? WriteStep { get; }

        public int HitCount { get; private set; }

        public bool IsExhausted => _remaining == 0;

        internal static bool PathMatches(string pattern, string path)
        {
            if (pattern == null)
            {
                return true;
            }

            if (path == null)
            {
                return false;
            }

            if (pattern.EndsWith("*", StringComparison.Ordinal))
            {
                return path.StartsWith(pattern.Substring(0, pattern.Length - 1), StringComparison.Ordinal);
            }

            return string.Equals(pattern, path, StringComparison.Ordinal);
        }

        internal bool Matches(StorageOperation operation, string path, string destinationPath, AtomicWriteStep? step)
        {
            if (_remaining == 0 || (Operations & operation) == 0)
            {
                return false;
            }

            if (operation == StorageOperation.Write && step != (WriteStep ?? AtomicWriteStep.WriteTmp))
            {
                return false;
            }

            return PathMatches(PathPattern, path) || (destinationPath != null && PathMatches(PathPattern, destinationPath));
        }

        internal Exception Hit()
        {
            if (_remaining > 0)
            {
                _remaining--;
            }

            HitCount++;
            return _exceptionFactory();
        }
    }

    /// <summary>Thrown by a simulated crash; the storage refuses every call until RecoverFromCrash.</summary>
    internal sealed class SimulatedCrashException : IOException
    {
        public SimulatedCrashException(string message)
            : base(message)
        {
        }
    }

    /// <summary>Deep copy of files and directories (not counters, faults or call log).</summary>
    internal sealed class StorageSnapshot
    {
        private readonly Dictionary<string, byte[]> _files;
        private readonly HashSet<string> _directories;

        internal StorageSnapshot(Dictionary<string, byte[]> files, HashSet<string> directories)
        {
            _files = files;
            _directories = directories;
        }

        public int FileCount => _files.Count;

        public IReadOnlyList<string> FilePaths
        {
            get
            {
                var paths = new List<string>(_files.Keys);
                paths.Sort(StringComparer.Ordinal);
                return paths;
            }
        }

        public byte[] GetBytes(string path)
        {
            return _files.TryGetValue(path, out byte[] bytes) ? (byte[])bytes.Clone() : null;
        }

        internal IEnumerable<KeyValuePair<string, byte[]>> Files => _files;

        internal IEnumerable<string> Directories => _directories;
    }

    /// <summary>
    /// Dictionary-backed ISaveStorage with AtomicFileStorage semantics (.tmp write, .bak rotation, promote),
    /// per-path scripted faults, crash simulation, write counters, a call log and snapshots. Thread-safe.
    /// </summary>
    internal sealed class InMemorySaveStorage : ISaveStorage
    {
        private readonly object _sync = new object();
        private readonly Dictionary<string, byte[]> _files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        private readonly HashSet<string> _directories = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<StorageFault> _faults = new List<StorageFault>();
        private readonly List<StorageCall> _calls = new List<StorageCall>();
        private readonly Dictionary<string, int> _writeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly HashSet<int> _writeThreadIds = new HashSet<int>();

        // Write park: models a slow disk before the file work starts, so another writer can overtake this one
        private readonly object _parkSync = new object();
        private readonly ManualResetEventSlim _parkRelease = new ManualResetEventSlim(false);
        private string _parkPathPattern;
        private TimeSpan _parkDuration;
        private int _parkRemaining;
        private int _parkedWrites;
        private int _parkedWriteTotal;

        private CrashPlan _crashPlan;
        private int _writeCount;
        private int _writeAttemptCount;
        private bool _crashed;

        /// <summary>Completed WriteAtomic calls.</summary>
        public int WriteCount
        {
            get
            {
                lock (_sync)
                {
                    return _writeCount;
                }
            }
        }

        /// <summary>WriteAtomic calls including failed and crashed ones.</summary>
        public int WriteAttemptCount
        {
            get
            {
                lock (_sync)
                {
                    return _writeAttemptCount;
                }
            }
        }

        /// <summary>Logged calls of write, delete, move and create operations.</summary>
        public int MutationCallCount => CountCalls(StorageOperation.Mutations);

        /// <summary>Managed thread ids that entered WriteAtomic, ascending. Cleared by ResetCounters.</summary>
        public IReadOnlyList<int> WriteThreadIds
        {
            get
            {
                lock (_sync)
                {
                    var ids = new List<int>(_writeThreadIds);
                    ids.Sort();
                    return ids;
                }
            }
        }

        public IReadOnlyList<StorageCall> Calls
        {
            get
            {
                lock (_sync)
                {
                    return _calls.ToArray();
                }
            }
        }

        public bool IsCrashed
        {
            get
            {
                lock (_sync)
                {
                    return _crashed;
                }
            }
        }

        /// <summary>Every file path, ordinal sorted.</summary>
        public IReadOnlyList<string> AllFilePaths
        {
            get
            {
                lock (_sync)
                {
                    var paths = new List<string>(_files.Keys);
                    paths.Sort(StringComparer.Ordinal);
                    return paths;
                }
            }
        }

        public byte[] ReadAllBytes(string relativePath)
        {
            string path = NormalizeFilePath(relativePath);
            lock (_sync)
            {
                Begin(StorageOperation.Read, path, null);
                return _files.TryGetValue(path, out byte[] bytes) ? Copy(bytes) : null;
            }
        }

        public void WriteAtomic(string relativePath, byte[] bytes)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            string path = NormalizeFilePath(relativePath);
            string tmp = SaveLayout.TmpPath(path);
            string bak = SaveLayout.BakPath(path);

            // Outside _sync: a parked write must not block writers to other paths
            ParkIfScheduled(path);

            lock (_sync)
            {
                _writeThreadIds.Add(Thread.CurrentThread.ManagedThreadId);
                ThrowIfCrashed();
                _calls.Add(new StorageCall(StorageOperation.Write, path, null));
                _writeAttemptCount++;
                EnsureDirectoryChain(ParentOf(path));

                Exception tmpFault = TakeFault(StorageOperation.Write, path, null, AtomicWriteStep.WriteTmp);
                if (tmpFault != null)
                {
                    // Production deletes a failed tmp
                    _files.Remove(tmp);
                    throw tmpFault;
                }

                _files[tmp] = Copy(bytes);
                CheckCrash(path, AtomicWriteStep.WriteTmp, true, tmp);

                if (_files.ContainsKey(path))
                {
                    if (_files.ContainsKey(bak))
                    {
                        CheckCrash(path, AtomicWriteStep.DeleteBak, false, tmp);
                        ThrowStepFault(path, AtomicWriteStep.DeleteBak);
                        _files.Remove(bak);
                        CheckCrash(path, AtomicWriteStep.DeleteBak, true, tmp);
                    }

                    CheckCrash(path, AtomicWriteStep.MovePrimaryToBak, false, tmp);
                    ThrowStepFault(path, AtomicWriteStep.MovePrimaryToBak);
                    _files[bak] = _files[path];
                    _files.Remove(path);
                    CheckCrash(path, AtomicWriteStep.MovePrimaryToBak, true, tmp);
                }

                CheckCrash(path, AtomicWriteStep.PromoteTmp, false, tmp);
                ThrowStepFault(path, AtomicWriteStep.PromoteTmp);
                _files[path] = _files[tmp];
                _files.Remove(tmp);
                CheckCrash(path, AtomicWriteStep.PromoteTmp, true, tmp);

                _writeCount++;
                _writeCounts.TryGetValue(path, out int count);
                _writeCounts[path] = count + 1;
            }
        }

        public bool FileExists(string relativePath)
        {
            string path = NormalizeFilePath(relativePath);
            lock (_sync)
            {
                Begin(StorageOperation.FileExists, path, null);
                return _files.ContainsKey(path);
            }
        }

        public void DeleteFile(string relativePath)
        {
            string path = NormalizeFilePath(relativePath);
            lock (_sync)
            {
                Begin(StorageOperation.DeleteFile, path, null);
                _files.Remove(path);
            }
        }

        public void MoveFile(string sourceRelativePath, string destinationRelativePath)
        {
            string from = NormalizeFilePath(sourceRelativePath);
            string to = NormalizeFilePath(destinationRelativePath);
            lock (_sync)
            {
                Begin(StorageOperation.MoveFile, from, to);
                if (!_files.TryGetValue(from, out byte[] bytes))
                {
                    throw new FileNotFoundException("Source file does not exist: " + from, from);
                }

                if (_files.ContainsKey(to) || _directories.Contains(to))
                {
                    throw new IOException("Destination already exists: " + to);
                }

                EnsureDirectoryChain(ParentOf(to));
                _files.Remove(from);
                _files[to] = bytes;
            }
        }

        public bool DirectoryExists(string relativeDirectory)
        {
            string directory = NormalizeDirectoryPath(relativeDirectory);
            lock (_sync)
            {
                Begin(StorageOperation.DirectoryExists, directory, null);
                return directory.Length == 0 || _directories.Contains(directory);
            }
        }

        public void CreateDirectory(string relativeDirectory)
        {
            string directory = NormalizeDirectoryPath(relativeDirectory);
            lock (_sync)
            {
                Begin(StorageOperation.CreateDirectory, directory, null);
                if (_files.ContainsKey(directory))
                {
                    throw new IOException("A file already exists at " + directory);
                }

                EnsureDirectoryChain(directory);
            }
        }

        public IReadOnlyList<string> ListFileNames(string relativeDirectory)
        {
            string directory = NormalizeDirectoryPath(relativeDirectory);
            lock (_sync)
            {
                Begin(StorageOperation.ListFileNames, directory, null);
                var names = new List<string>();
                foreach (string file in _files.Keys)
                {
                    if (string.Equals(ParentOf(file), directory, StringComparison.Ordinal))
                    {
                        names.Add(NameOf(file));
                    }
                }

                names.Sort(StringComparer.Ordinal);
                return names;
            }
        }

        public IReadOnlyList<string> ListDirectoryNames(string relativeDirectory)
        {
            string directory = NormalizeDirectoryPath(relativeDirectory);
            lock (_sync)
            {
                Begin(StorageOperation.ListDirectoryNames, directory, null);
                var names = new List<string>();
                foreach (string candidate in _directories)
                {
                    if (string.Equals(ParentOf(candidate), directory, StringComparison.Ordinal))
                    {
                        names.Add(NameOf(candidate));
                    }
                }

                names.Sort(StringComparer.Ordinal);
                return names;
            }
        }

        public void MoveDirectory(string sourceRelativeDirectory, string destinationRelativeDirectory)
        {
            string from = NormalizeDirectoryPath(sourceRelativeDirectory);
            string to = NormalizeDirectoryPath(destinationRelativeDirectory);
            lock (_sync)
            {
                Begin(StorageOperation.MoveDirectory, from, to);
                if (from.Length == 0 || !_directories.Contains(from))
                {
                    throw new DirectoryNotFoundException("Source directory does not exist: " + from);
                }

                if (to.Length == 0 || _directories.Contains(to) || _files.ContainsKey(to))
                {
                    throw new IOException("Destination already exists: " + to);
                }

                if (IsUnder(to, from))
                {
                    throw new IOException("Cannot move " + from + " into itself.");
                }

                EnsureDirectoryChain(ParentOf(to));

                var movedDirectories = new List<string>();
                foreach (string directory in _directories)
                {
                    if (string.Equals(directory, from, StringComparison.Ordinal) || IsUnder(directory, from))
                    {
                        movedDirectories.Add(directory);
                    }
                }

                foreach (string directory in movedDirectories)
                {
                    _directories.Remove(directory);
                    _directories.Add(to + directory.Substring(from.Length));
                }

                var movedFiles = new List<string>();
                foreach (string file in _files.Keys)
                {
                    if (IsUnder(file, from))
                    {
                        movedFiles.Add(file);
                    }
                }

                foreach (string file in movedFiles)
                {
                    byte[] bytes = _files[file];
                    _files.Remove(file);
                    _files[to + file.Substring(from.Length)] = bytes;
                }
            }
        }

        public void DeleteDirectory(string relativeDirectory)
        {
            string directory = NormalizeDirectoryPath(relativeDirectory);
            lock (_sync)
            {
                Begin(StorageOperation.DeleteDirectory, directory, null);
                RemoveTree(directory);
            }
        }

        /// <summary>Adds a fault; times -1 means unlimited.</summary>
        public StorageFault Fail(StorageOperation operations, string pathPattern, Func<Exception> exceptionFactory, int times = -1)
        {
            var fault = new StorageFault(operations, pathPattern, null, exceptionFactory, times);
            lock (_sync)
            {
                _faults.Add(fault);
            }

            return fault;
        }

        /// <summary>Throws a SaveStorageException with the given kind.</summary>
        public StorageFault FailWithKind(StorageOperation operations, string pathPattern, LocalWriteErrorKind kind, int times = -1)
        {
            return Fail(operations, pathPattern, () => new SaveStorageException(kind, "Scripted " + kind + " on " + (pathPattern ?? "any path") + "."), times);
        }

        /// <summary>Throws a plain IOException (classified as IoError).</summary>
        public StorageFault FailWithIOException(StorageOperation operations, string pathPattern, int times = -1)
        {
            return Fail(operations, pathPattern, () => new IOException("Scripted IOException on " + (pathPattern ?? "any path") + "."), times);
        }

        /// <summary>Fails WriteAtomic at a specific step; state before that step is kept (as production does after the tmp write).</summary>
        public StorageFault FailWriteStep(string pathPattern, AtomicWriteStep step, Func<Exception> exceptionFactory, int times = -1)
        {
            var fault = new StorageFault(StorageOperation.Write, pathPattern, step, exceptionFactory, times);
            lock (_sync)
            {
                _faults.Add(fault);
            }

            return fault;
        }

        /// <summary>
        /// Holds the next matching WriteAtomic calls before any file changes, for at most maxDuration; ReleaseParkedWrites
        /// ends the wait early. Used to give a second writer to the same path the chance to land first.
        /// </summary>
        public void ParkWrites(string pathPattern, TimeSpan maxDuration, int times = 1)
        {
            if (times <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(times), "Use a positive count.");
            }

            lock (_parkSync)
            {
                _parkPathPattern = pathPattern;
                _parkDuration = maxDuration;
                _parkRemaining = times;
                _parkRelease.Reset();
            }
        }

        /// <summary>Releases every parked write and disarms the park.</summary>
        public void ReleaseParkedWrites()
        {
            lock (_parkSync)
            {
                _parkRemaining = 0;
            }

            _parkRelease.Set();
        }

        /// <summary>True while at least one WriteAtomic call is held by ParkWrites.</summary>
        public bool IsWriteParked
        {
            get
            {
                lock (_parkSync)
                {
                    return _parkedWrites > 0;
                }
            }
        }

        /// <summary>WriteAtomic calls that were parked, including released ones.</summary>
        public int ParkedWriteCount
        {
            get
            {
                lock (_parkSync)
                {
                    return _parkedWriteTotal;
                }
            }
        }

        public void RemoveFault(StorageFault fault)
        {
            lock (_sync)
            {
                _faults.Remove(fault);
            }
        }

        public void ClearFaults()
        {
            lock (_sync)
            {
                _faults.Clear();
            }
        }

        /// <summary>
        /// One-shot: the next matching WriteAtomic stops right after step (or before the next executed step when it is skipped),
        /// throws SimulatedCrashException and the storage refuses all calls until RecoverFromCrash.
        /// partialTmpBytes truncates the tmp file when step is WriteTmp.
        /// </summary>
        public void SimulateCrashAfter(string pathPattern, AtomicWriteStep step, int? partialTmpBytes = null)
        {
            lock (_sync)
            {
                _crashPlan = new CrashPlan(pathPattern, step, partialTmpBytes);
            }
        }

        /// <summary>Simulated reboot: calls are accepted again; files stay as the crash left them.</summary>
        public void RecoverFromCrash()
        {
            lock (_sync)
            {
                _crashed = false;
                _crashPlan = null;
            }
        }

        /// <summary>Completed WriteAtomic calls for one primary path.</summary>
        public int GetWriteCount(string relativePath)
        {
            string path = NormalizeFilePath(relativePath);
            lock (_sync)
            {
                return _writeCounts.TryGetValue(path, out int count) ? count : 0;
            }
        }

        /// <summary>Logged calls matching any of operations and the path pattern (source or destination).</summary>
        public int CountCalls(StorageOperation operations, string pathPattern = null)
        {
            lock (_sync)
            {
                int count = 0;
                foreach (StorageCall call in _calls)
                {
                    if ((call.Operation & operations) != 0
                        && (StorageFault.PathMatches(pathPattern, call.Path) || (call.DestinationPath != null && StorageFault.PathMatches(pathPattern, call.DestinationPath))))
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        /// <summary>Clears the call log and write counters; files and faults stay.</summary>
        public void ResetCounters()
        {
            lock (_sync)
            {
                _calls.Clear();
                _writeCounts.Clear();
                _writeThreadIds.Clear();
                _writeCount = 0;
                _writeAttemptCount = 0;
            }
        }

        public StorageSnapshot Snapshot()
        {
            lock (_sync)
            {
                var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, byte[]> pair in _files)
                {
                    files[pair.Key] = Copy(pair.Value);
                }

                return new StorageSnapshot(files, new HashSet<string>(_directories, StringComparer.Ordinal));
            }
        }

        /// <summary>Replaces files and directories with the snapshot; counters, faults and the log are unchanged.</summary>
        public void RestoreSnapshot(StorageSnapshot snapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            lock (_sync)
            {
                _files.Clear();
                _directories.Clear();
                foreach (KeyValuePair<string, byte[]> pair in snapshot.Files)
                {
                    _files[pair.Key] = Copy(pair.Value);
                }

                foreach (string directory in snapshot.Directories)
                {
                    _directories.Add(directory);
                }
            }
        }

        // Raw helpers below bypass faults, crash state and the call log

        public bool HasFile(string relativePath)
        {
            string path = NormalizeFilePath(relativePath);
            lock (_sync)
            {
                return _files.ContainsKey(path);
            }
        }

        public bool HasDirectory(string relativeDirectory)
        {
            string directory = NormalizeDirectoryPath(relativeDirectory);
            lock (_sync)
            {
                return directory.Length == 0 || _directories.Contains(directory);
            }
        }

        public byte[] GetBytes(string relativePath)
        {
            string path = NormalizeFilePath(relativePath);
            lock (_sync)
            {
                return _files.TryGetValue(path, out byte[] bytes) ? Copy(bytes) : null;
            }
        }

        public string GetText(string relativePath)
        {
            byte[] bytes = GetBytes(relativePath);
            return bytes == null ? null : Encoding.UTF8.GetString(bytes);
        }

        /// <summary>Writes bytes directly (no tmp or bak) and creates parent directories.</summary>
        public void SetBytes(string relativePath, byte[] bytes)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            string path = NormalizeFilePath(relativePath);
            lock (_sync)
            {
                EnsureDirectoryChain(ParentOf(path));
                _files[path] = Copy(bytes);
            }
        }

        public void SetText(string relativePath, string text)
        {
            SetBytes(relativePath, new UTF8Encoding(false).GetBytes(text ?? throw new ArgumentNullException(nameof(text))));
        }

        public bool RemoveFile(string relativePath)
        {
            string path = NormalizeFilePath(relativePath);
            lock (_sync)
            {
                return _files.Remove(path);
            }
        }

        /// <summary>Replaces a file with transform(original bytes); throws when missing.</summary>
        public void Corrupt(string relativePath, Func<byte[], byte[]> transform)
        {
            if (transform == null)
            {
                throw new ArgumentNullException(nameof(transform));
            }

            string path = NormalizeFilePath(relativePath);
            lock (_sync)
            {
                if (!_files.TryGetValue(path, out byte[] bytes))
                {
                    throw new FileNotFoundException("Cannot corrupt a missing file: " + path, path);
                }

                _files[path] = transform(Copy(bytes)) ?? throw new InvalidOperationException("Corrupt transform returned null.");
            }
        }

        /// <summary>Removes all files and directories; counters, faults and the log stay.</summary>
        public void Clear()
        {
            lock (_sync)
            {
                _files.Clear();
                _directories.Clear();
            }
        }

        private void Begin(StorageOperation operation, string path, string destinationPath)
        {
            ThrowIfCrashed();
            _calls.Add(new StorageCall(operation, path, destinationPath));
            Exception fault = TakeFault(operation, path, destinationPath, null);
            if (fault != null)
            {
                throw fault;
            }
        }

        private void ParkIfScheduled(string path)
        {
            TimeSpan duration;
            lock (_parkSync)
            {
                if (_parkRemaining == 0 || !StorageFault.PathMatches(_parkPathPattern, path))
                {
                    return;
                }

                _parkRemaining--;
                _parkedWrites++;
                _parkedWriteTotal++;
                duration = _parkDuration;
            }

            try
            {
                _parkRelease.Wait(duration);
            }
            finally
            {
                lock (_parkSync)
                {
                    _parkedWrites--;
                }
            }
        }

        private void ThrowIfCrashed()
        {
            if (_crashed)
            {
                throw new SimulatedCrashException("InMemorySaveStorage is crashed; call RecoverFromCrash first.");
            }
        }

        private Exception TakeFault(StorageOperation operation, string path, string destinationPath, AtomicWriteStep? step)
        {
            for (int i = 0; i < _faults.Count; i++)
            {
                if (_faults[i].Matches(operation, path, destinationPath, step))
                {
                    return _faults[i].Hit();
                }
            }

            return null;
        }

        private void ThrowStepFault(string path, AtomicWriteStep step)
        {
            Exception fault = TakeFault(StorageOperation.Write, path, null, step);
            if (fault != null)
            {
                throw fault;
            }
        }

        private void CheckCrash(string path, AtomicWriteStep step, bool completed, string tmpPath)
        {
            CrashPlan plan = _crashPlan;
            if (plan == null || !StorageFault.PathMatches(plan.PathPattern, path))
            {
                return;
            }

            bool due = completed ? plan.Step <= step : plan.Step < step;
            if (!due)
            {
                return;
            }

            if (completed && step == AtomicWriteStep.WriteTmp && plan.PartialTmpBytes.HasValue && _files.TryGetValue(tmpPath, out byte[] tmp))
            {
                int length = Math.Max(0, Math.Min(plan.PartialTmpBytes.Value, tmp.Length));
                var partial = new byte[length];
                Array.Copy(tmp, partial, length);
                _files[tmpPath] = partial;
            }

            _crashPlan = null;
            _crashed = true;
            throw new SimulatedCrashException("Simulated crash in WriteAtomic(" + path + ") after " + plan.Step + ".");
        }

        private void RemoveTree(string directory)
        {
            if (directory.Length == 0)
            {
                _files.Clear();
                _directories.Clear();
                return;
            }

            _directories.RemoveWhere(candidate => string.Equals(candidate, directory, StringComparison.Ordinal) || IsUnder(candidate, directory));
            var removed = new List<string>();
            foreach (string file in _files.Keys)
            {
                if (IsUnder(file, directory))
                {
                    removed.Add(file);
                }
            }

            foreach (string file in removed)
            {
                _files.Remove(file);
            }
        }

        private void EnsureDirectoryChain(string directory)
        {
            while (directory.Length > 0 && _directories.Add(directory))
            {
                directory = ParentOf(directory);
            }
        }

        private static bool IsUnder(string path, string directory)
        {
            return directory.Length == 0 ? path.Length > 0 : path.StartsWith(directory + "/", StringComparison.Ordinal);
        }

        private static string ParentOf(string path)
        {
            int index = path.LastIndexOf('/');
            return index < 0 ? string.Empty : path.Substring(0, index);
        }

        private static string NameOf(string path)
        {
            int index = path.LastIndexOf('/');
            return index < 0 ? path : path.Substring(index + 1);
        }

        private static string NormalizeFilePath(string path)
        {
            string normalized = NormalizeDirectoryPath(path);
            if (normalized.Length == 0)
            {
                throw new ArgumentException("File path must not be empty.", nameof(path));
            }

            return normalized;
        }

        private static string NormalizeDirectoryPath(string path)
        {
            if (path == null)
            {
                throw new ArgumentNullException(nameof(path));
            }

            return path.Replace('\\', '/').Trim('/');
        }

        private static byte[] Copy(byte[] bytes)
        {
            return (byte[])bytes.Clone();
        }

        private sealed class CrashPlan
        {
            public CrashPlan(string pathPattern, AtomicWriteStep step, int? partialTmpBytes)
            {
                PathPattern = pathPattern;
                Step = step;
                PartialTmpBytes = partialTmpBytes;
            }

            public string PathPattern { get; }

            public AtomicWriteStep Step { get; }

            public int? PartialTmpBytes { get; }
        }
    }
}
