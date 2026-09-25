using System;
using Newtonsoft.Json.Linq;

namespace Ecanakli.SaveSystem
{
    /// <summary>Envelope shared by local files and CloudSync values; data is always written last.</summary>
    internal sealed class SaveEnvelope
    {
        /// <summary>Envelope format written by this build.</summary>
        public const int CurrentFormat = 1;

        /// <summary>JSON property name of the format.</summary>
        public const string FormatProperty = "fmt";

        /// <summary>JSON property name of the payload schema version.</summary>
        public const string SchemaProperty = "schema";

        /// <summary>JSON property name of the local revision.</summary>
        public const string RevisionProperty = "rev";

        /// <summary>JSON property name of the upload write id.</summary>
        public const string WriteIdProperty = "writeId";

        /// <summary>JSON property name of the diagnostic save time.</summary>
        public const string SavedAtUtcProperty = "savedAtUtc";

        /// <summary>JSON property name of the diagnostic device id.</summary>
        public const string DeviceIdProperty = "deviceId";

        /// <summary>JSON property name of the owning account id.</summary>
        public const string AccountIdProperty = "accountId";

        /// <summary>JSON property name of the data checksum.</summary>
        public const string DataSha256Property = "dataSha256";

        /// <summary>JSON property name of the payload; always the last property.</summary>
        public const string DataProperty = "data";

        /// <summary>Envelope format (fmt).</summary>
        public int Format { get; set; } = CurrentFormat;

        /// <summary>Payload schema version (schema).</summary>
        public int Schema { get; set; }

        /// <summary>Local revision (rev).</summary>
        public long Revision { get; set; }

        /// <summary>Upload write id; null when the payload was never uploaded under an id.</summary>
        public string WriteId { get; set; }

        /// <summary>Save time for diagnostics only; null when absent or unparseable.</summary>
        public DateTime? SavedAtUtc { get; set; }

        /// <summary>Device id for diagnostics only.</summary>
        public string DeviceId { get; set; }

        /// <summary>Owning account id; null for guest and local profiles.</summary>
        public string AccountId { get; set; }

        /// <summary>Lowercase hex SHA-256 of the exact data bytes; set by decode, ignored by encode.</summary>
        public string DataSha256 { get; set; }

        /// <summary>Payload token; null after a header peek.</summary>
        public JToken Data { get; set; }

        public override string ToString()
        {
            return "fmt=" + Format + " schema=" + Schema + " rev=" + Revision + " writeId=" + (WriteId ?? "null");
        }
    }
}
