using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace PokerEngine
{
    public enum DecisionKind { ModeAction = 1, SelectCards, SelectOption, SelectTargets }

    /// <summary>Serializable step passed to the registered selection continuation.</summary>
    [DataContract]
    public sealed class EffectContinuation
    {
        [DataMember(Order = 1, IsRequired = true)] public AbilityInstanceId AbilityId { get; private set; }
        [DataMember(Order = 2, IsRequired = true)] public int NextEffectIndex { get; private set; }
        [DataMember(Name = "Targets", Order = 3, IsRequired = true)] private EntityRef[] targets;
        public IReadOnlyList<EntityRef> Targets => Guard.View(targets);
        public EffectContinuation(AbilityInstanceId abilityId, int nextEffectIndex, IEnumerable<EntityRef> targets)
        {
            if (!abilityId.IsValid || nextEffectIndex < 0) throw new ArgumentException("Invalid effect continuation.");
            AbilityId = abilityId; NextEffectIndex = nextEffectIndex; this.targets = Guard.Copy(targets, nameof(targets));
        }
    }

    /// <summary>Exactly one active window; only fields for its kind may be populated.</summary>
    [DataContract]
    public sealed class DecisionState
    {
        [DataMember(Order = 1, IsRequired = true)] public DecisionId Id { get; private set; }
        [DataMember(Order = 2, IsRequired = true)] public ParticipantId ActorId { get; private set; }
        [DataMember(Order = 3, IsRequired = true)] public DecisionKind Kind { get; private set; }
        [DataMember(Name = "CardCandidates", Order = 4, IsRequired = true)] private CardInstanceId[] cardCandidates;
        [DataMember(Name = "OptionIds", Order = 5, IsRequired = true)] private string[] optionIds;
        [DataMember(Order = 6, IsRequired = true)] public int MinSelections { get; private set; }
        [DataMember(Order = 7, IsRequired = true)] public int MaxSelections { get; private set; }
        [DataMember(Order = 8)] public EffectContinuation Continuation { get; private set; }
        [DataMember(Order = 9)] public SelectionSpec Selection { get; private set; }
        public IReadOnlyList<CardInstanceId> CardCandidates => Guard.View(cardCandidates);
        public IReadOnlyList<string> OptionIds => Guard.View(optionIds);
        private DecisionState(DecisionId id, ParticipantId actor, DecisionKind kind,
            IEnumerable<CardInstanceId> cards, IEnumerable<string> options, int min, int max, EffectContinuation continuation)
        {
            if (!id.IsValid || !actor.IsValid || min < 0 || max < min) throw new ArgumentException("Invalid decision.");
            Id = id; ActorId = actor; Kind = kind; cardCandidates = Guard.Copy(cards, nameof(cards));
            optionIds = Guard.Copy(options, nameof(options)); MinSelections = min; MaxSelections = max; Continuation = continuation;
        }
        public static DecisionState ModeAction(DecisionId id, ParticipantId actor) =>
            new DecisionState(id, actor, DecisionKind.ModeAction, new CardInstanceId[0], new string[0], 0, 0, null);
        public static DecisionState SelectCards(DecisionId id, ParticipantId actor, IEnumerable<CardInstanceId> candidates,
            int min, int max, EffectContinuation continuation) =>
            new DecisionState(id, actor, DecisionKind.SelectCards, candidates, new string[0], min, max,
                continuation ?? throw new ArgumentNullException(nameof(continuation)));
        public static DecisionState SelectOption(DecisionId id, ParticipantId actor, IEnumerable<string> options, EffectContinuation continuation) =>
            new DecisionState(id, actor, DecisionKind.SelectOption, new CardInstanceId[0], options, 1, 1,
                continuation ?? throw new ArgumentNullException(nameof(continuation)));
        public static DecisionState SelectTargets(DecisionId id, ParticipantId chooser, SelectionSpec selection, EffectContinuation continuation)
        {
            var decision = new DecisionState(id, chooser, DecisionKind.SelectTargets, new CardInstanceId[0], new string[0], 0, 0,
                continuation ?? throw new ArgumentNullException(nameof(continuation)));
            decision.Selection = selection ?? throw new ArgumentNullException(nameof(selection));
            return decision;
        }
    }

    [DataContract]
    public sealed class ResolutionState
    {
        [DataMember(Name = "PendingEffects", Order = 1, IsRequired = true)] private EffectContinuation[] pendingEffects;
        [DataMember(Order = 2)] public string WindowId { get; private set; }
        [DataMember(Name = "ProcessedTriggers", Order = 3, IsRequired = true)] private AbilityInstanceId[] processedTriggers;
        [DataMember(Order = 4)] public DecisionState SuspendedDecision { get; private set; }
        [DataMember(Order = 5, IsRequired = true)] public int StepsUsed { get; private set; }
        public IReadOnlyList<EffectContinuation> PendingEffects => Guard.View(pendingEffects);
        public IReadOnlyList<AbilityInstanceId> ProcessedTriggers => Guard.View(processedTriggers);
        public ResolutionState(IEnumerable<EffectContinuation> pendingEffects, string windowId = null,
            IEnumerable<AbilityInstanceId> processedTriggers = null, DecisionState suspendedDecision = null, int stepsUsed = 0)
        {
            this.pendingEffects = Guard.Copy(pendingEffects, nameof(pendingEffects)); WindowId = windowId;
            this.processedTriggers = Guard.Copy(processedTriggers ?? new AbilityInstanceId[0], nameof(processedTriggers));
            SuspendedDecision = suspendedDecision; StepsUsed = stepsUsed;
        }
        public static ResolutionState Empty => new ResolutionState(new EffectContinuation[0]);
    }
}
