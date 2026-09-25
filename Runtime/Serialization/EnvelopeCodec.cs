using System;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Ecanakli.SaveSystem
{
    /// <summary>Outcome of an envelope decode or header peek.</summary>
    internal enum EnvelopeDecodeStatus
    {
        Ok = 0,
        Corrupt = 1,

        /// <summary>fmt is newer than this build; nothing else is interpreted.</summary>
        FormatTooNew = 2,

        /// <summary>schema is newer than the slot supports; data is not materialized.</summary>
        SchemaTooNew = 3,
    }

    /// <summary>Checksum verification state of a decoded envelope.</summary>
    internal enum ChecksumStatus
    {
        /// <summary>Verification was not requested (header peek).</summary>
        NotChecked = 0,

        /// <summary>No dataSha256 field; accepted as unverifiable.</summary>
        NotPresent = 1,

        /// <summary>dataSha256 present but no top-level compact data marker (hand-edited or pretty-printed); accepted.</summary>
        Unverifiable = 2,

        Verified = 3,

        /// <summary>Checksum did not match or was malformed; the result is Corrupt.</summary>
        Mismatch = 4,
    }

    /// <summary>Upgrade hook: returns the payload at schema fromSchema + 1.</summary>
    internal delegate JObject PayloadUpgradeHandler(JObject payload, int fromSchema);

    /// <summary>Encoded envelope bytes and the checksum written into them.</summary>
    internal readonly struct EncodedEnvelope
    {
        public EncodedEnvelope(byte[] bytes, string dataSha256)
        {
            Bytes = bytes;
            DataSha256 = dataSha256;
        }

        /// <summary>Compact UTF-8 envelope; data is the last property.</summary>
        public byte[] Bytes { get; }

        /// <summary>Lowercase hex SHA-256 of the data bytes.</summary>
        public string DataSha256 { get; }
    }

    /// <summary>Result of EnvelopeCodec.Decode or PeekHeader; never thrown.</summary>
    internal readonly struct EnvelopeDecodeResult
    {
        private EnvelopeDecodeResult(
            EnvelopeDecodeStatus status,
            SaveEnvelope envelope,
            ChecksumStatus checksum,
            CorruptionCause cause,
            string message,
            Exception exception)
        {
            Status = status;
            Envelope = envelope;
            Checksum = checksum;
            Cause = cause;
            Message = message;
            Exception = exception;
        }

        public EnvelopeDecodeStatus Status { get; }

        /// <summary>Ok: full header plus Data (null after a peek). TooNew: Format and Schema when read. Corrupt: null.</summary>
        public SaveEnvelope Envelope { get; }

        public ChecksumStatus Checksum { get; }

        /// <summary>ParseFailed or ChecksumMismatch when Corrupt; None otherwise.</summary>
        public CorruptionCause Cause { get; }

        /// <summary>Diagnostic text for Corrupt and TooNew results.</summary>
        public string Message { get; }

        /// <summary>Parser exception behind a Corrupt result, when any.</summary>
        public Exception Exception { get; }

        /// <summary>True when Status is Ok.</summary>
        public bool IsOk => Status == EnvelopeDecodeStatus.Ok;

        /// <summary>True for FormatTooNew and SchemaTooNew.</summary>
        public bool IsTooNew => Status == EnvelopeDecodeStatus.FormatTooNew || Status == EnvelopeDecodeStatus.SchemaTooNew;

        internal static EnvelopeDecodeResult Ok(SaveEnvelope envelope, ChecksumStatus checksum)
        {
            return new EnvelopeDecodeResult(EnvelopeDecodeStatus.Ok, envelope, checksum, CorruptionCause.None, null, null);
        }

        internal static EnvelopeDecodeResult Corrupt(CorruptionCause cause, string message, Exception exception, ChecksumStatus checksum = ChecksumStatus.NotChecked)
        {
            return new EnvelopeDecodeResult(EnvelopeDecodeStatus.Corrupt, null, checksum, cause, message, exception);
        }

        internal static EnvelopeDecodeResult TooNew(EnvelopeDecodeStatus status, SaveEnvelope envelope, string message)
        {
            envelope.Data = null;
            return new EnvelopeDecodeResult(status, envelope, ChecksumStatus.NotChecked, CorruptionCause.None, message, null);
        }

        public override string ToString()
        {
            return Message == null ? Status.ToString() : Status + ": " + Message;
        }
    }

    /// <summary>Result of decoding a raw (non-envelope) payload; never thrown.</summary>
    internal readonly struct RawPayloadDecodeResult
    {
        private RawPayloadDecodeResult(JToken data, string message, Exception exception)
        {
            Data = data;
            Message = message;
            Exception = exception;
        }

        /// <summary>True when the bytes parsed as one JSON value.</summary>
        public bool IsOk => Data != null;

        /// <summary>Parsed payload; null when corrupt.</summary>
        public JToken Data { get; }

        /// <summary>ParseFailed when not ok.</summary>
        public CorruptionCause Cause => IsOk ? CorruptionCause.None : CorruptionCause.ParseFailed;

        public string Message { get; }

        public Exception Exception { get; }

        internal static RawPayloadDecodeResult Ok(JToken data)
        {
            return new RawPayloadDecodeResult(data, null, null);
        }

        internal static RawPayloadDecodeResult Corrupt(string message, Exception exception)
        {
            return new RawPayloadDecodeResult(null, message, exception);
        }
    }

    /// <summary>Result of running the upgrade hook chain; never thrown.</summary>
    internal readonly struct PayloadUpgradeResult
    {
        private PayloadUpgradeResult(bool isSuccess, JToken payload, int failedFromSchema, string message, Exception exception)
        {
            IsSuccess = isSuccess;
            Payload = payload;
            FailedFromSchema = failedFromSchema;
            Message = message;
            Exception = exception;
        }

        public bool IsSuccess { get; }

        /// <summary>Payload at the target schema; null on failure.</summary>
        public JToken Payload { get; }

        /// <summary>Schema of the step that failed; -1 on success.</summary>
        public int FailedFromSchema { get; }

        public string Message { get; }

        /// <summary>Exception thrown by the hook, when any.</summary>
        public Exception Exception { get; }

        internal static PayloadUpgradeResult Success(JToken payload)
        {
            return new PayloadUpgradeResult(true, payload, -1, null, null);
        }

        internal static PayloadUpgradeResult Failed(int fromSchema, string message, Exception exception)
        {
            return new PayloadUpgradeResult(false, null, fromSchema, message, exception);
        }
    }

    /// <summary>Encodes and decodes save envelopes and raw payloads. Thread-safe and stateless.</summary>
    internal static class EnvelopeCodec
    {
        private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

        private const byte Quote = (byte)'"';
        private const byte Backslash = (byte)'\\';
        private const byte Comma = (byte)',';
        private const byte OpenBrace = (byte)'{';
        private const byte CloseBrace = (byte)'}';
        private const byte OpenBracket = (byte)'[';
        private const byte CloseBracket = (byte)']';

        // Compact top-level marker written by Encode
        private static readonly byte[] DataMarker = SaveJson.StrictUtf8.GetBytes(",\"" + SaveEnvelope.DataProperty + "\":");

        /// <summary>Encodes to compact UTF-8 with data last and dataSha256 over the exact data bytes. Throws only on null input.</summary>
        public static EncodedEnvelope Encode(SaveEnvelope envelope)
        {
            if (envelope == null)
            {
                throw new ArgumentNullException(nameof(envelope));
            }

            if (envelope.Data == null)
            {
                throw new ArgumentException("Envelope data must not be null.", nameof(envelope));
            }

            byte[] dataBytes = SaveJson.ToUtf8Bytes(envelope.Data);
            string checksum = PayloadChecksum.Compute(dataBytes);

            using (var stream = new MemoryStream(dataBytes.Length + 256))
            {
                using (var textWriter = new StreamWriter(stream, SaveJson.WriteUtf8, 256, true))
                using (JsonTextWriter writer = SaveJson.CreateWriter(textWriter))
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName(SaveEnvelope.FormatProperty);
                    writer.WriteValue(envelope.Format);
                    writer.WritePropertyName(SaveEnvelope.SchemaProperty);
                    writer.WriteValue(envelope.Schema);
                    writer.WritePropertyName(SaveEnvelope.RevisionProperty);
                    writer.WriteValue(envelope.Revision);
                    WriteOptionalString(writer, SaveEnvelope.WriteIdProperty, envelope.WriteId);
                    if (envelope.SavedAtUtc.HasValue)
                    {
                        WriteOptionalString(writer, SaveEnvelope.SavedAtUtcProperty, FormatTimestamp(envelope.SavedAtUtc.Value));
                    }

                    WriteOptionalString(writer, SaveEnvelope.DeviceIdProperty, envelope.DeviceId);
                    WriteOptionalString(writer, SaveEnvelope.AccountIdProperty, envelope.AccountId);
                    writer.WritePropertyName(SaveEnvelope.DataSha256Property);
                    writer.WriteValue(checksum);

                    // Leaves the object open; the raw data bytes and the closing brace follow
                    writer.WritePropertyName(SaveEnvelope.DataProperty);
                    writer.Flush();
                }

                stream.Write(dataBytes, 0, dataBytes.Length);
                stream.WriteByte(CloseBrace);
                return new EncodedEnvelope(stream.ToArray(), checksum);
            }
        }

        /// <summary>Full decode: header, data, too-new detection and checksum verification.</summary>
        public static EnvelopeDecodeResult Decode(byte[] bytes, int supportedSchema)
        {
            return Read(bytes, supportedSchema, true, true);
        }

        /// <summary>Reads the header without materializing data; optionally verifies the checksum (no data parse needed).</summary>
        public static EnvelopeDecodeResult PeekHeader(byte[] bytes, int supportedSchema, bool verifyChecksum)
        {
            return Read(bytes, supportedSchema, false, verifyChecksum);
        }

        /// <summary>Decodes a raw TData JSON value (CloudReadOnly); no envelope, no checksum.</summary>
        public static RawPayloadDecodeResult DecodeRaw(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return RawPayloadDecodeResult.Corrupt("Payload is empty.", null);
            }

            try
            {
                return RawPayloadDecodeResult.Ok(SaveJson.Parse(bytes));
            }
            catch (Exception exception)
            {
                return RawPayloadDecodeResult.Corrupt(exception.Message, exception);
            }
        }

        /// <summary>Runs the hook once per step (from, from+1, ... target-1). Hook exceptions and null returns become failures.</summary>
        public static PayloadUpgradeResult Upgrade(JToken payload, int fromSchema, int targetSchema, PayloadUpgradeHandler handler)
        {
            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            if (payload == null)
            {
                return PayloadUpgradeResult.Failed(fromSchema, "Payload is null.", null);
            }

            if (fromSchema == targetSchema)
            {
                return PayloadUpgradeResult.Success(payload);
            }

            if (fromSchema > targetSchema)
            {
                return PayloadUpgradeResult.Failed(fromSchema, "Payload schema " + fromSchema + " is newer than " + targetSchema + ".", null);
            }

            if (!(payload is JObject current))
            {
                return PayloadUpgradeResult.Failed(fromSchema, "Payload root must be a JSON object to upgrade.", null);
            }

            for (int schema = fromSchema; schema < targetSchema; schema++)
            {
                JObject next;
                try
                {
                    next = handler(current, schema);
                }
                catch (Exception exception)
                {
                    return PayloadUpgradeResult.Failed(schema, "UpgradePayload threw from schema " + schema + ".", exception);
                }

                if (next == null)
                {
                    return PayloadUpgradeResult.Failed(schema, "UpgradePayload returned null from schema " + schema + ".", null);
                }

                current = next;
            }

            return PayloadUpgradeResult.Success(current);
        }

        /// <summary>True when fmt or schema cannot be read by this build.</summary>
        public static bool IsTooNew(int format, int schema, int supportedSchema)
        {
            return format > SaveEnvelope.CurrentFormat || schema > supportedSchema;
        }

        /// <summary>Locates the exact data bytes via the first top-level compact data marker; false when data is not provably last.</summary>
        internal static bool TryLocateData(byte[] bytes, out int start, out int length)
        {
            start = -1;
            length = 0;
            if (bytes == null)
            {
                return false;
            }

            int depth = 0;
            bool inString = false;
            bool escaped = false;
            int dataStart = -1;
            int rootEnd = -1;

            for (int i = 0; i < bytes.Length; i++)
            {
                byte b = bytes[i];
                if (rootEnd >= 0)
                {
                    if (!IsJsonWhitespace(b))
                    {
                        return false;
                    }

                    continue;
                }

                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (b == Backslash)
                    {
                        escaped = true;
                    }
                    else if (b == Quote)
                    {
                        inString = false;
                    }

                    continue;
                }

                switch (b)
                {
                    case Quote:
                        inString = true;
                        break;
                    case OpenBrace:
                    case OpenBracket:
                        depth++;
                        break;
                    case CloseBrace:
                    case CloseBracket:
                        depth--;
                        if (depth < 0)
                        {
                            return false;
                        }

                        if (depth == 0)
                        {
                            rootEnd = i;
                        }

                        break;
                    case Comma:
                        if (depth == 1)
                        {
                            // A top-level property after data means data is not last
                            if (dataStart >= 0)
                            {
                                return false;
                            }

                            if (MatchesMarker(bytes, i))
                            {
                                dataStart = i + DataMarker.Length;
                                i = dataStart - 1;
                            }
                        }

                        break;
                }
            }

            if (rootEnd < 0 || dataStart < 0 || dataStart > rootEnd)
            {
                return false;
            }

            int first = dataStart;
            int end = rootEnd;
            while (first < end && IsJsonWhitespace(bytes[first]))
            {
                first++;
            }

            while (end > first && IsJsonWhitespace(bytes[end - 1]))
            {
                end--;
            }

            if (end <= first)
            {
                return false;
            }

            start = first;
            length = end - first;
            return true;
        }

        private static EnvelopeDecodeResult Read(byte[] bytes, int supportedSchema, bool materializeData, bool verifyChecksum)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return EnvelopeDecodeResult.Corrupt(CorruptionCause.ParseFailed, "Payload is empty.", null);
            }

            var envelope = new SaveEnvelope { Format = 0 };
            bool hasFormat = false;
            bool hasSchema = false;
            bool hasRevision = false;
            bool hasData = false;

            // A known too-new schema stays non-destructive even when the rest is unreadable
            EnvelopeDecodeResult CorruptOrTooNew(string message, Exception exception)
            {
                if (hasFormat && hasSchema && envelope.Schema > supportedSchema)
                {
                    return EnvelopeDecodeResult.TooNew(EnvelopeDecodeStatus.SchemaTooNew, envelope, SchemaTooNewMessage(envelope.Schema, supportedSchema));
                }

                return EnvelopeDecodeResult.Corrupt(CorruptionCause.ParseFailed, message, exception);
            }

            try
            {
                using (JsonTextReader reader = SaveJson.CreateReader(bytes, 0, bytes.Length))
                {
                    if (!ReadSkippingComments(reader) || reader.TokenType != JsonToken.StartObject)
                    {
                        return CorruptOrTooNew("Root is not a JSON object.", null);
                    }

                    bool closed = false;
                    while (ReadSkippingComments(reader))
                    {
                        if (reader.TokenType == JsonToken.EndObject)
                        {
                            closed = true;
                            break;
                        }

                        if (reader.TokenType != JsonToken.PropertyName)
                        {
                            return CorruptOrTooNew("Unexpected token " + reader.TokenType + ".", null);
                        }

                        string name = (string)reader.Value;
                        if (!ReadSkippingComments(reader))
                        {
                            return CorruptOrTooNew("Truncated property " + name + ".", null);
                        }

                        switch (name)
                        {
                            case SaveEnvelope.FormatProperty:
                                if (!TryReadInt(reader, out int format) || format < 1)
                                {
                                    return CorruptOrTooNew("fmt is invalid.", null);
                                }

                                envelope.Format = format;
                                hasFormat = true;
                                if (format > SaveEnvelope.CurrentFormat)
                                {
                                    return EnvelopeDecodeResult.TooNew(
                                        EnvelopeDecodeStatus.FormatTooNew, envelope, "fmt " + format + " > " + SaveEnvelope.CurrentFormat + ".");
                                }

                                break;
                            case SaveEnvelope.SchemaProperty:
                                if (!TryReadInt(reader, out int schema) || schema < 0)
                                {
                                    return CorruptOrTooNew("schema is invalid.", null);
                                }

                                envelope.Schema = schema;
                                hasSchema = true;
                                break;
                            case SaveEnvelope.RevisionProperty:
                                if (!TryReadLong(reader, out long revision))
                                {
                                    return CorruptOrTooNew("rev is invalid.", null);
                                }

                                envelope.Revision = revision;
                                hasRevision = true;
                                break;
                            case SaveEnvelope.WriteIdProperty:
                                if (!TryReadString(reader, out string writeId))
                                {
                                    return CorruptOrTooNew("writeId is invalid.", null);
                                }

                                envelope.WriteId = writeId;
                                break;
                            case SaveEnvelope.AccountIdProperty:
                                if (!TryReadString(reader, out string accountId))
                                {
                                    return CorruptOrTooNew("accountId is invalid.", null);
                                }

                                envelope.AccountId = accountId;
                                break;
                            case SaveEnvelope.DataSha256Property:
                                if (!TryReadString(reader, out string dataSha256))
                                {
                                    return CorruptOrTooNew("dataSha256 is invalid.", null);
                                }

                                envelope.DataSha256 = dataSha256;
                                break;
                            case SaveEnvelope.SavedAtUtcProperty:
                                // Diagnostics only; a bad value is ignored
                                envelope.SavedAtUtc = reader.TokenType == JsonToken.String ? ParseTimestamp((string)reader.Value) : null;
                                reader.Skip();
                                break;
                            case SaveEnvelope.DeviceIdProperty:
                                envelope.DeviceId = reader.TokenType == JsonToken.String ? (string)reader.Value : null;
                                reader.Skip();
                                break;
                            case SaveEnvelope.DataProperty:
                                hasData = true;
                                bool schemaTooNew = hasSchema && envelope.Schema > supportedSchema;
                                if (materializeData && !schemaTooNew)
                                {
                                    envelope.Data = JToken.ReadFrom(reader, SaveJson.LoadSettings);
                                }
                                else
                                {
                                    envelope.Data = null;
                                    reader.Skip();
                                }

                                break;
                            default:
                                reader.Skip();
                                break;
                        }
                    }

                    if (!closed)
                    {
                        return CorruptOrTooNew("Truncated envelope.", null);
                    }

                    SaveJson.EnsureEndOfContent(reader);
                }
            }
            catch (Exception exception)
            {
                return CorruptOrTooNew(exception.Message, exception);
            }

            if (!hasFormat || !hasSchema || !hasRevision)
            {
                return CorruptOrTooNew("fmt, schema or rev is missing.", null);
            }

            if (envelope.Schema > supportedSchema)
            {
                return EnvelopeDecodeResult.TooNew(EnvelopeDecodeStatus.SchemaTooNew, envelope, SchemaTooNewMessage(envelope.Schema, supportedSchema));
            }

            if (!hasData)
            {
                return EnvelopeDecodeResult.Corrupt(CorruptionCause.ParseFailed, "data is missing.", null);
            }

            if (!verifyChecksum)
            {
                return EnvelopeDecodeResult.Ok(envelope, ChecksumStatus.NotChecked);
            }

            if (envelope.DataSha256 == null)
            {
                return EnvelopeDecodeResult.Ok(envelope, ChecksumStatus.NotPresent);
            }

            if (!TryLocateData(bytes, out int dataStart, out int dataLength))
            {
                return EnvelopeDecodeResult.Ok(envelope, ChecksumStatus.Unverifiable);
            }

            if (!PayloadChecksum.Matches(envelope.DataSha256, bytes, dataStart, dataLength))
            {
                return EnvelopeDecodeResult.Corrupt(
                    CorruptionCause.ChecksumMismatch, "dataSha256 does not match the data bytes.", null, ChecksumStatus.Mismatch);
            }

            return EnvelopeDecodeResult.Ok(envelope, ChecksumStatus.Verified);
        }

        private static string SchemaTooNewMessage(int schema, int supportedSchema)
        {
            return "schema " + schema + " > " + supportedSchema + ".";
        }

        private static bool ReadSkippingComments(JsonReader reader)
        {
            while (reader.Read())
            {
                if (reader.TokenType != JsonToken.Comment)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryReadInt(JsonReader reader, out int value)
        {
            value = 0;
            if (!TryReadLong(reader, out long number) || number < int.MinValue || number > int.MaxValue)
            {
                return false;
            }

            value = (int)number;
            return true;
        }

        private static bool TryReadLong(JsonReader reader, out long value)
        {
            value = 0;
            if (reader.TokenType != JsonToken.Integer || !(reader.Value is long number))
            {
                return false;
            }

            value = number;
            return true;
        }

        private static bool TryReadString(JsonReader reader, out string value)
        {
            value = null;
            if (reader.TokenType == JsonToken.Null)
            {
                return true;
            }

            if (reader.TokenType != JsonToken.String)
            {
                return false;
            }

            value = (string)reader.Value;
            return true;
        }

        private static void WriteOptionalString(JsonWriter writer, string name, string value)
        {
            if (value == null)
            {
                return;
            }

            writer.WritePropertyName(name);
            writer.WriteValue(value);
        }

        private static string FormatTimestamp(DateTime value)
        {
            DateTime utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
            return utc.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        }

        private static DateTime? ParseTimestamp(string text)
        {
            if (DateTime.TryParse(
                    text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out DateTime value))
            {
                return value;
            }

            return null;
        }

        private static bool MatchesMarker(byte[] bytes, int index)
        {
            if (index > bytes.Length - DataMarker.Length)
            {
                return false;
            }

            for (int i = 0; i < DataMarker.Length; i++)
            {
                if (bytes[index + i] != DataMarker[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsJsonWhitespace(byte b)
        {
            return b == (byte)' ' || b == (byte)'\t' || b == (byte)'\n' || b == (byte)'\r';
        }
    }
}
