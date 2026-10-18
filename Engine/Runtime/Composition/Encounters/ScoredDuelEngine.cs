using System;
using System.Globalization;
using System.Linq;

namespace PokerEngine
{
    /// <summary>
    /// Authority-only duel: energy-funded player actions, explicit/automatic turn end, fixed enemy action.
    /// The host owns card legality/scoring, input, animation and persistence; no deck or Run ownership here.
    /// </summary>
    public sealed class ScoredDuelEngine
    {
        public const string RulesVersion = "scored-duel.4";
        public ScoredDuelDefinition Definition { get; }
        public ScoredDuelEngine(ScoredDuelDefinition definition) { Definition = definition ?? throw new ArgumentNullException(nameof(definition)); }

        public ScoredDuelState Create(string encounterId, ParticipantId player, ParticipantId enemy, int? initialPlayerHealth = null)
        {
            int health = initialPlayerHealth ?? Definition.Player.MaxHealth;
            if (!player.IsValid || !enemy.IsValid || player == enemy || health <= 0 || health > Definition.Player.MaxHealth)
                throw new ArgumentException("A duel requires two distinct living actors.");
            return new ScoredDuelState(Definition.Fingerprint, new CombatState(encounterId, 0, new[] {
                Definition.Player.Create(player, "player", health), Definition.Enemy.Create(enemy, "enemy") }),
                player, enemy, health, ScoredDuelPhase.AwaitingPlayerHand, new ScoreResult[0], 0, 0,
                new PlayerTurnState(Definition.MaxEnergy, Definition.InitialEnergy), new ScoredDuelActionRecord[0]);
        }

        /// <summary>Use this ID when the authoritative scoring owner calls ScoreEngine for the next hand.</summary>
        public string NextScoreId(ScoredDuelState state) => state.Combat.Id + "/score/" + state.Scores.Count.ToString(CultureInfo.InvariantCulture);

