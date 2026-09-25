namespace Ecanakli.SaveSystem
{
    /// <summary>Detailed reason for a failed or refused operation.</summary>
    public enum SaveErrorCode
    {
        None = 0,
        Unknown = 1,

        // Lifecycle
        NotInitialized = 10,
        AlreadyInitialized = 11,
        Disposed = 12,
        Superseded = 13,
        ReentrantCall = 14,
        CalledFromHook = 15,

        // Profiles and accounts
        NotSignedIn = 20,
        AccountMismatch = 21,
        ProfileActive = 22,
        ProfileNotCloudBacked = 23,
        UnsyncedChanges = 24,

        // Slots
        SlotNotReady = 30,
        SlotNotRegistered = 31,
        SlotReadOnly = 32,

        // Local storage
        DiskFull = 40,
        AccessDenied = 41,
        IoError = 42,
        InUse = 43,

        // Cloud
        CloudError = 50,
    }
}
