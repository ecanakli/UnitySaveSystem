using System;
using System.Collections.Generic;

namespace Ecanakli.SaveSystem
{
    /// <summary>Service-wide local write health snapshot.</summary>
    public sealed class LocalWriteHealth
    {
        internal static readonly LocalWriteHealth Healthy = new LocalWriteHealth(Array.Empty<LocalWriteFailure>());

        internal LocalWriteHealth(IEnumerable<LocalWriteFailure> failures)
        {
            Failures = ResultLists.Copy(failures);
            IsHealthy = Failures.Count == 0;
            Kind = MostSevere(Failures);
        }

        public bool IsHealthy { get; }

        /// <summary>Most severe failing kind (DiskFull > AccessDenied > IoError); null when healthy.</summary>
        public LocalWriteErrorKind? Kind { get; }

        public IReadOnlyList<LocalWriteFailure> Failures { get; }

        internal static LocalWriteErrorKind? MostSevere(IReadOnlyList<LocalWriteFailure> failures)
        {
            LocalWriteErrorKind? result = null;
            for (int i = 0; i < failures.Count; i++)
            {
                LocalWriteErrorKind kind = failures[i].Kind;
                if (!result.HasValue || (int)kind > (int)result.Value)
                {
                    result = kind;
                }
            }

            return result;
        }

        public override string ToString()
        {
            return IsHealthy ? "Healthy" : "Failing(" + Kind + ", " + Failures.Count + " targets)";
        }
    }
}
