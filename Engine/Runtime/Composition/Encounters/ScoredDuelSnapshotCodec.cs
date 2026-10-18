using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace PokerEngine
{
    /// <summary>Duel snapshot; restore checks pinned rules and replays its score/action ledger.</summary>
    public static class ScoredDuelSnapshotCodec
    {
        public const int SchemaVersion = 4;
        public const int MaxUtf8Bytes = 16 * 1024 * 1024;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        [DataContract] private sealed class Envelope
        {
            [DataMember(Order = 1, IsRequired = true)] public int SchemaVersion;
            [DataMember(Order = 2, IsRequired = true)] public ScoredDuelState State;
        }
        private static DataContractJsonSerializer Serializer() => new DataContractJsonSerializer(typeof(Envelope),
            new DataContractJsonSerializerSettings { MaxItemsInObjectGraph = 500000 });
        public static string Serialize(ScoredDuelState state, ScoredDuelEngine engine)
        {
            if (engine == null) throw new ArgumentNullException(nameof(engine)); engine.Validate(state);
            using (var stream = new MemoryStream())
            {
                Serializer().WriteObject(stream, new Envelope { SchemaVersion = SchemaVersion, State = state });
                if (stream.Length > MaxUtf8Bytes) throw new SnapshotException("Duel snapshot exceeds size limit.");
                return Utf8.GetString(stream.ToArray());
            }
        }
        public static ScoredDuelState Deserialize(string json, ScoredDuelEngine engine)
        {
            if (json == null || engine == null) throw new ArgumentNullException();
            try
            {
                if (json.Length > MaxUtf8Bytes) throw new SnapshotException("Duel snapshot exceeds size limit.");
                var bytes = Utf8.GetBytes(json); if (bytes.Length > MaxUtf8Bytes) throw new SnapshotException("Duel snapshot exceeds size limit.");
                Envelope value;
                using (var stream = new MemoryStream(bytes)) value = (Envelope)Serializer().ReadObject(stream);
                if (value == null || value.SchemaVersion != SchemaVersion) throw new SnapshotException("Unsupported duel snapshot format.");
                engine.Validate(value.State); return value.State;
            }
            catch (SerializationException ex) { throw new SnapshotException("Malformed duel snapshot.", ex); }
            catch (System.Xml.XmlException ex) { throw new SnapshotException("Malformed duel snapshot.", ex); }
            catch (EncoderFallbackException ex) { throw new SnapshotException("Invalid duel encoding.", ex); }
            catch (ArgumentException ex) { throw new SnapshotException("Invalid duel state.", ex); }
            catch (OverflowException ex) { throw new SnapshotException("Invalid duel score arithmetic.", ex); }
        }
    }
}
