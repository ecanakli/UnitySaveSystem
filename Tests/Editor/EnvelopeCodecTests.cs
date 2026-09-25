using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ecanakli.SaveSystem.Tests
{
    internal sealed class CodecTestMoney
    {
        public long Cents { get; set; }
    }

    internal sealed class CodecTestWalletData
    {
        public CodecTestMoney Balance { get; set; } = new CodecTestMoney();
    }

    /// <summary>Writes money as "cents:N" so a converter round trip is visible in the JSON.</summary>
    internal sealed class CodecTestMoneyConverter : JsonConverter<CodecTestMoney>
    {
        private const string Prefix = "cents:";

        public override void WriteJson(JsonWriter writer, CodecTestMoney value, JsonSerializer serializer)
        {
            writer.WriteValue(Prefix + value.Cents.ToString(CultureInfo.InvariantCulture));
        }

        public override CodecTestMoney ReadJson(JsonReader reader, Type objectType, CodecTestMoney existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            string text = (string)reader.Value;
            return new CodecTestMoney { Cents = long.Parse(text.Substring(Prefix.Length), CultureInfo.InvariantCulture) };
        }
    }

    internal sealed class CodecTestWalletSlot : TestSlot<CodecTestWalletData>
    {
        public CodecTestWalletSlot()
            : base("wallet", SyncMode.LocalOnly, SlotScope.Profile, 1)
        {
        }
    }

    /// <summary>Data type whose members are named like envelope fields.</summary>
    internal sealed class EnvelopeProbeData
    {
        [JsonProperty("fmt")]
        public int Format { get; set; }

        [JsonProperty("schema")]
        public int Schema { get; set; }

        [JsonProperty("rev")]
        public long Revision { get; set; }

        [JsonProperty("writeId")]
        public string WriteId { get; set; }

        [JsonProperty("savedAtUtc")]
        public string SavedAtUtc { get; set; }

        [JsonProperty("deviceId")]
        public string DeviceId { get; set; }

        [JsonProperty("accountId")]
        public string AccountId { get; set; }

        [JsonProperty("dataSha256")]
        public string DataSha256 { get; set; }

        [JsonProperty("data")]
        public string Data { get; set; }
    }

    /// <summary>EnvelopeCodec encode/decode, checksum, too-new detection, raw decode, upgrade chain and pinned serializer settings (F3, V2).</summary>
    [TestFixture]
    public sealed class EnvelopeCodecTests
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
        private static readonly DateTime SavedAt = new DateTime(2026, 1, 2, 3, 4, 5, 678, DateTimeKind.Utc);

        private static SaveJson Json => new SaveJson(Array.Empty<JsonConverter>());

        [Test]
        public void Encode_WritesDataLastAndChecksumOverExactDataBytes()
        {
            var data = new JObject { { "Coins", 5 }, { "Items", new JArray("a", "b") } };
            SaveEnvelope envelope = CreateEnvelope(data);
            envelope.DataSha256 = new string('0', PayloadChecksum.HexLength);

            EncodedEnvelope encoded = EnvelopeCodec.Encode(envelope);

            byte[] dataBytes = SaveJson.ToUtf8Bytes(data);
            string expected = "{\"fmt\":1,\"schema\":1,\"rev\":7,\"writeId\":\"write-1\",\"savedAtUtc\":\"2026-01-02T03:04:05.678Z\","
                              + "\"deviceId\":\"device-1\",\"accountId\":\"account-1\",\"dataSha256\":\"" + encoded.DataSha256 + "\",\"data\":"
                              + Text(dataBytes) + "}";
            Assert.That(Text(encoded.Bytes), Is.EqualTo(expected));
            Assert.That(encoded.DataSha256, Is.EqualTo(PayloadChecksum.Compute(dataBytes)), "Encode ignores the input DataSha256.");
            Assert.That(encoded.DataSha256, Does.Match("^[0-9a-f]{64}$"));

            Assert.That(EnvelopeCodec.TryLocateData(encoded.Bytes, out int start, out int length), Is.True);
            var located = new byte[length];
            Array.Copy(encoded.Bytes, start, located, 0, length);
            Assert.That(located, Is.EqualTo(dataBytes));
        }

        [Test]
        public void Encode_OmitsNullOptionalFields()
        {
            var envelope = new SaveEnvelope { Schema = 2, Revision = 1, Data = new JObject() };

            string text = Text(EnvelopeCodec.Encode(envelope).Bytes);

            Assert.That(text, Does.StartWith("{\"fmt\":1,\"schema\":2,\"rev\":1,\"dataSha256\":\""));
            Assert.That(text, Does.EndWith(",\"data\":{}}"));
        }

        [Test]
        public void Encode_NullEnvelopeOrData_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => EnvelopeCodec.Encode(null));
            Assert.Throws<ArgumentException>(() => EnvelopeCodec.Encode(new SaveEnvelope { Schema = 1 }));
        }

        [Test]
        public void Decode_RoundTrip_VerifiesChecksumAndHeader()
        {
            var data = new JObject { { "Coins", 5 } };
            EncodedEnvelope encoded = EnvelopeCodec.Encode(CreateEnvelope(data, 2, 42));

            EnvelopeDecodeResult decoded = EnvelopeCodec.Decode(encoded.Bytes, 2);

            Assert.That(decoded.IsOk, Is.True, decoded.ToString());
            Assert.That(decoded.Checksum, Is.EqualTo(ChecksumStatus.Verified));
            Assert.That(decoded.Cause, Is.EqualTo(CorruptionCause.None));
            SaveEnvelope envelope = decoded.Envelope;
            Assert.That(envelope.Format, Is.EqualTo(SaveEnvelope.CurrentFormat));
            Assert.That(envelope.Schema, Is.EqualTo(2));
            Assert.That(envelope.Revision, Is.EqualTo(42));
            Assert.That(envelope.WriteId, Is.EqualTo("write-1"));
            Assert.That(envelope.SavedAtUtc, Is.EqualTo(SavedAt));
            Assert.That(envelope.DeviceId, Is.EqualTo("device-1"));
            Assert.That(envelope.AccountId, Is.EqualTo("account-1"));
            Assert.That(envelope.DataSha256, Is.EqualTo(encoded.DataSha256));
            Assert.That(JToken.DeepEquals(envelope.Data, data), Is.True);
        }

        [Test]
        public void Decode_ChecksumMismatch_ReportsCorrupt()
        {
            byte[] bytes = EnvelopeCodec.Encode(CreateEnvelope(new JObject { { "Coins", 5 } })).Bytes;
            byte[] tampered = Utf8(Text(bytes).Replace("\"Coins\":5", "\"Coins\":6"));

            EnvelopeDecodeResult decoded = EnvelopeCodec.Decode(tampered, 1);

            Assert.That(decoded.Status, Is.EqualTo(EnvelopeDecodeStatus.Corrupt));
            Assert.That(decoded.Cause, Is.EqualTo(CorruptionCause.ChecksumMismatch));
            Assert.That(decoded.Checksum, Is.EqualTo(ChecksumStatus.Mismatch));
            Assert.That(decoded.Envelope, Is.Null);
        }

        [TestCase("abc")]
        [TestCase("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
        public void Decode_MalformedChecksum_ReportsCorrupt(string checksum)
        {
            EncodedEnvelope encoded = EnvelopeCodec.Encode(CreateEnvelope(new JObject { { "Coins", 5 } }));
            byte[] tampered = Utf8(Text(encoded.Bytes).Replace(encoded.DataSha256, checksum));

            EnvelopeDecodeResult decoded = EnvelopeCodec.Decode(tampered, 1);

            Assert.That(decoded.Status, Is.EqualTo(EnvelopeDecodeStatus.Corrupt));
            Assert.That(decoded.Cause, Is.EqualTo(CorruptionCause.ChecksumMismatch));
        }

        [Test]
        public void Decode_UppercaseChecksum_Verified()
        {
            EncodedEnvelope encoded = EnvelopeCodec.Encode(CreateEnvelope(new JObject { { "Coins", 5 } }));
            byte[] upper = Utf8(Text(encoded.Bytes).Replace(encoded.DataSha256, encoded.DataSha256.ToUpperInvariant()));

            EnvelopeDecodeResult decoded = EnvelopeCodec.Decode(upper, 1);

            Assert.That(decoded.Checksum, Is.EqualTo(ChecksumStatus.Verified));
        }

        [Test]
        public void Decode_MissingChecksum_Accepted()
        {
            byte[] bytes = Utf8("{\"fmt\":1,\"schema\":1,\"rev\":3,\"data\":{\"Coins\":5}}");

            EnvelopeDecodeResult decoded = EnvelopeCodec.Decode(bytes, 1);

            Assert.That(decoded.IsOk, Is.True, decoded.ToString());
            Assert.That(decoded.Checksum, Is.EqualTo(ChecksumStatus.NotPresent));
            Assert.That((int)decoded.Envelope.Data["Coins"], Is.EqualTo(5));
        }

        [Test]
        public void Decode_PrettyPrinted_ChecksumUnverifiable_Accepted()
        {
            var envelope = new SaveEnvelope { Schema = 1, Revision = 7, DeviceId = "device-1", Data = new JObject { { "Coins", 5 } } };
            string compact = Text(EnvelopeCodec.Encode(envelope).Bytes);
            string pretty = JToken.Parse(compact).ToString(Formatting.Indented);

            EnvelopeDecodeResult decoded = EnvelopeCodec.Decode(Utf8(pretty), 1);
            EnvelopeDecodeResult editedPretty = EnvelopeCodec.Decode(Utf8(pretty.Replace("\"Coins\": 5", "\"Coins\": 6")), 1);

            Assert.That(decoded.IsOk, Is.True, decoded.ToString());
            Assert.That(decoded.Checksum, Is.EqualTo(ChecksumStatus.Unverifiable));
            Assert.That(decoded.Envelope.Revision, Is.EqualTo(7));
            Assert.That(editedPretty.IsOk, Is.True, "Hand-edited pretty-printed files are accepted as unverifiable by design.");
            Assert.That((int)editedPretty.Envelope.Data["Coins"], Is.EqualTo(6));
        }

        [Test]
        public void Decode_DataNotLast_ChecksumUnverifiable_Accepted()
        {
            string checksum = PayloadChecksum.Compute(Utf8("{\"Coins\":5}"));
            byte[] bytes = Utf8("{\"fmt\":1,\"schema\":1,\"rev\":1,\"data\":{\"Coins\":5},\"dataSha256\":\"" + checksum + "\"}");

            EnvelopeDecodeResult decoded = EnvelopeCodec.Decode(bytes, 1);

            Assert.That(decoded.IsOk, Is.True, decoded.ToString());
            Assert.That(decoded.Checksum, Is.EqualTo(ChecksumStatus.Unverifiable));
        }

        [Test]
        public void Decode_HeaderStringContainingDataMarkerEscaped_LocatesTopLevelData()
        {
            var data = new JObject
            {
                { "Coins", 5 },
                { "Nested", new JObject { { "data", 1 } } },
                { "Text", ",\"data\":{}" },
            };
            SaveEnvelope envelope = CreateEnvelope(data);
            envelope.DeviceId = "x,\"data\":{\"Coins\":999}";
            envelope.AccountId = "a\\\",\"data\":";
            byte[] bytes = EnvelopeCodec.Encode(envelope).Bytes;

            EnvelopeDecodeResult decoded = EnvelopeCodec.Decode(bytes, 1);

            Assert.That(decoded.IsOk, Is.True, decoded.ToString());
            Assert.That(decoded.Checksum, Is.EqualTo(ChecksumStatus.Verified));
            Assert.That(decoded.Envelope.DeviceId, Is.EqualTo(envelope.DeviceId));
            Assert.That(decoded.Envelope.AccountId, Is.EqualTo(envelope.AccountId));
            Assert.That(JToken.DeepEquals(decoded.Envelope.Data, data), Is.True);

            Assert.That(EnvelopeCodec.TryLocateData(bytes, out int start, out int length), Is.True);
            Assert.That(Encoding.UTF8.GetString(bytes, start, length), Is.EqualTo(Text(SaveJson.ToUtf8Bytes(data))));
        }

        [Test]
        public void Decode_FormatTooNew_ReportsFormatTooNew_WithoutData()
        {
            EnvelopeDecodeResult decoded = EnvelopeCodec.Decode(Utf8("{\"fmt\":2,\"schema\":1,\"rev\":1,\"data\":{\"Coins\":5}}"), 1);

            Assert.That(decoded.Status, Is.EqualTo(EnvelopeDecodeStatus.FormatTooNew));
            Assert.That(decoded.IsTooNew, Is.True);
            Assert.That(decoded.Envelope.Format, Is.EqualTo(2));
            Assert.That(decoded.Envelope.Data, Is.Null);
        }

        [Test]
        public void Decode_FormatTooNew_RestUnreadable_StillFormatTooNew()
        {
            EnvelopeDecodeResult decoded = EnvelopeCodec.Decode(Utf8("{\"fmt\":9,\"schema\":{{{"), 1);

            Assert.That(decoded.Status, Is.EqualTo(EnvelopeDecodeStatus.FormatTooNew));
        }

        [Test]
        public void Decode_SchemaTooNew_ReportsSchemaTooNew_WithoutData()
        {
            byte[] bytes = EnvelopeCodec.Encode(CreateEnvelope(new JObject { { "Coins", 5 } }, 3, 9)).Bytes;

            EnvelopeDecodeResult decoded = EnvelopeCodec.Decode(bytes, 2);

            Assert.That(decoded.Status, Is.EqualTo(EnvelopeDecodeStatus.SchemaTooNew));
            Assert.That(decoded.Envelope.Schema, Is.EqualTo(3));
            Assert.That(decoded.Envelope.Data, Is.Null);
            Assert.That(EnvelopeCodec.IsTooNew(1, 3, 2), Is.True);
            Assert.That(EnvelopeCodec.IsTooNew(2, 1, 2), Is.True);
            Assert.That(EnvelopeCodec.IsTooNew(1, 2, 2), Is.False);
        }

        [Test]
        public void Decode_SchemaTooNew_TruncatedData_StaysSchemaTooNew()
        {
            EnvelopeDecodeResult decoded = EnvelopeCodec.Decode(Utf8("{\"fmt\":1,\"schema\":5,\"rev\":1,\"data\":{\"Coins\":"), 1);

            Assert.That(decoded.Status, Is.EqualTo(EnvelopeDecodeStatus.SchemaTooNew));
            Assert.That(decoded.Envelope.Schema, Is.EqualTo(5));
        }

        [TestCase("")]
        [TestCase("{")]
        [TestCase("[]")]
        [TestCase("null")]
        [TestCase("{\"fmt\":1,\"schema\":1,\"data\":{}}")]
        [TestCase("{\"fmt\":1,\"rev\":1,\"data\":{}}")]
        [TestCase("{\"schema\":1,\"rev\":1,\"data\":{}}")]
        [TestCase("{\"fmt\":1,\"schema\":1,\"rev\":1}")]
        [TestCase("{\"fmt\":\"1\",\"schema\":1,\"rev\":1,\"data\":{}}")]
        [TestCase("{\"fmt\":0,\"schema\":1,\"rev\":1,\"data\":{}}")]
        [TestCase("{\"fmt\":1,\"schema\":-1,\"rev\":1,\"data\":{}}")]
        [TestCase("{\"fmt\":1,\"schema\":1,\"rev\":1.5,\"data\":{}}")]
        [TestCase("{\"fmt\":1,\"schema\":1,\"rev\":1,\"writeId\":5,\"data\":{}}")]
        [TestCase("{\"fmt\":1,\"schema\":1,\"rev\":1,\"data\":{}} trailing")]
        [TestCase("{\"fmt\":1,\"schema\":1,\"rev\":1,\"data\":{\"Coins\":5}")]
        public void Decode_Malformed_ReportsCorruptParseFailed(string json)
        {
            EnvelopeDecodeResult decoded = EnvelopeCodec.Decode(Utf8(json), 1);

            Assert.That(decoded.Status, Is.EqualTo(EnvelopeDecodeStatus.Corrupt), decoded.ToString());
            Assert.That(decoded.Cause, Is.EqualTo(CorruptionCause.ParseFailed));
            Assert.That(decoded.Envelope, Is.Null);
        }

        [Test]
        public void Decode_NullOrInvalidUtf8_ReportsCorrupt()
        {
            Assert.That(EnvelopeCodec.Decode(null, 1).Status, Is.EqualTo(EnvelopeDecodeStatus.Corrupt));
            Assert.That(EnvelopeCodec.Decode(new byte[] { 0x7B, 0xFF, 0xFE, 0x7D }, 1).Status, Is.EqualTo(EnvelopeDecodeStatus.Corrupt));
        }

        [Test]
        public void Decode_Utf8Bom_IsSkipped()
        {
            byte[] bytes = EnvelopeCodec.Encode(CreateEnvelope(new JObject { { "Coins", 5 } })).Bytes;
            var withBom = new byte[bytes.Length + 3];
            withBom[0] = 0xEF;
            withBom[1] = 0xBB;
            withBom[2] = 0xBF;
            Array.Copy(bytes, 0, withBom, 3, bytes.Length);

            EnvelopeDecodeResult decoded = EnvelopeCodec.Decode(withBom, 1);

            Assert.That(decoded.IsOk, Is.True, decoded.ToString());
        }

        [Test]
        public void PeekHeader_DoesNotMaterializeData_OptionallyVerifiesChecksum()
        {
            byte[] bytes = EnvelopeCodec.Encode(CreateEnvelope(new JObject { { "Coins", 5 } }, 1, 11)).Bytes;
            byte[] tampered = Utf8(Text(bytes).Replace("\"Coins\":5", "\"Coins\":6"));

            EnvelopeDecodeResult verified = EnvelopeCodec.PeekHeader(bytes, 1, true);
            EnvelopeDecodeResult unchecked_ = EnvelopeCodec.PeekHeader(tampered, 1, false);
            EnvelopeDecodeResult mismatch = EnvelopeCodec.PeekHeader(tampered, 1, true);

            Assert.That(verified.IsOk, Is.True);
            Assert.That(verified.Envelope.Data, Is.Null);
            Assert.That(verified.Envelope.Revision, Is.EqualTo(11));
            Assert.That(verified.Checksum, Is.EqualTo(ChecksumStatus.Verified));
            Assert.That(unchecked_.IsOk, Is.True);
            Assert.That(unchecked_.Checksum, Is.EqualTo(ChecksumStatus.NotChecked));
            Assert.That(mismatch.Cause, Is.EqualTo(CorruptionCause.ChecksumMismatch));
        }

        [Test]
        public void DecodeRaw_ValidJson_ReturnsTokenWithoutEnvelope()
        {
            RawPayloadDecodeResult decoded = EnvelopeCodec.DecodeRaw(Utf8("{\"Granted\":[\"gem\"],\"ServerVersion\":3}"));

            Assert.That(decoded.IsOk, Is.True);
            Assert.That(decoded.Cause, Is.EqualTo(CorruptionCause.None));
            Assert.That((int)decoded.Data["ServerVersion"], Is.EqualTo(3));
        }

        [Test]
        public void DecodeRaw_Invalid_ReportsParseFailed()
        {
            Assert.That(EnvelopeCodec.DecodeRaw(null).Cause, Is.EqualTo(CorruptionCause.ParseFailed));
            Assert.That(EnvelopeCodec.DecodeRaw(new byte[0]).IsOk, Is.False);
            Assert.That(EnvelopeCodec.DecodeRaw(Utf8("{\"Granted\":")).IsOk, Is.False);
            Assert.That(EnvelopeCodec.DecodeRaw(Utf8("{} {}")).IsOk, Is.False);
            Assert.That(EnvelopeCodec.DecodeRaw(new byte[] { 0x22, 0xC3, 0x28, 0x22 }).IsOk, Is.False);
        }

        [Test]
        public void Upgrade_RunsOneStepPerSchema_InOrder()
        {
            var calls = new List<int>();
            var payload = new JObject { { "name", "Bob" }, { "Coins", 5 } };

            PayloadUpgradeResult result = EnvelopeCodec.Upgrade(payload, 1, 3, (current, from) =>
            {
                calls.Add(from);
                return SchemaMigrations.Step(current, from);
            });

            Assert.That(result.IsSuccess, Is.True, result.Message);
            Assert.That(result.FailedFromSchema, Is.EqualTo(-1));
            Assert.That(calls, Is.EqualTo(new[] { 1, 2 }));
            var upgraded = (JObject)result.Payload;
            Assert.That((string)upgraded["DisplayName"], Is.EqualTo("Bob"));
            Assert.That((long)upgraded["Gold"], Is.EqualTo(5));
            Assert.That(upgraded.ContainsKey("name"), Is.False);
            Assert.That(upgraded.ContainsKey("Coins"), Is.False);
        }

        [Test]
        public void Upgrade_SameSchema_DoesNotCallHook()
        {
            int calls = 0;
            var payload = new JObject();

            PayloadUpgradeResult result = EnvelopeCodec.Upgrade(payload, 2, 2, (current, from) =>
            {
                calls++;
                return current;
            });

            Assert.That(result.IsSuccess, Is.True);
            Assert.That(result.Payload, Is.SameAs(payload));
            Assert.That(calls, Is.EqualTo(0));
        }

        [Test]
        public void Upgrade_FailureCases_ReturnFailureNeverThrow()
        {
            PayloadUpgradeResult newer = EnvelopeCodec.Upgrade(new JObject(), 3, 2, (current, from) => current);
            PayloadUpgradeResult throws = EnvelopeCodec.Upgrade(new JObject(), 1, 3, (current, from) =>
            {
                if (from == 2)
                {
                    throw new InvalidOperationException("step 2 failed");
                }

                return current;
            });
            PayloadUpgradeResult returnsNull = EnvelopeCodec.Upgrade(new JObject(), 1, 2, (current, from) => null);
            PayloadUpgradeResult notObject = EnvelopeCodec.Upgrade(new JArray(), 1, 2, (current, from) => current);
            PayloadUpgradeResult nullPayload = EnvelopeCodec.Upgrade(null, 1, 2, (current, from) => current);

            Assert.That(newer.IsSuccess, Is.False);
            Assert.That(throws.IsSuccess, Is.False);
            Assert.That(throws.FailedFromSchema, Is.EqualTo(2));
            Assert.That(throws.Exception, Is.InstanceOf<InvalidOperationException>());
            Assert.That(returnsNull.IsSuccess, Is.False);
            Assert.That(returnsNull.FailedFromSchema, Is.EqualTo(1));
            Assert.That(notObject.IsSuccess, Is.False);
            Assert.That(nullPayload.IsSuccess, Is.False);
            Assert.Throws<ArgumentNullException>(() => EnvelopeCodec.Upgrade(new JObject(), 1, 2, null));
        }

        [Test]
        public void SlotRunUpgrade_FromV1ToV3_CallsHookForEachStep_AndLeavesHookScope()
        {
            var slot = new SchemaV3Slot();

            PayloadUpgradeResult result = slot.RunUpgrade(new JObject { { "name", "Ann" }, { "Coins", 2 } }, 1);

            Assert.That(result.IsSuccess, Is.True, result.Message);
            Assert.That(slot.UpgradeFromVersions, Is.EqualTo(new[] { 1, 2 }));
            Assert.That((string)result.Payload["DisplayName"], Is.EqualTo("Ann"));
            Assert.That(HookScope.IsActive, Is.False);
        }

        [Test]
        public void RoundTrip_NullSubObject_StaysNullUntilNormalize()
        {
            SaveJson json = Json;
            JToken token = json.Snapshot(new InitializerData());
            Assert.That(((JObject)token).ContainsKey("OptionalStats"), Is.False, "Nulls are not written.");

            JToken decoded = DecodeData(token);
            var slot = new InitializerSlot { NormalizeFillsOptionalStats = true };
            SlotMaterializeResult normalized = slot.MaterializeNormalized(decoded);

            Assert.That(json.Materialize<InitializerData>(decoded).OptionalStats, Is.Null);
            Assert.That(normalized.IsSuccess, Is.True, normalized.Message);
            Assert.That(((InitializerData)normalized.Data).OptionalStats, Is.Not.Null);
            Assert.That(slot.NormalizeCount, Is.EqualTo(1));
        }

        [Test]
        public void Decode_MissingSubObject_KeepsFieldInitializerInstance()
        {
            InitializerData data = Json.Materialize<InitializerData>(DecodeData(JObject.Parse("{\"Items\":[]}")));

            Assert.That(data.Stats, Is.Not.Null);
            Assert.That(data.Stats.Strength, Is.EqualTo(1));
            Assert.That(data.Stats.Agility, Is.EqualTo(0));
        }

        [Test]
        public void Decode_ExplicitNullSubObject_WithInitializer_KeepsInitializer()
        {
            byte[] bytes = Utf8("{\"fmt\":1,\"schema\":1,\"rev\":1,\"data\":{\"Stats\":null,\"Items\":null}}");
            EnvelopeDecodeResult decoded = EnvelopeCodec.Decode(bytes, 1);

            InitializerData data = Json.Materialize<InitializerData>(decoded.Envelope.Data);

            Assert.That(data.Stats, Is.Not.Null);
            Assert.That(data.Stats.Strength, Is.EqualTo(1));
            Assert.That(data.Items, Is.EqualTo(InitializerData.DefaultItems));
        }

        [Test]
        public void Decode_ListWithDefaultItemsInitializer_DoesNotDuplicateItems()
        {
            SaveJson json = Json;

            InitializerData replaced = json.Materialize<InitializerData>(DecodeData(JObject.Parse("{\"Items\":[\"gem\"]}")));
            InitializerData roundTripped = json.Materialize<InitializerData>(DecodeData(json.Snapshot(new InitializerData())));
            InitializerData twice = json.Materialize<InitializerData>(DecodeData(json.Snapshot(roundTripped)));

            Assert.That(replaced.Items, Is.EqualTo(new[] { "gem" }));
            Assert.That(roundTripped.Items, Is.EqualTo(InitializerData.DefaultItems));
            Assert.That(twice.Items, Is.EqualTo(InitializerData.DefaultItems));
        }

        [Test]
        public void Decode_DictionaryInitializer_IsReplacedNotMerged()
        {
            InitializerData data = Json.Materialize<InitializerData>(DecodeData(JObject.Parse("{\"Counters\":{\"losses\":3}}")));

            Assert.That(data.Counters.Keys, Is.EquivalentTo(new[] { "losses" }));
            Assert.That(data.Counters["losses"], Is.EqualTo(3));
        }

        [Test]
        public void Decode_UnknownProperty_IsIgnored()
        {
            JToken token = DecodeData(JObject.Parse("{\"Unknown\":{\"deep\":[1,2]},\"$type\":\"System.Object, mscorlib\",\"Items\":[\"x\"]}"));

            InitializerData data = null;
            Assert.DoesNotThrow(() => data = Json.Materialize<InitializerData>(token));
            Assert.That(data.Items, Is.EqualTo(new[] { "x" }));
            Assert.That(Json.Settings.TypeNameHandling, Is.EqualTo(TypeNameHandling.None));
        }

        [Test]
        public void CustomConverter_AppliedOnSnapshotAndMaterialize()
        {
            var json = new SaveJson(new JsonConverter[] { new CodecTestMoneyConverter() });
            var data = new CodecTestWalletData { Balance = new CodecTestMoney { Cents = 1234 } };

            JToken token = json.Snapshot(data);
            JToken decoded = DecodeData(token);

            Assert.That((string)token["Balance"], Is.EqualTo("cents:1234"));
            Assert.That(json.Materialize<CodecTestWalletData>(decoded).Balance.Cents, Is.EqualTo(1234));
            Assert.That(json.Settings.TypeNameHandling, Is.EqualTo(TypeNameHandling.None), "Converters never change pinned settings.");
        }

        [Test]
        public void CustomConverterFromOptions_AppliedOnEncodeAndDecode()
        {
            var slot = new CodecTestWalletSlot();
            var setup = new TestServiceSetup
            {
                Slots = new SaveSlot[] { slot },
                ConfigureOptions = options => options.JsonConverters = new JsonConverter[] { new CodecTestMoneyConverter() },
            };

            TestServiceContext context = TestServiceFactory.Create(setup);
            TestServiceContext restarted = null;
            try
            {
                Assert.That(context.InitializeSync().IsSuccess, Is.True);
                Assert.That(slot.Mutate(data => data.Balance = new CodecTestMoney { Cents = 1234 }), Is.True);
                Assert.That(context.Service.FlushLocalNow().IsComplete, Is.True);

                byte[] bytes = context.Storage.GetBytes(TestPaths.ProfileSlot(ProfileId.Guest, "wallet"));
                EnvelopeDecodeResult decoded = EnvelopeCodec.Decode(bytes, 1);
                Assert.That(decoded.IsOk, Is.True, decoded.ToString());
                Assert.That((string)decoded.Envelope.Data["Balance"], Is.EqualTo("cents:1234"));

                var reloaded = new CodecTestWalletSlot();
                restarted = TestServiceFactory.Restart(context, reloaded);
                Assert.That(restarted.InitializeSync().IsSuccess, Is.True);
                Assert.That(reloaded.Read(data => data.Balance.Cents), Is.EqualTo(1234));
            }
            finally
            {
                context.Dispose();
                restarted?.Dispose();
            }
        }

        [Test]
        public void LocalFileSnapshotAndConflictFile_ProduceIdenticalTokens()
        {
            var slot = new InitializerSlot(syncMode: SyncMode.LocalOnly);
            using (TestServiceContext context = TestServiceFactory.Create(slot))
            {
                Assert.That(context.InitializeSync().IsSuccess, Is.True);
                Assert.That(slot.Mutate(data =>
                {
                    data.Items.Add("gem");
                    data.Stats.Strength = 5;
                    data.Counters["wins"] = 2;
                }), Is.True);
                Assert.That(context.Service.FlushLocalNow().IsComplete, Is.True);

                string directory = TestPaths.ProfileDirectory(ProfileId.Guest);
                JToken fileData = EnvelopeCodec.Decode(context.Storage.GetBytes(SaveLayout.SlotPath(directory, slot.Key)), 1).Envelope.Data;
                JToken snapshot = slot.CaptureSnapshot(false).Data;
                var store = new SlotStore(context.Storage, new TestSaveLogger(), context.Clock);
                Assert.That(store.WriteConflictFile(directory, slot.Key, ConflictResolutionKind.KeepLocal, snapshot, fileData).IsSuccess, Is.True);
                var conflict = (JObject)SaveJson.Parse(context.Storage.GetBytes(SaveLayout.ConflictPath(directory, slot.Key)));

                Assert.That(JToken.DeepEquals(fileData, snapshot), Is.True);
                Assert.That(JToken.DeepEquals(conflict["local"], snapshot), Is.True);
                Assert.That(JToken.DeepEquals(conflict["cloud"], fileData), Is.True);
            }
        }

        // 04b B2 F3: local file, cloud upload payload, stored cloud value, snapshot, conflict file and the cloud restore path agree
        [Test]
        public void AllCodecPaths_UseSameSettings_ProduceIdenticalTokens()
        {
            const string account = "account-codec";
            ProfileId profile = ProfileId.Account(account);
            var store = new FakeCloudStore();
            var slot = new InitializerSlot();
            TestServiceContext deviceA = CreateCloudDevice(slot, store, account, TestServiceFactory.DefaultDeviceId);
            TestServiceContext deviceB = null;
            try
            {
                ActivateReconciled(deviceA, profile);
                Assert.That(slot.Mutate(data =>
                {
                    data.Items.Add("gem");
                    data.Stats.Strength = 5;
                    data.Counters["wins"] = 2;
                }), Is.True);

                FlushResult flush = deviceA.FlushSync();
                Assert.That(flush.IsComplete, Is.True, flush.ToString());

                string directory = TestPaths.ProfileDirectory(profile);
                JToken snapshot = slot.CaptureSnapshot(false).Data;
                Assert.That(((JObject)snapshot).ContainsKey("OptionalStats"), Is.False, "Premise: a null member is omitted.");

                EnvelopeDecodeResult local = EnvelopeCodec.Decode(deviceA.Storage.GetBytes(SaveLayout.SlotPath(directory, slot.Key)), 1);
                IReadOnlyList<CloudWriteRequest> writes = deviceA.Provider.GetAllWriteRequests();
                Assert.That(writes.Count, Is.EqualTo(1), flush.ToString());
                EnvelopeDecodeResult uploaded = EnvelopeCodec.Decode(writes[0].Value, 1);
                EnvelopeDecodeResult stored = EnvelopeCodec.Decode(store.GetValue(account, slot.Key), 1);

                Assert.That(local.IsOk, Is.True, local.ToString());
                Assert.That(uploaded.IsOk, Is.True, uploaded.ToString());
                Assert.That(stored.IsOk, Is.True, stored.ToString());
                Assert.That(local.Checksum, Is.EqualTo(ChecksumStatus.Verified));
                Assert.That(uploaded.Checksum, Is.EqualTo(ChecksumStatus.Verified));
                Assert.That(JToken.DeepEquals(local.Envelope.Data, snapshot), Is.True, "Local file vs snapshot.");
                Assert.That(JToken.DeepEquals(uploaded.Envelope.Data, snapshot), Is.True, "Cloud upload payload vs snapshot.");
                Assert.That(JToken.DeepEquals(stored.Envelope.Data, snapshot), Is.True, "Stored cloud value vs snapshot.");
                Assert.That(uploaded.Envelope.DataSha256, Is.EqualTo(local.Envelope.DataSha256), "Both paths serialize the same data bytes.");

                var slotStore = new SlotStore(deviceA.Storage, new TestSaveLogger(), deviceA.Clock);
                Assert.That(slotStore.WriteConflictFile(directory, slot.Key, ConflictResolutionKind.KeepLocal, snapshot, uploaded.Envelope.Data).IsSuccess, Is.True);
                var conflict = (JObject)SaveJson.Parse(deviceA.Storage.GetBytes(SaveLayout.ConflictPath(directory, slot.Key)));
                Assert.That(JToken.DeepEquals(conflict["local"], snapshot), Is.True, "Conflict file local side.");
                Assert.That(JToken.DeepEquals(conflict["cloud"], uploaded.Envelope.Data), Is.True, "Conflict file cloud side.");

                // Cloud decode and materialize on another device, then its own snapshot and local mirror
                var restored = new InitializerSlot();
                deviceB = CreateCloudDevice(restored, store, account, "device-b");
                RestoreReport report = ActivateReconciled(deviceB, profile);
                Assert.That(report.GetResult(restored).Outcome, Is.EqualTo(SlotRestoreOutcome.Restored), report.ToString());

                InitializerData restoredData = restored.PeekData();
                Assert.That(restoredData.Items, Is.EqualTo(new[] { "starter_sword", "starter_shield", "gem" }), "Replace, not append, on the cloud path.");
                Assert.That(restoredData.Counters.Keys, Is.EquivalentTo(new[] { "wins" }));
                Assert.That(restoredData.Counters["wins"], Is.EqualTo(2));
                Assert.That(restoredData.Stats.Strength, Is.EqualTo(5));
                Assert.That(restoredData.OptionalStats, Is.Null);
                Assert.That(JToken.DeepEquals(restored.CaptureSnapshot(false).Data, snapshot), Is.True, "Snapshot after the cloud path.");

                EnvelopeDecodeResult mirror = EnvelopeCodec.Decode(deviceB.Storage.GetBytes(SaveLayout.SlotPath(TestPaths.ProfileDirectory(profile), restored.Key)), 1);
                Assert.That(mirror.IsOk, Is.True, mirror.ToString());
                Assert.That(JToken.DeepEquals(mirror.Envelope.Data, snapshot), Is.True, "Local mirror written by the restore.");
            }
            finally
            {
                deviceA.Dispose();
                deviceB?.Dispose();
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NormalizedDefaultWithInitializers_IsEmpty(bool normalizeFillsOptionalStats)
        {
            var slot = new InitializerSlot { NormalizeFillsOptionalStats = normalizeFillsOptionalStats };

            SlotMaterializeResult created = slot.CreateNormalizedDefault();
            SlotMaterializeResult loaded = slot.MaterializeNormalized(DecodeData(slot.SnapshotOf(created.Data)));

            Assert.That(created.IsSuccess, Is.True);
            Assert.That(slot.EvaluateIsEmpty(created.Data), Is.True);
            Assert.That(loaded.IsSuccess, Is.True);
            Assert.That(slot.EvaluateIsEmpty(loaded.Data), Is.True, "A saved and reloaded default stays empty.");

            ((InitializerData)loaded.Data).Items.Add("gem");
            Assert.That(slot.EvaluateIsEmpty(loaded.Data), Is.False);
        }

        [Test]
        public void EnvelopeFields_NeverReachTData()
        {
            SaveEnvelope envelope = CreateEnvelope(new JObject(), 1, 42);
            EnvelopeDecodeResult decoded = EnvelopeCodec.Decode(EnvelopeCodec.Encode(envelope).Bytes, 1);

            EnvelopeProbeData probe = Json.Materialize<EnvelopeProbeData>(decoded.Envelope.Data);

            Assert.That(probe.Format, Is.EqualTo(0));
            Assert.That(probe.Schema, Is.EqualTo(0));
            Assert.That(probe.Revision, Is.EqualTo(0));
            Assert.That(probe.WriteId, Is.Null);
            Assert.That(probe.SavedAtUtc, Is.Null);
            Assert.That(probe.DeviceId, Is.Null);
            Assert.That(probe.AccountId, Is.Null);
            Assert.That(probe.DataSha256, Is.Null);
            Assert.That(probe.Data, Is.Null);
        }

        [Test]
        public void EnvelopeFields_DoNotAffectIsEmpty()
        {
            var slot = new ProfileSlot();
            var storage = new InMemorySaveStorage();
            var store = new SlotStore(storage, new TestSaveLogger(), new ManualSaveClock());
            SaveEnvelope envelope = CreateEnvelope(SaveJson.Default.Snapshot(new ProfileData()), 1, 42);
            storage.SetBytes(SaveLayout.SlotPath("profiles/guest", slot.Key), EnvelopeCodec.Encode(envelope).Bytes);

            SlotLoadResult result = store.Load(slot, "profiles/guest");

            Assert.That(result.IsReady, Is.True);
            Assert.That(result.Revision, Is.EqualTo(42));
            Assert.That(result.WriteId, Is.EqualTo("write-1"));
            Assert.That(slot.EvaluateIsEmpty(result.Data), Is.True);
        }

        private static SaveEnvelope CreateEnvelope(JToken data, int schema = 1, long revision = 7)
        {
            return new SaveEnvelope
            {
                Schema = schema,
                Revision = revision,
                WriteId = "write-1",
                SavedAtUtc = SavedAt,
                DeviceId = "device-1",
                AccountId = "account-1",
                Data = data,
            };
        }

        private static TestServiceContext CreateCloudDevice(SaveSlot slot, FakeCloudStore store, string accountId, string deviceId)
        {
            return TestServiceFactory.Create(new TestServiceSetup
            {
                Slots = new[] { slot },
                Provider = new FakeCloudSaveProvider(store, null, accountId),
                DeviceId = deviceId,
                ConfigureOptions = options => options.CloudRetryCount = 0,
            });
        }

        // Initialized, active and reconciled this epoch
        private static RestoreReport ActivateReconciled(TestServiceContext context, ProfileId profile)
        {
            Assert.That(context.InitializeSync().IsSuccess, Is.True);
            ProfileActivationResult activated = context.ActivateSync(profile);
            Assert.That(activated.IsSuccess, Is.True, activated.ToString());
            RestoreReport report = AsyncTestUtility.RunSync(context.Service.RestoreAsync(CancellationToken.None), nameof(SaveService.RestoreAsync));
            Assert.That(report.Status, Is.EqualTo(SaveStatus.Success), report.ToString());
            return report;
        }

        // Full local codec path: encode, decode with checksum, return data
        private static JToken DecodeData(JToken data)
        {
            EnvelopeDecodeResult decoded = EnvelopeCodec.Decode(EnvelopeCodec.Encode(CreateEnvelope(data)).Bytes, 1);
            Assert.That(decoded.IsOk, Is.True, decoded.ToString());
            Assert.That(decoded.Checksum, Is.EqualTo(ChecksumStatus.Verified));
            return decoded.Envelope.Data;
        }

        private static string Text(byte[] bytes)
        {
            return Encoding.UTF8.GetString(bytes);
        }

        private static byte[] Utf8(string text)
        {
            return Utf8NoBom.GetBytes(text);
        }
    }
}
