using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;

namespace PokerEngine
{
    [DataContract]
    internal sealed class AdventureTargetSlot
    {
        [DataMember(Order = 1, IsRequired = true)] public string Id;
        [DataMember(Order = 2, IsRequired = true)] public string[] Targets;
    }
    [DataContract]
    internal sealed class AdventureLogEntry
    {
        [DataMember(Order = 1, IsRequired = true)] public long Revision;
        [DataMember(Order = 2, IsRequired = true)] public bool IsBattle;
        [DataMember(Order = 3, IsRequired = true)] public AdventureCommandKind Kind;
        [DataMember(Order = 4)] public string Id;
        [DataMember(Order = 5)] public Suit? Suit;
        [DataMember(Order = 6, IsRequired = true)] public CardDuelAction Action;
        [DataMember(Order = 7)] public string[] Cards;
        [DataMember(Order = 8)] public string[] Targets;
        [DataMember(Order = 9)] public AdventureTargetSlot[] Bindings;
        [DataMember(Order = 10, IsRequired = true)] public int DrawCount;
        internal static AdventureLogEntry ForCommand(long revision, AdventureCommand command) =>
            new AdventureLogEntry { Revision = revision, Kind = command.Kind, Id = command.Id, Suit = command.Suit };
        internal static AdventureLogEntry ForBattle(long revision, CardDuelAction action, IEnumerable<CardInstanceId> cards,
            IEnumerable<ParticipantId> targets, BattleTargetBindings bindings, int drawCount)
        {
            var entry = new AdventureLogEntry { Revision = revision, IsBattle = true, Action = action, Cards = cards?.Select(c => c.Value).ToArray(),
                Targets = targets?.Select(t => t.Value).ToArray(), Bindings = bindings?.Slots.Select(p =>
                    new AdventureTargetSlot { Id = p.Key, Targets = p.Value.Select(t => t.Value).ToArray() }).ToArray(), DrawCount = drawCount };
            entry.Validate(); return entry;
        }
        internal BattleTargetBindings CreateBindings() => Bindings == null ? null : new BattleTargetBindings(Bindings.Select(b =>
            new KeyValuePair<string, IEnumerable<ParticipantId>>(b.Id, b.Targets.Select(t => new ParticipantId(t)))));
        internal void Validate()
        {
            if (Revision < 0 || Id != null && (string.IsNullOrWhiteSpace(Id) || Id.Length > 512) ||
                Suit.HasValue && !Enum.IsDefined(typeof(Suit), Suit.Value) || DrawCount < 0 || DrawCount > 512 ||
                IsBattle && Action != CardDuelAction.Play && Action != CardDuelAction.Discard && Action != CardDuelAction.EndTurn &&
                    Action != CardDuelAction.Draw && Action != CardDuelAction.SkipDraw && Action != CardDuelAction.DiscardForDraw &&
                    Action != CardDuelAction.ConfirmOverflow && Action != CardDuelAction.FinishTurn ||
                !IsBattle && (!Enum.IsDefined(typeof(AdventureCommandKind), Kind) || Action != 0 || Cards != null || Targets != null || Bindings != null || DrawCount != 0) ||
                IsBattle && (Id != null || Suit.HasValue || Kind != 0))
                throw new ArgumentException("Invalid adventure log command.");
            // Ordinary play/discard caps are enforced by BattleSession; overflow/effect draws can hold 512.
            CheckIds(Cards, 512); CheckIds(Targets, 64);
            if (Bindings != null)
            {
                if (Bindings.Length > 64 || Bindings.Any(b => b == null || string.IsNullOrWhiteSpace(b.Id) || b.Id.Length > 256))
                    throw new ArgumentException("Invalid target bindings.");
                foreach (var b in Bindings) { if (b.Targets == null) throw new ArgumentException("Missing targets."); CheckIds(b.Targets, 64); }
                CreateBindings();
            }
        }
        private static void CheckIds(string[] ids, int max)
        {
            if (ids != null && (ids.Length > max || ids.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 512) || ids.Distinct().Count() != ids.Length))
                throw new ArgumentException("Invalid command selection.");
        }
    }
    public sealed partial class BattleAdventureSession
    {
        public const int SnapshotSchemaVersion = 1;
        public const int MaxCommands = 8192;
        public const int MaxSnapshotBytes = 8 * 1024 * 1024;
        private static readonly UTF8Encoding SnapshotUtf8 = new UTF8Encoding(false, true);
        [DataContract]
        private sealed class AdventureEnvelope
        {
            [DataMember(Order = 1, IsRequired = true)] public int SchemaVersion;
            [DataMember(Order = 2, IsRequired = true)] public string AdventureFingerprint;
            [DataMember(Order = 3, IsRequired = true)] public string BattleFingerprint;
            [DataMember(Order = 4, IsRequired = true)] public string AdventureXml;
            [DataMember(Order = 5, IsRequired = true)] public string RunId;
            [DataMember(Order = 6, IsRequired = true)] public ulong Seed;
            [DataMember(Order = 7, IsRequired = true)] public string TarotId;
            [DataMember(Order = 8, IsRequired = true)] public string StartingProgression;
            [DataMember(Order = 9, IsRequired = true)] public string CurrentProgression;
            [DataMember(Order = 10, IsRequired = true)] public long Revision;
            [DataMember(Order = 11, IsRequired = true)] public AdventureLogEntry[] Commands;
            [DataMember(Order = 12, IsRequired = true)] public string ResultDigest;
        }
        private static DataContractJsonSerializer SnapshotSerializer() => new DataContractJsonSerializer(typeof(AdventureEnvelope),
            new DataContractJsonSerializerSettings { MaxItemsInObjectGraph = 500000 });
        public string Export()
        {
            var envelope = new AdventureEnvelope { SchemaVersion = SnapshotSchemaVersion, AdventureFingerprint = Definition.Fingerprint,
                BattleFingerprint = Content.Fingerprint, AdventureXml = Definition.SourceXml, RunId = RunId, Seed = Seed, TarotId = TarotId,
                StartingProgression = ProgressionSnapshotCodec.Serialize(startingProgression, Definition.ProgressionDefinition),
                CurrentProgression = ProgressionSnapshotCodec.Serialize(Progression, Definition.ProgressionDefinition), Revision = Revision,
                Commands = history.ToArray(), ResultDigest = ResultDigest() };
            using (var stream = new MemoryStream())
            {
                SnapshotSerializer().WriteObject(stream, envelope);
                if (stream.Length > MaxSnapshotBytes) throw new SnapshotException("Adventure snapshot exceeds size limit.");
                return SnapshotUtf8.GetString(stream.ToArray());
            }
        }
        public static BattleAdventureSession Restore(BattleAdventureDefinition definition, CompiledBattleContent content, string json)
        {
            if (definition == null || content == null || json == null) throw new ArgumentNullException();
            try
            {
                if (json.Length > MaxSnapshotBytes) throw new SnapshotException("Adventure snapshot exceeds size limit.");
                RequireSingleJsonObject(json);
                var bytes = SnapshotUtf8.GetBytes(json);
                if (bytes.Length > MaxSnapshotBytes) throw new SnapshotException("Adventure snapshot exceeds size limit.");
                AdventureEnvelope envelope;
                using (var reader = JsonReaderWriterFactory.CreateJsonReader(bytes, new XmlDictionaryReaderQuotas
                {
                    MaxDepth = 64, MaxStringContentLength = MaxSnapshotBytes, MaxArrayLength = MaxCommands,
                    MaxBytesPerRead = 4096, MaxNameTableCharCount = 16384
                }))
                {
                    envelope = (AdventureEnvelope)SnapshotSerializer().ReadObject(reader);
                    reader.MoveToContent();
                    if (!reader.EOF) throw new SnapshotException("Trailing adventure snapshot data.");
                }
                if (envelope == null || envelope.SchemaVersion != SnapshotSchemaVersion || envelope.AdventureFingerprint != definition.Fingerprint ||
                    envelope.BattleFingerprint != content.Fingerprint || envelope.Commands == null || envelope.Commands.Length > MaxCommands ||
                    envelope.Revision != envelope.Commands.Length || envelope.AdventureXml == null ||
                    BattleAdventureXml.Load(envelope.AdventureXml, content).Fingerprint != definition.Fingerprint)
                    throw new SnapshotException("Adventure snapshot schema/content/history mismatch.");
                var initial = ProgressionSnapshotCodec.Deserialize(envelope.StartingProgression, definition.ProgressionDefinition);
                if (ProgressionSnapshotCodec.Serialize(initial, definition.ProgressionDefinition) != envelope.StartingProgression)
                    throw new SnapshotException("Noncanonical initial progression snapshot.");
                var session = new BattleAdventureSession(definition, content, envelope.RunId, envelope.Seed, envelope.TarotId, initial);
                foreach (var entry in envelope.Commands)
                {
                    if (entry == null) throw new SnapshotException("Missing adventure command.");
                    entry.Validate();
                    if (entry.Revision != session.Revision) throw new SnapshotException("Nonsequential adventure command.");
                    var result = session.Apply(entry, false);
                    if (!result.Accepted) throw new SnapshotException("Illegal adventure replay: " + result.Error);
                }
                if (session.Revision != envelope.Revision || session.ResultDigest() != envelope.ResultDigest ||
                    ProgressionSnapshotCodec.Serialize(session.Progression, definition.ProgressionDefinition) != envelope.CurrentProgression)
                    throw new SnapshotException("Adventure replay result mismatch.");
                return session;
            }
            catch (SnapshotException) { throw; }
            catch (Exception e) when (e is SerializationException || e is System.Xml.XmlException || e is ArgumentException ||
                e is InvalidOperationException || e is EncoderFallbackException || e is OverflowException || e is StateValidationException)
            { throw new SnapshotException("Malformed adventure snapshot: " + e.Message, e); }
        }
        private static void RequireSingleJsonObject(string json)
        {
            // Framework JSON readers stop at the first root value and silently ignore a second value.
            // Validate root boundaries lexically before parsing; the serializer validates the JSON grammar.
            int depth = 0; bool inString = false, escaped = false, started = false, finished = false;
            foreach (char c in json)
            {
                if (!started || finished)
                {
                    if (c == ' ' || c == '\t' || c == '\r' || c == '\n') continue;
                    if (finished || c != '{') throw new SnapshotException("Expected one adventure JSON object.");
                    started = true; depth = 1; continue;
                }
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') inString = true;
                else if (c == '{' || c == '[')
                { if (++depth > 64) throw new SnapshotException("Adventure JSON nesting exceeds limit."); }
                else if (c == '}' || c == ']') { if (--depth == 0) finished = true; }
            }
            if (!finished || inString) throw new SnapshotException("Incomplete adventure JSON object.");
        }
        private string ResultDigest()
        {
            // Binary length-prefixed values avoid ambiguous identities and delimiter collisions.
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                {
                    writer.Write(Revision); writer.Write((int)Phase); writer.Write(Health); writer.Write(MaxHealth); writer.Write(Gold);
                    writer.Write(CurrentNode?.Id ?? ""); writer.Write(RestChoiceId ?? ""); writer.Write(LastRewardGold); writer.Write(entitySequence);
                    writer.Write(deck.Count);
                    foreach (var card in deck) { writer.Write(card.Id); writer.Write(card.DefinitionId.Value); writer.Write((int)card.Effects); writer.Write(card.IsDisabled); }
                    writer.Write(relics.Count);
                    foreach (var relic in relics) { writer.Write(relic.Id); writer.Write(relic.DefinitionId); writer.Write(relic.Level); writer.Write(relic.Growth); writer.Write(relic.Charges ?? -1); }
                    foreach (var values in new[] { visited, available, purchased })
                    { writer.Write(values.Count); foreach (string value in values) writer.Write(value); }
                    writer.Write(serviceUses.Count);
                    foreach (var value in serviceUses.OrderBy(v => v.Key)) { writer.Write((int)value.Key); writer.Write(value.Value); }
                    writer.Write(battle != null);
                    if (battle != null)
                    {
                        var state = battle.State;
                        writer.Write(state.Revision); writer.Write((int)state.Duel.Phase); writer.Write(state.Duel.CompletedRounds);
                        foreach (var unit in state.Duel.Combat.Combatants)
                        { writer.Write(unit.Id.Value); writer.Write(unit.Health); writer.Write(unit.MaxHealth); writer.Write(unit.Block); }
                        foreach (var zone in state.Table.Zones)
                        { writer.Write(zone.Id.Value); writer.Write(zone.Cards.Count); foreach (var card in zone.Cards) writer.Write(card.Value); }
                        foreach (byte value in state.Random.StateBytes) writer.Write(value);
                    }
                }
                return BattleAdventureXml.Hash(Convert.ToBase64String(stream.ToArray()));
            }
        }
    }
}
