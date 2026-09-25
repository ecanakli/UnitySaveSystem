namespace Ecanakli.SaveSystem
{
    /// <summary>How a slot relates to the cloud.</summary>
    public enum SyncMode
    {
        /// <summary>Device only; never read from or written to the cloud.</summary>
        LocalOnly = 0,

        /// <summary>Uploaded as an envelope and reconciled by write id.</summary>
        CloudSync = 1,

        /// <summary>Raw server-authoritative mirror; never uploaded and never mutated by the game.</summary>
        CloudReadOnly = 2,
    }
}
