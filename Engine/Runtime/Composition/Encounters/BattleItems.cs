using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace PokerEngine
{
    public enum BattleItemActivation { None, Effects, ReplaceHeld, Roulette }

    /// <summary>Declarative item behavior; definitions never depend on a designer's item ID.</summary>
    public sealed class BattleItemSpec
    {
        public BattleItemActivation Activation { get; }
        public IReadOnlyList<RelicTriggeredEffectSpec> Effects { get; }
        public bool RemoveOnDepleted { get; }
        public int CooldownBattles { get; }
        public decimal ReviveRatio { get; }
        public decimal ExecuteChance { get; }
        public decimal ReflectRatio { get; }
        public long LocationEnterHeal { get; }
        public decimal LocationLeaveGoldRatio { get; }
        public decimal RouletteBase { get; }
        public int RouletteTurns { get; }
        public long ExplosionDamage { get; }
        public BattleItemSpec(BattleItemActivation activation = BattleItemActivation.None,
            IEnumerable<RelicTriggeredEffectSpec> effects = null, bool removeOnDepleted = false, int cooldownBattles = 0,
            decimal reviveRatio = 0, decimal executeChance = 0, decimal reflectRatio = 0, long locationEnterHeal = 0,
            decimal locationLeaveGoldRatio = 0, decimal rouletteBase = 10, int rouletteTurns = 6, long explosionDamage = int.MaxValue)
        {
            var steps = (effects ?? Array.Empty<RelicTriggeredEffectSpec>()).ToArray();
            if (!Enum.IsDefined(typeof(BattleItemActivation), activation) || cooldownBattles < 0 || reviveRatio < 0 || reviveRatio > 1 ||
                executeChance < 0 || executeChance > 1 || reflectRatio < 0 || locationEnterHeal < 0 || locationLeaveGoldRatio < 0 ||
                rouletteBase < 1 || rouletteBase > 1000 || rouletteTurns < 1 || rouletteTurns > 6 || explosionDamage < 0 ||
                steps.Length > 64 || (activation == BattleItemActivation.Effects) != (steps.Length > 0) ||
                steps.Any(s => s == null || s.Target.Group != CombatTargetGroup.Self && s.Target.Group != CombatTargetGroup.Opponents && s.Target.Group != CombatTargetGroup.Allies ||
                    s.Effect.Kind != CombatEffectKind.Heal && s.Effect.Kind != CombatEffectKind.Damage && s.Effect.Kind != CombatEffectKind.GainBlock))
                throw new ArgumentException("Invalid configured battle item.");
            Activation = activation; Effects = Guard.View(steps); RemoveOnDepleted = removeOnDepleted; CooldownBattles = cooldownBattles;
            ReviveRatio = reviveRatio; ExecuteChance = executeChance; ReflectRatio = reflectRatio; LocationEnterHeal = locationEnterHeal;
            LocationLeaveGoldRatio = locationLeaveGoldRatio; RouletteBase = rouletteBase; RouletteTurns = rouletteTurns; ExplosionDamage = explosionDamage;
        }
        internal void Write(BinaryWriter w)
        {
            w.Write((int)Activation); w.Write(RemoveOnDepleted); w.Write(CooldownBattles); w.Write(ReviveRatio); w.Write(ExecuteChance);
            w.Write(ReflectRatio); w.Write(LocationEnterHeal); w.Write(LocationLeaveGoldRatio); w.Write(RouletteBase); w.Write(RouletteTurns);
            w.Write(ExplosionDamage); w.Write(Effects.Count); foreach (var e in Effects) e.Write(w);
        }
    }

    /// <summary>Transaction-local random cursor. Rejected commands discard this cursor with their draft state.</summary>
    internal sealed class BattleItemResolution
    {
        internal RandomState Random;
        internal bool AbortEnemyPhase;
        internal readonly HashSet<ParticipantId> ClearedRouletteOwners = new HashSet<ParticipantId>();
        internal BattleItemResolution(RandomState random, TeamBattleState state)
        {
            if (random == null) using (var hash = SHA256.Create()) random = HmacDeckShuffler.FromSeed(hash.ComputeHash(Encoding.UTF8.GetBytes(state.Combat.Id + "/" + state.Revision)));
            Random = random;
        }
        internal int Roll(int count)
        {
            if (count < 1 || Random.Algorithm != HmacDeckShuffler.Algorithm || Random.StateBytes.Count != 40) throw new ArgumentException("Invalid item random state.");
            var key = Random.StateBytes.Take(32).ToArray(); ulong counter = 0;
            for (int i = 0; i < 8; i++) counter |= (ulong)Random.StateBytes[32 + i] << (8 * i);
            uint bound = (uint)count, threshold = unchecked(0u - bound) % bound, value;
            using (var hmac = new HMACSHA256(key)) do {
                var bytes = new byte[8]; for (int i = 0; i < 8; i++) bytes[i] = (byte)(counter >> (8 * i));
                counter = checked(counter + 1); var data = hmac.ComputeHash(bytes);
                value = (uint)data[0] | (uint)data[1] << 8 | (uint)data[2] << 16 | (uint)data[3] << 24;
            } while (value < threshold);
            var tail = new byte[8]; for (int i = 0; i < 8; i++) tail[i] = (byte)(counter >> (8 * i));
            Random = new RandomState(HmacDeckShuffler.Algorithm, 1, key.Concat(tail)); return (int)(value % bound);
        }
    }

    public static class BattleItemRuntime
    {
        public const string CooldownBattlesKey = "item/cooldown-battles";
        public const string RouletteActiveKey = "item/roulette/active";
        public const string RouletteTurnKey = "item/roulette/turn";
        public const string RouletteBombKey = "item/roulette/bomb";
        internal const string RouletteRoundKey = "item/roulette/round";
        public static decimal HandMultiplier(BattleRelicLoadout loadout)
        {
            decimal value = 1;
            foreach (var r in loadout.Relics.Where(r => r.Memory.CounterValue(RouletteActiveKey) != 0)) {
                var spec = loadout.RulesFor(r.InstanceId).Item; if (spec == null) continue;
                for (long i = 0; i < r.Memory.CounterValue(RouletteTurnKey); i++) value = checked(value * spec.RouletteBase);
            }
            return value;
        }
        public static bool CanUse(CardDuelState state, ParticipantId actor, string instanceId)
        {
            if (state == null || !state.HasStarted || state.Duel.IsFinished || !state.Duel.CanAct(actor)) return false;
            var player = state.Duel.Player(actor);
            if (player.Rules.Flow.UsesTurnFlow
                ? state.FlowFor(actor).Window != BattlePlayerWindow.Action || state.FlowFor(actor).ActionsRemaining <= 0
                : player.Energy.Energy <= 0) return false;
            var relic = player.Relics.Relics.FirstOrDefault(r => r.InstanceId == instanceId); if (relic == null) return false;
            var spec = player.Relics.RulesFor(instanceId).Item;
            return spec != null && spec.Activation != BattleItemActivation.None && relic.RemainingCharges != 0 &&
                relic.Memory.CounterValue(CooldownBattlesKey) == 0 && relic.Memory.CounterValue(RouletteActiveKey) == 0;
        }
        public static RunRelic CompleteBattle(RunRelic relic)
        {
            var memory = ClearRoulette(relic.Memory).ResetRound().WithCounter(CooldownBattlesKey, Math.Max(0, relic.Memory.CounterValue(CooldownBattlesKey) - 1), MemoryScope.Encounter);
            return new RunRelic(relic.Id, relic.DefinitionId, relic.Level, relic.Growth, relic.Charges, relic.CooldownRounds, memory);
        }
        internal static AbilityMemory ClearRoulette(AbilityMemory memory) => new AbilityMemory(memory.Counters.Where(c => !c.Key.StartsWith("item/roulette/", StringComparison.Ordinal)), memory.Marks);
        internal static BattleRelicLoadout EndRoulette(BattleRelicLoadout loadout) => loadout.WithRuntime(loadout.Relics.Select(r => r.WithRuntime(r.RemainingCharges, r.UsesThisRound, r.CooldownRounds, ClearRoulette(r.Memory))));
        internal static BattleRelicLoadout Pay(BattleRelicLoadout loadout, string id, BattleItemSpec spec)
        {
            var current = loadout.Relics.Single(r => r.InstanceId == id); int? remaining = current.RemainingCharges.HasValue ? current.RemainingCharges.Value - 1 : (int?)null;
            if (remaining < 0) throw new ArgumentException("Item charges exhausted.");
            if (remaining == 0 && spec.RemoveOnDepleted) return loadout.RemoveItem(id);
            return loadout.WithRuntime(loadout.Relics.Select(r => r.InstanceId != id ? r : r.WithRuntime(remaining, r.UsesThisRound, r.CooldownRounds,
                r.Memory.WithCounter(CooldownBattlesKey, spec.CooldownBattles, MemoryScope.Encounter))));
        }
        internal static IEnumerable<CombatReactionBatch> React(CombatReactionContext context, TeamBattleState actionStart,
            ParticipantId actor, ScoredDuelActionKind action, HandAttackInfo hand, Dictionary<ParticipantId, BattleRelicLoadout> loadouts, BattleItemResolution resolution)
        {
            var fact = context.Event as CombatEvent; if (fact == null) yield break;
            string activation = "item/" + fact.Revision + "/" + fact.SequenceIndex + "/";
            if (fact.Died && loadouts.ContainsKey(fact.Effect.Target)) {
                var owner = fact.Effect.Target; resolution.ClearedRouletteOwners.Add(owner); var loadout = EndRoulette(loadouts[owner]);
                var item = loadout.Relics.FirstOrDefault(r => r.RemainingCharges != 0 && (loadout.RulesFor(r.InstanceId).Item?.ReviveRatio ?? 0) > 0);
                loadouts[owner] = loadout;
                if (item != null) {
                    var spec = loadout.RulesFor(item.InstanceId).Item; long hp = checked((long)decimal.Floor(context.State.Get(owner).MaxHealth * spec.ReviveRatio));
                    loadouts[owner] = Pay(loadout, item.InstanceId, spec);
                    if (actionStart.Phase == ScoredDuelPhase.AwaitingEnemyAction) resolution.AbortEnemyPhase = true;
                    yield return new CombatReactionBatch(activation + item.InstanceId, new[] { new CombatReaction(new CombatEffect(CombatEffectKind.Revive, owner, owner, hp),
                        "item/revive", owner, CombatEffectOrigin.Triggered, true, CombatReactionSourcePolicy.AllowDefeated, item.InstanceId) });
                }
            }
            if (fact.Effect.Kind != CombatEffectKind.Damage) yield break;
            if (fact.BlockAbsorbed > 0 && fact.Provenance.Origin == CombatEffectOrigin.Primary && loadouts.ContainsKey(fact.Effect.Target) && context.State.Get(fact.Effect.Target).IsAlive) {
                var owner = fact.Effect.Target; var loadout = loadouts[owner];
                foreach (var item in loadout.Relics.Where(r => r.RemainingCharges != 0)) {
                    var ratio = loadout.RulesFor(item.InstanceId).Item?.ReflectRatio ?? 0; long amount = checked((long)decimal.Floor(fact.BlockAbsorbed * ratio));
                    if (amount > 0 && context.State.Get(fact.Effect.Source).IsAlive) yield return new CombatReactionBatch(activation + item.InstanceId + "/reflect", new[] {
                        new CombatReaction(new CombatEffect(CombatEffectKind.Damage, owner, fact.Effect.Source, amount), "item/reflect", owner, CombatEffectOrigin.Reflection, false, sourceInstanceId:item.InstanceId) });
                }
            }
            if (hand == null || action != ScoredDuelActionKind.ScoredHand || fact.Effect.Source != actor || fact.Provenance.Origin != CombatEffectOrigin.Primary ||
                fact.Effect.Attack?.Origin != AttackOrigin.ScoredHand || fact.DamageToHealth <= 0 || !context.State.Get(fact.Effect.Target).IsAlive || !loadouts.ContainsKey(actor)) yield break;
            var attackLoadout = loadouts[actor];
            foreach (var item in attackLoadout.Relics.Where(r => r.RemainingCharges != 0)) {
                decimal chance = attackLoadout.RulesFor(item.InstanceId).Item?.ExecuteChance ?? 0;
                if (chance <= 0 || resolution.Roll(1000000) >= chance * 1000000) continue;
                yield return new CombatReactionBatch(activation + item.InstanceId + "/execute", new[] { new CombatReaction(CombatEffect.Eliminate(actor, fact.Effect.Target),
                    "item/execute", actor, CombatEffectOrigin.Triggered, true, sourceInstanceId:item.InstanceId) });
                break;
            }
        }
    }
}
