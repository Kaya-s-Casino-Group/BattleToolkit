using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PokerEngine
{
    public enum BattleRuleValueKey { MaxEnergy = 1, InitialEnergy, PlayEnergyCost, DiscardEnergyCost, HandLimit }
    public enum BattleRefillMode { Skip = 1, FillToLimit, FixedCount }

    /// <summary>Passive integer adjustment, independent of effect amounts and relic runtime counters.</summary>
    public sealed class RelicRuleValueSpec
    {
        public string Id { get; }
        public BattleRuleValueKey Key { get; }
        public int Delta { get; }
        public RelicRuleValueSpec(string id, BattleRuleValueKey key, int delta)
        {
            Id = Guard.Text(id, nameof(id));
            if (!Enum.IsDefined(typeof(BattleRuleValueKey), key)) throw new ArgumentException("Unknown battle rule value.");
            Key = key; Delta = delta;
        }
        internal void Write(BinaryWriter writer) { writer.Write(Id); writer.Write((int)Key); writer.Write(Delta); }
    }

    /// <summary>Optional exclusive mode plus additive count for one explicit deal boundary. Null mode inherits the base policy.</summary>
    public sealed class RelicRefillRuleSpec
    {
        public string Id { get; }
        public CardDuelDealTrigger Trigger { get; }
        public BattleRefillMode? Mode { get; }
        public int? FixedCount { get; }
        public int CountDelta { get; }
        public RelicRefillRuleSpec(string id, CardDuelDealTrigger trigger, BattleRefillMode? mode = null, int? fixedCount = null, int countDelta = 0)
        {
            Id = Guard.Text(id, nameof(id));
            if (!Enum.IsDefined(typeof(CardDuelDealTrigger), trigger) || mode.HasValue && !Enum.IsDefined(typeof(BattleRefillMode), mode.Value) ||
                (mode == BattleRefillMode.FixedCount) != fixedCount.HasValue || fixedCount < 0)
                throw new ArgumentException("Invalid relic refill rule.");
            Trigger = trigger; Mode = mode; FixedCount = fixedCount; CountDelta = countDelta;
        }
        internal void Write(BinaryWriter writer)
        {
            writer.Write(Id); writer.Write((int)Trigger); writer.Write(Mode.HasValue); if (Mode.HasValue) writer.Write((int)Mode.Value);
            writer.Write(FixedCount.HasValue); if (FixedCount.HasValue) writer.Write(FixedCount.Value); writer.Write(CountDelta);
        }
    }

    public sealed class BattleRuleContribution
    {
        public string SourceInstanceId { get; }
        public string RuleId { get; }
        public int Delta { get; }
        internal BattleRuleContribution(string source, string rule, int delta) { SourceInstanceId = source; RuleId = rule; Delta = delta; }
    }
    public sealed class BattleRuleValueResolution
    {
        public BattleRuleValueKey Key { get; }
        public int BaseValue { get; }
        public long Delta { get; }
        public long UnboundedValue => BaseValue + Delta;
        public int Value { get; }
        public IReadOnlyList<BattleRuleContribution> Contributions { get; }
        internal BattleRuleValueResolution(BattleRuleValueKey key, int baseValue, IEnumerable<BattleRuleContribution> contributions)
        {
            var items = contributions.OrderBy(item => item.SourceInstanceId, StringComparer.Ordinal).ThenBy(item => item.RuleId, StringComparer.Ordinal).ToArray();
            long delta = 0; foreach (var item in items) delta = checked(delta + item.Delta);
            long value = checked(baseValue + delta);
            if (key == BattleRuleValueKey.PlayEnergyCost || key == BattleRuleValueKey.DiscardEnergyCost) value = Math.Max(0, value);
            if (value < (key == BattleRuleValueKey.InitialEnergy || key == BattleRuleValueKey.PlayEnergyCost || key == BattleRuleValueKey.DiscardEnergyCost ? 0 : 1) ||
                key == BattleRuleValueKey.HandLimit && value > 52) throw new ArgumentException("Modified battle rule is outside its supported range.");
            Value = checked((int)value); Key = key; BaseValue = baseValue; Delta = delta; Contributions = Guard.View(items);
        }
    }

    public sealed class BattleLimitQuery
    {
        public BattlePlayerRules Rules { get; }
        public BattleRelicLoadout Loadout { get; }
        public BattleLimitQuery(BattlePlayerRules rules, BattleRelicLoadout loadout)
        { Rules = rules ?? throw new ArgumentNullException(nameof(rules)); Loadout = loadout ?? throw new ArgumentNullException(nameof(loadout)); }
    }
    public sealed class BattleLimitResolution
    {
        public ParticipantId OwnerId { get; }
        public BattleRuleValueResolution MaxEnergy { get; }
        public BattleRuleValueResolution InitialEnergy { get; }
        public BattleRuleValueResolution HandLimit { get; }
        internal BattleLimitResolution(ParticipantId owner, BattleRuleValueResolution maxEnergy, BattleRuleValueResolution initialEnergy, BattleRuleValueResolution handLimit)
        { OwnerId = owner; MaxEnergy = maxEnergy; InitialEnergy = initialEnergy; HandLimit = handLimit; }
    }

    public sealed class BattleActionCostQuery
    {
        public TeamBattleState State { get; }
        public ParticipantId Actor { get; }
        public ScoredDuelActionKind Action { get; }
        public BattleActionCostQuery(TeamBattleState state, ParticipantId actor, ScoredDuelActionKind action)
        {
            State = state ?? throw new ArgumentNullException(nameof(state));
            if (!state.Players.Any(player => player.Id == actor) || action != ScoredDuelActionKind.ScoredHand && action != ScoredDuelActionKind.Discard)
                throw new ArgumentException("Cost queries require a battle player and a play/discard action.");
            Actor = actor; Action = action;
        }
    }
    public sealed class BattleActionCostResolution
    {
        public ParticipantId Actor { get; }
        public long Revision { get; }
        public ScoredDuelActionKind Action { get; }
        public BattleRuleValueResolution Amount { get; }
        public int Cost => Amount.Value;
        public int AvailableEnergy { get; }
        public bool CanAfford => AvailableEnergy >= Cost;
        internal BattleActionCostResolution(BattleActionCostQuery query, BattleRuleValueResolution amount)
        { Actor = query.Actor; Revision = query.State.Revision; Action = query.Action; Amount = amount; AvailableEnergy = query.State.Player(query.Actor).Energy.Energy; }
    }

    public sealed class BattleDealQuery
    {
        public CardDuelDealContext Context { get; }
        public ICardDuelDealPolicy BasePolicy { get; }
        public BattleRelicLoadout Loadout { get; }
        public BattleDealQuery(CardDuelDealContext context, ICardDuelDealPolicy basePolicy, BattleRelicLoadout loadout)
        {
            Context = context ?? throw new ArgumentNullException(nameof(context));
            BasePolicy = basePolicy ?? throw new ArgumentNullException(nameof(basePolicy)); Loadout = loadout ?? throw new ArgumentNullException(nameof(loadout));
            if (context.PlayerId != loadout.OwnerId) throw new ArgumentException("Refill rules must belong to the recipient.");
        }
    }
    public sealed class BattleRefillContribution
    {
        public string SourceInstanceId { get; }
        public string RuleId => Rule.Id;
        public RelicRefillRuleSpec Rule { get; }
        internal BattleRefillContribution(string source, RelicRefillRuleSpec rule) { SourceInstanceId = source; Rule = rule; }
    }
    public sealed class BattleDealResolution
    {
        public ParticipantId PlayerId { get; }
        public CardDuelDealTrigger Trigger { get; }
        public long TableRevision { get; }
        public int HandLimit { get; }
        public int HandCount { get; }
        public int MissingCards => Math.Max(0, HandLimit - HandCount);
        public int BaseCount { get; }
        public BattleRefillMode? Mode { get; }
        public int SelectedCount { get; }
        public long CountDelta { get; }
        public int Count { get; }
        public IReadOnlyList<BattleRefillContribution> Contributions { get; }
        internal BattleDealResolution(CardDuelDealContext context, int baseCount, BattleRefillMode? mode, int selectedCount,
            long delta, int count, BattleRefillContribution[] contributions)
        {
            PlayerId = context.PlayerId; Trigger = context.Trigger; TableRevision = context.Table.Revision;
            HandLimit = context.HandLimit; HandCount = context.HandCount; BaseCount = baseCount; Mode = mode;
            SelectedCount = selectedCount; CountDelta = delta; Count = count; Contributions = Guard.View(contributions);
        }
    }
    public sealed class BattleDrawRecord
    {
        public BattleDealResolution Plan { get; }
        public int DrawnCount { get; }
        internal BattleDrawRecord(BattleDealResolution plan, int drawnCount) { Plan = plan; DrawnCount = drawnCount; }
    }

    public interface IBattleRuleResolver
    {
        BattleLimitResolution ResolveLimits(BattleLimitQuery query);
        BattleActionCostResolution ResolveActionCost(BattleActionCostQuery query);
        BattleDealResolution ResolveDeal(BattleDealQuery query);
    }
    /// <summary>Pure passive rules. No payment, card movement, random draws, runtime charges or action history writes.</summary>
    public sealed class BattleRuleResolver : IBattleRuleResolver
    {
        public BattleLimitResolution ResolveLimits(BattleLimitQuery query)
        {
            if (query == null) throw new ArgumentNullException(nameof(query));
            return new BattleLimitResolution(query.Loadout.OwnerId, Value(query.Rules, query.Loadout, BattleRuleValueKey.MaxEnergy),
                Value(query.Rules, query.Loadout, BattleRuleValueKey.InitialEnergy), Value(query.Rules, query.Loadout, BattleRuleValueKey.HandLimit));
        }
        public BattleActionCostResolution ResolveActionCost(BattleActionCostQuery query)
        {
            if (query == null) throw new ArgumentNullException(nameof(query));
            var player = query.State.Player(query.Actor);
            return new BattleActionCostResolution(query, Value(player.Rules, player.Relics,
                query.Action == ScoredDuelActionKind.ScoredHand ? BattleRuleValueKey.PlayEnergyCost : BattleRuleValueKey.DiscardEnergyCost));
        }
        public BattleDealResolution ResolveDeal(BattleDealQuery query)
        {
            if (query == null) throw new ArgumentNullException(nameof(query));
            var context = query.Context;
            int baseCount = query.BasePolicy.GetDrawCount(context);
            if (baseCount < 0 || baseCount > context.MissingCards) throw new ArgumentException("Base draw policy must respect the hand limit.");
            var contributions = query.Loadout.Relics.SelectMany(relic => query.Loadout.RulesFor(relic.InstanceId).RefillRules
                .Where(spec => spec.Trigger == context.Trigger).Select(spec => new BattleRefillContribution(relic.InstanceId, spec)))
                .OrderBy(item => item.SourceInstanceId, StringComparer.Ordinal).ThenBy(item => item.RuleId, StringComparer.Ordinal).ToArray();
            var replacement = contributions.FirstOrDefault(item => item.Rule.Mode.HasValue)?.Rule;
            var mode = replacement?.Mode;
            int selected = mode == BattleRefillMode.Skip ? 0 : mode == BattleRefillMode.FillToLimit ? context.MissingCards :
                mode == BattleRefillMode.FixedCount ? replacement.FixedCount.Value : baseCount;
            long delta = 0; foreach (var item in contributions) delta = checked(delta + item.Rule.CountDelta);
            int count = mode == BattleRefillMode.Skip ? 0 : (int)Math.Min(context.MissingCards, Math.Max(0L, checked(selected + delta)));
            return new BattleDealResolution(context, baseCount, mode, selected, delta, count, contributions);
        }
        internal static BattleRuleValueResolution Value(BattlePlayerRules rules, BattleRelicLoadout loadout, BattleRuleValueKey key)
        {
            int value;
            switch (key)
            {
                case BattleRuleValueKey.MaxEnergy: value = rules.MaxEnergy; break;
                case BattleRuleValueKey.InitialEnergy: value = rules.InitialEnergy; break;
                case BattleRuleValueKey.PlayEnergyCost: value = rules.PlayEnergyCost; break;
                case BattleRuleValueKey.DiscardEnergyCost: value = rules.DiscardEnergyCost; break;
                case BattleRuleValueKey.HandLimit: value = rules.HandSize; break;
                default: throw new ArgumentException("Unknown battle rule value.");
            }
            return new BattleRuleValueResolution(key, value, loadout.Relics.SelectMany(relic => loadout.RulesFor(relic.InstanceId).RuleValues
                .Where(spec => spec.Key == key).Select(spec => new BattleRuleContribution(relic.InstanceId, spec.Id, spec.Delta))));
        }
    }
    internal static class RelicRuleValidation
    {
        internal static void Refills(IEnumerable<RelicRefillRuleSpec> specs)
        {
            foreach (var group in specs.Where(spec => spec.Mode.HasValue).GroupBy(spec => spec.Trigger))
            {
                var first = group.First();
                if (group.Any(spec => spec.Mode != first.Mode || spec.FixedCount != first.FixedCount))
                    throw new ArgumentException("Conflicting refill modes for the same deal trigger.");
            }
        }
    }
}