        /// <summary>Accept only scores produced by the authoritative host, never an untrusted player's numeric score.</summary>
        public ScoredDuelTransition ResolveScoredHand(ScoredDuelState state, long expectedRevision, ScoreResult score)
        {
            var error = Check(state, expectedRevision, ScoredDuelPhase.AwaitingPlayerHand);
            if (error != ScoredDuelError.None) return new ScoredDuelTransition(state, error);
            if (state.PlayerTurn.Energy < Definition.PlayEnergyCost) return new ScoredDuelTransition(state, ScoredDuelError.InsufficientEnergy);
            try
            {
                ScoreEngine.Validate(score);
                if (score.Id != NextScoreId(state)) return new ScoredDuelTransition(state, ScoredDuelError.InvalidScore);
                checked { long total = state.TotalScore + score.Total; if (total < 0) throw new OverflowException(); }
                return ApplyHand(state, score);
            }
            catch (ArgumentException) { return new ScoredDuelTransition(state, ScoredDuelError.InvalidScore); }
            catch (NullReferenceException) { return new ScoredDuelTransition(state, ScoredDuelError.InvalidScore); }
            catch (OverflowException) { return new ScoredDuelTransition(state, ScoredDuelError.Overflow); }
        }
        public ScoredDuelTransition ResolveEnemyAction(ScoredDuelState state, long expectedRevision)
        {
            var error = Check(state, expectedRevision, ScoredDuelPhase.AwaitingEnemyAction);
            return error == ScoredDuelError.None ? ApplyEnemy(state) : new ScoredDuelTransition(state, error);
        }
        public ScoredDuelTransition EndPlayerTurn(ScoredDuelState state, long expectedRevision)
        {
            var error = Check(state, expectedRevision, ScoredDuelPhase.AwaitingPlayerHand);
            return error == ScoredDuelError.None ? ApplyEndTurn(state) : new ScoredDuelTransition(state, error);
        }
        /// <summary>The card authority validates membership and discard budget before requesting this energy charge.</summary>
        public ScoredDuelTransition ResolveDiscard(ScoredDuelState state, long expectedRevision)
        {
            var error = Check(state, expectedRevision, ScoredDuelPhase.AwaitingPlayerHand);
            if (error != ScoredDuelError.None) return new ScoredDuelTransition(state, error);
            if (state.PlayerTurn.Energy < Definition.DiscardEnergyCost) return new ScoredDuelTransition(state, ScoredDuelError.InsufficientEnergy);
            return ApplyDiscard(state);
        }
        private ScoredDuelError Check(ScoredDuelState state, long revision, ScoredDuelPhase phase)
        {
            try { Validate(state); }
            catch (ArgumentException) { return ScoredDuelError.InvalidState; }
            catch (OverflowException) { return ScoredDuelError.InvalidState; }
            if (state.Revision != revision) return ScoredDuelError.StaleRevision;
            return state.Phase == phase ? ScoredDuelError.None : ScoredDuelError.InvalidPhase;
        }
        private ScoredDuelTransition ApplyHand(ScoredDuelState state, ScoreResult score)
        {
            var result = CombatEngine.Resolve(state.Combat, state.Combat.Revision, new[] {
                new CombatEffect(CombatEffectKind.Damage, state.PlayerId, state.EnemyId, Definition.DamagePolicy.Calculate(score)) });
            if (!result.Accepted) throw new ArgumentException("Invalid duel damage state.");
            int energy = state.PlayerTurn.Energy - Definition.PlayEnergyCost;
            bool won = !result.State.Get(state.EnemyId).IsAlive;
            bool endTurn = energy == 0;
            var phase = won ? ScoredDuelPhase.Won : endTurn ? ScoredDuelPhase.AwaitingEnemyAction : ScoredDuelPhase.AwaitingPlayerHand;
            return new ScoredDuelTransition(Copy(state, result.State, phase, state.Scores.Concat(new[] { score }).ToArray(),
                won ? state.CompletedRounds + 1 : state.CompletedRounds, energy,
                new ScoredDuelActionRecord(ScoredDuelActionKind.ScoredHand, state.Scores.Count)), events: result.Events);
        }
        private ScoredDuelTransition ApplyDiscard(ScoredDuelState state)
        {
            int energy = state.PlayerTurn.Energy - Definition.DiscardEnergyCost;
            return new ScoredDuelTransition(Copy(state, state.Combat,
                energy == 0 ? ScoredDuelPhase.AwaitingEnemyAction : ScoredDuelPhase.AwaitingPlayerHand,
                state.Scores.ToArray(), state.CompletedRounds, energy, new ScoredDuelActionRecord(ScoredDuelActionKind.Discard)));
        }
        private ScoredDuelTransition ApplyEndTurn(ScoredDuelState state) => new ScoredDuelTransition(Copy(state, state.Combat,
            ScoredDuelPhase.AwaitingEnemyAction, state.Scores.ToArray(), state.CompletedRounds, 0,
            new ScoredDuelActionRecord(ScoredDuelActionKind.EndPlayerTurn)));
        private ScoredDuelTransition ApplyEnemy(ScoredDuelState state)
        {
            var result = CombatEngine.Resolve(state.Combat, state.Combat.Revision, new[] {
                new CombatEffect(CombatEffectKind.Damage, state.EnemyId, state.PlayerId, Definition.EnemyDamage) });
            if (!result.Accepted) throw new ArgumentException("Invalid enemy action state.");
            int rounds = state.CompletedRounds + 1;
            var phase = !result.State.Get(state.PlayerId).IsAlive || rounds >= Definition.RoundLimit
                ? ScoredDuelPhase.Lost : ScoredDuelPhase.AwaitingPlayerHand;
            return new ScoredDuelTransition(Copy(state, result.State, phase, state.Scores.ToArray(), rounds,
                phase == ScoredDuelPhase.AwaitingPlayerHand ? Definition.MaxEnergy : 0,
                new ScoredDuelActionRecord(ScoredDuelActionKind.EnemyAction)), events: result.Events);
        }
        private static ScoredDuelState Copy(ScoredDuelState state, CombatState combat, ScoredDuelPhase phase, ScoreResult[] scores,
            int completedRounds, int energy, ScoredDuelActionRecord action) =>
            new ScoredDuelState(state.DefinitionFingerprint, combat, state.PlayerId, state.EnemyId, state.InitialPlayerHealth, phase, scores,
                checked(state.Revision + 1), completedRounds, new PlayerTurnState(state.PlayerTurn.MaxEnergy, energy), state.Actions.Concat(new[] { action }));

        public EncounterContext Describe(ScoredDuelState state)
        {
            Validate(state); return DescribeValid(state);
        }
        private EncounterContext DescribeValid(ScoredDuelState state) => new EncounterContext(state.Combat.Id,
            new EncounterDefinition("scored-duel", Definition.Version, "scored-duel", RulesVersion,
                new EncounterObjectiveDefinition(EncounterObjectiveKind.DefeatOpposition), Definition.RoundLimit, new[] {
                    new EncounterParticipantDefinition("player", "player"),
                    new EncounterParticipantDefinition("enemy", "enemy") }),
            new[] { new EncounterParticipantBinding("player", state.PlayerId), new EncounterParticipantBinding("enemy", state.EnemyId) });

