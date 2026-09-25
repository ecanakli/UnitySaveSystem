using System;
using System.Collections;
using System.Diagnostics;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Ecanakli.SaveSystem.Tests
{
    /// <summary>
    /// UniTask helpers for tests. RunSync works only for tasks that complete without player-loop ticks
    /// (OffloadIo = false, uncontended gate, no yields). Scheduler dispatch (UniTask.Yield), thread-pool IO and
    /// RestoreOperation reconcile requests need ticks: use [UnityTest] with ToCoroutine plus WaitUntilAsync, or PlayMode.
    /// </summary>
    internal static class AsyncTestUtility
    {
        public const int DefaultMaxFrames = 600;

        /// <summary>Returns the result of an already completed task; throws a descriptive error when it is still pending.</summary>
        public static T RunSync<T>(UniTask<T> task, string description = null)
        {
            if (!task.Status.IsCompleted())
            {
                throw new InvalidOperationException(DescribePending(description));
            }

            return task.GetAwaiter().GetResult();
        }

        public static void RunSync(UniTask task, string description = null)
        {
            if (!task.Status.IsCompleted())
            {
                throw new InvalidOperationException(DescribePending(description));
            }

            task.GetAwaiter().GetResult();
        }

        /// <summary>Body for [UnityTest] methods: return AsyncTestUtility.ToCoroutine(async () => { ... }).</summary>
        public static IEnumerator ToCoroutine(Func<UniTask> body)
        {
            return UniTask.ToCoroutine(body);
        }

        public static async UniTask WaitFramesAsync(int frames, CancellationToken ct = default)
        {
            for (int i = 0; i < frames; i++)
            {
                await UniTask.Yield(PlayerLoopTiming.Update, ct);
            }
        }

        /// <summary>Real-time floor so fast batchmode frames do not time out before thread-pool continuations run.</summary>
        public static readonly TimeSpan MinWaitTime = TimeSpan.FromSeconds(10);

        /// <summary>Yields frames until condition is true; throws TimeoutException once both maxFrames and MinWaitTime have passed.</summary>
        public static async UniTask WaitUntilAsync(Func<bool> condition, int maxFrames = DefaultMaxFrames, string description = null, CancellationToken ct = default)
        {
            if (condition == null)
            {
                throw new ArgumentNullException(nameof(condition));
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            for (int frame = 0; !condition(); frame++)
            {
                if (frame >= maxFrames && stopwatch.Elapsed >= MinWaitTime)
                {
                    throw new TimeoutException((description ?? "Condition") + " was not met within " + frame + " frames and " + stopwatch.Elapsed.TotalSeconds.ToString("F1") + " s.");
                }

                await UniTask.Yield(PlayerLoopTiming.Update, ct);
            }
        }

        private static string DescribePending(string description)
        {
            return (description ?? "The task") + " did not complete synchronously; it needs player-loop ticks. "
                   + "Use [UnityTest] with AsyncTestUtility.ToCoroutine, or a PlayMode test.";
        }
    }
}
