using System;

namespace Ecanakli.SaveSystem
{
    /// <summary>DI-agnostic log sink for the save system.</summary>
    public interface ISaveLogger
    {
        /// <summary>True when verbose messages are emitted; callers skip building verbose strings otherwise.</summary>
        bool IsVerboseEnabled { get; }

        void Verbose(string message);

        void Info(string message);

        void Warning(string message);

        void Error(string message, Exception exception = null);
    }
}
