using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class BattleRelicBindingTests
    {
        private static readonly ParticipantId Player = new ParticipantId("relic-player"), Ally = new ParticipantId("relic-ally"),
            Enemy = new ParticipantId("relic-enemy");
        private static AbilityMemory PersistentMemory(long value = 2) => AbilityMemory.Empty.WithCounter("persistent", value, MemoryScope.Encounter);
        private static AbilityMemory RuntimeMemory(long persistent = 9, long round = 5) =>
            PersistentMemory(persistent).WithCounter("round", round, MemoryScope.Round);
        private static BattleRelicSetup Setup(string instance, bool charged = false, decimal amount = 500)
        {
            string id = charged ? "charged" : "passive";
            var definition = new RelicDefinition(id,
                new[] { new ScoreEffect(ScoreTiming.AfterCards, ScoreOperation.AddPoints, amount, perLevel: 10, perGrowth: 20) },
                maxCopies: 3, maxLevel: 4, growthPerScoredRound: 7,
                abilityId: charged ? new AbilityDefinitionId("not-installed") : (AbilityDefinitionId?)null, charges: charged ? 4 : (int?)null);
            return new BattleRelicSetup(definition, new RunRelic(instance, id, level: 2, growth: 7, charges: charged ? 3 : (int?)null,
                cooldownRounds: 2, memory: PersistentMemory()));
        }
        private static BattleRelicSetup[] Setups(ParticipantId owner) => new[] { Setup(owner.Value + "/passive"), Setup(owner.Value + "/charged", true) };
        private static BattleRelicLoadout Loadout(ParticipantId? owner = null) => new BattleRelicLoadout(owner ?? Player, Setups(owner ?? Player));
        private static BattleRelicState[] Updated(BattleRelicLoadout binding) => binding.Relics.Select((item, index) =>
            item.WithRuntime(item.RemainingCharges.HasValue ? 2 : (int?)null, 2 + index, 1 + index, RuntimeMemory(9 + index, 5 + index))).ToArray();
        private static BattleRelicState Copy(BattleRelicState item, ParticipantId? owner = null, string instance = null,
            string definition = null, int? order = null, int? level = null, long? growth = null) => new BattleRelicState(
                instance ?? item.InstanceId, definition ?? item.DefinitionId, owner ?? item.OwnerId, order ?? item.Order,
                level ?? item.Level, growth ?? item.Growth, item.RemainingCharges, item.UsesThisRound, item.CooldownRounds, item.Memory);
        private static string Snapshot(BattleRelicLoadout binding) => BattleRelicSnapshotCodec.Serialize(binding);
        private sealed class Fixture
        {
            public readonly CardDuelEngine Engine = new CardDuelEngine(new BattleRules("relic-binding.1", 20));
            public CardDuelState State;
            public Fixture(bool bound = true, bool ally = true)
            {
                var players = new List<BattlePlayerSetup> { new BattlePlayerSetup(Player, new CombatantDefinition("hero", 100),
                    relics: bound ? Setups(Player) : null) };
                if (ally) players.Add(new BattlePlayerSetup(Ally, new CombatantDefinition("ally", 100), relics: bound ? Setups(Ally) : null));
                State = Engine.Create("relic-binding", HmacDeckShuffler.FromSeed(new byte[32]), players,
                    new[] { new BattleEnemySetup(Enemy, new CombatantDefinition("enemy", 10000), 7) });
            }
            public CardDuelTransition Do(CardDuelAction action, ParticipantId? actor = null, int cards = 0)
            {
                var who = actor ?? (action == CardDuelAction.EnemyAction ? Enemy : Player);
                var result = Engine.Execute(State, State.Revision, who, action,
                    cards == 0 ? null : State.Zone(CardZoneKinds.Hand, who).Cards.Take(cards));
                Assert.IsTrue(result.Accepted, action + ": " + result.Error); State = result.State; return result;
            }
        }

        [Test] public void BindingsCopyInputsKeepDefinitionIdentityAndSeparateSameDefinitionAcrossOwners()
        {
            var inputs = Setups(Player); var first = inputs[0]; var binding = new BattleRelicLoadout(Player, inputs);
            inputs[0] = Setup("replacement");
            Assert.AreEqual(Player.Value + "/passive", binding.Relics[0].InstanceId); Assert.AreEqual(2, binding.Relics.Count);
            Assert.AreSame(first.Definition, binding.Definition(first.Instance.Id)); Assert.AreEqual(Player, binding.OwnerId);
            Assert.AreEqual(0, binding.Relics[0].Order); Assert.AreEqual(1, binding.Relics[1].Order);
            Assert.IsNull(binding.Definition(binding.Relics[0].InstanceId).AbilityId);
            Assert.Catch<NotSupportedException>(() => ((IList<BattleRelicState>)binding.Relics)[0] = binding.Relics[1]);
            var runtime = Updated(binding); var next = binding.WithRuntime(runtime); runtime[0] = binding.Relics[0];
            Assert.AreEqual(9, next.Relics[0].Memory.CounterValue("persistent")); Assert.AreEqual(2, binding.Relics[0].Memory.CounterValue("persistent"));
            Assert.AreEqual(binding.Fingerprint, next.Fingerprint); Assert.AreSame(first.Definition, next.Definition(first.Instance.Id));
            var other = Loadout(Ally); Assert.AreEqual(binding.Relics[0].DefinitionId, other.Relics[0].DefinitionId);
            Assert.AreEqual(Ally, other.Relics[0].OwnerId); Assert.AreNotEqual(binding.Fingerprint, other.Fingerprint);
            Assert.AreEqual(0, other.Relics[0].UsesThisRound); Assert.AreEqual(2, other.Relics[0].Memory.CounterValue("persistent"));
        }

        [Test] public void PlayerSetupCopiesTheBindingInputBeforeTheBattleIsCreated()
        {
            var inputs = Setups(Player); var setup = new BattlePlayerSetup(Player, new CombatantDefinition("hero", 100), relics: inputs);
            inputs[0] = Setup("replacement");
            var engine = new TeamBattleEngine(new BattleRules("setup-copy.1", 10));
            var state = engine.Create("setup-copy", new[] { setup }, new[] { new BattleEnemySetup(Enemy, new CombatantDefinition("enemy", 100), 2) });
            Assert.AreEqual(Player.Value + "/passive", state.Player(Player).Relics.Relics[0].InstanceId);
            Assert.AreEqual(2, state.Player(Player).Relics.Relics.Count);
        }

        [Test] public void RuntimeReplacementAdvancesOnlyBattleAndCardRevisionsWithoutAnActionOrCombatFact()
        {
            var fixture = new Fixture(); var before = fixture.State; var ally = Snapshot(before.Duel.Player(Ally).Relics);
            var replacement = Updated(before.Duel.Player(Player).Relics);
            var result = fixture.Engine.UpdateRelicRuntime(before, before.Revision, Player, replacement);
            Assert.IsTrue(result.Accepted); Assert.AreEqual(before.Revision + 1, result.State.Revision);
            Assert.AreEqual(before.Duel.Revision + 1, result.State.Duel.Revision); Assert.AreSame(before.Duel.Combat, result.State.Duel.Combat);
            Assert.IsFalse(result.State.HasStarted); Assert.IsEmpty(result.RuleEvents); Assert.IsEmpty(result.Events);
            Assert.IsNull(result.Score); Assert.IsNull(result.EffectPlan); Assert.IsEmpty(result.State.Duel.Actions); Assert.IsEmpty(result.State.Duel.Scores);
            Assert.AreEqual(before.DiscardsRemaining, result.State.DiscardsRemaining); Assert.AreEqual(3, result.State.Duel.Player(Player).Energy.Energy);
            Assert.AreEqual(ally, Snapshot(result.State.Duel.Player(Ally).Relics));
            Assert.AreEqual(2, before.Duel.Player(Player).Relics.Relics[0].Memory.CounterValue("persistent"));
            Assert.AreEqual(9, result.State.Duel.Player(Player).Relics.Relics[0].Memory.CounterValue("persistent"));
            foreach (var zone in before.Table.Zones) CollectionAssert.AreEqual(zone.Cards, result.State.Table.GetZone(zone.Id).Cards);
            CollectionAssert.AreEqual(before.Random.StateBytes, result.State.Random.StateBytes);
            replacement[0] = before.Duel.Player(Player).Relics.Relics[0];
            Assert.AreEqual(9, result.State.Duel.Player(Player).Relics.Relics[0].Memory.CounterValue("persistent"));
        }

        [Test] public void EveryCardAndTurnTransitionPreservesExplicitRuntimeWithoutImplicitGrowthOrCooldown()
        {
            var fixture = new Fixture();
            var changed = fixture.Engine.UpdateRelicRuntime(fixture.State, fixture.State.Revision, Player, Updated(fixture.State.Duel.Player(Player).Relics));
            Assert.IsTrue(changed.Accepted); fixture.State = changed.State;
            var expected = Snapshot(fixture.State.Duel.Player(Player).Relics); var ally = Snapshot(fixture.State.Duel.Player(Ally).Relics);
            Action<CardDuelState> unchanged = state => {
                Assert.AreEqual(expected, Snapshot(state.Duel.Player(Player).Relics)); Assert.AreEqual(ally, Snapshot(state.Duel.Player(Ally).Relics));
            };
            unchanged(fixture.Do(CardDuelAction.Start).State);
            var before = fixture.State; var selection = before.Zone(CardZoneKinds.Hand, Player).Cards.Take(1).ToArray();
            var hand = fixture.Engine.PreviewHand(before, Player, selection);
            var preview = fixture.Engine.PreviewPlay(before, before.Revision, Player, selection);
            Assert.IsTrue(hand.Accepted); Assert.IsTrue(preview.Accepted); unchanged(hand.State); unchanged(preview.State); unchanged(before);
            unchanged(fixture.Do(CardDuelAction.Play, cards: 1).State);
            unchanged(fixture.Do(CardDuelAction.CollectPlayed).State);
            before = fixture.State;
            var discardPreview = fixture.Engine.PreviewDiscard(before, before.Revision, Player, before.Zone(CardZoneKinds.Hand, Player).Cards.Take(1));
            Assert.IsTrue(discardPreview.Accepted); unchanged(discardPreview.State); unchanged(before);
            unchanged(fixture.Do(CardDuelAction.Discard, cards: 1).State);
            var failed = fixture.Engine.Execute(fixture.State, fixture.State.Revision, Player, CardDuelAction.Play, Array.Empty<CardInstanceId>());
            Assert.IsFalse(failed.Accepted); Assert.AreSame(fixture.State, failed.State); unchanged(failed.State);
            unchanged(fixture.Do(CardDuelAction.EndTurn).State); unchanged(fixture.Do(CardDuelAction.EndTurn, Ally).State);
            unchanged(fixture.Do(CardDuelAction.EnemyAction).State);
            Assert.AreEqual(1, fixture.State.Duel.CompletedRounds);
            var relic = fixture.State.Duel.Player(Player).Relics.Relics[0];
            Assert.AreEqual(7, relic.Growth); Assert.AreEqual(2, relic.UsesThisRound); Assert.AreEqual(1, relic.CooldownRounds);
            Assert.AreEqual(5, relic.Memory.CounterValue("round"));
        }

        [Test] public void BoundScoreEffectsAndAbilityIdentifiersRemainDormantInThisInfrastructureStep()
        {
            var bound = new Fixture(true, false); var plain = new Fixture(false, false);
            bound.Do(CardDuelAction.Start); plain.Do(CardDuelAction.Start);
            var cards = bound.State.Zone(CardZoneKinds.Hand, Player).Cards.Take(1).ToArray();
            CollectionAssert.AreEqual(cards, plain.State.Zone(CardZoneKinds.Hand, Player).Cards.Take(1));
            var preview = bound.Engine.PreviewHand(bound.State, Player, cards);
            var baseline = plain.Engine.PreviewHand(plain.State, Player, cards);
            Assert.IsTrue(preview.Accepted); Assert.AreEqual(baseline.Score.Total, preview.Score.Total);
            Assert.IsFalse(preview.Score.Steps.Any(step => bound.State.Duel.Player(Player).Relics.Relics.Any(relic => relic.InstanceId == step.SourceId)));
            var played = bound.Do(CardDuelAction.Play, cards: 1); var ordinary = plain.Do(CardDuelAction.Play, cards: 1);
            Assert.AreEqual(ordinary.Score.Total, played.Score.Total); Assert.AreEqual(ordinary.State.Duel.Combat.Get(Enemy).Health, played.State.Duel.Combat.Get(Enemy).Health);
            Assert.IsEmpty(played.State.Duel.Combat.Statuses); Assert.AreEqual(3, played.State.Duel.Player(Player).Relics.Relics[1].RemainingCharges);
            Assert.AreEqual(0, played.State.Duel.Player(Player).Relics.Relics[1].UsesThisRound);
            bound.Do(CardDuelAction.CollectPlayed); plain.Do(CardDuelAction.CollectPlayed);
            var discarded = bound.Do(CardDuelAction.Discard, cards: 1); var defaultDiscard = plain.Do(CardDuelAction.Discard, cards: 1);
            Assert.AreEqual(defaultDiscard.State.Duel.Combat.Get(Player).Block, discarded.State.Duel.Combat.Get(Player).Block);
            Assert.AreEqual(defaultDiscard.State.Duel.Player(Player).Energy.Energy, discarded.State.Duel.Player(Player).Energy.Energy);
            Assert.AreEqual(defaultDiscard.Events.Count, discarded.Events.Count);
        }

        [TestCase("owner")] [TestCase("instance")] [TestCase("definition")] [TestCase("order")]
        [TestCase("level")] [TestCase("growth")] [TestCase("remove")] [TestCase("add")]
        [TestCase("duplicate")] [TestCase("charges")] [TestCase("charge-kind")] [TestCase("null")]
        public void InvalidRuntimeReplacementIsRejectedAtomicallyByBothAuthorities(string variant)
        {
            var fixture = new Fixture(); fixture.Do(CardDuelAction.Start);
            var state = fixture.State; var current = state.Duel.Player(Player).Relics.Relics.ToArray(); var replacement = current.ToArray();
            switch (variant)
            {
                case "owner": replacement[0] = Copy(current[0], owner: Ally); break;
                case "instance": replacement[0] = Copy(current[0], instance: "not-bound"); break;
                case "definition": replacement[0] = Copy(current[0], definition: "other-definition"); break;
                case "order": replacement = current.Reverse().ToArray(); break;
                case "level": replacement[0] = Copy(current[0], level: 3); break;
                case "growth": replacement[0] = Copy(current[0], growth: 8); break;
                case "remove": replacement = current.Take(1).ToArray(); break;
                case "add": replacement = current.Concat(new[] { Copy(current[0], instance: "new", order: 2) }).ToArray(); break;
                case "duplicate": replacement[1] = current[0]; break;
                case "charges": replacement[1] = current[1].WithRuntime(5, 0, 0, AbilityMemory.Empty); break;
                case "charge-kind": replacement[1] = current[1].WithRuntime(null, 0, 0, AbilityMemory.Empty); break;
                case "null": replacement[0] = null; break;
            }
            string before = Snapshot(state.Duel.Player(Player).Relics);
            var result = fixture.Engine.UpdateRelicRuntime(state, state.Revision, Player, replacement);
            Assert.AreEqual(CardDuelError.InvalidRelicState, result.Error); Assert.AreSame(state, result.State);
            Assert.IsEmpty(result.RuleEvents); Assert.IsEmpty(result.Events); Assert.AreEqual(before, Snapshot(state.Duel.Player(Player).Relics));
            Assert.AreEqual(3, state.Duel.Player(Player).Energy.Energy); Assert.AreEqual(8, state.Zone(CardZoneKinds.Hand, Player).Cards.Count);
            var team = fixture.Engine.Duel.UpdateRelicRuntime(state.Duel, state.Duel.Revision, Player, replacement);
            Assert.AreEqual(CardDuelError.InvalidRelicState, team.Error); Assert.AreSame(state.Duel, team.State); Assert.IsEmpty(team.RuleEvents);
        }

        [Test] public void RuntimeAuthorityRejectsUnknownOwnersAndChecksRevisionBeforeEnumeratingInput()
        {
            var fixture = new Fixture(); var state = fixture.State; var items = Updated(state.Duel.Player(Player).Relics);
            foreach (var owner in new[] { Enemy, new ParticipantId("unknown") })
            {
                var rejected = fixture.Engine.UpdateRelicRuntime(state, state.Revision, owner, items);
                Assert.AreEqual(CardDuelError.InvalidRelicState, rejected.Error); Assert.AreSame(state, rejected.State);
            }
            var lazyFailure = new[] { 0 }.Select<int, BattleRelicState>(_ => { throw new InvalidOperationException("Stale input must not be enumerated."); });
            var stale = fixture.Engine.UpdateRelicRuntime(state, state.Revision + 1, Player, lazyFailure);
            Assert.AreEqual(CardDuelError.StaleRevision, stale.Error); Assert.AreSame(state, stale.State);
            var teamStale = fixture.Engine.Duel.UpdateRelicRuntime(state.Duel, state.Duel.Revision + 1, Player, lazyFailure);
            Assert.AreEqual(CardDuelError.StaleRevision, teamStale.Error); Assert.AreSame(state.Duel, teamStale.State);
        }

        [Test] public void DirectTeamUpdateChangesOnlyItsOwnersRuntime()
        {
            var fixture = new Fixture(); var before = fixture.State.Duel;
            var updated = fixture.Engine.Duel.UpdateRelicRuntime(before, before.Revision, Ally, Updated(before.Player(Ally).Relics));
            Assert.IsTrue(updated.Accepted); Assert.AreEqual(before.Revision + 1, updated.State.Revision);
            Assert.AreSame(before.Combat, updated.State.Combat); Assert.IsEmpty(updated.RuleEvents); Assert.IsEmpty(updated.State.Actions);
            Assert.AreEqual(Snapshot(before.Player(Player).Relics), Snapshot(updated.State.Player(Player).Relics));
            Assert.AreEqual(9, updated.State.Player(Ally).Relics.Relics[0].Memory.CounterValue("persistent"));
            Assert.AreEqual(2, before.Player(Ally).Relics.Relics[0].Memory.CounterValue("persistent"));
        }

        [Test] public void ComponentCodecRestoresNonDefaultRuntimeAgainstTheOriginalDefinitionBinding()
        {
            var binding = Loadout(); var runtime = binding.WithRuntime(Updated(binding)); var json = Snapshot(runtime);
            var restored = BattleRelicSnapshotCodec.Deserialize(json, binding);
            Assert.AreEqual(json, Snapshot(restored)); Assert.AreEqual(binding.Fingerprint, restored.Fingerprint);
            Assert.AreSame(binding.Definition(binding.Relics[0].InstanceId), restored.Definition(binding.Relics[0].InstanceId));
            Assert.AreEqual(2, restored.Relics[1].RemainingCharges); Assert.AreEqual(3, restored.Relics[1].UsesThisRound);
            Assert.AreEqual(2, restored.Relics[1].CooldownRounds); Assert.AreEqual(10, restored.Relics[1].Memory.CounterValue("persistent"));
            Assert.AreEqual(6, restored.Relics[1].Memory.CounterValue("round"));
            Assert.IsTrue(restored.Relics.All(item => item.Level == 2 && item.Growth == 7));
            Assert.AreEqual(3, binding.Relics[1].RemainingCharges); Assert.AreEqual(0, binding.Relics[1].UsesThisRound);
            var empty = new BattleRelicLoadout(Player);
            Assert.IsEmpty(BattleRelicSnapshotCodec.Deserialize(Snapshot(empty), empty).Relics);
        }

        [Test] public void RestoredComponentCanBeAppliedWithoutRewindingCardsCombatOrActions()
        {
            var fixture = new Fixture(); fixture.Do(CardDuelAction.Start); fixture.Do(CardDuelAction.Discard, cards: 1);
            var before = fixture.State; var binding = before.Duel.Player(Player).Relics;
            var restored = BattleRelicSnapshotCodec.Deserialize(Snapshot(binding.WithRuntime(Updated(binding))), binding);
            var result = fixture.Engine.UpdateRelicRuntime(before, before.Revision, Player, restored.Relics);
            Assert.IsTrue(result.Accepted); Assert.AreSame(before.Duel.Combat, result.State.Duel.Combat);
            Assert.AreEqual(before.Duel.Actions.Count, result.State.Duel.Actions.Count); Assert.AreEqual(before.DiscardsRemaining, result.State.DiscardsRemaining);
            Assert.AreEqual(2, result.State.Duel.Player(Player).Energy.Energy);
            foreach (var zone in before.Table.Zones) CollectionAssert.AreEqual(zone.Cards, result.State.Table.GetZone(zone.Id).Cards);
            CollectionAssert.AreEqual(before.Random.StateBytes, result.State.Random.StateBytes);
        }

        [Test] public void CodecRejectsAnotherOwnerOrChangedDefinitionEvenWhenInstanceAndDefinitionIdsMatch()
        {
            var binding = Loadout(); var json = Snapshot(binding.WithRuntime(Updated(binding)));
            var foreign = new BattleRelicLoadout(Ally, Setups(Player));
            Assert.Throws<SnapshotException>(() => BattleRelicSnapshotCodec.Deserialize(json, foreign));
            var changed = new BattleRelicLoadout(Player, new[] { Setup(Player.Value + "/passive", amount: 501), Setup(Player.Value + "/charged", true) });
            Assert.AreNotEqual(binding.Fingerprint, changed.Fingerprint);
            Assert.Throws<SnapshotException>(() => BattleRelicSnapshotCodec.Deserialize(json, changed));
            var reversed = new BattleRelicLoadout(Player, Setups(Player).Reverse());
            Assert.AreNotEqual(binding.Fingerprint, reversed.Fingerprint);
            Assert.Throws<SnapshotException>(() => BattleRelicSnapshotCodec.Deserialize(json, reversed));
        }

        [TestCase("schema")] [TestCase("owner")] [TestCase("fingerprint")] [TestCase("duplicate")]
        [TestCase("required")] [TestCase("required-charges")] [TestCase("charges")] [TestCase("negative-uses")]
        [TestCase("memory-version")] [TestCase("marks")]
        [TestCase("tail")] [TestCase("malformed")]
        public void CodecRejectsTamperingWithoutChangingTheSuppliedBinding(string variant)
        {
            var binding = Loadout(); var json = Snapshot(binding.WithRuntime(Updated(binding))); string invalid = json;
            switch (variant)
            {
                case "schema": invalid = json.Replace("\"SchemaVersion\":1", "\"SchemaVersion\":999"); break;
                case "owner": invalid = json.Replace(Player.Value, Ally.Value); break;
                case "fingerprint": invalid = json.Replace(binding.Fingerprint, new string('0', binding.Fingerprint.Length)); break;
                case "duplicate": invalid = json.Replace(Player.Value + "\\/charged", Player.Value + "\\/passive")
                    .Replace(Player.Value + "/charged", Player.Value + "/passive"); break;
                case "required": invalid = json.Replace("\"UsesThisRound\":", "\"Unexpected\":"); break;
                case "required-charges": invalid = json.Replace("\"RemainingCharges\":", "\"Unexpected\":"); break;
                case "charges": invalid = json.Replace("\"RemainingCharges\":2", "\"RemainingCharges\":99"); break;
                case "negative-uses": invalid = json.Replace("\"UsesThisRound\":2", "\"UsesThisRound\":-1"); break;
                case "memory-version": invalid = json.Replace("\"Version\":1", "\"Version\":99"); break;
                case "marks": invalid = json.Replace("\"Marks\":[]", "\"Marks\":[{\"Key\":\"mark\",\"Target\":{\"Kind\":2,\"Id\":\"relic-enemy\"},\"Scope\":2}]"); break;
                case "tail": invalid = json + " {}"; break;
                case "malformed": invalid = "{broken"; break;
            }
            Assert.AreNotEqual(json, invalid, variant + " must actually modify the serialized input");
            string before = Snapshot(binding);
            Assert.Throws<SnapshotException>(() => BattleRelicSnapshotCodec.Deserialize(invalid, binding));
            Assert.AreEqual(before, Snapshot(binding));
        }

        [TestCase(" []")] [TestCase(" null")] [TestCase(" true")] [TestCase(" 12")]
        [TestCase(" garbage")] [TestCase("}")] [TestCase("\u00a0")]
        public void CodecRejectsAdditionalValuesOrGarbageAfterTheSnapshot(string suffix)
        {
            var binding = Loadout();
            Assert.Throws<SnapshotException>(() => BattleRelicSnapshotCodec.Deserialize(Snapshot(binding) + suffix, binding));
        }

        [Test] public void CodecAcceptsJsonWhitespaceAndEscapedDelimitersInsideMemoryKeys()
        {
            var binding = Loadout(); var items = Updated(binding);
            var memory = items[0].Memory.WithCounter("escaped \\\" } [ { \\ \n 中文", 11, MemoryScope.Encounter);
            items[0] = items[0].WithRuntime(null, 2, 1, memory);
            string json = Snapshot(binding.WithRuntime(items));
            var restored = BattleRelicSnapshotCodec.Deserialize(" \t\r\n" + json + "\n\r\t ", binding);
            Assert.AreEqual(json, Snapshot(restored));
        }

        [Test] public void ExportKeepsEncounterCountersAndPersistentFieldsWithoutLeakingRoundCountersOrGrowing()
        {
            var binding = Loadout().WithRuntime(Updated(Loadout())); var exported = binding.ExportRunRelics().ToArray();
            Assert.AreEqual(2, exported.Length);
            for (int index = 0; index < exported.Length; index++)
            {
                var saved = exported[index]; var runtime = binding.Relics[index];
                Assert.AreEqual(runtime.InstanceId, saved.Id); Assert.AreEqual(runtime.DefinitionId, saved.DefinitionId);
                Assert.AreEqual(runtime.Level, saved.Level); Assert.AreEqual(runtime.Growth, saved.Growth);
                Assert.AreEqual(runtime.RemainingCharges, saved.Charges); Assert.AreEqual(runtime.CooldownRounds, saved.CooldownRounds);
                Assert.AreEqual(runtime.Memory.CounterValue("persistent"), saved.Memory.CounterValue("persistent"));
                Assert.IsTrue(saved.Memory.Counters.All(counter => counter.Scope == MemoryScope.Encounter));
                Assert.AreEqual(0, saved.Memory.CounterValue("round")); Assert.IsEmpty(saved.Memory.Marks);
                Assert.AreEqual(5 + index, runtime.Memory.CounterValue("round"));
            }
            var rebound = new BattleRelicLoadout(Player, exported.Select(item => new BattleRelicSetup(binding.Definition(item.Id), item)));
            Assert.AreEqual(binding.Fingerprint, rebound.Fingerprint); Assert.IsTrue(rebound.Relics.All(item => item.UsesThisRound == 0));
            Assert.AreEqual(9, rebound.Relics[0].Memory.CounterValue("persistent")); Assert.AreEqual(7, rebound.Relics[0].Growth);
        }

        [Test] public void BindingAndStateRejectDuplicateIdentityInvalidChargesAndEntityMarks()
        {
            var first = Setup("duplicate");
            Assert.Catch<ArgumentException>(() => new BattleRelicLoadout(Player, new[] { first, first }));
            Assert.Catch<ArgumentException>(() => new BattleRelicLoadout(Player, new BattleRelicSetup[] { null }));
            Assert.Catch<ArgumentException>(() => new BattleRelicLoadout(default(ParticipantId)));
            Assert.Catch<ArgumentException>(() => new BattleRelicSetup(first.Definition, new RunRelic("mismatch", "unknown")));
            Assert.Catch<ArgumentException>(() => new BattleRelicSetup(first.Definition, new RunRelic("too-high", first.Definition.Id, level: 5)));
            Assert.Catch<ArgumentException>(() => new BattleRelicSetup(first.Definition, new RunRelic("charged", first.Definition.Id, charges: 1)));
            Assert.Catch<ArgumentException>(() => new BattleRelicSetup(first.Definition, new RunRelic("round-memory", first.Definition.Id, memory: RuntimeMemory())));
            var marked = new AbilityMemory(Array.Empty<AbilityCounter>(), new[] { new AbilityMark("target", EntityRef.Participant(Enemy), MemoryScope.Encounter) });
            Assert.Catch<ArgumentException>(() => new BattleRelicState("marked", "passive", Player, 0, 1, 0, memory: marked));
            Assert.Catch<ArgumentException>(() => new BattleRelicState("negative", "passive", Player, 0, 1, 0, usesThisRound: -1));
            Assert.Catch<ArgumentException>(() => new BattleRelicState("negative", "passive", Player, 0, 1, 0, cooldownRounds: -1));
        }

        [Test] public void DifferentPlayersCannotBindTheSamePersistentRelicInstance()
        {
            var shared = new[] { Setup("same-persistent-instance") };
            var engine = new TeamBattleEngine(new BattleRules("duplicate-owner.1", 10));
            Assert.Catch<ArgumentException>(() => engine.Create("duplicate-owner", new[] {
                new BattlePlayerSetup(Player, new CombatantDefinition("hero", 100), relics: shared),
                new BattlePlayerSetup(Ally, new CombatantDefinition("ally", 100), relics: shared)
            }, new[] { new BattleEnemySetup(Enemy, new CombatantDefinition("enemy", 100), 2) }));
        }
    }
}
