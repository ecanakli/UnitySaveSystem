using System;

namespace Ecanakli.SaveSystem
{
    /// <summary>How a listener failed.</summary>
    public enum ListenerFailureKind
    {
        Threw = 0,
        TimedOut = 1,

        /// <summary>Not run because the shared budget was exhausted or dispatch was cancelled.</summary>
        Skipped = 2,
    }

    /// <summary>A restore or deactivation listener that did not complete.</summary>
    public sealed class ListenerFailure
    {
        internal ListenerFailure(Type listenerType, int order, ListenerFailureKind kind, Exception exception = null)
        {
            ListenerType = listenerType;
            Order = order;
            Kind = kind;
            Exception = exception;
        }

        public Type ListenerType { get; }

        public int Order { get; }

        public ListenerFailureKind Kind { get; }

        /// <summary>Set when Kind is Threw.</summary>
        public Exception Exception { get; }

        public override string ToString()
        {
            string name = ListenerType == null ? "<unknown>" : ListenerType.Name;
            return name + " (Order " + Order + ") " + Kind + (Exception == null ? string.Empty : ": " + Exception.Message);
        }
    }
}
