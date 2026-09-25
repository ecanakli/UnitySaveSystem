using Ecanakli.SaveSystem;

namespace Ecanakli.SaveSystem.DependencyInjection
{
    /// <summary>Mirrors ISaveService.ProfileActivated; also fires for the initial activation at InitializeAsync.</summary>
    public sealed class SaveProfileActivatedSignal
    {
        public SaveProfileActivatedSignal(ProfileActivationResult result)
        {
            Result = result;
        }

        public ProfileActivationResult Result { get; }
    }
}
