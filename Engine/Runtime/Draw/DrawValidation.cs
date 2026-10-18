using System;
using System.Linq;

namespace PokerEngine
{
    public sealed partial class DrawEngine
    {
        public void Validate(DrawState state)
        {
            try { ValidateCore(state); }
            catch (NullReferenceException) { throw new ArgumentException("Draw state is missing required data."); }
            catch (InvalidOperationException) { throw new ArgumentException("Draw state has an invalid mode structure."); }
        }
        private void ValidateCore(DrawState state)
        {
            if (state == null || string.IsNullOrWhiteSpace(state.Id) || state.Revision < 0 || !state.PlayerId.IsValid ||
                state.ContentVersion != catalog.Version || state.RulesVersion != definition.Version || state.ScoreVersion != definition.Scoring.Version ||
                !Enum.IsDefined(typeof(DrawPhase), state.Phase) || state.HandsRemaining < 0 || state.HandsRemaining > definition.Hands ||
                state.DiscardsRemaining < 0 || state.DiscardsRemaining > definition.Discards || state.Score < 0)
                throw new ArgumentException("Invalid draw header, versions, or budgets.");
            var world = state.World; bool terminal = state.Phase == DrawPhase.Won || state.Phase == DrawPhase.Lost;
            AbilityWorldValidator.Validate(world, catalog);
            if (!runtime.Supports(world, catalog) || world.Id != state.Id || world.Revision != state.Revision || world.Mode != MatchMode.Solo ||
                world.IsRunning == terminal || world.PhaseKey != state.Phase.ToString() || world.Participants.Count != 1 ||
                world.Participants[0].Id != state.PlayerId || !world.Participants[0].Active ||
                world.CurrentActorId != (terminal ? (ParticipantId?)null : state.PlayerId) || !world.AllowCardChanges || !world.AllowChipChanges || !world.AllowKnowledgeProjection ||
                world.Resolution.PendingEffects.Count != 0 || world.ActiveDecision != null && world.ActiveDecision.Kind != DecisionKind.SelectTargets)
                throw new ArgumentException("Draw world does not match its mode state.");
            if (state.Random == null || state.Random.Algorithm != HmacDeckShuffler.Algorithm || state.Random.AlgorithmVersion != 1 || state.Random.StateBytes.Count != 40)
                throw new ArgumentException("Unsupported draw random state.");
            var phaseKeys = new[] { DrawPhase.Ready.ToString(), DrawPhase.BeforeScoring.ToString(), DrawPhase.AfterScoring.ToString(), DrawPhase.AfterDiscard.ToString() };
            if (world.Statuses.Any(s => s.Duration.Kind == DurationKind.UntilPhaseEnd && !phaseKeys.Contains(s.Duration.PhaseKey)) ||
                world.Knowledge.Any(k => k.UntilPhaseEnd != null && !phaseKeys.Contains(k.UntilPhaseEnd)))
                throw new ArgumentException("A draw duration references an unknown mode phase.");
            if (state.Cards.Count < definition.HandSize || state.Cards.Count > 512 || state.Cards.Select(c => c.Card.Id).Distinct().Count() != state.Cards.Count ||
                state.Cards.Count != world.Cards.Count || state.Cards.Any(c => c.BonusPoints < 0 || c.BonusMultiplier < 0 || c.MultiplierFactor < 0 || c.Retriggers < 0 || c.Retriggers > 8 ||
                    !world.Cards.Any(w => w.Id == c.Card.Id && w.DefinitionId == c.Card.DefinitionId && w.OriginId == c.Card.OriginId &&
                        w.Effects == c.Card.Effects && w.IsDisabled == c.Card.IsDisabled)))
                throw new ArgumentException("Invalid draw card enhancements or catalog.");
            var kinds = new[] { CardZoneKinds.Deck, CardZoneKinds.Hand, CardZoneKinds.Played, CardZoneKinds.Discard, CardZoneKinds.Removed };
            if (world.Zones.Count != kinds.Length || kinds.Any(k => world.Zones.Count(z => z.Kind == k) != 1) ||
                world.Zones.Any(z => z.Id != Zone(state.Id, z.Kind) || z.ControllerId != (z.Kind == CardZoneKinds.Hand || z.Kind == CardZoneKinds.Played ? (ParticipantId?)state.PlayerId : null)))
                throw new ArgumentException("Invalid draw zones.");
            var hand = world.Zones.Single(z => z.Kind == CardZoneKinds.Hand).Cards;
            var deck = world.Zones.Single(z => z.Kind == CardZoneKinds.Deck).Cards;
            var played = world.Zones.Single(z => z.Kind == CardZoneKinds.Played).Cards;
            if (hand.Count > definition.HandSize || state.PendingCards.Count > definition.MaxSelection || state.PendingCards.Distinct().Count() != state.PendingCards.Count ||
                state.PendingCards.Any(c => !world.Cards.Any(w => w.Id == c))) throw new ArgumentException("Invalid draw hand or pending play.");
            bool inPlay = state.Phase == DrawPhase.BeforeScoring || state.Phase == DrawPhase.AfterScoring;
            bool pending = inPlay || state.Phase == DrawPhase.AfterDiscard;
            if (pending != (state.PendingCards.Count > 0) || inPlay && !played.SequenceEqual(state.PendingCards) || !inPlay && played.Count != 0 ||
                pending && world.ActiveDecision == null) throw new ArgumentException("A pending draw phase must retain an executable ability choice.");
            if (state.Scores.Count != definition.Hands - state.HandsRemaining) throw new ArgumentException("Draw hand budget disagrees with score history.");
            long total = 0;
            for (int i = 0; i < state.Scores.Count; i++)
            {
                var score = state.Scores[i]; ScoreEngine.Validate(score);
                if (score.Id != state.Id + "/score/" + i) throw new ArgumentException("Invalid draw score identity.");
                total = checked(total + score.Total);
            }
            if (total != state.Score) throw new ArgumentException("Draw score disagrees with its history.");
            int roundIndex = state.Scores.Count - (state.Phase == DrawPhase.AfterScoring || state.Phase == DrawPhase.Scored || state.Phase == DrawPhase.Won ? 1 : 0);
            bool lostPreviousRound = state.Phase == DrawPhase.Lost && state.Scores.Count > 0 && world.ScopeId == state.Id + "/play/" + (state.Scores.Count - 1);
            if (roundIndex < 0 || world.ScopeId != state.Id + "/play/" + roundIndex && !lostPreviousRound) throw new ArgumentException("Invalid draw round identity.");
            if (state.LastPattern != null)
            {
                var pattern = state.LastPattern;
                if (!Enum.IsDefined(typeof(HandCategory), pattern.Category) || pattern.SubmittedCards.Count < 1 || pattern.SubmittedCards.Count > definition.MaxSelection ||
                    pattern.SubmittedCards.Distinct().Count() != pattern.SubmittedCards.Count || pattern.ScoringCards.Count < 1 ||
                    pattern.ScoringCards.Distinct().Count() != pattern.ScoringCards.Count || pattern.ScoringCards.Any(c => !pattern.SubmittedCards.Contains(c)) ||
                    pattern.SubmittedCards.Any(c => !world.Cards.Any(w => w.Id == c)) || pattern.TieBreakRanks.Count < 1 || pattern.TieBreakRanks.Count > 5 || pattern.TieBreakRanks.Any(r => r < 2 || r > 14))
                    throw new ArgumentException("Invalid draw pattern result.");
                if (state.Scores.Count == 0 || state.Scores.Last().Category != pattern.Category) throw new ArgumentException("Pattern disagrees with the latest score.");
            }
            else if (state.Scores.Count != 0) throw new ArgumentException("Missing draw pattern history.");
            bool checkpoint = state.Phase == DrawPhase.Scored || terminal;
            if (checkpoint && (world.AbilityWindow != null || world.ActiveDecision != null) || checkpoint && pending ||
                state.Phase == DrawPhase.Won && state.Score < definition.TargetScore ||
                state.Phase == DrawPhase.Lost && (state.Score >= definition.TargetScore || state.HandsRemaining > 0 && hand.Count + deck.Count > 0) ||
                state.Phase == DrawPhase.Scored && (state.Score >= definition.TargetScore || state.HandsRemaining == 0 || hand.Count + deck.Count == 0) ||
                state.Phase == DrawPhase.Ready && (state.HandsRemaining == 0 || hand.Count == 0))
                throw new ArgumentException("Invalid draw checkpoint or terminal state.");
            string timing = state.Phase == DrawPhase.Ready ? DrawTimings.BeforeAction : state.Phase == DrawPhase.BeforeScoring ? DrawTimings.BeforeScoring :
                state.Phase == DrawPhase.AfterScoring ? DrawTimings.AfterScoring : state.Phase == DrawPhase.AfterDiscard ? DrawTimings.AfterDiscard : null;
            if (timing != null && (world.AbilityWindow == null || world.AbilityWindow.Timing != timing)) throw new ArgumentException("Draw ability window disagrees with its phase.");
        }
    }
}
