using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace PokerEngine
{
    /// <summary>A complete draw encounter snapshot, including paused ability choices and score arithmetic.</summary>
    public static class DrawSnapshotCodec
    {
        public const int SchemaVersion = 2;
        public const int MaxUtf8Bytes = 16 * 1024 * 1024;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        [DataContract] private sealed class Envelope
        {
            [DataMember(Order = 1, IsRequired = true)] public int SchemaVersion;
            [DataMember(Order = 2, IsRequired = true)] public DrawState State;
        }
        private static DataContractJsonSerializer Serializer() => new DataContractJsonSerializer(typeof(Envelope),
            new DataContractJsonSerializerSettings { MaxItemsInObjectGraph = 500000 });
        public static string Serialize(DrawState state, DrawEngine engine)
        {
            if (engine == null) throw new ArgumentNullException(nameof(engine)); engine.Validate(state);
            using (var stream = new MemoryStream())
            {
                Serializer().WriteObject(stream, new Envelope { SchemaVersion = SchemaVersion, State = state });
                if (stream.Length > MaxUtf8Bytes) throw new SnapshotException("Draw snapshot exceeds size limit.");
                return Utf8.GetString(stream.ToArray());
            }
        }
        public static DrawState Deserialize(string json, DrawEngine engine)
        {
            if (json == null || engine == null) throw new ArgumentNullException();
            try
            {
                if (json.Length > MaxUtf8Bytes) throw new SnapshotException("Draw snapshot exceeds size limit.");
                var bytes = Utf8.GetBytes(json); if (bytes.Length > MaxUtf8Bytes) throw new SnapshotException("Draw snapshot exceeds size limit.");
                Envelope value;
                using (var stream = new MemoryStream(bytes)) value = (Envelope)Serializer().ReadObject(stream);
                if (value == null || value.SchemaVersion != SchemaVersion) throw new SnapshotException("Unsupported draw snapshot format.");
                engine.Validate(value.State); return value.State;
            }
            catch (SerializationException ex) { throw new SnapshotException("Malformed draw snapshot.", ex); }
            catch (System.Xml.XmlException ex) { throw new SnapshotException("Malformed draw snapshot.", ex); }
            catch (EncoderFallbackException ex) { throw new SnapshotException("Invalid draw encoding.", ex); }
            catch (ArgumentException ex) { throw new SnapshotException("Invalid draw state.", ex); }
            catch (OverflowException ex) { throw new SnapshotException("Invalid draw score arithmetic.", ex); }
            catch (StateValidationException ex) { throw new SnapshotException("Invalid draw ability state.", ex); }
        }
    }
}
