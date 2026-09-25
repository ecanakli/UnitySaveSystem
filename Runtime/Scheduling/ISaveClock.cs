using System;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Ecanakli.SaveSystem
{
    /// <summary>Single time seam for the save system.</summary>
    public interface ISaveClock
    {
        DateTime UtcNow { get; }

        /// <summary>Completes after the delay; throws OperationCanceledException when the token is cancelled.</summary>
        UniTask Delay(TimeSpan delay, CancellationToken ct);
    }
}
