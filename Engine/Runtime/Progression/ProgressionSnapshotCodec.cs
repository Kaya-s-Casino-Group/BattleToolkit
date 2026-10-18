using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace PokerEngine
{
    public static class ProgressionSnapshotCodec
    {
        public const int SchemaVersion = 1;
        public const int MaxUtf8Bytes = 16 * 1024 * 1024;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        [DataContract] private sealed class Envelope
        {
            [DataMember(Order = 1, IsRequired = true)] public int SchemaVersion;
            [DataMember(Order = 2, IsRequired = true)] public PlayerProgressionState State;
        }
        private static DataContractJsonSerializer Serializer() => new DataContractJsonSerializer(typeof(Envelope),
            new DataContractJsonSerializerSettings { MaxItemsInObjectGraph = 2000000 });
        public static string Serialize(PlayerProgressionState state, ProgressionDefinition definition)
        {
            ProgressionValidator.Validate(state, definition);
            using (var stream = new MemoryStream())
            {
                Serializer().WriteObject(stream, new Envelope { SchemaVersion = SchemaVersion, State = state });
                if (stream.Length > MaxUtf8Bytes) throw new SnapshotException("Progression snapshot exceeds size limit.");
                return Utf8.GetString(stream.ToArray());
            }
        }
        public static PlayerProgressionState Deserialize(string json, ProgressionDefinition definition)
        {
            if (json == null || definition == null) throw new ArgumentNullException();
            try
            {
                if (json.Length > MaxUtf8Bytes) throw new SnapshotException("Progression snapshot exceeds size limit.");
                var bytes = Utf8.GetBytes(json); if (bytes.Length > MaxUtf8Bytes) throw new SnapshotException("Progression snapshot exceeds size limit.");
                Envelope envelope;
                using (var stream = new MemoryStream(bytes)) envelope = (Envelope)Serializer().ReadObject(stream);
                if (envelope == null || envelope.SchemaVersion != SchemaVersion) throw new SnapshotException("Unsupported progression schema.");
                ProgressionValidator.Validate(envelope.State, definition); return envelope.State;
            }
            catch (SerializationException e) { throw new SnapshotException("Malformed progression snapshot.", e); }
            catch (System.Xml.XmlException e) { throw new SnapshotException("Malformed progression snapshot.", e); }
            catch (EncoderFallbackException e) { throw new SnapshotException("Invalid progression encoding.", e); }
            catch (StateValidationException e) { throw new SnapshotException("Invalid progression state: " + e.Message, e); }
        }
    }
}
