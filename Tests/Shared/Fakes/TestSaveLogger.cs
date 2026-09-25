using System;
using System.Collections.Generic;
using System.Text;

namespace Ecanakli.SaveSystem.Tests
{
    internal enum TestLogLevel
    {
        Verbose = 0,
        Info = 1,
        Warning = 2,
        Error = 3,
    }

    internal readonly struct TestLogEntry
    {
        public TestLogEntry(TestLogLevel level, string message, Exception exception)
        {
            Level = level;
            Message = message;
            Exception = exception;
        }

        public TestLogLevel Level { get; }

        public string Message { get; }

        public Exception Exception { get; }

        public override string ToString()
        {
            return Exception == null ? Level + ": " + Message : Level + ": " + Message + " (" + Exception.GetType().Name + ": " + Exception.Message + ")";
        }
    }

    /// <summary>Recording ISaveLogger. Never writes Debug.LogError, so expected errors do not fail tests.</summary>
    internal sealed class TestSaveLogger : ISaveLogger
    {
        private readonly object _sync = new object();
        private readonly List<TestLogEntry> _entries = new List<TestLogEntry>();

        public bool IsVerboseEnabled { get; set; } = true;

        /// <summary>Echo every entry to the Unity console with Debug.Log (debugging aid).</summary>
        public bool EchoToUnityConsole { get; set; }

        public IReadOnlyList<TestLogEntry> Entries
        {
            get
            {
                lock (_sync)
                {
                    return _entries.ToArray();
                }
            }
        }

        public void Verbose(string message)
        {
            Add(TestLogLevel.Verbose, message, null);
        }

        public void Info(string message)
        {
            Add(TestLogLevel.Info, message, null);
        }

        public void Warning(string message)
        {
            Add(TestLogLevel.Warning, message, null);
        }

        public void Error(string message, Exception exception = null)
        {
            Add(TestLogLevel.Error, message, exception);
        }

        /// <summary>Entries at level whose message contains text (null counts all).</summary>
        public int Count(TestLogLevel level, string contains = null)
        {
            lock (_sync)
            {
                int count = 0;
                foreach (TestLogEntry entry in _entries)
                {
                    if (entry.Level == level && (contains == null || (entry.Message != null && entry.Message.IndexOf(contains, StringComparison.Ordinal) >= 0)))
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        public bool Contains(TestLogLevel level, string contains)
        {
            return Count(level, contains) > 0;
        }

        public IReadOnlyList<string> GetMessages(TestLogLevel level)
        {
            lock (_sync)
            {
                var messages = new List<string>();
                foreach (TestLogEntry entry in _entries)
                {
                    if (entry.Level == level)
                    {
                        messages.Add(entry.Message);
                    }
                }

                return messages;
            }
        }

        /// <summary>Entries at or above minimum, one per line; for assertion messages.</summary>
        public string Describe(TestLogLevel minimum = TestLogLevel.Warning)
        {
            lock (_sync)
            {
                var builder = new StringBuilder();
                foreach (TestLogEntry entry in _entries)
                {
                    if (entry.Level >= minimum)
                    {
                        builder.AppendLine(entry.ToString());
                    }
                }

                return builder.Length == 0 ? "<no log entries>" : builder.ToString();
            }
        }

        public void Clear()
        {
            lock (_sync)
            {
                _entries.Clear();
            }
        }

        private void Add(TestLogLevel level, string message, Exception exception)
        {
            var entry = new TestLogEntry(level, message, exception);
            lock (_sync)
            {
                _entries.Add(entry);
            }

            if (EchoToUnityConsole)
            {
                UnityEngine.Debug.Log("[TestSaveLogger] " + entry);
            }
        }
    }
}
