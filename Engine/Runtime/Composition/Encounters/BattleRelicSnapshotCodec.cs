using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;

namespace PokerEngine
{
    /// <summary>
    /// Persists only relic runtime against an injected loadout binding. This is not a battle
    /// checkpoint; a complete battle snapshot must bind this component to its own state.
    /// </summary>
    public static class BattleRelicSnapshotCodec
    {
        public const int SchemaVersion = 1;
        public const int MaxUtf8Bytes = 16 * 1024 * 1024;
        private const int MaxItemsInObjectGraph = 500000;
        private const int MaxJsonDepth = 64;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        [DataContract]
        private sealed class Envelope
        {
            [DataMember(Order = 1, IsRequired = true)] public int SchemaVersion;
            [DataMember(Order = 2, IsRequired = true)] public ParticipantId OwnerId;
            [DataMember(Order = 3, IsRequired = true)] public string BindingFingerprint;
            [DataMember(Order = 4, IsRequired = true)] public BattleRelicState[] Relics;
        }

        private static DataContractJsonSerializer Serializer() => new DataContractJsonSerializer(typeof(Envelope),
            new DataContractJsonSerializerSettings { MaxItemsInObjectGraph = MaxItemsInObjectGraph });

        public static string Serialize(BattleRelicLoadout state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            try
            {
                var validated = state.WithRuntime(state.Relics);
                using (var stream = new MemoryStream())
                {
                    Serializer().WriteObject(stream, new Envelope
                    {
                        SchemaVersion = SchemaVersion,
                        OwnerId = validated.OwnerId,
                        BindingFingerprint = validated.Fingerprint,
                        Relics = validated.Relics.ToArray()
                    });
                    if (stream.Length > MaxUtf8Bytes) throw new SnapshotException("Battle relic snapshot exceeds size limit.");
                    return Utf8.GetString(stream.ToArray());
                }
            }
            catch (SerializationException ex) { throw new SnapshotException("Invalid battle relic snapshot.", ex); }
            catch (XmlException ex) { throw new SnapshotException("Invalid battle relic snapshot.", ex); }
            catch (StateValidationException ex) { throw new SnapshotException("Invalid battle relic state: " + ex.Message, ex); }
            catch (ArgumentException ex) { throw new SnapshotException("Invalid battle relic state.", ex); }
            catch (OverflowException ex) { throw new SnapshotException("Invalid battle relic arithmetic.", ex); }
        }

        public static BattleRelicLoadout Deserialize(string json, BattleRelicLoadout binding)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            if (binding == null) throw new ArgumentNullException(nameof(binding));
            try
            {
                if (json.Length > MaxUtf8Bytes) throw new SnapshotException("Battle relic snapshot exceeds size limit.");
                var bytes = Utf8.GetBytes(json);
                if (bytes.Length > MaxUtf8Bytes) throw new SnapshotException("Battle relic snapshot exceeds size limit.");
                ValidateSingleDocument(json);
                Envelope envelope;
                var quotas = new XmlDictionaryReaderQuotas
                {
                    MaxDepth = MaxJsonDepth,
                    MaxStringContentLength = MaxUtf8Bytes,
                    MaxArrayLength = MaxItemsInObjectGraph,
                    MaxBytesPerRead = 4096,
                    MaxNameTableCharCount = 16384
                };
                using (var reader = JsonReaderWriterFactory.CreateJsonReader(bytes, quotas))
                {
                    envelope = (Envelope)Serializer().ReadObject(reader);
                    if (reader.MoveToContent() != XmlNodeType.None)
                        throw new SnapshotException("Malformed battle relic snapshot.");
                }
                if (envelope == null || envelope.SchemaVersion != SchemaVersion)
                    throw new SnapshotException("Unsupported battle relic snapshot format.");
                if (!envelope.OwnerId.IsValid || envelope.OwnerId != binding.OwnerId ||
                    string.IsNullOrEmpty(envelope.BindingFingerprint) ||
                    !string.Equals(envelope.BindingFingerprint, binding.Fingerprint, StringComparison.Ordinal))
                    throw new SnapshotException("Battle relic snapshot does not match the loadout binding.");
                if (envelope.Relics == null) throw new SnapshotException("Missing battle relic runtime.");
                return binding.WithRuntime(envelope.Relics);
            }
            catch (SerializationException ex) { throw new SnapshotException("Malformed battle relic snapshot.", ex); }
            catch (XmlException ex) { throw new SnapshotException("Malformed battle relic snapshot.", ex); }
            catch (EncoderFallbackException ex) { throw new SnapshotException("Invalid battle relic encoding.", ex); }
            catch (StateValidationException ex) { throw new SnapshotException("Invalid battle relic state: " + ex.Message, ex); }
            catch (ArgumentException ex) { throw new SnapshotException("Invalid battle relic state.", ex); }
            catch (OverflowException ex) { throw new SnapshotException("Invalid battle relic arithmetic.", ex); }
        }

        // Mono's JSON reader can report EOF after the first value even when input remains.
        // Check the document boundary ourselves; the serializer still validates its contents.
        private static void ValidateSingleDocument(string json)
        {
            int start = 0;
            while (start < json.Length && IsJsonWhitespace(json[start])) start++;
            if (start == json.Length || json[start] != '{')
                throw new SnapshotException("Malformed battle relic snapshot.");
            var containers = new char[MaxJsonDepth];
            int depth = 0;
            for (int index = start; index < json.Length; index++)
            {
                char value = json[index];
                if (value == '"')
                {
                    SkipJsonString(json, ref index);
                    continue;
                }
                if (value == '{' || value == '[')
                {
                    if (depth == containers.Length) throw new SnapshotException("Battle relic snapshot exceeds depth limit.");
                    containers[depth++] = value;
                }
                else if (value == '}' || value == ']')
                {
                    if (depth == 0 || containers[depth - 1] != (value == '}' ? '{' : '['))
                        throw new SnapshotException("Malformed battle relic snapshot.");
                    if (--depth == 0)
                    {
                        for (int tail = index + 1; tail < json.Length; tail++)
                            if (!IsJsonWhitespace(json[tail])) throw new SnapshotException("Malformed battle relic snapshot.");
                        return;
                    }
                }
                else if (value < 0x20 && !IsJsonWhitespace(value))
                    throw new SnapshotException("Malformed battle relic snapshot.");
            }
            throw new SnapshotException("Malformed battle relic snapshot.");
        }

        private static void SkipJsonString(string json, ref int index)
        {
            while (++index < json.Length)
            {
                char value = json[index];
                if (value == '"') return;
                if (value < 0x20) throw new SnapshotException("Malformed battle relic snapshot.");
                if (value != '\\') continue;
                if (++index == json.Length) throw new SnapshotException("Malformed battle relic snapshot.");
                char escape = json[index];
                if (escape == 'u')
                {
                    for (int digit = 0; digit < 4; digit++)
                        if (++index == json.Length || !IsHexDigit(json[index]))
                            throw new SnapshotException("Malformed battle relic snapshot.");
                }
                else if (escape != '"' && escape != '\\' && escape != '/' && escape != 'b' &&
                    escape != 'f' && escape != 'n' && escape != 'r' && escape != 't')
                    throw new SnapshotException("Malformed battle relic snapshot.");
            }
            throw new SnapshotException("Malformed battle relic snapshot.");
        }

        private static bool IsJsonWhitespace(char value) => value == ' ' || value == '\t' || value == '\r' || value == '\n';
        private static bool IsHexDigit(char value) => value >= '0' && value <= '9' || value >= 'a' && value <= 'f' || value >= 'A' && value <= 'F';
    }
}
