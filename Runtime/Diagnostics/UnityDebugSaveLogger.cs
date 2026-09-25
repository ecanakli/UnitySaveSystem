using System;
using UnityEngine;

namespace Ecanakli.SaveSystem
{
    /// <summary>Default logger that writes to the Unity console.</summary>
    public sealed class UnityDebugSaveLogger : ISaveLogger
    {
        private const string Prefix = "[SaveSystem] ";

        // Fallback used when no host logger is available
        internal static readonly UnityDebugSaveLogger Fallback = new UnityDebugSaveLogger();

        public UnityDebugSaveLogger(bool verboseEnabled = false)
        {
            IsVerboseEnabled = verboseEnabled;
        }

        public bool IsVerboseEnabled { get; }

        public void Verbose(string message)
        {
            if (IsVerboseEnabled)
            {
                Debug.Log(Prefix + message);
            }
        }

        public void Info(string message)
        {
            Debug.Log(Prefix + message);
        }

        public void Warning(string message)
        {
            Debug.LogWarning(Prefix + message);
        }

        public void Error(string message, Exception exception = null)
        {
            Debug.LogError(Prefix + message);
            if (exception != null)
            {
                Debug.LogException(exception);
            }
        }
    }
}
