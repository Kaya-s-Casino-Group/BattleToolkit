using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    /// <summary>Executable example content for integration/testing; numbers are not final game balance.</summary>
    public static class ExampleRunContent
    {
        public static readonly AbilityDefinitionId CallEnergy = new AbilityDefinitionId("example.call-energy");
        public static readonly AbilityDefinitionId SwapCard = new AbilityDefinitionId("example.swap-card");
        public static readonly AbilityDefinitionId PeekDeck = new AbilityDefinitionId("example.peek-deck");
        /// <summary>Three independent branching floors followed by a separate boss floor.</summary>
        public static RunContent CreateFourFloors(bool drawMode = true)
        {
            var sample = Create(drawMode); var floors = new List<RunFloorDefinition>();
            for (int i = 1; i <= 3; i++)
            {
                var tree = new RunNodeTree("example.floor." + i + ".1", "opening", new RunNodeDefinition[] {
                    new RunEncounterNodeDefinition("opening", new RunStageDefinition("opening", 200 * i, 6), new[] { "reward" }),
                    new RunRewardNodeDefinition("reward", new[] { "normal", "elite" }),
                    new RunEncounterNodeDefinition("normal", new RunStageDefinition("normal", 500 * i, 8), new[] { "shop" }),
                    new RunEncounterNodeDefinition("elite", new RunStageDefinition("elite", 800 * i, 8, clearGold: 10 * i), new[] { "elite-reward" }, RunNodeKind.Elite),
                    new RunRewardNodeDefinition("elite-reward", new[] { "shop" }),
                    new RunShopNodeDefinition("shop")
                });
                floors.Add(new RunFloorDefinition("floor-" + i, tree));
            }
            floors.Add(new RunFloorDefinition("boss-floor", new RunNodeTree("example.boss-floor.1", "boss", new RunNodeDefinition[] {
                new RunEncounterNodeDefinition("boss", new RunStageDefinition("boss", 5000, 10, clearGold: 15), new[] { "final-reward" }, RunNodeKind.Boss),
                new RunRewardNodeDefinition("final-reward")
            }), RunFloorKind.Boss));
            var offers = sample.Definition.ShopPool.Select(sample.Offer).ToArray();
            var relics = offers.Where(o => o.Kind == RunOfferKind.Relic).Select(o => o.RelicId)
                .Concat(sample.Definition.StartingRelics).Distinct().Select(sample.Relic);
            var definition = new RunDefinition(drawMode ? "run.draw.four-floors.1" : "run.holdem.four-floors.1", sample.Definition.Scoring,
                floors, sample.Definition.ShopPool, sample.Definition.RewardPool, sample.Definition.StartingRelics);
            return new RunContent(sample.CardsAndAbilities, definition, relics, offers, sample.StartingDeckVersion, sample.StartingDeck);
        }
        /// <summary>A normal/elite branch that merges at a shop before the final boss.</summary>
        public static RunContent CreateBranching(bool drawMode = true)
        {
            var sample = Create(drawMode);
            var tree = new RunNodeTree("example.branching.1", "opening", new RunNodeDefinition[] {
                new RunEncounterNodeDefinition("opening", new RunStageDefinition("opening", 200, 6), new[] { "opening-reward" }),
                new RunRewardNodeDefinition("opening-reward", new[] { "normal", "elite" }),
                new RunEncounterNodeDefinition("normal", new RunStageDefinition("normal", 500, 8), new[] { "shop" }),
                new RunEncounterNodeDefinition("elite", new RunStageDefinition("elite", 800, 8, clearGold: 10), new[] { "elite-reward" }, RunNodeKind.Elite),
                new RunRewardNodeDefinition("elite-reward", new[] { "shop" }),
                new RunShopNodeDefinition("shop", new[] { "boss" }),
                new RunEncounterNodeDefinition("boss", new RunStageDefinition("boss", 2000, 8, clearGold: 15), new[] { "final-reward" }, RunNodeKind.Boss),
                new RunRewardNodeDefinition("final-reward")
            });
            var offers = sample.Definition.ShopPool.Select(sample.Offer).ToArray();
            var relics = offers.Where(o => o.Kind == RunOfferKind.Relic).Select(o => o.RelicId)
                .Concat(sample.Definition.StartingRelics).Distinct().Select(sample.Relic);
            var definition = new RunDefinition(drawMode ? "run.draw.branching.1" : "run.holdem.branching.1", sample.Definition.Scoring,
                tree, sample.Definition.ShopPool, sample.Definition.RewardPool, sample.Definition.StartingRelics);
            return new RunContent(sample.CardsAndAbilities, definition, relics, offers, sample.StartingDeckVersion, sample.StartingDeck);
        }
        public static RunContent Create(bool drawMode = false)
        {
            var definitions = new[] {
                new AbilityDefinition(CallEnergy, AbilityMechanism.Triggered, AbilityModes.All, new[] { (drawMode ? DrawTimings.AfterScoring : HoldemTiming.AfterBettingAction) },
                    new[] { new AbilityCondition(ConditionKind.OwnerIsEventActor) }, requiredAction: drawMode ? null : HoldemActionKeys.Call),
                new AbilityDefinition(SwapCard, AbilityMechanism.Activated, AbilityModes.Solo, new[] { (drawMode ? DrawTimings.BeforeAction : HoldemTiming.BeforeBettingAction) },
                    new[] { new AbilityCondition(ConditionKind.OwnerIsCurrentActor), new AbilityCondition(ConditionKind.UsesThisRoundBelow, 1) }),
                new AbilityDefinition(PeekDeck, AbilityMechanism.Activated, AbilityModes.Solo, new[] { (drawMode ? DrawTimings.BeforeAction : HoldemTiming.BeforeBettingAction) },
                    new[] { new AbilityCondition(ConditionKind.OwnerIsCurrentActor), new AbilityCondition(ConditionKind.UsesThisRoundBelow, 1) })
            };
            var catalog = new ContentCatalog(drawMode ? "roguelike.draw.example.2" : "roguelike.holdem.example.2", StandardDeck.Cards(), definitions, new StatusDefinitionId[0]);
            var relics = new[] {
                new RelicDefinition("steady", new[] { new ScoreEffect(ScoreTiming.AfterCards, ScoreOperation.AddMultiplier, 4, perLevel: 2) }),
                new RelicDefinition("double", new[] { new ScoreEffect(ScoreTiming.AfterCards, ScoreOperation.MultiplyMultiplier, 2, perLevel: 0.5m) }),
                new RelicDefinition("growing", new[] { new ScoreEffect(ScoreTiming.AfterCards, ScoreOperation.AddPoints, 10, perGrowth: 5) }, growthPerScoredRound: 1),
                new RelicDefinition("focus", new[] { new ScoreEffect(ScoreTiming.PerCard, ScoreOperation.AddPoints, 3, suit: Suit.Hearts) }, abilityId: CallEnergy),
                new RelicDefinition("exchange", new ScoreEffect[0], abilityId: SwapCard),
                new RelicDefinition("peek", new ScoreEffect[0], abilityId: PeekDeck)
            };
            var offers = relics.Select(j => new RunOfferDefinition("buy-" + j.Id, RunOfferKind.Relic, 5, relicId: j.Id)).Concat(new[] {
                new RunOfferDefinition("enhance", RunOfferKind.EnhanceCard, 3, amount: 10, multiplier: 1),
                new RunOfferDefinition("retrigger", RunOfferKind.EnhanceCard, 4, retriggers: 1),
                new RunOfferDefinition("duplicate", RunOfferKind.DuplicateCard, 3),
                new RunOfferDefinition("remove", RunOfferKind.RemoveCard, 2),
                new RunOfferDefinition("pair-level", RunOfferKind.UpgradeHand, 3, handCategory: HandCategory.OnePair),
                new RunOfferDefinition("gold", RunOfferKind.Gold, 4, amount: 3)
            }).ToArray();
            var definition = new RunDefinition(drawMode ? "run.draw.2" : "run.holdem-score.2", ScoreProfile.Default,
                new[] { new RunStageDefinition("opening", 200, 6), new RunStageDefinition("middle", 800, 8), new RunStageDefinition("final", 2000, 8) },
                offers.Select(o => o.Id), offers.Select(o => o.Id), new[] { "steady", "focus" });
            return new RunContent(catalog, definition, relics, offers, "standard52.1", StandardDeck.Cards().Select(card => card.Id));
        }
        public static AbilityRuntime Runtime(bool drawMode = false)
        {
            var swap = new Swap(drawMode ? CardZoneKinds.Hand : HoldemZoneKinds.HoleCards, drawMode ? (Func<AbilityWorld, int>)(w => 0) : ReservedHoldemCards);
            return new AbilityRuntime(new[] {
                new AbilityBehavior(CallEnergy, triggered: new Energy()),
                new AbilityBehavior(SwapCard, activated: swap, selection: swap, cost: new AbilityCost(Chips.Zero, 1)),
                new AbilityBehavior(PeekDeck, activated: new Peek(), cost: new AbilityCost(Chips.Zero, 1))
            });
        }
        private sealed class Energy : ITriggeredAbility
        {
            public IReadOnlyList<EffectIntent> Plan(AbilityContext c) => new EffectIntent[] {
                new ChangeResourceEffect(c.Ability.Id, c.Ability.OwnerId, ResourceKind.Energy, 1),
                new SetAbilityCounterEffect(c.Ability.Id, "calls", checked(c.Ability.Memory.CounterValue("calls") + 1), MemoryScope.Encounter)
            };
        }
        private static int ReservedHoldemCards(AbilityWorld world)
        {
            if (world.PhaseKey == HoldemAbilityAdapter.Phase(Street.Preflop)) return 8;
            if (world.PhaseKey == HoldemAbilityAdapter.Phase(Street.Flop)) return 4;
            return world.PhaseKey == HoldemAbilityAdapter.Phase(Street.Turn) ? 2 : 0;
        }
        private sealed class Swap : IActivatedAbility, IAbilitySelectionHandler
        {
            private readonly Func<AbilityWorld, int> reserved;
            private readonly string handKind;
            public Swap(string handKind, Func<AbilityWorld, int> reserved) { this.handKind = handKind; this.reserved = reserved; }
            public IReadOnlyList<EffectIntent> Plan(AbilityContext c)
            {
                if (c.State.Zones.Single(z => z.Kind == CardZoneKinds.Deck).Cards.Count <= reserved(c.State))
                    throw new System.ArgumentException("Insufficient cards for this mode's exchange policy.");
                var hole = c.State.Zones.Single(z => z.Kind == handKind && z.ControllerId == c.Ability.OwnerId);
                var spec = new SelectionSpec(SelectionTargetKind.Cards,
                    hole.Cards.Select((id, i) => new SelectionCandidate("choice-" + i, EntityRef.Card(id))), 1, 1);
                return new EffectIntent[] { new RequestSelectionEffect(c.Ability.Id, c.Ability.OwnerId, spec) };
            }
            public IReadOnlyList<EffectIntent> Resume(AbilityContext c, int step, SelectionResult selection)
            {
                if (step != 1 || selection.Skipped) throw new System.ArgumentException("Invalid exchange continuation.");
                var card = new CardInstanceId(selection.Selected.Single().Entity.Id);
                var hole = c.State.Zones.Single(z => z.Kind == handKind && z.ControllerId == c.Ability.OwnerId);
                if (!hole.Cards.Contains(card)) throw new System.ArgumentException("Exchange target is no longer held.");
                return new EffectIntent[] { new MoveCardEffect(c.Ability.Id, card, hole.Id, c.State.Zones.Single(z => z.Kind == CardZoneKinds.Discard).Id),
                    new DrawCardsEffect(c.Ability.Id, c.State.Zones.Single(z => z.Kind == CardZoneKinds.Deck).Id, hole.Id, 1) };
            }
        }
        private sealed class Peek : IActivatedAbility
        {
            public IReadOnlyList<EffectIntent> Plan(AbilityContext c)
            {
                var card = c.State.Zones.Single(z => z.Kind == CardZoneKinds.Deck).Cards.Last();
                var face = new BaseCardFaceResolver().Resolve(c.State, c.Catalog, card);
                return new EffectIntent[] { new GrantKnowledgeEffect(c.Ability.Id, new KnowledgeGrant(
                    new KnowledgeGrantId(c.Ability.Id.Value + "/peek/" + c.State.Revision), EntityRef.Ability(c.Ability.Id), KnowledgeAudience.Participant,
                    c.Ability.OwnerId, KnowledgeMode.Snapshot, c.State.ScopeId, card, c.State.Revision, face)) };
            }
        }
    }
}
