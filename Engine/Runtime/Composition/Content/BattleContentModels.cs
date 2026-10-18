using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace PokerEngine
{
    public sealed class BattleRelicContentDefinition
    {
        public RelicDefinition Definition { get; }
        public BattleRelicRules Rules { get; }
        public BattleRelicContentDefinition(RelicDefinition definition, BattleRelicRules rules)
        { Definition = definition ?? throw new ArgumentNullException(nameof(definition)); Rules = rules ?? throw new ArgumentNullException(nameof(rules)); }
    }
    public sealed class BattleRuleContentDefinition
    {
        public string ScoringId { get; }
        public BattleRules Rules { get; }
        internal BattleRuleContentDefinition(string scoringId, BattleRules rules) { ScoringId = scoringId; Rules = rules; }
    }
    public sealed class BattlePlayerContentDefinition
    {
        public CombatantDefinition Definition { get; }
        public BattlePlayerRules Rules { get; }
        public BattleActorDamageRules DamageRules { get; }
        public BattleActorTargetRules TargetRules { get; }
        internal BattlePlayerContentDefinition(CombatantDefinition definition, BattlePlayerRules rules, BattleActorDamageRules damageRules, BattleActorTargetRules targetRules)
        { Definition = definition; Rules = rules; DamageRules = damageRules; TargetRules = targetRules; }
    }
    public sealed class BattleEnemyContentDefinition
    {
        public CombatantDefinition Definition { get; }
        public long Damage { get; }
        public BattleActorDamageRules DamageRules { get; }
        public BattleActorTargetRules TargetRules { get; }
        public IEnemyBattleEffectRule EffectRule { get; }
        internal BattleEnemyContentDefinition(CombatantDefinition definition, long damage, BattleActorDamageRules damageRules, BattleActorTargetRules targetRules, IEnemyBattleEffectRule effectRule)
        { Definition = definition; Damage = damage; DamageRules = damageRules; TargetRules = targetRules; EffectRule = effectRule; }
    }
    public sealed class BattleLoadoutContentDefinition
    {
        public string DeckId { get; }
        public BattleHandLevels HandLevels { get; }
        public IReadOnlyList<BattleRelicSetup> Relics { get; }
        internal BattleLoadoutContentDefinition(string deckId, BattleHandLevels handLevels, IEnumerable<BattleRelicSetup> relics)
        { DeckId = deckId; HandLevels = handLevels; Relics = Array.AsReadOnly(relics.ToArray()); }
    }
    public sealed class BattlePlayerSlotDefinition
    {
        public string Id { get; }
        public string TemplateId { get; }
        public string LoadoutId { get; }
        public int? InitialHealth { get; }
        internal BattlePlayerSlotDefinition(string id, string templateId, string loadoutId, int? initialHealth)
        { Id = id; TemplateId = templateId; LoadoutId = loadoutId; InitialHealth = initialHealth; }
    }
    public sealed class BattleEnemySlotDefinition
    {
        public string Id { get; }
        public string TemplateId { get; }
        internal BattleEnemySlotDefinition(string id, string templateId) { Id = id; TemplateId = templateId; }
    }
    public sealed class BattleEncounterContentDefinition
    {
        public string RulesId { get; }
        public IReadOnlyList<BattlePlayerSlotDefinition> Players { get; }
        public IReadOnlyList<BattleEnemySlotDefinition> Enemies { get; }
        internal BattleEncounterContentDefinition(string rulesId, IEnumerable<BattlePlayerSlotDefinition> players, IEnumerable<BattleEnemySlotDefinition> enemies)
        { RulesId = rulesId; Players = Array.AsReadOnly(players.ToArray()); Enemies = Array.AsReadOnly(enemies.ToArray()); }
    }
    /// <summary>Validated immutable definitions. Every assembly creates new battle-owned state.</summary>
    public sealed class CompiledBattleContent
    {
        public const string CurrentRulesContractVersion = "battle-content.1";
        public string Id { get; }
        public string Version { get; }
        public int SchemaVersion => 1;
        public string RulesContractVersion => CurrentRulesContractVersion;
        public string Fingerprint { get; }
        public IReadOnlyDictionary<string, ScoreProfile> ScoringProfiles { get; }
        public IReadOnlyDictionary<string, BattleRuleContentDefinition> BattleRules { get; }
        public IReadOnlyDictionary<string, ConfiguredHandEffectRule> HandRules { get; }
        public IReadOnlyDictionary<string, CombatStatusDefinition> Statuses { get; }
        public IReadOnlyDictionary<string, BattleRelicContentDefinition> Relics { get; }
        public IReadOnlyDictionary<string, BattlePlayerContentDefinition> Players { get; }
        public IReadOnlyDictionary<string, BattleEnemyContentDefinition> Enemies { get; }
        public IReadOnlyDictionary<string, IReadOnlyList<DeckEntry>> Decks { get; }
        public IReadOnlyDictionary<string, BattleLoadoutContentDefinition> Loadouts { get; }
        public IReadOnlyDictionary<string, BattleEncounterContentDefinition> Encounters { get; }
        internal BattleSourceLocation Location { get; }
        internal IReadOnlyDictionary<string, BattleSourceLocation> EncounterLocations { get; }
        internal CompiledBattleContent(string id, string version, string fingerprint, BattleSourceLocation location,
            IDictionary<string, ScoreProfile> scoringProfiles, IDictionary<string, BattleRuleContentDefinition> battleRules,
            IDictionary<string, ConfiguredHandEffectRule> handRules, IDictionary<string, CombatStatusDefinition> statuses,
            IDictionary<string, BattleRelicContentDefinition> relics, IDictionary<string, BattlePlayerContentDefinition> players,
            IDictionary<string, BattleEnemyContentDefinition> enemies, IDictionary<string, IReadOnlyList<DeckEntry>> decks,
            IDictionary<string, BattleLoadoutContentDefinition> loadouts, IDictionary<string, BattleEncounterContentDefinition> encounters,
            IDictionary<string, BattleSourceLocation> encounterLocations)
        {
            Id = id; Version = version; Fingerprint = fingerprint; Location = location;
            ScoringProfiles = Freeze(scoringProfiles); BattleRules = Freeze(battleRules); HandRules = Freeze(handRules); Statuses = Freeze(statuses);
            Relics = Freeze(relics); Players = Freeze(players); Enemies = Freeze(enemies); Decks = Freeze(decks); Loadouts = Freeze(loadouts);
            Encounters = Freeze(encounters); EncounterLocations = Freeze(encounterLocations);
        }
        private static IReadOnlyDictionary<string, T> Freeze<T>(IDictionary<string, T> values) =>
            new ReadOnlyDictionary<string, T>(new Dictionary<string, T>(values, StringComparer.Ordinal));
    }
    public sealed class BattleContentBuildResult
    {
        public CompiledBattleContent Content { get; }
        public IReadOnlyList<BattleDiagnostic> Diagnostics { get; }
        public bool Success => Content != null && !Diagnostics.Any(item => item.Severity == BattleDiagnosticSeverity.Error);
        internal BattleContentBuildResult(CompiledBattleContent content, IEnumerable<BattleDiagnostic> diagnostics)
        { Content = content; Diagnostics = Array.AsReadOnly(diagnostics.ToArray()); }
    }
    public sealed class BattleSetupRequest
    {
        public string EncounterId { get; }
        public string BattleId { get; }
        public RandomState Random { get; }
        public BattleSetupRequest(string encounterId, string battleId, RandomState random)
        {
            if (string.IsNullOrWhiteSpace(encounterId) || string.IsNullOrWhiteSpace(battleId) || random == null)
                throw new ArgumentException("An encounter, battle ID and explicit random state are required.");
            EncounterId = encounterId; BattleId = battleId; Random = random;
        }
    }
    public sealed class BattleContentAssemblyResult
    {
        public BattleSession Session { get; }
        public IReadOnlyList<BattleDiagnostic> Diagnostics { get; }
        public bool Success => Session != null && !Diagnostics.Any(item => item.Severity == BattleDiagnosticSeverity.Error);
        internal BattleContentAssemblyResult(BattleSession session, IEnumerable<BattleDiagnostic> diagnostics)
        { Session = session; Diagnostics = Array.AsReadOnly(diagnostics.ToArray()); }
    }
}
