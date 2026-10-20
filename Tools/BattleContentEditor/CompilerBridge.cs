using System;
using System.Globalization;
using System.Linq;
using System.Text;
using PokerEngine;

// Host adapter only. The existing rules compiler remains the sole semantic authority.
internal static class CompilerBridge
{
    private static readonly BridgeJson Json = new BridgeJson();
    public static int Run(string[] args)
    {
        Console.InputEncoding = Encoding.UTF8; Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            object output;
            if (args.Length == 1 && args[0] == "--metadata")
            {
                var types = new[] { typeof(KickerScoring), typeof(ScoreOperation), typeof(BattleDiscardKind), typeof(HandCategory), typeof(CombatEffectKind), typeof(EffectAmountKind), typeof(HandEffectVariant),
                    typeof(StatusTags), typeof(StatusLifetimeKind), typeof(StatusReapplyPolicy), typeof(CombatTimingKind), typeof(PeriodicTimingSubject),
                    typeof(CombatReactionSourcePolicy), typeof(EffectTargetSelection), typeof(CombatTargetSelection),
                    typeof(BattleNumericStat), typeof(RelicNumericOperation), typeof(CombatEffectOrigin), typeof(RelicHandEffectOperation),
                    typeof(BattleRuleValueKey), typeof(CardDuelDealTrigger), typeof(BattleRefillMode), typeof(RelicTriggerTiming),
                    typeof(RelicEventRelation), typeof(RelicTriggerOccurrence), typeof(RelicFactAmount), typeof(RelicBoundaryTiming),
                    typeof(RelicPhaseRelation), typeof(RelicActionTiming), typeof(RelicActionRelation), typeof(PlayerBlockExpiry),
                    typeof(CardDuelRefillTiming), typeof(OpponentTargetSelection), typeof(Rank), typeof(Suit), typeof(CardEffects), typeof(CardInsertionPosition) };
                output = new {
                    contract = CompiledBattleContent.CurrentRulesContractVersion,
                    enums = types.ToDictionary(type => type.Name, type => Enum.GetNames(type)),
                    cards = StandardDeck.Cards().Select(card => new { id = card.Id.Value, rank = card.Rank.ToString(), suit = card.Suit.ToString() }).ToArray(),
                    scoring = Enum.GetValues(typeof(HandCategory)).Cast<HandCategory>().Select(category => {
                        var rule = ScoreProfile.Default.Rule(category);
                        return new { category = category.ToString(), points = rule.BasePoints.ToString(CultureInfo.InvariantCulture),
                            multiplier = rule.BaseMultiplier.ToString(CultureInfo.InvariantCulture), pointsPerLevel = rule.PointsPerLevel.ToString(CultureInfo.InvariantCulture),
                            multiplierPerLevel = rule.MultiplierPerLevel.ToString(CultureInfo.InvariantCulture) };
                    }).ToArray(),
                    rankPoints = Enum.GetValues(typeof(Rank)).Cast<Rank>().Select(rank => new { rank = rank.ToString(), points = CardPointProfile.Default.GetBasePoints(rank).ToString(CultureInfo.InvariantCulture) }).ToArray()
                };
            }
            else
            {
                string xml = Console.In.ReadToEnd();
                var read = BattleContentXmlReader.Read(xml, "当前内容包.xml");
                var build = read.Success ? BattleContentCompiler.Compile(read.Source) : null;
                var diagnostics = build == null ? read.Diagnostics : build.Diagnostics;
                output = new { readable = read.Success, success = build != null && build.Success,
                    fingerprint = build?.Content?.Fingerprint,
                    diagnostics = diagnostics.Select(item => new {
                        code = item.Code, severity = item.Severity.ToString(), message = item.Message,
                        path = item.Location.Path, line = item.Location.Line, column = item.Location.Column,
                        source = item.Location.SourceName, definitionId = item.DefinitionId, field = item.FieldPath
                    }).ToArray() };
            }
            Console.Write(Json.Serialize(output)); return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.GetType().Name + ": " + error.Message); return 1;
        }
    }
}
