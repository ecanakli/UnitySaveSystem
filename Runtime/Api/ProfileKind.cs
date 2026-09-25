namespace Ecanakli.SaveSystem
{
    /// <summary>Kind of a save profile.</summary>
    public enum ProfileKind
    {
        /// <summary>Signed-out player; never uploads.</summary>
        Guest = 0,

        /// <summary>Signed-in account; the only cloud-backed kind.</summary>
        Account = 1,

        /// <summary>Player-named local profile (Save 1/2/3); never uploads or claims.</summary>
        Local = 2,
    }
}
