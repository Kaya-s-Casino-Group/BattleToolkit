using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace PokerEngine
{
    /// <summary>Versioned authoritative save format. Not a network view and not proof of authenticity.</summary>
    public static class MatchSnapshotCodec
    {
        public const int SchemaVersion = 5;
        public const int MaxUtf8Bytes = 4 * 1024 * 1024;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        [DataContract]
        private sealed class Envelope
        {
            [DataMember(Order = 1, IsRequired = true)] public int SchemaVersion;
            [DataMember(Order = 2, IsRequired = true)] public MatchState State;
        }

        private static DataContractJsonSerializer Serializer() => new DataContractJsonSerializer(typeof(Envelope),
            new DataContractJsonSerializerSettings { MaxItemsInObjectGraph = 100000 });

        public static string Serialize(MatchState state, ContentCatalog catalog)
        {
            StateValidator.Validate(state, catalog);
            using (var stream = new MemoryStream())
            {
                Serializer().WriteObject(stream, new Envelope { SchemaVersion = SchemaVersion, State = state });
                if (stream.Length > MaxUtf8Bytes) throw new SnapshotException("Snapshot exceeds the size limit.");
                return Utf8.GetString(stream.ToArray());
            }
        }

        /// <summary>Caller selects a supported rules definition and exact content; no silent migration.</summary>
        public static MatchState Deserialize(string json, ContentCatalog catalog, MatchDefinition expectedDefinition)
        {
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));
            if (expectedDefinition == null) throw new ArgumentNullException(nameof(expectedDefinition));
            if (json == null) throw new ArgumentNullException(nameof(json));
            if (json.Length > MaxUtf8Bytes) throw new SnapshotException("Snapshot exceeds the size limit.");
            try
            {
                byte[] bytes = Utf8.GetBytes(json);
                if (bytes.Length > MaxUtf8Bytes) throw new SnapshotException("Snapshot exceeds the size limit.");
                Envelope envelope;
                using (var stream = new MemoryStream(bytes)) envelope = (Envelope)Serializer().ReadObject(stream);
                if (envelope == null || envelope.SchemaVersion != SchemaVersion)
                    throw new SnapshotException("Unsupported snapshot schema; explicit migration is required.");
                StateValidator.Validate(envelope.State, catalog);
                var definition = envelope.State.Definition;
                if (definition.RulesetId != expectedDefinition.RulesetId || definition.RulesetVersion != expectedDefinition.RulesetVersion ||
                    definition.ContentVersion != expectedDefinition.ContentVersion || definition.MaxSeats != expectedDefinition.MaxSeats ||
                    definition.SmallBlind != expectedDefinition.SmallBlind || definition.BigBlind != expectedDefinition.BigBlind || definition.Mode != expectedDefinition.Mode)
                    throw new SnapshotException("Snapshot rules do not match the selected definition.");
                return envelope.State;
            }
            catch (SerializationException e) { throw new SnapshotException("Malformed snapshot.", e); }
            catch (System.Xml.XmlException e) { throw new SnapshotException("Malformed snapshot.", e); }
            catch (EncoderFallbackException e) { throw new SnapshotException("Invalid snapshot text encoding.", e); }
            catch (StateValidationException e) { throw new SnapshotException("Invalid snapshot state: " + e.Message, e); }
        }
    }
}
