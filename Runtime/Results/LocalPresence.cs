namespace Ecanakli.SaveSystem
{
    /// <summary>Whether local data exists for a slot or profile.</summary>
    public enum LocalPresence
    {
        Absent = 0,
        Present = 1,

        /// <summary>Could not be decided (IO error, corrupt or too-new file).</summary>
        Undetermined = 2,
    }
}