        public EncounterProgress ReadProgress(ScoredDuelState state)
        {
            Validate(state); EncounterProgress progress;
            if (!state.IsFinished)
                progress = new EncounterProgress(EncounterPhase.AwaitingAction, state.Revision, state.CompletedRounds,
                    state.Phase == ScoredDuelPhase.AwaitingPlayerHand ? state.PlayerId : state.EnemyId);
            else
            {
                bool won = state.Phase == ScoredDuelPhase.Won;
                var reason = won ? EncounterEndReason.ObjectiveReached : !state.Combat.Get(state.PlayerId).IsAlive
                    ? EncounterEndReason.ParticipantEliminated : EncounterEndReason.RoundLimit;
                var result = new EncounterResult(state.Combat.Id, state.Revision, state.CompletedRounds, reason, new[] {
                    new EncounterParticipantResult(state.PlayerId, won ? EncounterOutcome.Won : EncounterOutcome.Lost, state.TotalScore),
                    new EncounterParticipantResult(state.EnemyId, won ? EncounterOutcome.Lost : EncounterOutcome.Won) });
                progress = new EncounterProgress(EncounterPhase.Finished, state.Revision, state.CompletedRounds, result: result);
            }
            progress.Validate(DescribeValid(state)); return progress;
        }

        /// <summary>Replay the action ledger, including zero-play turns; HP, energy, rounds and phase must agree.</summary>
        public void Validate(ScoredDuelState state)
        {
            try
            {
                long energyBudget = Definition.InitialEnergy + (long)(Definition.RoundLimit - 1) * Definition.MaxEnergy;
                if (state == null || state.DefinitionFingerprint != Definition.Fingerprint ||
                    !Enum.IsDefined(typeof(ScoredDuelPhase), state.Phase) || state.Revision < 0 ||
                    state.Scores.Count > energyBudget / Definition.PlayEnergyCost ||
                    state.Actions.Count > energyBudget / Math.Min(Definition.PlayEnergyCost, Definition.DiscardEnergyCost) + Definition.RoundLimit * 2L)
                    throw new ArgumentException("Invalid or incompatible duel state.");
                CombatEngine.Validate(state.Combat);
                if (state.Combat.Statuses.Count != 0) throw new ArgumentException("This minimal duel has no status execution policy.");
                var replay = Create(state.Combat.Id, state.PlayerId, state.EnemyId, state.InitialPlayerHealth);
                long total = 0; int scoreIndex = 0;
                foreach (var action in state.Actions)
                {
                    if (action == null || replay.IsFinished) throw new ArgumentException("Unreachable duel action.");
                    new ScoredDuelActionRecord(action.Kind, action.ScoreIndex);
                    if (action.Kind == ScoredDuelActionKind.ScoredHand)
                    {
                        if (replay.Phase != ScoredDuelPhase.AwaitingPlayerHand || replay.PlayerTurn.Energy < Definition.PlayEnergyCost ||
                            action.ScoreIndex != scoreIndex || scoreIndex >= state.Scores.Count) throw new ArgumentException("Invalid scored action.");
                        var score = state.Scores[scoreIndex++]; ScoreEngine.Validate(score); total = checked(total + score.Total);
                        if (score.Id != NextScoreId(replay)) throw new ArgumentException("Repeated or foreign duel score.");
                        replay = ApplyHand(replay, score).State;
                    }
                    else if (action.Kind == ScoredDuelActionKind.EndPlayerTurn)
                    {
                        if (replay.Phase != ScoredDuelPhase.AwaitingPlayerHand) throw new ArgumentException("Invalid turn end.");
                        replay = ApplyEndTurn(replay).State;
                    }
                    else if (action.Kind == ScoredDuelActionKind.Discard)
                    {
                        if (replay.Phase != ScoredDuelPhase.AwaitingPlayerHand || replay.PlayerTurn.Energy < Definition.DiscardEnergyCost)
                            throw new ArgumentException("Invalid discard energy action.");
                        replay = ApplyDiscard(replay).State;
                    }
                    else
                    {
                        if (replay.Phase != ScoredDuelPhase.AwaitingEnemyAction) throw new ArgumentException("Invalid enemy action.");
                        replay = ApplyEnemy(replay).State;
                    }
                }
                if (total < 0 || scoreIndex != state.Scores.Count || replay.Phase != state.Phase || replay.Revision != state.Revision ||
                    replay.Combat.Revision != state.Combat.Revision || replay.CompletedRounds != state.CompletedRounds ||
                    replay.PlayerTurn.MaxEnergy != state.PlayerTurn.MaxEnergy || replay.PlayerTurn.Energy != state.PlayerTurn.Energy ||
                    state.Combat.Combatants.Count != 2)
                    throw new ArgumentException("Duel phase or revision disagrees with its ledger.");
                foreach (var actual in state.Combat.Combatants)
                {
                    var expected = replay.Combat.Get(actual.Id);
                    if (actual.DefinitionId != expected.DefinitionId || actual.TeamId != expected.TeamId || actual.MaxHealth != expected.MaxHealth ||
                        actual.Health != expected.Health || actual.Block != expected.Block)
                        throw new ArgumentException("Duel unit disagrees with its ledger.");
                }
            }
            catch (NullReferenceException error) { throw new ArgumentException("Missing duel state data.", error); }
        }
    }
}
