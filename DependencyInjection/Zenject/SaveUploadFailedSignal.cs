using Ecanakli.SaveSystem;

namespace Ecanakli.SaveSystem.DependencyInjection
{
    /// <summary>Mirrors ISaveService.UploadFailed; never raised for a transient retry.</summary>
    public sealed class SaveUploadFailedSignal
    {
        public SaveUploadFailedSignal(UploadFailure failure)
        {
            Failure = failure;
        }

        public UploadFailure Failure { get; }
    }
}
