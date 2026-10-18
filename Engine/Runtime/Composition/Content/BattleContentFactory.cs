using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PokerEngine
{
    /// <summary>Assembles immutable content into independent battle instances without starting the encounter.</summary>
    public static class BattleContentFactory
    {
        public static BattleContentAssemblyResult Create(CompiledBattleContent content, BattleSetupRequest request)
        {
            try { return new BattleContentAssemblyResult(CreateSession(content, request), Array.Empty<BattleDiagnostic>()); }
            catch (ArgumentException exception) { return Failure(content, request, "AssemblyInvalid", exception.Message); }
            catch (OverflowException exception) { return Failure(content, request, "AssemblyOverflow", exception.Message); }
        }

        internal static BattleSession CreateSession(CompiledBattleContent content, BattleSetupRequest request)
        {
            if (content == null) throw new ArgumentNullException(nameof(content));
            if (request == null) throw new ArgumentNullException(nameof(request));
            var encounter = Find(content.Encounters, request.EncounterId, "encounter");
            var rules = Find(content.BattleRules, encounter.RulesId, "battle rules");
            var scoring = Find(content.ScoringProfiles, rules.ScoringId, "scoring profile");
            var players = encounter.Players.Select(slot =>
            {
                var template = Find(content.Players, slot.TemplateId, "player template");
                var loadout = slot.LoadoutId == null ? null : Find(content.Loadouts, slot.LoadoutId, "loadout");
                var deck = loadout?.DeckId == null ? null : Find(content.Decks, loadout.DeckId, "deck");
                var id = new ParticipantId(slot.Id);
                return new BattlePlayerSetup(id, template.Definition, deck, slot.InitialHealth,
                    WithHandLevels(template.Rules, loadout?.HandLevels ?? template.Rules.HandLevels), template.DamageRules, template.TargetRules,
                    (loadout?.Relics ?? Array.Empty<BattleRelicSetup>()).Select(relic => InstantiateRelic(id, relic)));
            }).ToArray();
            var enemies = encounter.Enemies.Select(slot =>
            {
                var template = Find(content.Enemies, slot.TemplateId, "enemy template");
                return new BattleEnemySetup(new ParticipantId(slot.Id), template.Definition, template.Damage,
                    template.DamageRules, template.TargetRules, template.EffectRule);
            }).ToArray();
            var engine = new CardDuelEngine(rules.Rules, scoring: scoring);
            return new BattleSession(engine, engine.Create(request.BattleId, request.Random, players, enemies));
        }

        private static BattlePlayerRules WithHandLevels(BattlePlayerRules rules, BattleHandLevels levels) =>
            new BattlePlayerRules(rules.MaxEnergy, rules.InitialEnergy, rules.PlayEnergyCost, rules.DiscardEnergyCost,
                rules.HandSize, rules.Discards, rules.DealPolicy, rules.UnlimitedDiscards, rules.MaxPlayCards, rules.MaxDiscardCards,
                rules.DiscardReward, rules.EffectRule == null ? rules.DamagePolicy : null,
                new BattleHandLevels(levels.Levels), rules.EffectRule);

        private static BattleRelicSetup InstantiateRelic(ParticipantId owner, BattleRelicSetup setup)
        {
            var original = setup.Instance;
            // Length prefixes preserve identity even when author IDs contain slashes, colons or common prefixes.
            string instanceId = "content-relic/" + Segment(owner.Value) + "/" + Segment(original.Id);
            return new BattleRelicSetup(setup.Definition,
                new RunRelic(instanceId, original.DefinitionId, original.Level, original.Growth,
                    original.Charges, original.CooldownRounds, BattleRelicValidation.CopyMemory(original.Memory)), setup.BattleRules);
        }

        private static string Segment(string value) => value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value;

        private static T Find<T>(IReadOnlyDictionary<string, T> entries, string id, string kind)
        {
            T value;
            if (id == null || !entries.TryGetValue(id, out value)) throw new ArgumentException("Unknown " + kind + " reference: " + id);
            return value;
        }

        private static BattleContentAssemblyResult Failure(CompiledBattleContent content, BattleSetupRequest request, string code, string message)
        {
            var location = content?.Location ?? new BattleSourceLocation("content.xml", 0, 0, "/BattleContent");
            BattleSourceLocation encounterLocation;
            if (content != null && request != null && content.EncounterLocations.TryGetValue(request.EncounterId, out encounterLocation))
                location = encounterLocation;
            return new BattleContentAssemblyResult(null, new[] {
                new BattleDiagnostic(code, BattleDiagnosticSeverity.Error, message, location, request?.EncounterId)
            });
        }
    }
}
