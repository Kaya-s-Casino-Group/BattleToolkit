using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    public sealed class PerEffectTargetTests
    {
        private static readonly ParticipantId Player = new ParticipantId("p"), Ally = new ParticipantId("a"), Enemy = new ParticipantId("e"), Other = new ParticipantId("e2");
        private sealed class SequenceRule : IScoredHandEffectRule
        {
            public string Version => "per-effect.test.1";
            private readonly BattleEffectStep[] steps;
            public ScoredHandEffectContext LastContext { get; private set; }
            public SequenceRule(params BattleEffectStep[] steps) { this.steps = steps; }
            public BattleEffectSequence Resolve(ScoredHandEffectContext context)
            { LastContext = context; return new BattleEffectSequence(Version, "test sequence", steps); }
        }
        private sealed class InvalidEnemyTargets : IBattleTargetPolicy
        {
            public IReadOnlyList<ParticipantId> SelectTargets(BattleTargetContext context) => new[] { context.Source };
        }
        private sealed class InvalidSequenceRule : IScoredHandEffectRule
        {
            private readonly bool returnNull;
            public string Version => "invalid-sequence.1";
            public InvalidSequenceRule(bool returnNull) { this.returnNull = returnNull; }
            public BattleEffectSequence Resolve(ScoredHandEffectContext context) => returnNull ? null :
                new BattleEffectSequence("wrong-version", "invalid sequence", new[] { Step(BattleEffectSpec.Heal(1), EffectTargetSpec.Self()) });
        }
        private sealed class StatusCapacityRule : IScoredHandEffectRule
        {
            private readonly int initialStatusCount;
            private readonly bool includeLivingTarget;
            public string Version => "status-capacity-targets.1";
            public StatusCapacityRule(int initialStatusCount, bool includeLivingTarget)
            { this.initialStatusCount = initialStatusCount; this.includeLivingTarget = includeLivingTarget; }
            public BattleEffectSequence Resolve(ScoredHandEffectContext context)
            {
                if (context.Combat.Statuses.Count < initialStatusCount)
                {
                    var seed = Step(BattleEffectSpec.ApplyStatus(new StatusApplicationSpec(new StatusDefinitionId("stored"), 1,
                        StatusDuration.UntilRemoved())), EffectTargetSpec.Self());
                    return new BattleEffectSequence(Version, "populate existing statuses", Enumerable.Repeat(seed,
                        Math.Min(64, initialStatusCount - context.Combat.Statuses.Count)));
                }
                return new BattleEffectSequence(Version, "skip dead target before checking live status capacity", new[] {
                    Step(BattleEffectSpec.Damage(1000), EffectTargetSpec.Opponents(EffectTargetSelection.FirstLiving)),
                    Step(BattleEffectSpec.ApplyStatus(new StatusApplicationSpec(new StatusDefinitionId("new-mark"), 1, StatusDuration.UntilRemoved())),
                        EffectTargetSpec.Opponents(includeLivingTarget ? EffectTargetSelection.AllLiving : EffectTargetSelection.FirstLiving)),
                    Step(BattleEffectSpec.GainBlock(1), EffectTargetSpec.Self())
                });
            }
        }
        private sealed class ReadSourceBlock : IAttackDamageModifier
        {
            public DamageModification Modify(DamageModifierContext context) => new DamageModification("source-block.1", "source block",
                context.Amount, context.Amount + context.Combat.Get(context.Source).Block);
        }
        private sealed class ReadFirstEnemyHealth : IIncomingDamageModifier
        {
            public DamageModification Modify(DamageModifierContext context) => new DamageModification("first-health.1", "first enemy health",
                context.Amount, context.Amount + context.Combat.Get(Enemy).Health / 10);
        }
        private sealed class ImmediatePresentation : IBattleSessionPresentation
        {
            public bool IsPresenting => false;
            public BattleSessionResult Last { get; private set; }
            public void Initialize(CardDuelState state) { }
            public void Present(BattleSessionResult result, bool animate) { Last = result; }
            public void Synchronize(CardDuelState state) { }
        }
        private static BattleEffectStep Step(BattleEffectSpec effect, EffectTargetSpec target) => new BattleEffectStep(effect, target);
        private static BattleTargetBindings Bind(string slot, params ParticipantId[] targets) => new BattleTargetBindings(slot, targets);
        private static BattlePlayerSetup[] Players(IScoredHandEffectRule rule, int energy = 3, bool withAlly = true,
            BattleActorDamageRules damage = null, BattleActorTargetRules targets = null)
        {
            var rules = new BattlePlayerRules(maxEnergy: energy, initialEnergy: energy, unlimitedDiscards: true, effectRule: rule,
                discardReward: new PerCardBlockRewardRule(8), dealPolicy: new CardDuelDealPolicy(CardDuelRefillTiming.AfterAction, CardDuelRefillTiming.AfterAction));
            var players = new List<BattlePlayerSetup> {
                new BattlePlayerSetup(Player, new CombatantDefinition("player", 100, 10), initialHealth: 40, rules: rules, damageRules: damage, targetRules: targets)
            };
            if (withAlly) players.Add(new BattlePlayerSetup(Ally, new CombatantDefinition("ally", 100, 4), initialHealth: 30));
            return players.ToArray();
        }
        private static BattleEnemySetup[] Enemies(BattleActorDamageRules damage = null, bool invalidSecondTargets = false) => new[] {
            new BattleEnemySetup(Enemy, new CombatantDefinition("enemy", 100), 7, damage),
            new BattleEnemySetup(Other, new CombatantDefinition("other", 80), 11, damage,
                invalidSecondTargets ? new BattleActorTargetRules(new InvalidEnemyTargets()) : null)
        };
        private static TeamBattleState Create(TeamBattleEngine engine, IScoredHandEffectRule rule = null,
            BattleActorDamageRules outgoing = null, BattleActorDamageRules incoming = null, BattleActorTargetRules targets = null) =>
            engine.Create("per-effect", Players(rule, damage: outgoing, targets: targets), Enemies(incoming));
        private static TeamBattleTransition Play(TeamBattleEngine engine, TeamBattleState state, BattleTargetBindings bindings = null,
            IEnumerable<ParticipantId> targets = null) => engine.Execute(state, state.Revision, Player, ScoredDuelActionKind.ScoredHand,
                ScoreEngine.Fixed(engine.NextScoreId(state), 10), requestedTargets: targets, targetBindings: bindings);
        private static BattleSession Session(IScoredHandEffectRule rule, int energy = 3, bool withAlly = true, bool invalidSecondTargets = false)
        {
            var engine = new CardDuelEngine(new BattleRules("per-effect.card.1", 20));
            var state = engine.Create("per-effect-card", HmacDeckShuffler.FromSeed(new byte[32]),
                Players(rule, energy, withAlly), Enemies(invalidSecondTargets: invalidSecondTargets));
            return new BattleSession(engine, state);
        }
        private static void Rejected(TeamBattleTransition result, TeamBattleState before, CardDuelError error)
        {
            Assert.AreEqual(error, result.Error); Assert.AreSame(before, result.State);
            Assert.IsEmpty(result.RuleEvents); Assert.IsEmpty(result.Events); Assert.IsNull(result.EffectPlan); Assert.IsNull(result.Damage);
        }

        [TestCase(CombatEffectKind.Damage)] [TestCase(CombatEffectKind.Heal)] [TestCase(CombatEffectKind.GainBlock)]
        [TestCase(CombatEffectKind.ApplyStatus)] [TestCase(CombatEffectKind.Eliminate)]
        public void AllFiveOperationsCanExpandToMultipleTargetsWithoutChangingTheOperation(CombatEffectKind kind)
        {
            BattleEffectSpec effect;
            switch (kind)
            {
                case CombatEffectKind.Damage: effect = BattleEffectSpec.Damage(7); break;
                case CombatEffectKind.Heal: effect = BattleEffectSpec.Heal(7); break;
                case CombatEffectKind.GainBlock: effect = BattleEffectSpec.GainBlock(7); break;
                case CombatEffectKind.ApplyStatus: effect = BattleEffectSpec.ApplyStatus(new StatusApplicationSpec(new StatusDefinitionId("mark"), 2, StatusDuration.ForTriggers(1))); break;
                default: effect = BattleEffectSpec.Eliminate(); break;
            }
            bool friendly = kind == CombatEffectKind.Heal || kind == CombatEffectKind.GainBlock;
            var target = friendly ? EffectTargetSpec.Allies(EffectTargetSelection.AllLiving) : EffectTargetSpec.Opponents(EffectTargetSelection.AllLiving);
            var engine = new TeamBattleEngine(new BattleRules("all-operations.1", 20)); var state = Create(engine, new SequenceRule(Step(effect, target)));
            var result = Play(engine, state);
            Assert.IsTrue(result.Accepted); Assert.AreEqual(2, result.EffectPlan.Effects.Count);
            CollectionAssert.AreEqual(friendly ? new[] { Player, Ally } : new[] { Enemy, Other }, result.EffectPlan.Effects.Select(item => item.Target));
            Assert.IsTrue(result.EffectPlan.Effects.All(item => item.Kind == kind && item.Source == Player));
            Assert.AreEqual(2, result.State.Player(Player).Energy.Energy); Assert.AreEqual(1, result.State.Scores.Count);
            Assert.AreEqual(state.Combat.Revision + 1, result.State.Combat.Revision); Assert.IsNull(result.Damage); Assert.IsNull(result.DiscardReward);
            if (kind == CombatEffectKind.Damage) { Assert.AreEqual(93, result.State.Combat.Get(Enemy).Health); Assert.AreEqual(73, result.State.Combat.Get(Other).Health); }
            if (kind == CombatEffectKind.Heal) { Assert.AreEqual(47, result.State.Combat.Get(Player).Health); Assert.AreEqual(37, result.State.Combat.Get(Ally).Health); }
            if (kind == CombatEffectKind.GainBlock) { Assert.AreEqual(17, result.State.Combat.Get(Player).Block); Assert.AreEqual(11, result.State.Combat.Get(Ally).Block); }
            if (kind == CombatEffectKind.ApplyStatus)
            {
                Assert.IsEmpty(result.Events); Assert.AreEqual(2, result.State.Combat.Statuses.Select(item => item.Id).Distinct().Count());
                Assert.IsTrue(result.State.Combat.Statuses.All(item => item.Stacks == 2 && item.Source.Id == Player.Value));
            }
            if (kind == CombatEffectKind.Eliminate) { Assert.AreEqual(ScoredDuelPhase.Won, result.State.Phase); Assert.IsTrue(result.Events.All(item => item.DeathCause == CombatDeathCause.Eliminate && item.DamageToHealth == 0)); }
        }

        [Test] public void EnemyStatusesAndSelfProtectionHaveDistinctStableIdsInPreviewAndCommit()
        {
            var rule = new SequenceRule(
                Step(BattleEffectSpec.ApplyStatus(new StatusApplicationSpec(new StatusDefinitionId("stun"), 1, StatusDuration.ForTriggers(1))), EffectTargetSpec.Opponents(EffectTargetSelection.AllLiving)),
                Step(BattleEffectSpec.ApplyStatus(new StatusApplicationSpec(new StatusDefinitionId("control-immunity"), 1, StatusDuration.UntilPhaseEnd("round/1", "enemy"))), EffectTargetSpec.Self()));
            var session = Session(rule); session.Execute(session.State.Revision, Player, CardDuelAction.Start);
            var state = session.State; var cards = state.Zone(CardZoneKinds.Hand, Player).Cards.Take(1).ToArray();
            var one = session.Engine.PreviewPlay(state, state.Revision, Player, cards);
            var two = session.Engine.PreviewPlay(state, state.Revision, Player, cards);
            var actual = session.Engine.Execute(state, state.Revision, Player, CardDuelAction.Play, cards);
            foreach (var result in new[] { one, two, actual })
            {
                Assert.IsTrue(result.Accepted); Assert.IsNull(result.Damage); Assert.IsEmpty(result.Events); Assert.AreEqual(3, result.RuleEvents.Count);
                CollectionAssert.AreEqual(new[] { Enemy, Other, Player }, result.EffectPlan.Effects.Select(item => item.Target));
                Assert.AreEqual(3, result.State.Duel.Combat.Statuses.Select(item => item.Id).Distinct().Count());
                CollectionAssert.AreEqual(actual.State.Duel.Combat.Statuses.Select(item => item.Id), result.State.Duel.Combat.Statuses.Select(item => item.Id));
                CollectionAssert.AreEqual(actual.State.Random.StateBytes, result.State.Random.StateBytes);
                Assert.AreEqual(2, result.State.Duel.Player(Player).Energy.Energy);
            }
            Assert.AreSame(actual.Pattern, rule.LastContext.Hand.Pattern); Assert.AreSame(actual.Score, rule.LastContext.Score);
            Assert.IsEmpty(state.Duel.Combat.Statuses); Assert.AreSame(state, session.State);
        }

        [Test] public void NamedEnemyAndAllySlotsAreIndependentAndRequestedMeansExactlyOne()
        {
            var rule = new SequenceRule(Step(BattleEffectSpec.Damage(7), EffectTargetSpec.Opponents(EffectTargetSelection.Requested, "attack")),
                Step(BattleEffectSpec.Heal(9), EffectTargetSpec.Allies(EffectTargetSelection.Requested, "support")));
            var engine = new TeamBattleEngine(new BattleRules("named.1", 20)); var state = Create(engine, rule);
            var requests = new BattleTargetBindings(new[] {
                new KeyValuePair<string, IEnumerable<ParticipantId>>("attack", new[] { Other }),
                new KeyValuePair<string, IEnumerable<ParticipantId>>("support", new[] { Ally })
            });
            var result = Play(engine, state, requests); Assert.IsTrue(result.Accepted);
            CollectionAssert.AreEqual(new[] { Other, Ally }, result.EffectPlan.Effects.Select(item => item.Target));
            Assert.AreEqual(73, result.State.Combat.Get(Other).Health); Assert.AreEqual(39, result.State.Combat.Get(Ally).Health);
            Assert.AreEqual(100, result.State.Combat.Get(Enemy).Health); Assert.AreEqual(40, result.State.Combat.Get(Player).Health);
            var tooMany = new BattleTargetBindings(new[] {
                new KeyValuePair<string, IEnumerable<ParticipantId>>("attack", new[] { Enemy, Other }),
                new KeyValuePair<string, IEnumerable<ParticipantId>>("support", new[] { Ally })
            });
            Rejected(Play(engine, state, tooMany), state, CardDuelError.InvalidTarget);
        }

        [Test] public void MissingWrongUnknownUnusedAndUnsupportedBindingsRejectWithoutSpendingAnything()
        {
            var engine = new TeamBattleEngine(new BattleRules("invalid-bindings.1", 20));
            var rule = new SequenceRule(Step(BattleEffectSpec.Damage(1), EffectTargetSpec.Opponents(EffectTargetSelection.Requested, "victim")));
            var state = Create(engine, rule);
            foreach (var bindings in new[] { null, Bind("victim", Ally), Bind("victim", new ParticipantId("missing")), Bind("extra", Enemy) })
                Rejected(Play(engine, state, bindings), state, CardDuelError.InvalidTarget);
            Rejected(Play(engine, state, Bind("victim", Enemy), new[] { Other }), state, CardDuelError.InvalidTarget);
            var fixedState = Create(engine, new SequenceRule(Step(BattleEffectSpec.Damage(1), EffectTargetSpec.Opponents(EffectTargetSelection.AllLiving))));
            Rejected(Play(engine, fixedState, Bind("default", Enemy)), fixedState, CardDuelError.InvalidTarget);
            var selfState = Create(engine, new SequenceRule(Step(BattleEffectSpec.GainBlock(1), EffectTargetSpec.Self())));
            Rejected(Play(engine, selfState, Bind("default", Ally)), selfState, CardDuelError.InvalidTarget);
            var legacyState = Create(engine);
            Rejected(Play(engine, legacyState, Bind("default", Enemy)), legacyState, CardDuelError.InvalidTarget);
            var session = Session(rule); var beforeStart = session.State;
            var start = session.Execute(beforeStart.Revision, Player, CardDuelAction.Start, targetBindings: Bind("victim", Enemy));
            Assert.AreEqual(CardDuelError.InvalidTarget, start.Error); Assert.AreSame(beforeStart, session.State);
            session.Execute(session.State.Revision, Player, CardDuelAction.Start); var started = session.State;
            var discarded = session.Execute(started.Revision, Player, CardDuelAction.Discard, started.Zone(CardZoneKinds.Hand, Player).Cards.Take(1), targetBindings: Bind("victim", Enemy));
            Assert.AreEqual(CardDuelError.InvalidTarget, discarded.Error); Assert.AreSame(started, session.State); Assert.IsEmpty(discarded.Steps);
        }

        [Test] public void ExpandedEffectLimitIsCheckedAfterTargetFanOut()
        {
            var engine = new TeamBattleEngine(new BattleRules("fan-out-limit.1", 20));
            var step = Step(BattleEffectSpec.Damage(0), EffectTargetSpec.Opponents(EffectTargetSelection.AllLiving));
            var maximum = Create(engine, new SequenceRule(Enumerable.Repeat(step, 32).ToArray()));
            var accepted = Play(engine, maximum); Assert.IsTrue(accepted.Accepted); Assert.AreEqual(64, accepted.EffectPlan.Effects.Count);
            var excessive = Create(engine, new SequenceRule(Enumerable.Repeat(step, 33).ToArray()));
            Rejected(Play(engine, excessive), excessive, CardDuelError.InvalidEffectPlan);
            Assert.AreEqual(3, excessive.Player(Player).Energy.Energy); Assert.IsEmpty(excessive.Scores);
        }

        [Test] public void NewSequencesKeepOutgoingSnapshotAndIncomingWorkingStateSemantics()
        {
            var rule = new SequenceRule(Step(BattleEffectSpec.GainBlock(5), EffectTargetSpec.Self()),
                Step(BattleEffectSpec.Damage(10), EffectTargetSpec.Opponents(EffectTargetSelection.AllLiving)));
            var engine = new TeamBattleEngine(new BattleRules("new-modifiers.1", 20));
            var state = Create(engine, rule, new BattleActorDamageRules(new ReadSourceBlock()), new BattleActorDamageRules(incomingDamage: new ReadFirstEnemyHealth()));
            var result = Play(engine, state); Assert.IsTrue(result.Accepted);
            CollectionAssert.AreEqual(new long[] { 5, 20, 20 }, result.EffectPlan.Effects.Select(item => item.Amount));
            CollectionAssert.AreEqual(new long[] { 30, 27 }, result.Events.Where(item => item.Effect.Kind == CombatEffectKind.Damage).Select(item => item.SettledDamage));
            Assert.AreEqual(15, result.State.Combat.Get(Player).Block); Assert.AreEqual(70, result.State.Combat.Get(Enemy).Health); Assert.AreEqual(53, result.State.Combat.Get(Other).Health);
            Assert.IsTrue(result.EffectPlan.Effects.Where(item => item.Kind == CombatEffectKind.Damage).All(item => item.Attack.BaseDamage == 10));
            Assert.AreEqual(state.Combat.Revision + 1, result.State.Combat.Revision);
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, result.RuleEvents.Select(item => item.Index));
            Assert.IsTrue(result.RuleEvents.All(item => item.Revision == result.State.Combat.Revision));
        }

        [Test] public void AutomaticSelectorsUseTheActionStartSnapshotForEachEffect()
        {
            var rule = new SequenceRule(Step(BattleEffectSpec.Damage(30), EffectTargetSpec.Opponents(EffectTargetSelection.FirstLiving)),
                Step(BattleEffectSpec.Heal(1), EffectTargetSpec.Opponents(EffectTargetSelection.HighestHealth)),
                Step(BattleEffectSpec.Damage(1), EffectTargetSpec.Opponents(EffectTargetSelection.LowestHealth)),
                Step(BattleEffectSpec.GainBlock(2), EffectTargetSpec.Allies(EffectTargetSelection.LowestHealth)),
                Step(BattleEffectSpec.GainBlock(3), EffectTargetSpec.Allies(EffectTargetSelection.HighestHealth)));
            var engine = new TeamBattleEngine(new BattleRules("selector-snapshot.1", 20)); var state = Create(engine, rule);
            var result = Play(engine, state); Assert.IsTrue(result.Accepted);
            CollectionAssert.AreEqual(new[] { Enemy, Enemy, Other, Ally, Player }, result.EffectPlan.Effects.Select(item => item.Target));
            Assert.AreEqual(71, result.State.Combat.Get(Enemy).Health); Assert.AreEqual(79, result.State.Combat.Get(Other).Health);
            Assert.AreEqual(6, result.State.Combat.Get(Ally).Block); Assert.AreEqual(13, result.State.Combat.Get(Player).Block);
        }

        [Test] public void SessionForwardsSlotsOnlyToTheCommandAndRollsBackARejectedAutomaticStep()
        {
            var rule = new SequenceRule(Step(BattleEffectSpec.Damage(1), EffectTargetSpec.Opponents(EffectTargetSelection.Requested, "strike")),
                Step(BattleEffectSpec.ApplyStatus(new StatusApplicationSpec(new StatusDefinitionId("self-mark"), 1, StatusDuration.UntilRemoved())), EffectTargetSpec.Self()));
            var session = Session(rule, energy: 1, withAlly: false); session.Execute(session.State.Revision, Player, CardDuelAction.Start);
            var before = session.State; var cards = before.Zone(CardZoneKinds.Hand, Player).Cards.Take(1).ToArray();
            var accepted = session.Execute(before.Revision, Player, CardDuelAction.Play, cards, targetBindings: Bind("strike", Other));
            Assert.IsTrue(accepted.Accepted); Assert.AreEqual(2, accepted.Steps.Count(item => item.Action == CardDuelAction.EnemyAction));
            Assert.AreEqual(1, accepted.State.Duel.CompletedRounds); Assert.AreEqual(1, accepted.State.Duel.Player(Player).Energy.Energy);
            Assert.AreEqual(79, accepted.State.Duel.Combat.Get(Other).Health); Assert.AreEqual(1, accepted.State.Duel.Combat.Statuses.Count);
            var failing = Session(rule, energy: 1, withAlly: false, invalidSecondTargets: true); failing.Execute(failing.State.Revision, Player, CardDuelAction.Start);
            var original = failing.State;
            var rejected = failing.Execute(original.Revision, Player, CardDuelAction.Play, original.Zone(CardZoneKinds.Hand, Player).Cards.Take(1), targetBindings: Bind("strike", Other));
            Assert.AreEqual(CardDuelError.InvalidTarget, rejected.Error); Assert.AreSame(original, failing.State); Assert.AreSame(original, rejected.State);
            Assert.IsEmpty(rejected.Steps); Assert.IsEmpty(original.Duel.Combat.Statuses); Assert.IsEmpty(original.Duel.Scores);
        }

        [Test] public void LegacyTargetArraysMapToDefaultOnlyForNewRulesAndActorPolicyKeepsItsDefaults()
        {
            var engine = new TeamBattleEngine(new BattleRules("legacy-targets.1", 20));
            var newState = Create(engine, new SequenceRule(Step(BattleEffectSpec.Damage(1), EffectTargetSpec.Opponents(EffectTargetSelection.Requested))));
            var selected = Play(engine, newState, targets: new[] { Other }); Assert.IsTrue(selected.Accepted);
            Assert.AreEqual(Other, selected.EffectPlan.Effects.Single().Target); Assert.IsNull(selected.Damage);
            var actorState = Create(engine, new SequenceRule(Step(BattleEffectSpec.Damage(1), EffectTargetSpec.Opponents())),
                targets: new BattleActorTargetRules(new OpponentTargetPolicy(OpponentTargetSelection.AllLiving)));
            Assert.AreEqual(2, Play(engine, actorState).EffectPlan.Effects.Count);
            Assert.AreEqual(Other, Play(engine, actorState, Bind("default", Other)).EffectPlan.Effects.Single().Target);
            var oldState = Create(engine); var old = Play(engine, oldState, targets: new[] { Other });
            Assert.IsTrue(old.Accepted); Assert.IsNotNull(old.Damage); Assert.AreEqual(Other, old.Damage.Requests.Single().Target);
            var session = Session(null); session.Execute(session.State.Revision, Player, CardDuelAction.Start);
            var discarded = session.Execute(session.State.Revision, Player, CardDuelAction.Discard, session.State.Zone(CardZoneKinds.Hand, Player).Cards.Take(2), new[] { Ally });
            Assert.IsTrue(discarded.Accepted); Assert.IsNotNull(discarded.Steps[0].Transition.DiscardReward);
            Assert.AreEqual(20, discarded.State.Duel.Combat.Get(Ally).Block); Assert.AreEqual(10, discarded.State.Duel.Combat.Get(Player).Block);
        }

        [Test] public void NewDescriptorsCopyInputsAndRuleConfigurationIsUnambiguous()
        {
            var originalStep = Step(BattleEffectSpec.Damage(1), EffectTargetSpec.Opponents(EffectTargetSelection.Requested, "victim"));
            var supplied = new[] { originalStep }; var sequence = new BattleEffectSequence("sequence.1", "copy", supplied); supplied[0] = null;
            Assert.AreSame(originalStep, sequence.Steps.Single()); Assert.Throws<NotSupportedException>(() => ((IList<BattleEffectStep>)sequence.Steps)[0] = null);
            Assert.Catch<ArgumentException>(() => new BattleEffectSequence("sequence.1", "empty", Array.Empty<BattleEffectStep>()));
            var requested = new[] { Other }; var bindings = new BattleTargetBindings("victim", requested); requested[0] = Ally;
            Assert.AreEqual(Other, bindings.Slots["victim"].Single());
            Assert.Throws<NotSupportedException>(() => ((IList<ParticipantId>)bindings.Slots["victim"])[0] = Ally);
            var rule = new SequenceRule(originalStep);
            Assert.Throws<ArgumentException>(() => new BattlePlayerRules(damagePolicy: new EqualScoreDamageRule(), effectRule: rule));
            var engine = new TeamBattleEngine(new BattleRules("copied-bindings.1", 20)); var result = Play(engine, Create(engine, rule), bindings);
            Assert.IsTrue(result.Accepted); Assert.AreEqual(Other, result.EffectPlan.Effects.Single().Target);
        }

        [Test] public void PlaybackPreviewAndSubmissionPassNamedSlotsThroughTheSameRulePath()
        {
            var rule = new SequenceRule(Step(BattleEffectSpec.Damage(1), EffectTargetSpec.Opponents(EffectTargetSelection.Requested, "victim")),
                Step(BattleEffectSpec.Heal(2), EffectTargetSpec.Allies(EffectTargetSelection.Requested, "friend")));
            var session = Session(rule); var presentation = new ImmediatePresentation(); var playback = new BattleSessionPlayback(session, presentation); playback.Start(false);
            var before = session.State; var cards = before.Zone(CardZoneKinds.Hand, Player).Cards.Take(1).ToArray();
            var bindings = new BattleTargetBindings(new[] {
                new KeyValuePair<string, IEnumerable<ParticipantId>>("victim", new[] { Other }),
                new KeyValuePair<string, IEnumerable<ParticipantId>>("friend", new[] { Ally })
            });
            var preview = playback.PreviewPlay(Player, cards, targetBindings: bindings); Assert.IsTrue(preview.Accepted); Assert.AreSame(before, session.State);
            var committed = playback.Submit(Player, CardDuelAction.Play, cards, null, false, targetBindings: bindings);
            Assert.IsTrue(committed.Accepted); Assert.AreSame(committed, presentation.Last);
            CollectionAssert.AreEqual(preview.EffectPlan.Effects.Select(item => item.Target), committed.Steps[0].Transition.EffectPlan.Effects.Select(item => item.Target));
            Assert.AreEqual(preview.State.Duel.Combat.Get(Other).Health, committed.State.Duel.Combat.Get(Other).Health);
            Assert.AreEqual(preview.State.Duel.Combat.Get(Ally).Health, committed.State.Duel.Combat.Get(Ally).Health);
            Assert.AreEqual(2, committed.State.Duel.Player(Player).Energy.Energy);
        }

        [TestCase(false)] [TestCase(true)]
        public void LethalEffectThenStatusOnTheSameTargetCommitsTheHitAndSkipsTheStatus(bool eliminate)
        {
            var target = EffectTargetSpec.Opponents(EffectTargetSelection.FirstLiving);
            var rule = new SequenceRule(Step(eliminate ? BattleEffectSpec.Eliminate() : BattleEffectSpec.Damage(1000), target),
                Step(BattleEffectSpec.ApplyStatus(new StatusApplicationSpec(new StatusDefinitionId("after-hit"), 1, StatusDuration.UntilRemoved())), target));
            var session = Session(rule); session.Execute(session.State.Revision, Player, CardDuelAction.Start);
            var before = session.State; var cards = before.Zone(CardZoneKinds.Hand, Player).Cards.Take(1).ToArray();
            var preview = session.Engine.PreviewPlay(before, before.Revision, Player, cards);
            var result = session.Execute(before.Revision, Player, CardDuelAction.Play, cards);
            Assert.IsTrue(preview.Accepted); Assert.IsTrue(result.Accepted); var committed = result.Steps[0].Transition;
            foreach (var action in new[] { preview, committed })
            {
                Assert.AreEqual(2, action.EffectPlan.Effects.Count); Assert.AreEqual(1, action.RuleEvents.Count); Assert.AreEqual(1, action.Events.Count);
                Assert.AreEqual(0, action.RuleEvents[0].Index); Assert.IsTrue(action.Events[0].Died);
                Assert.AreEqual(eliminate ? CombatDeathCause.Eliminate : CombatDeathCause.Damage, action.Events[0].DeathCause);
                Assert.AreEqual(0, action.State.Duel.Combat.Get(Enemy).Health); Assert.IsEmpty(action.State.Duel.Combat.Statuses);
                Assert.AreEqual(2, action.State.Duel.Player(Player).Energy.Energy); Assert.AreEqual(1, action.State.Duel.Scores.Count);
                Assert.IsTrue(cards.All(card => action.State.Zone(CardZoneKinds.Played, Player).Cards.Contains(card)));
            }
            Assert.AreEqual(preview.Score.Total, committed.Score.Total); Assert.AreEqual(preview.State.Revision, committed.State.Revision);
            CollectionAssert.AreEqual(preview.State.Random.StateBytes, committed.State.Random.StateBytes);
            foreach (var zone in preview.State.Table.Zones) CollectionAssert.AreEqual(zone.Cards, committed.State.Table.GetZone(zone.Id).Cards);
            Assert.IsTrue(cards.All(card => result.State.Zone(CardZoneKinds.Discard, Player).Cards.Contains(card)));
            Assert.IsEmpty(before.Duel.Combat.Statuses); Assert.IsEmpty(before.Duel.Scores);
            Assert.AreEqual(3, before.Duel.Player(Player).Energy.Energy); Assert.AreEqual(100, before.Duel.Combat.Get(Enemy).Health);
        }

        [TestCase(false)] [TestCase(true)]
        public void MixedAoeSkipsOnlyDeadTargetsAndContinuesLivingStatusAndSelfSupport(bool eliminate)
        {
            var rule = new SequenceRule(
                Step(eliminate ? BattleEffectSpec.Eliminate() : BattleEffectSpec.Damage(1000), EffectTargetSpec.Opponents(EffectTargetSelection.FirstLiving)),
                Step(BattleEffectSpec.ApplyStatus(new StatusApplicationSpec(new StatusDefinitionId("aoe-mark"), 1, StatusDuration.UntilRemoved())),
                    EffectTargetSpec.Opponents(EffectTargetSelection.AllLiving)),
                Step(BattleEffectSpec.Heal(7), EffectTargetSpec.Self()), Step(BattleEffectSpec.GainBlock(3), EffectTargetSpec.Self()));
            var engine = new TeamBattleEngine(new BattleRules("skip-dead-aoe.1", 20)); var state = Create(engine, rule);
            var result = Play(engine, state); Assert.IsTrue(result.Accepted);
            CollectionAssert.AreEqual(new[] { Enemy, Enemy, Other, Player, Player }, result.EffectPlan.Effects.Select(effect => effect.Target));
            CollectionAssert.AreEqual(new[] { 0, 2, 3, 4 }, result.RuleEvents.Select(item => item.Index));
            CollectionAssert.AreEqual(new[] { 0, 3, 4 }, result.Events.Select(item => item.Index));
            Assert.AreEqual(Other.Value, result.State.Combat.Statuses.Single().Target.Id);
            Assert.AreEqual(0, result.State.Combat.Get(Enemy).Health); Assert.AreEqual(80, result.State.Combat.Get(Other).Health);
            Assert.AreEqual(47, result.State.Combat.Get(Player).Health); Assert.AreEqual(13, result.State.Combat.Get(Player).Block);
            Assert.AreEqual(2, result.State.Player(Player).Energy.Energy); Assert.AreEqual(1, result.State.Scores.Count);
            Assert.AreEqual(state.Combat.Revision + 1, result.State.Combat.Revision);
        }

        [Test] public void OverflowAfterASkippedDeadTargetStillRollsBackTheWholeAction()
        {
            var target = EffectTargetSpec.Opponents(EffectTargetSelection.FirstLiving);
            var rule = new SequenceRule(Step(BattleEffectSpec.Damage(1000), target),
                Step(BattleEffectSpec.ApplyStatus(new StatusApplicationSpec(new StatusDefinitionId("skipped"), 1, StatusDuration.UntilRemoved())), target),
                Step(BattleEffectSpec.GainBlock(long.MaxValue), EffectTargetSpec.Self()));
            var engine = new TeamBattleEngine(new BattleRules("skip-then-overflow.1", 20)); var state = Create(engine, rule);
            Rejected(Play(engine, state), state, CardDuelError.Overflow);
            Assert.AreEqual(100, state.Combat.Get(Enemy).Health); Assert.AreEqual(10, state.Combat.Get(Player).Block);
            Assert.AreEqual(3, state.Player(Player).Energy.Energy); Assert.IsEmpty(state.Scores); Assert.IsEmpty(state.Combat.Statuses);
        }

        [TestCase(255, true, true)] [TestCase(256, false, true)] [TestCase(256, true, false)]
        public void SkippedStatusApplicationsDoNotUseCapacityButLivingApplicationsStillDo(int existingCount, bool includeLivingTarget, bool accepted)
        {
            var rule = new StatusCapacityRule(existingCount, includeLivingTarget);
            var engine = new TeamBattleEngine(new BattleRules("status-capacity-skip.1", 20));
            var state = engine.Create("status-capacity-skip", Players(rule, energy: 10), Enemies());
            for (int action = 0; action < 4; action++)
            {
                var initialized = Play(engine, state); Assert.IsTrue(initialized.Accepted); state = initialized.State;
            }
            Assert.AreEqual(existingCount, state.Combat.Statuses.Count); var result = Play(engine, state);
            Assert.AreEqual(accepted, result.Accepted);
            if (!accepted)
            {
                Rejected(result, state, CardDuelError.InvalidTarget);
                Assert.AreEqual(100, state.Combat.Get(Enemy).Health); Assert.AreEqual(existingCount, state.Combat.Statuses.Count);
                Assert.AreEqual(6, state.Player(Player).Energy.Energy); Assert.AreEqual(4, state.Scores.Count); return;
            }
            Assert.AreEqual(0, result.State.Combat.Get(Enemy).Health); Assert.AreEqual(existingCount + (includeLivingTarget ? 1 : 0), result.State.Combat.Statuses.Count);
            Assert.AreEqual(includeLivingTarget ? 4 : 3, result.EffectPlan.Effects.Count);
            CollectionAssert.AreEqual(includeLivingTarget ? new[] { 0, 2, 3 } : new[] { 0, 2 }, result.RuleEvents.Select(item => item.Index));
            Assert.AreEqual(includeLivingTarget ? 1 : 0, result.RuleEvents.OfType<CombatStatusEvent>().Count());
            Assert.AreEqual(11, result.State.Combat.Get(Player).Block); Assert.AreEqual(5, result.State.Player(Player).Energy.Energy);
            Assert.AreEqual(5, result.State.Scores.Count); Assert.AreEqual(existingCount, state.Combat.Statuses.Count);
            if (includeLivingTarget) Assert.AreEqual(Other.Value, result.State.Combat.Statuses.Last().Target.Id);
        }

        [Test] public void ConsecutiveActionsGenerateDifferentStatusIdsWhileEachPreviewMatchesItsCommit()
        {
            var rule = new SequenceRule(Step(BattleEffectSpec.ApplyStatus(
                new StatusApplicationSpec(new StatusDefinitionId("repeated-mark"), 1, StatusDuration.UntilRemoved())), EffectTargetSpec.Self()));
            var session = Session(rule); session.Execute(session.State.Revision, Player, CardDuelAction.Start);
            var beforeFirst = session.State; var firstCards = beforeFirst.Zone(CardZoneKinds.Hand, Player).Cards.Take(1).ToArray();
            var firstPreview = session.Engine.PreviewPlay(beforeFirst, beforeFirst.Revision, Player, firstCards);
            var first = session.Execute(beforeFirst.Revision, Player, CardDuelAction.Play, firstCards);
            Assert.IsTrue(firstPreview.Accepted); Assert.IsTrue(first.Accepted);
            var firstId = first.Steps[0].Transition.EffectPlan.Effects.Single().Status.Id;
            Assert.AreEqual(firstPreview.EffectPlan.Effects.Single().Status.Id, firstId);
            var beforeSecond = session.State; var secondCards = beforeSecond.Zone(CardZoneKinds.Hand, Player).Cards.Take(1).ToArray();
            var secondPreview = session.Engine.PreviewPlay(beforeSecond, beforeSecond.Revision, Player, secondCards);
            var secondPreviewAgain = session.Engine.PreviewPlay(beforeSecond, beforeSecond.Revision, Player, secondCards);
            var second = session.Execute(beforeSecond.Revision, Player, CardDuelAction.Play, secondCards);
            Assert.IsTrue(secondPreview.Accepted); Assert.IsTrue(secondPreviewAgain.Accepted); Assert.IsTrue(second.Accepted);
            var secondId = second.Steps[0].Transition.EffectPlan.Effects.Single().Status.Id;
            Assert.AreNotEqual(firstId, secondId); Assert.AreEqual(secondPreview.EffectPlan.Effects.Single().Status.Id, secondId);
            Assert.AreEqual(secondPreviewAgain.EffectPlan.Effects.Single().Status.Id, secondId);
            CollectionAssert.AreEqual(new[] { firstId, secondId }, second.State.Duel.Combat.Statuses.Select(status => status.Id));
            Assert.AreEqual(1, second.State.Duel.Player(Player).Energy.Energy); Assert.AreEqual(2, second.State.Duel.Scores.Count);
            Assert.IsEmpty(beforeFirst.Duel.Combat.Statuses); Assert.AreEqual(1, beforeSecond.Duel.Combat.Statuses.Count);
        }

        [TestCase(false)] [TestCase(true)]
        public void MissingOrMismatchedRuleOutputRejectsWithoutPartialResults(bool returnNull)
        {
            var engine = new TeamBattleEngine(new BattleRules("invalid-sequence-output.1", 20));
            var state = Create(engine, new InvalidSequenceRule(returnNull));
            Rejected(Play(engine, state), state, CardDuelError.InvalidEffectPlan);
            Assert.AreEqual(3, state.Player(Player).Energy.Energy); Assert.IsEmpty(state.Scores); Assert.IsEmpty(state.Combat.Statuses);
            Assert.AreEqual(40, state.Combat.Get(Player).Health);
        }
    }
}
