using System;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Ecanakli.SaveSystem
{
    /// <summary>Internal seam a registered slot calls into; implemented by the save service.</summary>
    internal interface ISaveSlotHost
    {
        ISaveLogger Logger { get; }

        SaveServiceOptions Options { get; }

        /// <summary>Serializer shared by every path of this service (options converters applied).</summary>
        SaveJson Json { get; }

        /// <summary>True while a profile switch is in progress; Mutate is refused.</summary>
        bool IsSwitchingProfile { get; }

        /// <summary>True after Dispose; Mutate is refused.</summary>
        bool IsDisposed { get; }

        /// <summary>Called after a successful Mutate (revision already incremented); marks local and, for CloudSync, cloud dirty.</summary>
        void OnSlotMutated(SaveSlot slot);

        /// <summary>Durable write of the slot's current revision; the host checks readiness and cancellation.</summary>
        UniTask<SaveResult> SaveSlotNowAsync(SaveSlot slot, CancellationToken ct);
    }

    /// <summary>
    /// Thread-static guard set while a slot hook runs (CreateDefault, Normalize, UpgradePayload, IsEmpty, ResolveConflict).
    /// Mutate, SaveNowAsync and every service call check it and refuse with an error log.
    /// </summary>
    internal static class HookScope
    {
        [ThreadStatic]
        private static int t_depth;

        [ThreadStatic]
        private static string t_slotKey;

        [ThreadStatic]
        private static string t_hookName;

        /// <summary>True when the current thread is inside a hook.</summary>
        public static bool IsActive => t_depth > 0;

        /// <summary>Innermost active hook as "Normalize of slot 'key'"; null when inactive.</summary>
        public static string ActiveHookDescription => t_depth > 0 ? t_hookName + " of slot '" + t_slotKey + "'" : null;

        /// <summary>Enters a hook scope; dispose to leave (use a using block so exceptions also leave).</summary>
        public static Token Enter(string slotKey, string hookName)
        {
            var token = new Token(t_slotKey, t_hookName);
            t_depth++;
            t_slotKey = slotKey;
            t_hookName = hookName;
            return token;
        }

        /// <summary>Returns true and logs an error when called from inside a hook.</summary>
        public static bool RefuseIfActive(ISaveLogger logger, string operation)
        {
            if (t_depth == 0)
            {
                return false;
            }

            logger?.Error(
                operation + " was refused because it was called from " + ActiveHookDescription
                + ". Slot hooks must be synchronous and pure and must not call the save service.");
            return true;
        }

        private static void Exit(string previousSlotKey, string previousHookName)
        {
            if (t_depth > 0)
            {
                t_depth--;
            }

            t_slotKey = t_depth > 0 ? previousSlotKey : null;
            t_hookName = t_depth > 0 ? previousHookName : null;
        }

        /// <summary>Leaves the scope on Dispose and restores the outer hook description.</summary>
        public readonly struct Token : IDisposable
        {
            private readonly string _previousSlotKey;
            private readonly string _previousHookName;

            internal Token(string previousSlotKey, string previousHookName)
            {
                _previousSlotKey = previousSlotKey;
                _previousHookName = previousHookName;
            }

            public void Dispose()
            {
                Exit(_previousSlotKey, _previousHookName);
            }
        }
    }
}
