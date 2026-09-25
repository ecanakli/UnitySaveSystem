namespace Ecanakli.SaveSystem.Samples.DependencyInjection
{
    /// <summary>Sample settings payload: audio levels, vibration and language.</summary>
    public sealed class SettingsData
    {
        public float MusicVolume { get; set; } = 1f;

        public float SfxVolume { get; set; } = 1f;

        public bool VibrationEnabled { get; set; } = true;

        public string Language { get; set; } = "en";
    }
}
