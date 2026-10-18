using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace PokerEngine
{
    /// <summary>Atomic build and typed encounter snapshots. Mode identity, rules and data formats are pinned explicitly.</summary>
    public static class RunSnapshotCodec<TState> where TState : class
    {
        public const int SchemaVersion = 4;
        public const int MaxUtf8Bytes = 16 * 1024 * 1024;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        [DataContract] private sealed class Envelope
        {
            [DataMember(Order = 1, IsRequired = true)] public int SchemaVersion;
            [DataMember(Order = 2, IsRequired = true)] public string EncounterKind;
            [DataMember(Order = 3, IsRequired = true)] public string EncounterRulesVersion;
            [DataMember(Order = 4, IsRequired = true)] public int EncounterSchemaVersion;
            [DataMember(Order = 5, IsRequired = true)] public RunState<TState> State;
        }
        private static DataContractJsonSerializer Serializer() => new DataContractJsonSerializer(typeof(Envelope),
            new DataContractJsonSerializerSettings { MaxItemsInObjectGraph = 500000 });
        public static string Serialize(RunState<TState> state, RunContent content, IRunEncounterState<TState> encounter)
        {
            RunValidator.Validate(state, content, encounter);
            using (var stream = new MemoryStream())
            {
                Serializer().WriteObject(stream, new Envelope { SchemaVersion = SchemaVersion, EncounterKind = encounter.Kind,
                    EncounterRulesVersion = encounter.RulesVersion, EncounterSchemaVersion = encounter.SchemaVersion, State = state });
                if (stream.Length > MaxUtf8Bytes) throw new SnapshotException("Run snapshot exceeds size limit.");
                return Utf8.GetString(stream.ToArray());
            }
        }
        public static RunState<TState> Deserialize(string json, RunContent content, IRunEncounterState<TState> encounter)
        {
            if (json == null || content == null || encounter == null) throw new ArgumentNullException();
            try
            {
                if (json.Length > MaxUtf8Bytes) throw new SnapshotException("Run snapshot exceeds size limit.");
                var bytes = Utf8.GetBytes(json); if (bytes.Length > MaxUtf8Bytes) throw new SnapshotException("Run snapshot exceeds size limit.");
                Envelope envelope;
                using (var stream = new MemoryStream(bytes)) envelope = (Envelope)Serializer().ReadObject(stream);
                if (envelope == null || envelope.SchemaVersion != SchemaVersion || envelope.EncounterKind != encounter.Kind ||
                    envelope.EncounterRulesVersion != encounter.RulesVersion || envelope.EncounterSchemaVersion != encounter.SchemaVersion)
                    throw new SnapshotException("Unsupported run or embedded encounter format/rules.");
                RunValidator.Validate(envelope.State, content, encounter); return envelope.State;
            }
            catch (SerializationException e) { throw new SnapshotException("Malformed run snapshot.", e); }
            catch (System.Xml.XmlException e) { throw new SnapshotException("Malformed run snapshot.", e); }
            catch (EncoderFallbackException e) { throw new SnapshotException("Invalid run encoding.", e); }
            catch (StateValidationException e) { throw new SnapshotException("Invalid run state: " + e.Message, e); }
        }
    }
}
