using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    /// <summary>Immutable proposal; null SourceAbilityId denotes a core rule operation.</summary>
    public abstract class EffectIntent
    {
        public AbilityInstanceId? SourceAbilityId { get; }
        protected EffectIntent(AbilityInstanceId? source)
        { if (source.HasValue && !source.Value.IsValid) throw new ArgumentException("Invalid effect source."); SourceAbilityId = source; }
    }

    public sealed class MoveCardEffect : EffectIntent
    {
        public CardInstanceId CardId { get; }
        public ZoneId From { get; }
        public ZoneId To { get; }
        public MoveCardEffect(AbilityInstanceId? source, CardInstanceId card, ZoneId from, ZoneId to) : base(source)
        {
            if (!card.IsValid || !from.IsValid || !to.IsValid || from == to) throw new ArgumentException("Invalid card move.");
            CardId = card; From = from; To = to;
        }
    }

    public sealed class DrawCardsEffect : EffectIntent
    {
        public ZoneId From { get; }
        public ZoneId To { get; }
        public int Count { get; }
        public DrawCardsEffect(AbilityInstanceId? source, ZoneId from, ZoneId to, int count) : base(source)
        {
            if (!from.IsValid || !to.IsValid || from == to || count <= 0) throw new ArgumentException("Invalid draw.");
            From = from; To = to; Count = count;
        }
    }

    public sealed class ReorderCardsEffect : EffectIntent
    {
        public ZoneId ZoneId { get; }
        public IReadOnlyList<CardInstanceId> OrderedCards { get; }
        public ReorderCardsEffect(AbilityInstanceId? source, ZoneId zone, IEnumerable<CardInstanceId> cards) : base(source)
        {
            var copy = Guard.Copy(cards, nameof(cards));
            if (!zone.IsValid || copy.Length == 0 || copy.Any(c => !c.IsValid) || copy.Distinct().Count() != copy.Length) throw new ArgumentException("Invalid ordering.");
            ZoneId = zone; OrderedCards = Guard.View(copy);
        }
    }

    public enum ResourceKind { Chips = 1, Energy, Health }
    public sealed class ChangeResourceEffect : EffectIntent
    {
        public ParticipantId Target { get; }
        public ResourceKind Resource { get; }
        public long Delta { get; }
        public ChangeResourceEffect(AbilityInstanceId? source, ParticipantId target, ResourceKind resource, long delta) : base(source)
        {
            if (!target.IsValid || !Enum.IsDefined(typeof(ResourceKind), resource) || delta == 0) throw new ArgumentException("Invalid resource effect.");
            Target = target; Resource = resource; Delta = delta;
        }
    }

    public sealed class ApplyStatusEffect : EffectIntent
    {
        public StatusInstance Status { get; }
        public ApplyStatusEffect(AbilityInstanceId? source, StatusInstance status) : base(source)
        { Status = status ?? throw new ArgumentNullException(nameof(status)); }
    }

    public sealed class GrantKnowledgeEffect : EffectIntent
    {
        public KnowledgeGrant Grant { get; }
        public GrantKnowledgeEffect(AbilityInstanceId? source, KnowledgeGrant grant) : base(source)
        { Grant = grant ?? throw new ArgumentNullException(nameof(grant)); }
    }

    public sealed class RequestSelectionEffect : EffectIntent
    {
        public ParticipantId Chooser { get; }
        public SelectionSpec Selection { get; }
        public int NextStep { get; }
        public RequestSelectionEffect(AbilityInstanceId? source, ParticipantId chooser, SelectionSpec selection, int nextStep = 1) : base(source)
        {
            if (!chooser.IsValid || nextStep < 1) throw new ArgumentException("Invalid chooser or continuation step.");
            Chooser = chooser; Selection = selection ?? throw new ArgumentNullException(nameof(selection));
            NextStep = nextStep;
        }
    }

    public sealed class SetAbilityCounterEffect : EffectIntent
    {
        public string Key { get; }
        public long Value { get; }
        public MemoryScope Scope { get; }
        public SetAbilityCounterEffect(AbilityInstanceId source, string key, long value, MemoryScope scope) : base(source)
        { var counter = new AbilityCounter(key, value, scope); Key = counter.Key; Value = counter.Value; Scope = counter.Scope; }
    }

    public sealed class ConsumeStatusEffect : EffectIntent
    {
        public StatusInstanceId StatusId { get; }
        public ConsumeStatusEffect(AbilityInstanceId? source, StatusInstanceId status) : base(source)
        { if (!status.IsValid) throw new ArgumentException("Invalid status."); StatusId = status; }
    }
}
