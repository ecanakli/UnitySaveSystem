using System;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Ecanakli.SaveSystem
{
    /// <summary>Clock backed by DateTime.UtcNow and realtime UniTask delays.</summary>
    public sealed class UnitySaveClock : ISaveClock
    {
        public static readonly UnitySaveClock Instance = new UnitySaveClock();

        public DateTime UtcNow => DateTime.UtcNow;

        public UniTask Delay(TimeSpan delay, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (delay <= TimeSpan.Zero)
            {
                return UniTask.CompletedTask;
            }

            // Realtime keeps save timing independent of Time.timeScale
            return UniTask.Delay(delay, DelayType.Realtime, PlayerLoopTiming.Update, ct);
        }
    }
}
