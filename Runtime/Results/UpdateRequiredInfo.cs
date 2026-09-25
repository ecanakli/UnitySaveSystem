namespace Ecanakli.SaveSystem
{
    /// <summary>Where a payload came from.</summary>
    public enum PayloadSource
    {
        Local = 0,
        Cloud = 1,
    }

    /// <summary>A payload needs a newer app build; raised once per slot per epoch.</summary>
    public sealed class UpdateRequiredInfo
    {
        internal UpdateRequiredInfo(
            ProfileId? profile,
            string slotKey,
            PayloadSource source,
            int foundFormat,
            int foundSchema,
            int supportedFormat,
            int supportedSchema)
        {
            Profile = profile;
            SlotKey = slotKey;
            Source = source;
            FoundFormat = foundFormat;
            FoundSchema = foundSchema;
            SupportedFormat = supportedFormat;
            SupportedSchema = supportedSchema;
        }

        /// <summary>Owning profile; null for Device-scope slots.</summary>
        public ProfileId? Profile { get; }

        public string SlotKey { get; }

        public PayloadSource Source { get; }

        public int FoundFormat { get; }

        /// <summary>0 when the schema could not be read because the format was already too new.</summary>
        public int FoundSchema { get; }

        public int SupportedFormat { get; }

        public int SupportedSchema { get; }

        public override string ToString()
        {
            return SlotKey + " " + Source + " fmt " + FoundFormat + "/" + SupportedFormat + " schema " + FoundSchema + "/" + SupportedSchema;
        }
    }
}
