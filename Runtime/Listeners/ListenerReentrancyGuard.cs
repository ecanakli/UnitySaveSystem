namespace Ecanakli.SaveSystem
{
    /// <summary>Service-wide "listener dispatch in progress" flag shared by the restore and deactivation dispatchers. Main thread only.</summary>
    internal sealed class ListenerReentrancyGuard
    {
        private int _depth;

        /// <summary>True while any dispatch runs; Restore, Initialize and ActivateProfile are refused with ReentrantCall.</summary>
        public bool IsDispatching => _depth > 0;

        /// <summary>Number of overlapping dispatches.</summary>
        public int Depth => _depth;

        public void Enter()
        {
            _depth++;
        }

        public void Exit()
        {
            if (_depth > 0)
            {
                _depth--;
            }
        }

        /// <summary>Logs and returns true when called during dispatch; the caller returns ReentrantCall.</summary>
        public bool RefuseIfDispatching(ISaveLogger logger, string operation)
        {
            if (_depth == 0)
            {
                return false;
            }

            logger?.Warning("[SaveSystem] " + operation + " was called during listener dispatch and was refused (ReentrantCall).");
            return true;
        }
    }
}
