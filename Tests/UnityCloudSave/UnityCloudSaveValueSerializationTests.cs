using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Ecanakli.SaveSystem.UnityCloudSave.Tests
{
    /// <summary>
    /// Pins the SDK write path: JsonObjectConverter does JToken.FromObject(value, serializer).WriteTo(writer), so the
    /// provider must hand SaveItem a parsed JToken. A raw string would be stored as an escaped JSON string instead.
    /// </summary>
    [TestFixture]
    public sealed class UnityCloudSaveValueSerializationTests
    {
        private const string EnvelopeJson =
            "{\"fmt\":1,\"schema\":2,\"rev\":7,\"writeId\":\"w-1\",\"accountId\":\"acc-1\",\"dataSha256\":\"abc\",\"data\":{\"Coins\":5}}";

        [Test]
        public void ParsedToken_IsWrittenAsJsonObject_AndSurvivesUnchanged()
        {
            JToken parsed = JToken.Parse(EnvelopeJson);

            JToken written = JToken.FromObject(parsed, JsonSerializer.CreateDefault());

            Assert.That(written.Type, Is.EqualTo(JTokenType.Object), "The envelope must reach the backend as a JSON object.");
            Assert.That(JToken.DeepEquals(written, parsed), Is.True, "FromObject must not re-wrap or re-encode the token.");
            Assert.That(written.ToString(Formatting.None), Is.EqualTo(parsed.ToString(Formatting.None)));
        }

        [Test]
        public void RawString_WouldBeWrittenAsEscapedText()
        {
            JToken written = JToken.FromObject(EnvelopeJson, JsonSerializer.CreateDefault());

            Assert.That(written.Type, Is.EqualTo(JTokenType.String), "Documents the trap the provider avoids by parsing first.");
        }
    }
}
