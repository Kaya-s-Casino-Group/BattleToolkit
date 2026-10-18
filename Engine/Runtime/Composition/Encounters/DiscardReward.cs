using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    /// <summary>Pure discard-only rule. No scoring, targeting, card movement, costs, randomness or state writes.</summary>
    public interface IDiscardRewardRule
    {
        string Version { get; }
        DiscardRewardPlan Resolve(DiscardRewardContext context);
    }
    public sealed class DiscardCardSnapshot
    {
        public CardInstance Card { get; }
        public EffectiveCard Face { get; }
        public CardInstanceId Id => Card.Id;
        public DiscardCardSnapshot(CardInstance card, EffectiveCard face)
        {
            if (card == null || face == null || card.Id != face.Id) throw new ArgumentException("Discard snapshot requires matching card identities.");
            Card = card; Face = face;
        }
    }
    public sealed class DiscardRewardContext
    {
        public TeamBattleState State { get; }
        public CombatState Combat => State.Combat;
        public ParticipantId Source { get; }
        public IReadOnlyList<DiscardCardSnapshot> Cards { get; }
        public IReadOnlyList<ParticipantId> Targets { get; }
        public DiscardRewardContext(TeamBattleState state, ParticipantId source, IEnumerable<DiscardCardSnapshot> cards, IEnumerable<ParticipantId> targets)
        {
            State = state ?? throw new ArgumentNullException(nameof(state)); Source = source;
            var copy = Guard.Copy(cards, nameof(cards)); var allies = Guard.Copy(targets, nameof(targets));
            if (!state.CanAct(source) || copy.Length < 1 || copy.Length > state.Player(source).Rules.MaxDiscardCards ||
                copy.Any(card => card == null || !card.Card.CanDiscard) || copy.Select(card => card.Id).Distinct().Count() != copy.Length ||
                allies.Length < 1 || allies.Length > 64 || allies.Distinct().Count() != allies.Length ||
                allies.Any(id => !Combat.Get(id).IsAlive || Combat.Get(id).TeamId != Combat.Get(source).TeamId))
                throw new ArgumentException("Discard rewards require legal cards and living allies.");
            Cards = Guard.View(copy); Targets = Guard.View(allies);
        }
    }
    /// <summary>Support effects only. No reward is an empty plan, not a synthetic zero-value combat event.</summary>
    public sealed class DiscardRewardPlan
    {
        public string RuleVersion { get; }
        public string Explanation { get; }
        public IReadOnlyList<CombatEffect> Effects { get; }
        public DiscardRewardPlan(string ruleVersion, string explanation, IEnumerable<CombatEffect> effects = null)
        {
            RuleVersion = Guard.Text(ruleVersion, nameof(ruleVersion)); Explanation = Guard.Text(explanation, nameof(explanation));
            var copy = Guard.Copy(effects ?? Array.Empty<CombatEffect>(), nameof(effects));
            if (copy.Length > 64 || copy.Any(effect => effect == null)) throw new ArgumentException("Invalid discard reward effects.");
            Effects = Guard.View(copy);
        }
        internal void Validate(DiscardRewardContext context, string version)
        {
            if (RuleVersion != version || Effects.Any(effect => effect.Source != context.Source || !context.Targets.Contains(effect.Target) ||
                (effect.Kind != CombatEffectKind.GainBlock && effect.Kind != CombatEffectKind.Heal)))
                throw new ArgumentException("Discard rewards must preserve the source/version and support only selected living allies.");
        }
    }
    public sealed class DiscardRewardResolution
    {
        public DiscardRewardContext Context { get; }
        public DiscardRewardPlan Plan { get; }
        internal DiscardRewardResolution(DiscardRewardContext context, DiscardRewardPlan plan) { Context = context; Plan = plan; }
    }
    public sealed class NoDiscardRewardRule : IDiscardRewardRule
    {
        public string Version { get; }
        public NoDiscardRewardRule(string version = "discard.none.1") => Version = Guard.Text(version, nameof(version));
        public DiscardRewardPlan Resolve(DiscardRewardContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            return new DiscardRewardPlan(Version, context.State.Player(context.Source).Rules.UnlimitedDiscards
                ? "无额外收益；按战局规则支付弃牌费用并过牌，不限制次数。"
                : "无额外收益；仅按战局规则支付弃牌费用、消耗次数并过牌。");
        }
    }
}
