using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace PokerEngine
{
    public static class CardTableSnapshotCodec
    {
        public const int SchemaVersion = 1;
        public const int MaxUtf8Bytes = 16 * 1024 * 1024;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        [DataContract] private sealed class Envelope
        {
            [DataMember(Order = 1, IsRequired = true)] public int SchemaVersion;
            [DataMember(Order = 2, IsRequired = true)] public CardTableState State;
        }
        private static DataContractJsonSerializer Serializer() => new DataContractJsonSerializer(typeof(Envelope),
            new DataContractJsonSerializerSettings { MaxItemsInObjectGraph = 500000 });
        public static string Serialize(CardTableState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state)); state.Validate();
            using (var stream = new MemoryStream())
            {
                Serializer().WriteObject(stream, new Envelope { SchemaVersion = SchemaVersion, State = state });
                if (stream.Length > MaxUtf8Bytes) throw new SnapshotException("Card table snapshot exceeds size limit.");
                return Utf8.GetString(stream.ToArray());
            }
        }
        public static CardTableState Deserialize(string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            try
            {
                if (json.Length > MaxUtf8Bytes) throw new SnapshotException("Card table snapshot exceeds size limit.");
                var bytes = Utf8.GetBytes(json); if (bytes.Length > MaxUtf8Bytes) throw new SnapshotException("Card table snapshot exceeds size limit.");
                Envelope envelope; using (var stream = new MemoryStream(bytes)) envelope = (Envelope)Serializer().ReadObject(stream);
                if (envelope == null || envelope.SchemaVersion != SchemaVersion || envelope.State == null)
                    throw new SnapshotException("Unsupported card table snapshot.");
                envelope.State.Validate(); return envelope.State;
            }
            catch (SerializationException ex) { throw new SnapshotException("Malformed card table snapshot.", ex); }
            catch (System.Xml.XmlException ex) { throw new SnapshotException("Malformed card table snapshot.", ex); }
            catch (ArgumentException ex) { throw new SnapshotException("Invalid card table state.", ex); }
        }
    }
}
