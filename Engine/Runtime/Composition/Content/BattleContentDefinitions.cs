using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    internal partial class BattleContentBuildContext
    {
        private Dictionary<string, ScoreProfile> ReadScores(BattleContentNode section) => ReadDefinitions(section, "Scoring", node =>
        {
            Check(node, new[] { "id", "name", "version", "preset" }, new[] { "Hand", "CardPoints" });
            if (Optional(node, "preset") != null)
            {
                if (Text(node, "preset") != "Default" || node.Children.Count != 0 || Optional(node, "version") != null)
                    throw new ArgumentException("Default scoring preset cannot contain custom rules or version.");
                return ScoreProfile.Default;
            }
            var hands = node.Children.Where(item => item.Name == "Hand").Select(hand =>
            {
                Check(hand, new[] { "category", "points", "multiplier", "pointsPerLevel", "multiplierPerLevel" }, new string[0]);
                Text(hand, "category"); Text(hand, "points"); Text(hand, "multiplier");
                return new HandScoreRule(Enum(hand, "category", default(HandCategory)), Long(hand, "points", 0), Decimal(hand, "multiplier", 1),
                    Long(hand, "pointsPerLevel", 10), Decimal(hand, "multiplierPerLevel", 1));
            }).ToArray();
            var points = node.Children.Where(item => item.Name == "CardPoints").ToArray();
            if (points.Length > 1) throw new ArgumentException("Only one CardPoints table is allowed.");
            return new ScoreProfile(Text(node, "version", ContentVersion), hands, points.Length == 0 ? null : ReadCardPoints(points[0]));
        });
        private CardPointProfile ReadCardPoints(BattleContentNode node)
        {
            Check(node, new string[0], new[] { "Rank", "Face" });
            var ranks = node.Children.Where(item => item.Name == "Rank").Select(item =>
            {
                Check(item, new[] { "rank", "points" }, new string[0]);
                Text(item, "rank"); Text(item, "points");
                return new RankPointRule(Enum(item, "rank", default(Rank)), Long(item, "points", 0));
            });
            var faces = node.Children.Where(item => item.Name == "Face").Select(item =>
            {
                Check(item, new[] { "rank", "suit", "points" }, new string[0]);
                Text(item, "rank"); Text(item, "suit"); Text(item, "points");
                return new FacePointRule(new CardFace(Enum(item, "rank", default(Rank)), Enum(item, "suit", default(Suit))), Long(item, "points", 0));
            });
            return new CardPointProfile(ranks, faces);
        }
        private static readonly string[] ActorAttributes = { "id", "name", "maxHealth", "initialBlock", "attackMultiplier", "attackFlat", "incomingMultiplier", "incomingFlat", "attackTarget", "allowRequestedTargets" };
        private CombatantDefinition ReadActor(BattleContentNode node) => new CombatantDefinition(Text(node, "id"), Long(node, "maxHealth", 100), Long(node, "initialBlock", 0));
        private BattleActorDamageRules ReadDamageRules(BattleContentNode node) => new BattleActorDamageRules(
            Optional(node, "attackMultiplier") == null && Optional(node, "attackFlat") == null ? null : new ScaledAttackDamageModifier(Decimal(node, "attackMultiplier", 1), Long(node, "attackFlat", 0)),
            Optional(node, "incomingMultiplier") == null && Optional(node, "incomingFlat") == null ? null : new ScaledIncomingDamageModifier(Decimal(node, "incomingMultiplier", 1), Long(node, "incomingFlat", 0)));
        private BattleActorTargetRules ReadTargetRules(BattleContentNode node) => new BattleActorTargetRules(
            Optional(node, "attackTarget") == null && Optional(node, "allowRequestedTargets") == null ? null :
                new OpponentTargetPolicy(Enum(node, "attackTarget", OpponentTargetSelection.FirstLiving), Bool(node, "allowRequestedTargets", true)));
        private Dictionary<string, BattlePlayerContentDefinition> ReadPlayers(BattleContentNode section, Dictionary<string, ConfiguredHandEffectRule> hands) =>
            ReadDefinitions(section, "Player", node =>
            {
                Check(node, ActorAttributes.Concat(new[] { "handRuleRef", "maxEnergy", "initialEnergy", "playEnergyCost", "discardEnergyCost", "handSize", "discards", "unlimitedDiscards", "maxPlayCards", "maxDiscardCards", "playRefill", "discardRefill", "discardBlockPerCard", "pointsPerDamage", "manualDraw", "autoDrawToLimit", "openingDeal", "drawCount", "drawUses", "drawWarningThreshold", "minPlayCards", "effectDrawOverflow", "discardEndsTurn", "discardCostsAction", "endDefense", "overflowTriggersDefense", "forceDiscardOverflow", "kickerScoring", "layeredScoreRelics", "discardBlockPerPoint", "actionsPerTurn", "minFlushCards", "minStraightCards" }).ToArray(), new string[0]);
                ConfiguredHandEffectRule effect = null; string reference = Optional(node, "handRuleRef");
                if (reference != null)
                {
                    if (Reference(node, "handRuleRef", reference, hands)) effect = hands[reference];
                    if (Optional(node, "pointsPerDamage") != null) throw new ArgumentException("handRuleRef and pointsPerDamage are mutually exclusive.");
                }
                if (Optional(node, "discardBlockPerPoint") != null && Optional(node, "discardBlockPerCard") != null)
                    throw new ArgumentException("Choose discardBlockPerPoint or discardBlockPerCard, not both.");
                var playerRules = new BattlePlayerRules(Int(node, "maxEnergy", 3), Int(node, "initialEnergy", 3), Int(node, "playEnergyCost", 1), Int(node, "discardEnergyCost", 1),
                    Int(node, "handSize", 8), Int(node, "discards", 3), new CardDuelDealPolicy(Enum(node, "playRefill", CardDuelRefillTiming.NextPlayerTurn), Enum(node, "discardRefill", CardDuelRefillTiming.AfterAction)),
                    Bool(node, "unlimitedDiscards", false), Int(node, "maxPlayCards", 5), Int(node, "maxDiscardCards", 5),
                    Optional(node, "discardBlockPerPoint") != null ? (IDiscardRewardRule)new ChipBlockRewardRule(Decimal(node, "discardBlockPerPoint", 0)) : Optional(node, "discardBlockPerCard") == null ? null : new PerCardBlockRewardRule(Long(node, "discardBlockPerCard", 0)),
                    Optional(node, "pointsPerDamage") == null ? null : new ScoreDamagePolicy(Long(node, "pointsPerDamage", 1)), effectRule: effect, flow: new BattleTurnFlowRules(Bool(node, "manualDraw", false), Bool(node, "openingDeal", false),
                        Int(node, "drawCount", 5), Int(node, "drawUses", 1), Int(node, "drawWarningThreshold", 3), Int(node, "minPlayCards", 1),
                        Bool(node, "effectDrawOverflow", true), Bool(node, "discardEndsTurn", true), Bool(node, "discardCostsAction", true),
                        Bool(node, "endDefense", true), Bool(node, "overflowTriggersDefense", false), Enum(node, "kickerScoring", KickerScoring.None), Bool(node, "layeredScoreRelics", false), Int(node, "actionsPerTurn", 3), Int(node, "minFlushCards", 5), Int(node, "minStraightCards", 5), Bool(node, "autoDrawToLimit", false), Bool(node, "forceDiscardOverflow", true)));
                return new BattlePlayerContentDefinition(ReadActor(node), playerRules, ReadDamageRules(node), ReadTargetRules(node));
            });
        private Dictionary<string, BattleEnemyContentDefinition> ReadEnemies(BattleContentNode section) => ReadDefinitions(section, "Enemy", node =>
        {
            Check(node, ActorAttributes.Concat(new[] { "damage", "scaleHealthWithAdventure" }).ToArray(), new[] { "Effects", "Behavior" });
            var behavior = node.Children.Where(item => item.Name == "Behavior").ToArray();
            if (behavior.Length > 1 || behavior.Length > 0 && (Optional(node, "damage") != null || node.Children.Any(c => c.Name == "Effects")))
                throw new ArgumentException("Behavior is exclusive with fixed damage/Effects.");
            var sequences = node.Children.Where(item => item.Name == "Effects").ToArray();
            if (sequences.Length > 1 || sequences.Length > 0 && Optional(node, "damage") != null)
                throw new ArgumentException("Enemy accepts either damage or one Effects sequence.");
            long damage = Long(node, "damage", 0); if (damage < 0) throw new ArgumentException("Enemy damage cannot be negative.");
            var sequence = sequences.Length == 0 ? null : ReadEnemySequence(sequences[0]);
            if (sequence?.Steps.Any(step => step.Effect.EnemyAttackScale.HasValue) == true)
                throw new ArgumentException("EnemyAttackScale requires a Behavior with a positive attack.");
            return new BattleEnemyContentDefinition(ReadActor(node), damage, ReadDamageRules(node), ReadTargetRules(node),
                sequence == null ? null : new ConfiguredEnemyEffectRule(sequence),
                behavior.Length == 0 ? null : ReadEnemyBehavior(behavior[0]), Bool(node, "scaleHealthWithAdventure", true));
        });
        private Dictionary<string, IReadOnlyList<DeckEntry>> ReadDecks(BattleContentNode section) => ReadDefinitions<IReadOnlyList<DeckEntry>>(section, "Deck", node =>
        {
            Check(node, new[] { "id", "name" }, new[] { "Card" });
            var known = new HashSet<CardDefinitionId>(StandardDeck.Cards().Select(item => item.Id));
            var entries = node.Children.Where(item => item.Name == "Card").Select(item =>
            {
                Check(item, new[] { "id", "face", "effects", "disabled" }, new string[0]);
                var face = new CardDefinitionId(Text(item, "face"));
                if (!known.Contains(face)) throw new ArgumentException("Unknown standard card face: " + face.Value);
                return new DeckEntry(Text(item, "id"), face, Enum(item, "effects", CardEffects.None), Bool(item, "disabled", false));
            }).ToArray();
            if (entries.Length < 1 || entries.Length > 512 || entries.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != entries.Length)
                throw new ArgumentException("A deck requires 1..512 cards with unique origin IDs.");
            return Array.AsReadOnly(entries);
        });
        private Dictionary<string, BattleLoadoutContentDefinition> ReadLoadouts(BattleContentNode section, Dictionary<string, BattleRelicContentDefinition> relics,
            Dictionary<string, IReadOnlyList<DeckEntry>> decks) => ReadDefinitions(section, "Loadout", node =>
        {
            Check(node, new[] { "id", "name", "deckRef" }, new[] { "Relic", "HandLevel" });
            string deck = Optional(node, "deckRef"); if (deck != null) Reference(node, "deckRef", deck, decks);
            var levels = node.Children.Where(item => item.Name == "HandLevel").Select(item =>
            {
                Check(item, new[] { "category", "level" }, new string[0]);
                Text(item, "category"); Text(item, "level");
                return new KeyValuePair<HandCategory, int>(Enum(item, "category", default(HandCategory)), Int(item, "level", 1));
            });
            var setups = node.Children.Where(item => item.Name == "Relic").Select(item => Capture(item, () =>
            {
                Check(item, new[] { "instanceId", "ref", "level", "growth", "charges", "cooldownRounds" }, new string[0]);
                string reference = Text(item, "ref"); if (!Reference(item, "ref", reference, relics)) return null;
                var relic = relics[reference];
                return new BattleRelicSetup(relic.Definition, new RunRelic(Text(item, "instanceId"), reference, Int(item, "level", 1), Long(item, "growth", 0),
                    OptionalInt(item, "charges") ?? relic.Definition.Charges, Int(item, "cooldownRounds", 0)), relic.Rules);
            })).Where(item => item != null).ToArray();
            // Even unused loadouts must be internally valid; player-dependent limits are checked at encounter assembly.
            new BattleRelicLoadout(new ParticipantId("content-validation"), setups);
            return new BattleLoadoutContentDefinition(deck, new BattleHandLevels(levels), setups);
        });
    }
}
