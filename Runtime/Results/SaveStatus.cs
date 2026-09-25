namespace Ecanakli.SaveSystem
{
    /// <summary>Coarse outcome of an operation; details are in SaveError.Code.</summary>
    public enum SaveStatus
    {
        Success = 0,
        Failed = 1,
        Canceled = 2,
    }
}
