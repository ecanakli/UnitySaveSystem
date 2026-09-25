using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Ecanakli.SaveSystem.Tests
{
    /// <summary>IFileOps members; used by faults and the call log.</summary>
    internal enum FileOpKind
    {
        FileExists = 0,
        DirectoryExists = 1,
        ReadAllBytes = 2,
        WriteAllBytesFlushed = 3,
        MoveFile = 4,
        DeleteFile = 5,
        CreateDirectory = 6,
        MoveDirectory = 7,
        DeleteDirectory = 8,
        GetFileNames = 9,
        GetDirectoryNames = 10,
        Sleep = 11,
    }

    /// <summary>One logged IFileOps call.</summary>
    internal readonly struct FileOpCall
    {
        public FileOpCall(FileOpKind kind, string path, string destinationPath, int threadId)
        {
            Kind = kind;
            Path = path;
            DestinationPath = destinationPath;
            ThreadId = threadId;
        }

        public FileOpKind Kind { get; }

        public string Path { get; }

        public string DestinationPath { get; }

        public int ThreadId { get; }

        public override string ToString()
        {
            return DestinationPath == null ? Kind + " " + Path : Kind + " " + Path + " -> " + DestinationPath;
        }
    }

    /// <summary>Scripted IFileOps failure; path suffix matches source or destination ('\' treated as '/'), null matches any.</summary>
    internal sealed class FileOpFault
    {
        private readonly Func<Exception> _exceptionFactory;
        private int _remaining;

        internal FileOpFault(FileOpKind kind, string pathSuffix, Func<Exception> exceptionFactory, int times)
        {
            if (times == 0 || times < -1)
            {
                throw new ArgumentOutOfRangeException(nameof(times), "Use a positive count or -1 for unlimited.");
            }

            _exceptionFactory = exceptionFactory ?? throw new ArgumentNullException(nameof(exceptionFactory));
            Kind = kind;
            PathSuffix = pathSuffix == null ? null : pathSuffix.Replace('\\', '/');
            _remaining = times;
        }

        public FileOpKind Kind { get; }

        public string PathSuffix { get; }

        public int HitCount { get; private set; }

        public bool IsExhausted => _remaining == 0;

        internal bool Matches(FileOpKind kind, string path, string destinationPath)
        {
            if (_remaining == 0 || kind != Kind)
            {
                return false;
            }

            return PathSuffix == null || EndsWith(path) || EndsWith(destinationPath);
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

        private bool EndsWith(string path)
        {
            return path != null && path.Replace('\\', '/').EndsWith(PathSuffix, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// IFileOps decorator for AtomicFileStorage tests: forwards to an inner IFileOps (default SystemFileOps),
    /// injects scripted exceptions, logs calls and records sleeps without blocking.
    /// </summary>
    internal sealed class FakeFileOps : IFileOps
    {
        /// <summary>Win32 ERROR_SHARING_VIOLATION as an HResult.</summary>
        public const int SharingViolationHResult = unchecked((int)0x80070020);

        private readonly object _sync = new object();
        private readonly IFileOps _inner;
        private readonly List<FileOpFault> _faults = new List<FileOpFault>();
        private readonly List<FileOpCall> _calls = new List<FileOpCall>();
        private readonly List<int> _sleeps = new List<int>();

        public FakeFileOps()
            : this(SystemFileOps.Instance)
        {
        }

        public FakeFileOps(IFileOps inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        /// <summary>Runs before fault checks and forwarding (e.g. start a concurrent write).</summary>
        public Action<FileOpKind, string> BeforeOperation { get; set; }

        /// <summary>Runs on every Sleep call with the requested milliseconds.</summary>
        public Action<int> OnSleep { get; set; }

        public IReadOnlyList<int> Sleeps
        {
            get
            {
                lock (_sync)
                {
                    return _sleeps.ToArray();
                }
            }
        }

        public int TotalSleepMilliseconds
        {
            get
            {
                lock (_sync)
                {
                    int total = 0;
                    foreach (int sleep in _sleeps)
                    {
                        total += sleep;
                    }

                    return total;
                }
            }
        }

        public IReadOnlyList<FileOpCall> Calls
        {
            get
            {
                lock (_sync)
                {
                    return _calls.ToArray();
                }
            }
        }

        public int Count(FileOpKind kind)
        {
            lock (_sync)
            {
                int count = 0;
                foreach (FileOpCall call in _calls)
                {
                    if (call.Kind == kind)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        /// <summary>times -1 means unlimited.</summary>
        public FileOpFault FailOn(FileOpKind kind, string pathSuffix, Func<Exception> exceptionFactory, int times = 1)
        {
            if (kind == FileOpKind.Sleep)
            {
                throw new ArgumentException("Sleep cannot fail.", nameof(kind));
            }

            var fault = new FileOpFault(kind, pathSuffix, exceptionFactory, times);
            lock (_sync)
            {
                _faults.Add(fault);
            }

            return fault;
        }

        /// <summary>IOException with an HResult (default sharing violation, retryable on Windows).</summary>
        public FileOpFault FailWithIOException(FileOpKind kind, string pathSuffix, int times = 1, int hResult = SharingViolationHResult)
        {
            return FailOn(kind, pathSuffix, () => new IOException("Scripted " + kind + " failure on " + (pathSuffix ?? "any path") + ".", hResult), times);
        }

        public void ClearFaults()
        {
            lock (_sync)
            {
                _faults.Clear();
            }
        }

        public void ClearLog()
        {
            lock (_sync)
            {
                _calls.Clear();
                _sleeps.Clear();
            }
        }

        public bool FileExists(string path)
        {
            Before(FileOpKind.FileExists, path, null);
            return _inner.FileExists(path);
        }

        public bool DirectoryExists(string path)
        {
            Before(FileOpKind.DirectoryExists, path, null);
            return _inner.DirectoryExists(path);
        }

        public byte[] ReadAllBytes(string path)
        {
            Before(FileOpKind.ReadAllBytes, path, null);
            return _inner.ReadAllBytes(path);
        }

        public void WriteAllBytesFlushed(string path, byte[] bytes)
        {
            Before(FileOpKind.WriteAllBytesFlushed, path, null);
            _inner.WriteAllBytesFlushed(path, bytes);
        }

        public void MoveFile(string sourcePath, string destinationPath)
        {
            Before(FileOpKind.MoveFile, sourcePath, destinationPath);
            _inner.MoveFile(sourcePath, destinationPath);
        }

        public void DeleteFile(string path)
        {
            Before(FileOpKind.DeleteFile, path, null);
            _inner.DeleteFile(path);
        }

        public void CreateDirectory(string path)
        {
            Before(FileOpKind.CreateDirectory, path, null);
            _inner.CreateDirectory(path);
        }

        public void MoveDirectory(string sourcePath, string destinationPath)
        {
            Before(FileOpKind.MoveDirectory, sourcePath, destinationPath);
            _inner.MoveDirectory(sourcePath, destinationPath);
        }

        public void DeleteDirectory(string path)
        {
            Before(FileOpKind.DeleteDirectory, path, null);
            _inner.DeleteDirectory(path);
        }

        public string[] GetFileNames(string directoryPath)
        {
            Before(FileOpKind.GetFileNames, directoryPath, null);
            return _inner.GetFileNames(directoryPath);
        }

        public string[] GetDirectoryNames(string directoryPath)
        {
            Before(FileOpKind.GetDirectoryNames, directoryPath, null);
            return _inner.GetDirectoryNames(directoryPath);
        }

        // Never blocks; records the request only
        public void Sleep(int milliseconds)
        {
            lock (_sync)
            {
                _calls.Add(new FileOpCall(FileOpKind.Sleep, null, null, Thread.CurrentThread.ManagedThreadId));
                _sleeps.Add(milliseconds);
            }

            OnSleep?.Invoke(milliseconds);
        }

        private void Before(FileOpKind kind, string path, string destinationPath)
        {
            lock (_sync)
            {
                _calls.Add(new FileOpCall(kind, path, destinationPath, Thread.CurrentThread.ManagedThreadId));
            }

            BeforeOperation?.Invoke(kind, path);

            Exception exception = null;
            lock (_sync)
            {
                for (int i = 0; i < _faults.Count; i++)
                {
                    if (_faults[i].Matches(kind, path, destinationPath))
                    {
                        exception = _faults[i].Hit();
                        break;
                    }
                }
            }

            if (exception != null)
            {
                throw exception;
            }
        }
    }
}
