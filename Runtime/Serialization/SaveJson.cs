using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Ecanakli.SaveSystem
{
    /// <summary>Shared JSON settings and serializer used by every save path (local, cloud, snapshot, empty reference, conflict, probe).</summary>
    internal sealed class SaveJson
    {
        /// <summary>Read depth limit; above the Newtonsoft default of 64 so deep payloads that write fine never load as corrupt.</summary>
        internal const int MaxDepth = 256;

        /// <summary>Strict UTF-8 without BOM for reading; invalid bytes throw and count as a parse failure.</summary>
        internal static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        /// <summary>UTF-8 without BOM for writing; lone surrogates are replaced instead of throwing.</summary>
        internal static readonly UTF8Encoding WriteUtf8 = new UTF8Encoding(false, false);

        /// <summary>Load settings for JToken parsing; line info is not kept.</summary>
        internal static readonly JsonLoadSettings LoadSettings = new JsonLoadSettings
        {
            LineInfoHandling = LineInfoHandling.Ignore,
            CommentHandling = CommentHandling.Ignore,
        };

        /// <summary>Instance without custom converters, for paths that never touch TData.</summary>
        internal static readonly SaveJson Default = new SaveJson(null);

        /// <summary>Creates the serializer; converters are appended after the pinned settings.</summary>
        public SaveJson(IReadOnlyList<JsonConverter> converters)
        {
            Settings = CreateSettings(converters);
            Serializer = JsonSerializer.Create(Settings);
        }

        /// <summary>Pinned settings; never mutate after construction.</summary>
        public JsonSerializerSettings Settings { get; }

        /// <summary>Shared serializer built from Settings.</summary>
        public JsonSerializer Serializer { get; }

        /// <summary>Builds the pinned settings: TypeNameHandling.None, Replace, Ignore nulls, Ignore missing members.</summary>
        public static JsonSerializerSettings CreateSettings(IReadOnlyList<JsonConverter> converters)
        {
            var settings = new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.None,
                ObjectCreationHandling = ObjectCreationHandling.Replace,
                NullValueHandling = NullValueHandling.Ignore,
                MissingMemberHandling = MissingMemberHandling.Ignore,
                MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
                DateParseHandling = DateParseHandling.None,
                Formatting = Formatting.None,
                MaxDepth = MaxDepth,
            };

            if (converters != null)
            {
                for (int i = 0; i < converters.Count; i++)
                {
                    if (converters[i] == null)
                    {
                        throw new ArgumentException("Converters must not contain null entries.", nameof(converters));
                    }

                    settings.Converters.Add(converters[i]);
                }
            }

            return settings;
        }

        /// <summary>Snapshots data to a detached JToken. Throws on serializer errors (programmer error).</summary>
        public JToken Snapshot<TData>(TData data) where TData : class
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            return JToken.FromObject(data, Serializer);
        }

        /// <summary>Materializes a token to TData; returns null for a JSON null. Throws on deserialize errors.</summary>
        public TData Materialize<TData>(JToken token) where TData : class
        {
            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            return token.ToObject<TData>(Serializer);
        }

        /// <summary>Writes a token as compact UTF-8 JSON bytes.</summary>
        public static byte[] ToUtf8Bytes(JToken token)
        {
            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            using (var stream = new MemoryStream())
            {
                WriteCompact(token, stream);
                return stream.ToArray();
            }
        }

        /// <summary>Writes a token as compact UTF-8 JSON to a stream and leaves the stream open.</summary>
        internal static void WriteCompact(JToken token, Stream stream)
        {
            using (var textWriter = new StreamWriter(stream, WriteUtf8, 1024, true))
            using (var jsonWriter = CreateWriter(textWriter))
            {
                token.WriteTo(jsonWriter);
                jsonWriter.Flush();
            }
        }

        /// <summary>Creates a compact writer that does not close the underlying TextWriter.</summary>
        internal static JsonTextWriter CreateWriter(TextWriter textWriter)
        {
            return new JsonTextWriter(textWriter)
            {
                Formatting = Formatting.None,
                CloseOutput = false,
                AutoCompleteOnClose = false,
            };
        }

        /// <summary>Creates a strict reader over a byte range; a leading UTF-8 BOM is skipped. Dates stay strings.</summary>
        internal static JsonTextReader CreateReader(byte[] bytes, int offset, int count)
        {
            if (count >= 3 && bytes[offset] == 0xEF && bytes[offset + 1] == 0xBB && bytes[offset + 2] == 0xBF)
            {
                offset += 3;
                count -= 3;
            }

            var stream = new MemoryStream(bytes, offset, count, false);
            var textReader = new StreamReader(stream, StrictUtf8, false);
            return new JsonTextReader(textReader)
            {
                CloseInput = true,
                DateParseHandling = DateParseHandling.None,
                FloatParseHandling = FloatParseHandling.Double,
                MaxDepth = MaxDepth,
            };
        }

        /// <summary>Parses a complete JSON document; throws JsonException or DecoderFallbackException on bad input.</summary>
        internal static JToken Parse(byte[] bytes)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            using (JsonTextReader reader = CreateReader(bytes, 0, bytes.Length))
            {
                JToken token = JToken.ReadFrom(reader, LoadSettings);
                EnsureEndOfContent(reader);
                return token;
            }
        }

        /// <summary>Throws when anything other than comments follows the root value.</summary>
        internal static void EnsureEndOfContent(JsonReader reader)
        {
            while (reader.Read())
            {
                if (reader.TokenType != JsonToken.Comment)
                {
                    throw new JsonReaderException("Unexpected content after the root JSON value.");
                }
            }
        }
    }
}
