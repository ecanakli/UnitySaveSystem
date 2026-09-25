namespace Ecanakli.SaveSystem
{
    /// <summary>Where a slot's files live.</summary>
    public enum SlotScope
    {
        /// <summary>Account-scoped; stored under the active profile directory.</summary>
        Profile = 0,

        /// <summary>Device-level; stored under the device directory. Must be LocalOnly.</summary>
        Device = 1,
    }
}
