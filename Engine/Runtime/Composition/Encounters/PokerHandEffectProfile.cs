using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace PokerEngine
{
    /// <summary>Every hit uses floor(Score.Total * ScoreFactorPerHit), before ordinary damage modifiers.</summary>
    public sealed class PokerHandDamageSettings
    {
        public decimal ScoreFactorPerHit { get; }
        public int HitCount { get; }
        public PokerHandDamageSettings(decimal scoreFactorPerHit, int hitCount = 1)
        {
            if (scoreFactorPerHit < 0 || hitCount < 1 || hitCount > 64)
                throw new ArgumentOutOfRangeException(nameof(scoreFactorPerHit), "Damage factors must be nonnegative and hit counts must be between 1 and 64.");
            ScoreFactorPerHit = scoreFactorPerHit; HitCount = hitCount;
        }
    }

    /// <summary>Explicit content numbers for the documented hand mapping; no implicit balance defaults.</summary>
    public sealed class PokerHandEffectProfile
    {
        public string Version { get; }
        public IReadOnlyDictionary<HandCategory, PokerHandDamageSettings> DamageByHand { get; }
        public decimal FlushHealPerChip { get; }
        public int StunActionWindows { get; }
        public StatusLifetimeSpec ControlImmunityLifetime { get; }
        public StatusDefinitionId StunStatusId { get; }
        public StatusDefinitionId ControlImmunityStatusId { get; }
        public IReadOnlyList<CombatStatusDefinition> StatusDefinitions { get; }

        public PokerHandEffectProfile(string version,
            IEnumerable<KeyValuePair<HandCategory, PokerHandDamageSettings>> damageByHand, decimal flushHealPerChip,
            int stunActionWindows = 1, StatusLifetimeSpec controlImmunityLifetime = null,
            StatusDefinitionId? stunStatusId = null, StatusDefinitionId? controlImmunityStatusId = null)
        {
            Version = Guard.Text(version, nameof(version));
            if (flushHealPerChip < 0 || stunActionWindows <= 0)
                throw new ArgumentException("Healing factors must be nonnegative and stun windows must be positive.");
            var entries = Guard.Copy(damageByHand, nameof(damageByHand));
            var required = Enum.GetValues(typeof(HandCategory)).Cast<HandCategory>().Where(category => category != HandCategory.FullHouse).ToArray();
            if (entries.Length != required.Length || entries.Select(entry => entry.Key).Distinct().Count() != entries.Length ||
                entries.Any(entry => !required.Contains(entry.Key) || entry.Value == null ||
                    (entry.Key == HandCategory.Straight ? entry.Value.HitCount < 2 : entry.Value.HitCount != 1)))
                throw new ArgumentException("Configure every damage hand exactly once; only a straight has multiple hits. Full house has no damage entry.", nameof(damageByHand));
            DamageByHand = new ReadOnlyDictionary<HandCategory, PokerHandDamageSettings>(entries.ToDictionary(entry => entry.Key, entry => entry.Value));
            FlushHealPerChip = flushHealPerChip; StunActionWindows = stunActionWindows;
            ControlImmunityLifetime = controlImmunityLifetime ?? StatusLifetimeSpec.ThroughNextPhase(BattleStatusTiming.EnemyPhase);
            if (ControlImmunityLifetime.Kind == StatusLifetimeKind.ThroughNextPhase &&
                ControlImmunityLifetime.PhaseKey != BattleStatusTiming.PlayerPhase && ControlImmunityLifetime.PhaseKey != BattleStatusTiming.EnemyPhase)
                throw new ArgumentException("Control immunity must use a known battle phase.", nameof(controlImmunityLifetime));
            StunStatusId = stunStatusId ?? new StatusDefinitionId("poker-hand.full-house.stun");
            ControlImmunityStatusId = controlImmunityStatusId ?? new StatusDefinitionId("poker-hand.full-house.control-immunity");
            if (!StunStatusId.IsValid || !ControlImmunityStatusId.IsValid || StunStatusId == ControlImmunityStatusId)
                throw new ArgumentException("Stun and immunity require distinct valid status IDs.");
            StatusDefinitions = Guard.View(new[] {
                new CombatStatusDefinition(StunStatusId, StatusTags.Control, new StatusBehaviorSpec[] { new BlockActionWindowSpec() },
                    StatusLifetimeSpec.ForTriggers(StunActionWindows)),
                new CombatStatusDefinition(ControlImmunityStatusId, StatusTags.None,
                    new StatusBehaviorSpec[] { new PreventStatusApplicationSpec(StatusTags.Control) }, ControlImmunityLifetime)
            });
        }

        public CombatStatusCatalog CreateStatusCatalog(string version, IEnumerable<CombatStatusDefinition> additionalDefinitions = null) =>
            new CombatStatusCatalog(version, StatusDefinitions.Concat(additionalDefinitions ?? Array.Empty<CombatStatusDefinition>()));
    }
}
