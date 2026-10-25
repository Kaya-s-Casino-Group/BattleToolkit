using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    /// <summary>Public-API traces captured before the Tyche extraction. Never regenerate expectations
    /// merely to make a refactor pass: changes require an intentional gameplay contract change.</summary>
    [TestFixture]
    public sealed class BattleRefactoringContractTests
    {
        private sealed class Presentation : IBattleSessionPresentation
        {
            public bool IsPresenting { get; set; }
            public void Initialize(CardDuelState state) { }
            public void Present(BattleSessionResult result, bool animate) { }
            public void Synchronize(CardDuelState state) { }
        }

        [TestCase("legacy", 1, "527486eb26844b9221c87da2420c793e08995860537dcc6dd0dffb9ee825906e")]
        [TestCase("legacy", 2, "04db940901cdca08ca33ae6f879f020ef7d95005a251160addabfdb0a88aab00")]
        [TestCase("manual", 1, "3f2069319e71cb1c4236418b914b788445ac98e087c2960f67f9989ae88106d4")]
        [TestCase("manual", 2, "9b3f0193d705cfdea427c8d5d667977f8be3b0b9775853ac69551951d4b2dfb5")]
        [TestCase("automatic", 1, "1d26e1d204e24a1799ab32bdc8aa5382ab11209ed8ce0e522bcacac242c8ee3e")]
        [TestCase("automatic", 2, "3f5128fd2b86e1f730dabd1634959fd78742ee3dcae580d20b0257935ca33060")]
        public void SeededCommandsPreserveQueriesPreviewsStepsAndEveryTimelineSnapshot(string mode, int players, string expected)
        {
            var flow = mode == "legacy" ? new BattleTurnFlowRules() :
                new BattleTurnFlowRules(manualDraw: mode == "manual", autoDrawToLimit: mode == "automatic",
                    openingDeal: mode == "automatic", drawCount: 5, actionsPerTurn: 2,
                    discardEndsTurn: false, endDefense: mode == "manual");
            var rules = new BattlePlayerRules(handSize: 8, maxPlayCards: 5, unlimitedDiscards: true,
                dealPolicy: new CardDuelDealPolicy(CardDuelRefillTiming.AfterAction, CardDuelRefillTiming.DiscardedCount),
                damagePolicy: new ScoreDamagePolicy(1), flow: flow);
            var deck = StandardDeck.Cards().Take(10).Select((card, i) => new DeckEntry("card/" + i, card.Id)).ToArray();
            var engine = new CardDuelEngine(new BattleRules("refactoring-contract", 4));
            var roster = Enumerable.Range(0, players).Select(i => new BattlePlayerSetup(new ParticipantId("p" + i),
                new CombatantDefinition("hero", 1000), deck, rules: rules)).ToArray();
            var session = new BattleSession(engine, engine.Create("contract", HmacDeckShuffler.FromSeed(new byte[32]), roster,
                new[] { new BattleEnemySetup(new ParticipantId("enemy"), new CombatantDefinition("enemy", 1000000), 7) }));
            var presentation = new Presentation();
            var playback = new BattleSessionPlayback(session, presentation);
            var trace = new StringBuilder();
            Record(trace, playback.Start(false));
            int command = 0;
            while (!session.State.Duel.IsFinished && command++ < 80)
            {
                var before = session.State;
                var actor = before.Duel.Players.First(p => before.Duel.HasPlayerWindow(p.Id)).Id;
                var cards = before.Zone(CardZoneKinds.Hand, actor).Cards.Take(command % 2 + 1).ToArray();
                Append(trace, playback.CanAct(actor), playback.CanPlay(actor, cards), playback.CanDiscard(actor, cards));
                presentation.IsPresenting = true;
                Assert.IsFalse(playback.CanAct(actor)); Assert.IsFalse(playback.CanPlay(actor, cards));
                Assert.IsFalse(playback.CanDiscard(actor, cards));
                presentation.IsPresenting = false;

                var stale = session.Execute(before.Revision - 1, actor, CardDuelAction.EndTurn);
                Assert.AreEqual(CardDuelError.StaleRevision, stale.Error);
                Assert.AreSame(before, session.State); Assert.IsEmpty(stale.Timeline); Assert.IsEmpty(stale.Steps);
                var preview = engine.PreviewHand(before, actor, cards);
                Append(trace, preview.Error, preview.Pattern?.Category, preview.Score?.Total);
                Assert.AreSame(before, preview.State);
                if (playback.CanPlay(actor, cards))
                {
                    var result = playback.PreviewPlay(actor, cards);
                    Append(trace, result.Error); RecordState(trace, result.State);
                    Assert.AreSame(before, session.State);
                }

                CardDuelAction action;
                var window = before.FlowFor(actor).Window;
                if (mode == "manual" && window == BattlePlayerWindow.Draw)
                    action = CardDuelAction.Draw;
                else if (window == BattlePlayerWindow.ForcedDiscard)
                {
                    action = CardDuelAction.ConfirmOverflow;
                    cards = before.Zone(CardZoneKinds.Hand, actor).Cards.Take(engine.RequiredDiscards(before, actor)).ToArray();
                }
                else if (window == BattlePlayerWindow.Ending)
                    action = playback.CanDiscard(actor, cards) ? CardDuelAction.Discard : CardDuelAction.FinishTurn;
                else if (command % 4 == 0)
                    action = CardDuelAction.EndTurn;
                else if (command % 3 == 0 && playback.CanDiscard(actor, cards))
                    action = CardDuelAction.Discard;
                else
                    action = playback.CanPlay(actor, cards) ? CardDuelAction.Play : CardDuelAction.EndTurn;
                if (action != CardDuelAction.Play && action != CardDuelAction.Discard && action != CardDuelAction.ConfirmOverflow)
                    cards = null;
                var batch = playback.Submit(actor, action, cards, animate: false);
                Assert.IsTrue(batch.Accepted, action + ": " + batch.Error);
                Record(trace, batch);
            }
            Assert.IsTrue(session.State.Duel.IsFinished, "Scenario must reach its terminal boundary.");
            string actual;
            using (var sha = SHA256.Create())
                actual = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(trace.ToString()))).Replace("-", "").ToLowerInvariant();
            TestContext.Out.WriteLine(mode + "/" + players + ": " + actual);
            Assert.AreEqual(expected, actual, "The pre-refactor public behavior trace changed.");
        }

        private static void Append(StringBuilder text, params object[] values)
        {
            foreach (var value in values)
            {
                string s = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
                text.Append(s.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(s).Append('|');
            }
            text.Append('\n');
        }

        private static void Record(StringBuilder text, BattleSessionResult result)
        {
            Append(text, result.Error); RecordState(text, result.Before); RecordState(text, result.State);
            foreach (var step in result.Steps)
            {
                Append(text, step.Action, step.Actor, step.Transition.Error, step.Transition.Score?.Total, step.Transition.Pattern?.Category);
                foreach (var card in step.Selected) Append(text, card);
                RecordState(text, step.Before); RecordState(text, step.Transition.State);
                foreach (var fact in step.Transition.RuleEvents)
                {
                    Append(text, fact.GetType().Name, fact.Revision, fact.SequenceIndex);
                    if (fact is CombatEvent combat)
                        Append(text, combat.Effect.Kind, combat.Effect.Source, combat.Effect.Target, combat.Effect.Amount,
                            combat.Before.Health, combat.Before.Block, combat.After.Health, combat.After.Block, combat.BlockAbsorbed, combat.SettledAmount);
                }
            }
            foreach (var entry in result.Timeline)
            {
                Append(text, entry.Sequence, entry.StepIndex, entry.Kind, entry.Action, entry.Actor, entry.ActionId,
                    entry.IsActionSkipped, entry.PhaseKey, entry.Reason, entry.FromZone, entry.ToZone, entry.CardOwner, entry.DealTrigger);
                foreach (var card in entry.Cards) Append(text, card);
                RecordState(text, entry.State);
            }
        }

        private static void RecordState(StringBuilder text, CardDuelState state)
        {
            Append(text, state.Revision, state.HasStarted, state.Duel.Revision, state.Duel.Combat.Revision,
                state.Duel.Phase, state.Duel.CompletedRounds, Convert.ToBase64String(state.Random.StateBytes.ToArray()));
            foreach (var zone in state.Table.Zones)
            {
                Append(text, zone.Id, zone.Kind, zone.ControllerId);
                foreach (var card in zone.Cards) Append(text, card);
            }
            foreach (var unit in state.Duel.Combat.Combatants) Append(text, unit.Id, unit.Health, unit.MaxHealth, unit.Block);
            foreach (var player in state.Duel.Players)
            {
                var flow = state.FlowFor(player.Id);
                Append(text, player.Id, player.Energy.Energy, player.HasEnded, state.DiscardsFor(player.Id), state.GoldFor(player.Id),
                    flow.Window, flow.DrawUsesRemaining, flow.ActionsRemaining, flow.DefenseUsed, flow.FinishAfterOverflow);
            }
            foreach (var enemy in state.Duel.PendingEnemies) Append(text, enemy);
            foreach (var score in state.Duel.Scores)
            {
                Append(text, score.Id, score.Category, score.Total, score.Points, score.Multiplier);
                foreach (var step in score.Steps) Append(text, step.CardId, step.Operation, step.Amount);
            }
            foreach (var action in state.Duel.Actions) Append(text, action.Actor, action.Kind, action.ScoreIndex);
        }
    }
}
