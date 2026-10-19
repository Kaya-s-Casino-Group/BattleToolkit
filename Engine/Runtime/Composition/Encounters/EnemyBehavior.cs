using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    public enum EnemyDeadTargetPolicy { Retarget, Skip }
    public enum EnemySelectionMode { Sequence, Priority, Weighted }
    public enum EnemyConditionKind { Always, All, Any, Not, HealthAtMost, RoundAtLeast, AlliesBelow, HasStatus }
    /// <summary>Conditions are evaluated only when committing the next intent.</summary>
    public sealed class EnemyCondition
    {
        public EnemyConditionKind Kind { get; }
        public decimal Value { get; }
        public string StatusId { get; }
        public IReadOnlyList<EnemyCondition> Children { get; }
        public EnemyCondition(EnemyConditionKind kind = EnemyConditionKind.Always, decimal value = 0, string statusId = null, IEnumerable<EnemyCondition> children = null)
        {
            var copy = (children ?? Array.Empty<EnemyCondition>()).ToArray();
            if (!Enum.IsDefined(typeof(EnemyConditionKind), kind) || value < 0 || copy.Any(c => c == null) || copy.Length > 32 ||
                kind == EnemyConditionKind.HealthAtMost && value > 1 || kind == EnemyConditionKind.Not && copy.Length != 1 ||
                (kind == EnemyConditionKind.All || kind == EnemyConditionKind.Any) && copy.Length == 0 ||
                kind != EnemyConditionKind.All && kind != EnemyConditionKind.Any && kind != EnemyConditionKind.Not && copy.Length != 0 ||
                kind == EnemyConditionKind.HasStatus && string.IsNullOrWhiteSpace(statusId)) throw new ArgumentException("Invalid enemy condition.");
            Kind = kind; Value = value; StatusId = statusId; Children = Array.AsReadOnly(copy);
        }
        internal bool Matches(TeamBattleState state, ParticipantId owner)
        {
            var unit = state.Combat.Get(owner);
            switch (Kind)
            {
                case EnemyConditionKind.All: return Children.All(c => c.Matches(state, owner));
                case EnemyConditionKind.Any: return Children.Any(c => c.Matches(state, owner));
                case EnemyConditionKind.Not: return !Children[0].Matches(state, owner);
                case EnemyConditionKind.HealthAtMost: return (decimal)unit.Health / unit.MaxHealth <= Value;
                case EnemyConditionKind.RoundAtLeast: return state.CompletedRounds + 1 >= Value;
                case EnemyConditionKind.AlliesBelow: return state.Enemies.Count(e => e.Id != owner && state.Combat.Get(e.Id).IsAlive) < Value;
                case EnemyConditionKind.HasStatus: return state.Combat.Statuses.Any(s => s.Target.Id == owner.Value && s.DefinitionId.Value == StatusId);
                default: return true;
            }
        }
    }
    /// <summary>World effects settle after the combat batch, in declaration order.</summary>
    public sealed class EnemyWorldEffect
    {
        public string EnemyRef { get; }
        public string CardFace { get; }
        public int Count { get; }
        public int LivingLimit { get; }
        public string Zone { get; }
        public bool Disabled { get; }
        public CardEffects CardEffects { get; }
        private EnemyWorldEffect(string enemyRef, string face, int count, int limit, string zone, bool disabled, CardEffects effects)
        {
            if (count < 1 || count > 64 || limit < 1 || limit > 63) throw new ArgumentException("Invalid world effect capacity.");
            CardEffectRules.Validate(effects);
            EnemyRef = enemyRef; CardFace = face; Count = count; LivingLimit = limit; Zone = zone; Disabled = disabled; CardEffects = effects;
        }
        public static EnemyWorldEffect Summon(string enemyRef, int count = 1, int livingLimit = 4) =>
            new EnemyWorldEffect(Guard.Text(enemyRef, nameof(enemyRef)), null, count, livingLimit, null, false, CardEffects.None);
        public static EnemyWorldEffect AddCards(string face, int count = 1, string zone = "discard", bool disabled = false, CardEffects effects = CardEffects.Ineffective | CardEffects.Unplayable)
        {
            if (zone != CardZoneKinds.Deck && zone != CardZoneKinds.Discard || !StandardDeck.Cards().Any(c => c.Id.Value == face))
                throw new ArgumentException("Generated cards require a known face and deck/discard destination.");
            return new EnemyWorldEffect(null, face, count, 63, zone, disabled, effects);
        }
    }
    public sealed class EnemyBehaviorAction
    {
        public string Id { get; }
        public string Name { get; }
        public EnemyCondition Condition { get; }
        public BattleEffectSequence Effects { get; }
        public IReadOnlyList<EnemyWorldEffect> WorldEffects { get; }
        public int Weight { get; }
        public int CooldownRounds { get; }
        public int MaxUses { get; }
        public EnemyDeadTargetPolicy DeadTargetPolicy { get; }
        public EnemyBehaviorAction(string id, string name, BattleEffectSequence effects, IEnumerable<EnemyWorldEffect> worldEffects = null,
            EnemyCondition condition = null, int weight = 1, int cooldownRounds = 0, int maxUses = 0, EnemyDeadTargetPolicy deadTargetPolicy = EnemyDeadTargetPolicy.Retarget)
        {
            Id = Guard.Text(id, nameof(id)); Name = Guard.Text(name, nameof(name));
            var world = (worldEffects ?? Array.Empty<EnemyWorldEffect>()).ToArray();
            if (!Enum.IsDefined(typeof(EnemyDeadTargetPolicy), deadTargetPolicy) || weight < 1 || weight > 10000 || cooldownRounds < 0 || maxUses < 0 || world.Length > 16 || world.Any(w => w == null) || effects == null && world.Length == 0)
                throw new ArgumentException("Invalid enemy action.");
            if (effects != null) new ConfiguredEnemyEffectRule(effects);
            Effects = effects; WorldEffects = Array.AsReadOnly(world); Condition = condition ?? new EnemyCondition();
            Weight = weight; CooldownRounds = cooldownRounds; MaxUses = maxUses; DeadTargetPolicy = deadTargetPolicy;
        }
    }
    public sealed class EnemyBehaviorStage
    {
        public string Id { get; }
        public EnemySelectionMode Mode { get; }
        public EnemyCondition Condition { get; }
        public IReadOnlyList<EnemyBehaviorAction> Actions { get; }
        public EnemyBehaviorStage(string id, EnemySelectionMode mode, IEnumerable<EnemyBehaviorAction> actions, EnemyCondition condition = null)
        {
            Id = Guard.Text(id, nameof(id)); var copy = actions.ToArray();
            if (!Enum.IsDefined(typeof(EnemySelectionMode), mode) || copy.Length == 0 || copy.Length > 64 || copy.Any(a => a == null) || copy.Select(a => a.Id).Distinct().Count() != copy.Length)
                throw new ArgumentException("Invalid enemy stage.");
            Mode = mode; Actions = Array.AsReadOnly(copy); Condition = condition ?? new EnemyCondition();
        }
    }
    public sealed class EnemyBehaviorDefinition
    {
        public IReadOnlyList<EnemyBehaviorStage> Stages { get; }
        public BattleEffectSequence OpeningEffects { get; }
        public int Seed { get; }
        public EnemyBehaviorDefinition(IEnumerable<EnemyBehaviorStage> stages, BattleEffectSequence openingEffects = null, int seed = 1)
        {
            var copy = stages.ToArray();
            if (copy.Length == 0 || copy.Length > 16 || copy.Any(s => s == null) || copy.Select(s => s.Id).Distinct().Count() != copy.Length)
                throw new ArgumentException("Invalid enemy behavior stages.");
            if (openingEffects != null && openingEffects.Steps.Any(s => s.Target.Group != EffectTargetGroup.Self || s.Effect.Kind != CombatEffectKind.ApplyStatus))
                throw new ArgumentException("Opening effects only apply statuses to Self.");
            Stages = Array.AsReadOnly(copy); OpeningEffects = openingEffects; Seed = seed;
        }
    }
    /// <summary>Immutable memory and committed intent. Previews never consume this memory.</summary>
    public sealed class EnemyBehaviorRuntime
    {
        public string StageId { get; }
        public IReadOnlyList<IReadOnlyList<ParticipantId>> Targets { get; }
        public EnemyBehaviorAction Intent { get; }
        public int SequenceCursor { get; }
        public IReadOnlyDictionary<string, int> Uses { get; }
        public IReadOnlyDictionary<string, int> LastUsedRound { get; }
        internal EnemyBehaviorRuntime(string stage = null, EnemyBehaviorAction intent = null, int cursor = 0,
            IDictionary<string, int> uses = null, IDictionary<string, int> last = null, IEnumerable<IReadOnlyList<ParticipantId>> targets = null)
        {
            StageId = stage; Intent = intent; SequenceCursor = cursor;
            Targets = Array.AsReadOnly((targets ?? Array.Empty<IReadOnlyList<ParticipantId>>()).Select(t => (IReadOnlyList<ParticipantId>)Array.AsReadOnly(t.ToArray())).ToArray());
            Uses = new System.Collections.ObjectModel.ReadOnlyDictionary<string, int>(new Dictionary<string, int>(uses ?? new Dictionary<string, int>()));
            LastUsedRound = new System.Collections.ObjectModel.ReadOnlyDictionary<string, int>(new Dictionary<string, int>(last ?? new Dictionary<string, int>()));
        }
        internal EnemyBehaviorRuntime Complete(int round)
        {
            if (Intent == null) return this;
            string key = StageId + "/" + Intent.Id;
            var uses = Uses.ToDictionary(p => p.Key, p => p.Value); var last = LastUsedRound.ToDictionary(p => p.Key, p => p.Value);
            uses[key] = uses.TryGetValue(key, out int n) ? checked(n + 1) : 1; last[key] = round;
            return new EnemyBehaviorRuntime(StageId, null, checked(SequenceCursor + 1), uses, last);
        }
        internal EnemyBehaviorRuntime Plan(TeamBattleState state, BattleEnemySetup enemy)
        {
            var stage = enemy.Behavior.Stages.FirstOrDefault(s => s.Condition.Matches(state, enemy.Id));
            int cursor = stage?.Id == StageId ? SequenceCursor : 0;
            EnemyBehaviorAction selected = null;
            if (stage != null)
            {
                var candidates = stage.Actions.Where(a => {
                    string key = stage.Id + "/" + a.Id;
                    return a.Condition.Matches(state, enemy.Id) && (a.MaxUses == 0 || !Uses.ContainsKey(key) || Uses[key] < a.MaxUses) &&
                        (!LastUsedRound.ContainsKey(key) || state.CompletedRounds - LastUsedRound[key] > a.CooldownRounds);
                }).ToArray();
                if (stage.Mode == EnemySelectionMode.Sequence)
                {
                    for (int i = 0; i < stage.Actions.Count; i++)
                    {
                        int next = (cursor + i) % stage.Actions.Count;
                        if (candidates.Contains(stage.Actions[next])) { selected = stage.Actions[next]; cursor = next; break; }
                    }
                }
                else if (stage.Mode == EnemySelectionMode.Priority) selected = candidates.FirstOrDefault();
                else if (candidates.Length > 0)
                {
                    // Stable across processes, independent from deck shuffling and preview call count.
                    uint hash = unchecked((uint)enemy.Behavior.Seed);
                    foreach (char c in state.Combat.Id + "/" + enemy.Id.Value + "/" + state.CompletedRounds) hash = unchecked((hash ^ c) * 16777619);
                    int draw = (int)(hash % (uint)candidates.Sum(a => a.Weight));
                    foreach (var candidate in candidates) { draw -= candidate.Weight; if (draw < 0) { selected = candidate; break; } }
                }
            }
            return new EnemyBehaviorRuntime(stage?.Id, selected, cursor, Uses.ToDictionary(p => p.Key, p => p.Value), LastUsedRound.ToDictionary(p => p.Key, p => p.Value),
                selected?.Effects?.Steps.Select(step => BattleTargetResolver.Resolve(state, enemy.Id, step.Target)));
        }
    }
}
