namespace Ecanakli.SaveSystem
{
    /// <summary>Why restore listeners were dispatched.</summary>
    public enum RestoreTrigger
    {
        /// <summary>Profile slots were loaded from local storage (init or activation).</summary>
        ProfileActivated = 0,

        /// <summary>A cloud restore or single-slot conflict reconcile ran.</summary>
        CloudRestore = 1,
    }
}
