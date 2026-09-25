namespace Ecanakli.SaveSystem
{
    /// <summary>Lifecycle state of a slot.</summary>
    public enum SlotState
    {
        /// <summary>Not loaded; memory holds defaults and mutations are refused.</summary>
        Unloaded = 0,

        /// <summary>Loaded; reads and mutations are allowed.</summary>
        Ready = 1,

        /// <summary>Load failed; reads return defaults, mutation and persistence are refused.</summary>
        Failed = 2,
    }

    /// <summary>Reason a slot is in the Failed state.</summary>
    internal enum SlotFailure
    {
        None = 0,
        IoError = 1,
        NormalizeFailed = 2,
        SchemaTooNew = 3,
    }
}
