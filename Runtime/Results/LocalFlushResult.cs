using System.Collections.Generic;

namespace Ecanakli.SaveSystem
{
    /// <summary>Result of a synchronous local flush.</summary>
    public sealed class LocalFlushResult
    {
        internal static readonly LocalFlushResult Complete = new LocalFlushResult(null);

        internal LocalFlushResult(IEnumerable<LocalWriteFailure> failures)
        {
            Failures = ResultLists.Copy(failures);
        }

        /// <summary>True when every dirty target was written.</summary>
        public bool IsComplete => Failures.Count == 0;

        public IReadOnlyList<LocalWriteFailure> Failures { get; }

        public override string ToString()
        {
            return IsComplete ? "Complete" : "Incomplete(" + Failures.Count + " failures)";
        }
    }
}
