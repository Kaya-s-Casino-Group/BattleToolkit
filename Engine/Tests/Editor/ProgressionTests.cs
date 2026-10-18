using System;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class ProgressionTests
    {
        private static readonly ParticipantId Player = new ParticipantId("player");
        private static ProgressionUnlock BaseDeck => new ProgressionUnlock(UnlockKind.StartingDeck, "deck.basic");
        private static ProgressionUnlock Relic => new ProgressionUnlock(UnlockKind.Relic, "joker.steady");
        private static ProgressionDefinition Definition(long firstCost = 5) => new ProgressionDefinition("meta.1", new[] { "shards", "tokens" },
            new[] { BaseDeck, Relic, new ProgressionUnlock(UnlockKind.Card, "joker.steady") },
            new[] { new ProgressionUpgradeDefinition("starting-gold", "shards", new[] { firstCost, 10L }, Relic) }, new[] { BaseDeck });
        private static RunSettlement Settlement(string id = "run-1", long shards = 20, RunOutcome outcome = RunOutcome.Won, string mode = "draw") =>
            new RunSettlement(id, Player, outcome, mode, "flow.1", "deck.1", 500, outcome == RunOutcome.Won ? 4 : 2, 4,
                new ProgressionReward(new[] { new ProgressionCurrency("shards", shards) }, new[] { Relic }));
        private static PlayerProgressionState Copy(PlayerProgressionState source, ProgressionCurrency[] wallet = null,
            ProgressionUpgradeLevel[] upgrades = null, ProgressionUnlock[] unlocks = null, RunSettlement[] settlements = null, long? revision = null) =>
            new PlayerProgressionState(source.PlayerId, revision ?? source.Revision, source.RulesVersion, source.RulesFingerprint,
                wallet ?? source.Currencies.ToArray(), unlocks ?? source.Unlocks.ToArray(), upgrades ?? source.Upgrades.ToArray(), settlements ?? source.Settlements.ToArray());

        [Test] public void NewProfileHasOnlyBaseUnlocksAndNoRunInstances()
        {
            var definition = Definition(); var state = new ProgressionEngine(definition).Create(Player);
            ProgressionValidator.Validate(state, definition);
            Assert.AreEqual(0, state.Revision); Assert.AreEqual(0, state.Balance("shards")); Assert.AreEqual(0, state.UpgradeLevel("starting-gold"));
            Assert.IsTrue(state.IsUnlocked(BaseDeck)); Assert.IsFalse(state.IsUnlocked(Relic)); Assert.AreEqual(0, state.Statistics.RunsFinished);
            Assert.IsFalse(typeof(PlayerProgressionState).GetProperties().Any(p => p.Name == "Deck" || p.Name == "Relics" || p.Name == "Encounter"));
        }

        [Test] public void SettlementGrantsOnceAndStatisticsAreDerivedWithoutDoubleCounting()
        {
            var engine = new ProgressionEngine(Definition()); var initial = engine.Create(Player); var first = engine.Settle(initial, Settlement(), 0, Player);
            Assert.AreEqual(20, first.Balance("shards")); Assert.IsTrue(first.IsUnlocked(Relic)); Assert.AreEqual(1, first.Statistics.Wins);
            Assert.AreSame(first, engine.Settle(first, Settlement(), 0, Player));
            var lost = engine.Settle(first, Settlement("run-2", 3, RunOutcome.Lost), first.Revision, Player);
            var abandoned = engine.Settle(lost, Settlement("run-3", 0, RunOutcome.Abandoned, "holdem"), lost.Revision, Player);
            Assert.AreEqual(23, abandoned.Balance("shards")); Assert.AreEqual(3, abandoned.Statistics.RunsFinished);
            Assert.AreEqual(1, abandoned.Statistics.Losses); Assert.AreEqual(1, abandoned.Statistics.Abandoned);
            Assert.AreEqual(2, abandoned.StatisticsForMode("draw").RunsFinished); Assert.AreEqual(500, abandoned.Statistics.BestScore);
            Assert.AreEqual(4, abandoned.Statistics.MostFloorsCompleted); Assert.AreEqual(2, abandoned.Unlocks.Count);
            Assert.AreEqual(0, initial.Revision); Assert.IsEmpty(initial.Settlements);
        }

        [Test] public void DuplicateRunWithChangedRewardOrOutcomeCannotReplaceReceipt()
        {
            var engine = new ProgressionEngine(Definition()); var state = engine.Settle(engine.Create(Player), Settlement(), 0, Player);
            Assert.Throws<InvalidOperationException>(() => engine.Settle(state, Settlement(shards: 21), 1, Player));
            Assert.Throws<InvalidOperationException>(() => engine.Settle(state, Settlement(outcome: RunOutcome.Lost), 1, Player));
            Assert.AreEqual(20, state.Balance("shards")); Assert.AreEqual(1, state.Statistics.RunsFinished);
        }

        [Test] public void SettlementRetriesIgnoreRewardOrderingButKeepTypedUnlockIdentities()
        {
            var definition = Definition(); var engine = new ProgressionEngine(definition);
            var reward = new ProgressionReward(new[] { new ProgressionCurrency("shards", 8), new ProgressionCurrency("tokens", 3) },
                new[] { Relic, new ProgressionUnlock(UnlockKind.Card, Relic.Id) });
            var original = new RunSettlement("run", Player, RunOutcome.Won, "draw", "flow", "deck", 10, 1, 1, reward);
            var state = engine.Settle(engine.Create(Player), original, 0, Player);
            var reordered = new RunSettlement("run", Player, RunOutcome.Won, "draw", "flow", "deck", 10, 1, 1,
                new ProgressionReward(reward.Currencies.Reverse(), reward.Unlocks.Reverse()));
            Assert.AreSame(state, engine.Settle(state, reordered, 0, Player));
            Assert.IsTrue(state.IsUnlocked(new ProgressionUnlock(UnlockKind.Card, Relic.Id)));
            Assert.AreEqual(3, state.Unlocks.Count);
        }

        [Test] public void PreRenameProgressionSnapshotPreservesRelicUnlockFingerprintAndSettlementIdempotency()
        {
            var definition = Definition();
            Assert.AreEqual("a3545eb42c95da1330adfc4d66e4e1758a18ccab3d7f46c5b7abb8115b9aa18b", definition.Fingerprint);
            var restored = ProgressionSnapshotCodec.Deserialize(LegacyRelicSnapshots.Progression, definition);
            Assert.IsTrue(restored.IsUnlocked(Relic)); Assert.IsTrue(restored.IsUnlocked(BaseDeck));
            Assert.AreEqual(15, restored.Balance("shards")); Assert.AreEqual(1, restored.UpgradeLevel("starting-gold"));
            Assert.AreEqual(LegacyRelicSnapshots.Progression, ProgressionSnapshotCodec.Serialize(restored, definition));
            var engine = new ProgressionEngine(definition); Assert.AreSame(restored, engine.Settle(restored, Settlement(), 0, Player));
            var upgraded = engine.PurchaseUpgrade(restored, "starting-gold", restored.Revision, Player);
            Assert.AreEqual(5, upgraded.Balance("shards")); Assert.AreEqual(2, upgraded.UpgradeLevel("starting-gold"));
            Assert.IsTrue(upgraded.IsUnlocked(Relic)); Assert.AreEqual(1, upgraded.Statistics.RunsFinished);
        }

        [Test] public void PermanentUpgradeSpendsMetaCurrencyAndPersistsAcrossSnapshot()
        {
            var definition = Definition(); var engine = new ProgressionEngine(definition); var earned = engine.Settle(engine.Create(Player), Settlement(), 0, Player);
            var purchased = engine.PurchaseUpgrade(earned, "starting-gold", 1, Player);
            Assert.AreEqual(1, purchased.UpgradeLevel("starting-gold")); Assert.AreEqual(15, purchased.Balance("shards"));
            var restored = ProgressionSnapshotCodec.Deserialize(ProgressionSnapshotCodec.Serialize(purchased, definition), definition);
            var capped = engine.PurchaseUpgrade(restored, "starting-gold", restored.Revision, Player);
            Assert.AreEqual(2, capped.UpgradeLevel("starting-gold")); Assert.AreEqual(5, capped.Balance("shards"));
            Assert.Throws<InvalidOperationException>(() => engine.PurchaseUpgrade(capped, "starting-gold", capped.Revision, Player));
            Assert.AreSame(capped, engine.Settle(capped, Settlement(), 0, Player));
            Assert.AreEqual(20, earned.Balance("shards")); Assert.AreEqual(0, earned.UpgradeLevel("starting-gold"));
        }

        [Test] public void UnknownLockedUnaffordableUnauthorizedAndStaleActionsAreAtomic()
        {
            var engine = new ProgressionEngine(Definition()); var initial = engine.Create(Player); var stranger = new ParticipantId("other");
            Assert.Throws<InvalidOperationException>(() => engine.PurchaseUpgrade(initial, "starting-gold", 0, Player));
            Assert.Throws<ArgumentException>(() => engine.PurchaseUpgrade(initial, "missing", 0, Player));
            Assert.Throws<InvalidOperationException>(() => engine.Settle(initial, Settlement(), 0, stranger));
            Assert.Throws<InvalidOperationException>(() => engine.Settle(initial, Settlement(), 1, Player));
            var foreign = new RunSettlement("foreign", stranger, RunOutcome.Won, "draw", "flow", "deck", 10, 1, 1, new ProgressionReward());
            Assert.Throws<StateValidationException>(() => engine.Settle(initial, foreign, 0, Player));
            var earned = engine.Settle(initial, Settlement(shards: 1), 0, Player);
            Assert.Throws<InvalidOperationException>(() => engine.PurchaseUpgrade(earned, "starting-gold", 1, Player));
            Assert.Throws<InvalidOperationException>(() => engine.PurchaseUpgrade(earned, "starting-gold", 0, Player));
            Assert.AreEqual(1, earned.Balance("shards")); Assert.AreEqual(1, earned.Revision); Assert.AreEqual(0, initial.Revision);
        }

        [Test] public void SnapshotRejectsForgedBalancesUnlocksLevelsLedgerAndRevision()
        {
            var definition = Definition(); var engine = new ProgressionEngine(definition); var state = engine.Settle(engine.Create(Player), Settlement(), 0, Player);
            Assert.Throws<StateValidationException>(() => ProgressionValidator.Validate(Copy(state,
                wallet: new[] { new ProgressionCurrency("shards", 999), new ProgressionCurrency("tokens", 0) }), definition));
            Assert.Throws<StateValidationException>(() => ProgressionValidator.Validate(Copy(state, upgrades: new[] { new ProgressionUpgradeLevel("starting-gold", 3) }), definition));
            Assert.Throws<StateValidationException>(() => ProgressionValidator.Validate(Copy(state, unlocks: new[] { BaseDeck }), definition));
            Assert.Throws<StateValidationException>(() => ProgressionValidator.Validate(Copy(state, settlements: new[] { Settlement(), Settlement() }), definition));
            Assert.Throws<StateValidationException>(() => ProgressionValidator.Validate(Copy(state, revision: 10), definition));
            var json = ProgressionSnapshotCodec.Serialize(state, definition);
            const string amount = "\"Amount\":20";
            int walletAmount = json.IndexOf(amount, StringComparison.Ordinal); Assert.GreaterOrEqual(walletAmount, 0);
            var forgedWallet = json.Remove(walletAmount, amount.Length).Insert(walletAmount, "\"Amount\":999");
            Assert.Throws<SnapshotException>(() => ProgressionSnapshotCodec.Deserialize(forgedWallet, definition));
            Assert.Throws<SnapshotException>(() => ProgressionSnapshotCodec.Deserialize(json.Replace("\"Currencies\":[", "\"Unexpected\":["), definition));
        }

        [Test] public void SnapshotPinsSchemaAndRulesIncludingCostChangesWithSameVersion()
        {
            var definition = Definition(); var engine = new ProgressionEngine(definition); var state = engine.Settle(engine.Create(Player), Settlement(), 0, Player);
            var json = ProgressionSnapshotCodec.Serialize(state, definition); var restored = ProgressionSnapshotCodec.Deserialize(json, definition);
            Assert.AreEqual(json, ProgressionSnapshotCodec.Serialize(restored, definition));
            Assert.Throws<SnapshotException>(() => ProgressionSnapshotCodec.Deserialize(json.Replace("\"SchemaVersion\":1", "\"SchemaVersion\":999"), definition));
            Assert.Throws<SnapshotException>(() => ProgressionSnapshotCodec.Deserialize(json, Definition(6)));
            Assert.Throws<SnapshotException>(() => ProgressionSnapshotCodec.Deserialize("{broken", definition));
            Assert.Throws<SnapshotException>(() => ProgressionSnapshotCodec.Deserialize(json.Replace("\"Reward\":{", "\"NotReward\":{"), definition));
        }

        [Test] public void CurrencyOverflowAndUnknownRewardsDoNotModifyProfile()
        {
            var definition = Definition(); var engine = new ProgressionEngine(definition); var initial = engine.Create(Player);
            var full = engine.Settle(initial, Settlement(shards: long.MaxValue), 0, Player);
            Assert.Throws<OverflowException>(() => engine.Settle(full, Settlement("next", 1), 1, Player));
            var unknown = new RunSettlement("unknown", Player, RunOutcome.Won, "draw", "flow", "deck", 1, 1, 1,
                new ProgressionReward(new[] { new ProgressionCurrency("run-gold", 10) }));
            Assert.Throws<StateValidationException>(() => engine.Settle(initial, unknown, 0, Player));
            var unknownUnlock = new RunSettlement("unknown-unlock", Player, RunOutcome.Won, "draw", "flow", "deck", 1, 1, 1,
                new ProgressionReward(unlocks: new[] { new ProgressionUnlock(UnlockKind.Relic, "unknown") }));
            Assert.Throws<StateValidationException>(() => engine.Settle(initial, unknownUnlock, 0, Player));
            Assert.AreEqual(long.MaxValue, full.Balance("shards")); Assert.IsEmpty(initial.Settlements);
        }

        [Test] public void DefinitionCopiesInputsAndFingerprintsCatalogSemantics()
        {
            var costs = new[] { 5L, 10L }; var unlocks = new[] { BaseDeck, Relic };
            var definition = new ProgressionDefinition("meta", new[] { "shards" }, unlocks,
                new[] { new ProgressionUpgradeDefinition("upgrade", "shards", costs) }, new[] { BaseDeck });
            costs[0] = 999; unlocks[1] = BaseDeck;
            Assert.AreEqual(5, definition.Upgrade("upgrade").LevelCosts[0]); Assert.IsTrue(definition.Unlocks.Contains(Relic));
            var reordered = new ProgressionDefinition("meta", new[] { "shards" }, new[] { Relic, BaseDeck }, definition.Upgrades, new[] { BaseDeck });
            Assert.AreEqual(definition.Fingerprint, reordered.Fingerprint);
            Assert.Throws<ArgumentException>(() => new ProgressionDefinition("meta", new[] { "x", "x" }, new ProgressionUnlock[0]));
            Assert.Throws<ArgumentException>(() => new ProgressionDefinition("meta", new[] { "x" }, new[] { BaseDeck }, initialUnlocks: new[] { Relic }));
            Assert.Throws<ArgumentException>(() => new ProgressionReward(unlocks: new[] { Relic, Relic }));
            Assert.Throws<ArgumentException>(() => new RunSettlement("x", Player, RunOutcome.Won, "draw", "flow", "deck", 1, 1, 4, new ProgressionReward()));
        }

        [Test] public void RealRunCompletionProducesSeparateMetaSettlementAndActiveRunNeedsExplicitAbandonment()
        {
            var sample = ExampleRunContent.Create(true); var offers = sample.Definition.ShopPool.Select(sample.Offer).ToArray();
            var relics = offers.Where(o => o.Kind == RunOfferKind.Relic).Select(o => o.RelicId).Distinct().Select(sample.Relic);
            var definition = new RunDefinition("one-floor", sample.Definition.Scoring, new[] { new RunStageDefinition("battle", 1, 6) },
                sample.Definition.ShopPool, sample.Definition.RewardPool);
            var content = new RunContent(sample.CardsAndAbilities, definition, relics, offers, sample.StartingDeckVersion, sample.StartingDeck);
            var adapter = new DrawRunAdapter(ExampleRunContent.Runtime(true)); var engine = new RunEngine<DrawState, DrawCommand, DrawEvent>(content, adapter);
            var state = engine.Create("real-run", Player, HmacDeckShuffler.FromSeed(new byte[32]));
            var reward = new ProgressionReward(new[] { new ProgressionCurrency("shards", 7) }, new[] { Relic });
            Assert.Throws<InvalidOperationException>(() => RunProgressionSettlement.Create(state, content, adapter, reward));
            var abandoned = RunProgressionSettlement.Create(state, content, adapter, new ProgressionReward(), abandoned: true);
            Assert.AreEqual(RunOutcome.Abandoned, abandoned.Outcome);
            state = engine.ExecuteSystem(state, new RunCommand<DrawCommand>("start", state.Revision, RunAction.StartRun)).State;
            for (int i = 0; state.Phase != RunPhase.Completed; i++)
            {
                Assert.Less(i, 10); RunCommand<DrawCommand> command;
                if (state.Phase == RunPhase.Encounter)
                {
                    var hand = state.Encounter.World.Zones.Single(z => z.Kind == CardZoneKinds.Hand).Cards;
                    command = new RunCommand<DrawCommand>("play-" + i, state.Revision, RunAction.PlayerEncounter,
                        encounterCommand: new DrawCommand("play", state.Encounter.Revision, DrawAction.Play, hand.Take(1)));
                }
                else command = new RunCommand<DrawCommand>("next-" + i, state.Revision,
                    state.Phase == RunPhase.Reward ? RunAction.SkipReward : RunAction.CompleteNode);
                var result = engine.Execute(state, command, Player); Assert.IsTrue(result.Accepted, result.Error.ToString()); state = result.State;
            }
            var settlement = RunProgressionSettlement.Create(state, content, adapter, reward); var meta = new ProgressionEngine(Definition());
            var profile = meta.Settle(meta.Create(Player), settlement, 0, Player);
            Assert.AreEqual(RunOutcome.Won, settlement.Outcome); Assert.AreEqual(1, settlement.CompletedFloors);
            Assert.AreEqual(state.Scores.Sum(s => s.Total), profile.Statistics.BestScore);
            Assert.AreEqual(7, profile.Balance("shards")); Assert.IsTrue(profile.IsUnlocked(Relic));
            Assert.Throws<InvalidOperationException>(() => RunProgressionSettlement.Create(state, content, adapter, reward, abandoned: true));
            Assert.AreEqual(RunPhase.Completed, state.Phase);
        }
    }
}
