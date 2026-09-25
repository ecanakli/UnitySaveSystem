namespace Ecanakli.SaveSystem
{
    /// <summary>How DeleteProfileAsync treats data that has not reached the cloud.</summary>
    public enum ProfileDeleteMode
    {
        /// <summary>Refuse with UnsyncedChanges when any CloudSync slot is unsynced or undetermined.</summary>
        RequireSynced = 0,

        /// <summary>Delete regardless of unsynced local changes.</summary>
        DiscardUnsynced = 1,
    }
}
