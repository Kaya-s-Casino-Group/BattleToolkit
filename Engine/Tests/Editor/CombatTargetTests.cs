using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class CombatTargetTests
    {
        private static readonly ParticipantId Player = new ParticipantId("target-player"), Ally = new ParticipantId("target-ally"),
            Enemy = new ParticipantId("target-enemy"), Other = new ParticipantId("target-other"), High = new ParticipantId("target-high"),
            Dead = new ParticipantId("target-dead"), ThirdTeam = new ParticipantId("target-third"), Missing = new ParticipantId("target-missing");

        private static CombatantState Unit(ParticipantId id, string team, int health, int maxHealth = 100) =>
            new CombatantState(id, "target-unit", team, maxHealth, health);
        private static CombatState State() => new CombatState("target-combat", 0, new[] {
            Unit(Player, "red", 40), Unit(Dead, "blue", 0), Unit(Enemy, "blue", 30, 30), Unit(Ally, "red", 20),
            Unit(Other, "blue", 30, 300), Unit(High, "blue", 80), Unit(ThirdTeam, "green", 50)
        });
        private static CombatState WithHealth(CombatState state, ParticipantId id, int health) => new CombatState(state.Id, state.Revision,
            state.Combatants.Select(unit => unit.Id == id ? new CombatantState(unit.Id, unit.DefinitionId, unit.TeamId, unit.MaxHealth, health, unit.Block) : unit), state.Statuses);
        private static CombatTransition Hit(CombatState state, ParticipantId source, ParticipantId target, long damage = 0)
        {
            var result = CombatEngine.Resolve(state, state.Revision, new[] { new CombatEffect(CombatEffectKind.Damage, source, target, damage) });
            Assert.IsTrue(result.Accepted, result.Error.ToString()); return result;
        }
        private static TeamBattleState Battle(out TeamBattleEngine engine, IBattleTargetPolicy attack = null, int playerHealth = 40)
        {
            engine = new TeamBattleEngine(new BattleRules("target-battle.1", 10));
            return engine.Create("target-battle", new[] {
                new BattlePlayerSetup(Player, new CombatantDefinition("player", 100), initialHealth: playerHealth,
                    targetRules: new BattleActorTargetRules(attack)),
                new BattlePlayerSetup(Ally, new CombatantDefinition("ally", 100), initialHealth: 20)
            }, new[] {
                new BattleEnemySetup(Enemy, new CombatantDefinition("enemy", 30), 1),
                new BattleEnemySetup(Other, new CombatantDefinition("other", 80), 1)
            });
        }
        private sealed class InvalidPolicy : IBattleTargetPolicy
        {
            public IReadOnlyList<ParticipantId> SelectTargets(BattleTargetContext context) => new[] { context.Source };
        }

        [Test] public void SpecsAreImmutableAndKeepCombatIndependentFromComposition()
        {
            Assert.AreEqual(CombatTargetGroup.Self, CombatTargetSpec.Self().Group);
            Assert.AreEqual(CombatTargetGroup.EventSource, CombatTargetSpec.EventSource().Group);
            Assert.AreEqual(CombatTargetGroup.EventTarget, CombatTargetSpec.EventTarget().Group);
            Assert.AreEqual(CombatTargetSelection.FirstLiving, CombatTargetSpec.Opponents().Selection);
            Assert.AreEqual(CombatTargetSelection.FirstLiving, CombatTargetSpec.Allies().Selection);
            Assert.IsFalse(typeof(CombatTargetSpec).GetProperties().Any(property => property.CanWrite));
            Assert.IsEmpty(typeof(CombatTargetSpec).GetConstructors());
            Assert.AreSame(typeof(CombatEngine).Assembly, typeof(CombatTargetSpec).Assembly);
            Assert.AreSame(typeof(CombatEngine).Assembly, typeof(CombatTargetResolver).Assembly);
            Assert.AreSame(typeof(TeamBattleEngine).Assembly, typeof(EffectTargetSpec).Assembly);
            Assert.Throws<ArgumentException>(() => CombatTargetSpec.Opponents((CombatTargetSelection)0));
            Assert.Throws<ArgumentException>(() => CombatTargetSpec.Allies((CombatTargetSelection)99));
        }

        [Test] public void GroupsUseOwnerTeamIncludeSelfAmongAlliesAndPreserveLivingRosterOrder()
        {
            var state = State();
            var opponents = CombatTargetResolver.Resolve(state, Player, CombatTargetSpec.Opponents(CombatTargetSelection.AllLiving));
            CollectionAssert.AreEqual(new[] { Enemy, Other, High, ThirdTeam }, opponents);
            CollectionAssert.AreEqual(new[] { Player, Ally }, CombatTargetResolver.Resolve(state, Player, CombatTargetSpec.Allies(CombatTargetSelection.AllLiving)));
            CollectionAssert.AreEqual(new[] { Player, Ally, ThirdTeam }, CombatTargetResolver.Resolve(state, Enemy, CombatTargetSpec.Opponents(CombatTargetSelection.AllLiving)));
            CollectionAssert.AreEqual(new[] { Enemy, Other, High }, CombatTargetResolver.Resolve(state, Enemy, CombatTargetSpec.Allies(CombatTargetSelection.AllLiving)));
            CollectionAssert.AreEqual(new[] { Enemy }, CombatTargetResolver.Resolve(state, Player, CombatTargetSpec.Opponents()));
            CollectionAssert.AreEqual(new[] { Player }, CombatTargetResolver.Resolve(state, Player, CombatTargetSpec.Allies()));
            Assert.Throws<NotSupportedException>(() => ((IList<ParticipantId>)opponents)[0] = Player);
        }

        [Test] public void HealthSelectorsUseAbsoluteHealthAndKeepRosterOrderForTies()
        {
            var state = State();
            // Enemy has full health while Other has only ten percent, but both have 30 HP.
            CollectionAssert.AreEqual(new[] { Enemy }, CombatTargetResolver.Resolve(state, Player, CombatTargetSpec.Opponents(CombatTargetSelection.LowestHealth)));
            CollectionAssert.AreEqual(new[] { High }, CombatTargetResolver.Resolve(state, Player, CombatTargetSpec.Opponents(CombatTargetSelection.HighestHealth)));
            var tiedHighest = WithHealth(state, Other, 80);
            CollectionAssert.AreEqual(new[] { Other }, CombatTargetResolver.Resolve(tiedHighest, Player, CombatTargetSpec.Opponents(CombatTargetSelection.HighestHealth)));
            CollectionAssert.AreEqual(new[] { Ally }, CombatTargetResolver.Resolve(state, Player, CombatTargetSpec.Allies(CombatTargetSelection.LowestHealth)));
            CollectionAssert.AreEqual(new[] { Player }, CombatTargetResolver.Resolve(state, Player, CombatTargetSpec.Allies(CombatTargetSelection.HighestHealth)));
        }

        [Test] public void EventRelativeTargetsAreIndependentFromTheOwnerAndItsTeam()
        {
            var hit = Hit(State(), Enemy, Player); var fact = hit.RuleEvents.Single();
            CollectionAssert.AreEqual(new[] { Ally }, CombatTargetResolver.Resolve(hit.State, Ally, CombatTargetSpec.Self(), fact));
            CollectionAssert.AreEqual(new[] { Enemy }, CombatTargetResolver.Resolve(hit.State, Ally, CombatTargetSpec.EventSource(), fact));
            CollectionAssert.AreEqual(new[] { Player }, CombatTargetResolver.Resolve(hit.State, Ally, CombatTargetSpec.EventTarget(), fact));
            CollectionAssert.AreEqual(new[] { Player }, CombatTargetResolver.Resolve(hit.State, Enemy, CombatTargetSpec.EventTarget(), fact));
        }

        [Test] public void DeadFixedTargetsReturnEmptyWithoutFallingBackToOtherLivingUnits()
        {
            var killed = Hit(State(), Enemy, Player, 100); var fact = killed.RuleEvents.Single();
            Assert.IsEmpty(CombatTargetResolver.Resolve(killed.State, Ally, CombatTargetSpec.EventTarget(), fact));
            Assert.IsEmpty(CombatTargetResolver.Resolve(killed.State, Player, CombatTargetSpec.Self(), fact));
            var sourceAlsoDead = WithHealth(killed.State, Enemy, 0);
            Assert.IsEmpty(CombatTargetResolver.Resolve(sourceAlsoDead, Ally, CombatTargetSpec.EventSource(), fact));
            CollectionAssert.AreEqual(new[] { Other }, CombatTargetResolver.Resolve(sourceAlsoDead, Player, CombatTargetSpec.Opponents()));
        }

        [Test] public void AnOwnerCanBeDefeatedWhileAutomaticSelectionStillUsesItsTeam()
        {
            var state = State();
            CollectionAssert.AreEqual(new[] { Player }, CombatTargetResolver.Resolve(state, Dead, CombatTargetSpec.Opponents()));
            CollectionAssert.AreEqual(new[] { Enemy, Other, High }, CombatTargetResolver.Resolve(state, Dead, CombatTargetSpec.Allies(CombatTargetSelection.AllLiving)));
            Assert.IsEmpty(CombatTargetResolver.Resolve(state, Dead, CombatTargetSpec.Self()));
        }

        [Test] public void NoLivingCandidatesIsAValidEmptyResultForEveryAutomaticSelection()
        {
            var state = new CombatState("no-candidates", 0, new[] { Unit(Player, "red", 0) });
            foreach (CombatTargetSelection selection in Enum.GetValues(typeof(CombatTargetSelection)))
            {
                Assert.IsEmpty(CombatTargetResolver.Resolve(state, Player, CombatTargetSpec.Opponents(selection)));
                Assert.IsEmpty(CombatTargetResolver.Resolve(state, Player, CombatTargetSpec.Allies(selection)));
            }
        }

        [Test] public void AllLivingCanReturnTheEntireSixtyFourUnitRosterInOrder()
        {
            var units = Enumerable.Range(0, 64).Select(index => Unit(new ParticipantId("roster/" + index), "red", 1)).ToArray();
            var state = new CombatState("full-roster", 0, units);
            CollectionAssert.AreEqual(units.Select(unit => unit.Id),
                CombatTargetResolver.Resolve(state, units[0].Id, CombatTargetSpec.Allies(CombatTargetSelection.AllLiving)));
        }

        [Test] public void MissingOwnersEventsAndUnknownEventParticipantsAreRejected()
        {
            var state = State();
            Assert.Throws<ArgumentException>(() => CombatTargetResolver.Resolve(state, default(ParticipantId), CombatTargetSpec.Self()));
            Assert.Throws<ArgumentException>(() => CombatTargetResolver.Resolve(state, Missing, CombatTargetSpec.Opponents()));
            Assert.Throws<ArgumentException>(() => CombatTargetResolver.Resolve(state, Player, CombatTargetSpec.EventSource()));
            Assert.Throws<ArgumentException>(() => CombatTargetResolver.Resolve(state, Player, CombatTargetSpec.EventTarget()));
            Assert.Throws<ArgumentNullException>(() => CombatTargetResolver.Resolve(null, Player, CombatTargetSpec.Self()));
            Assert.Throws<ArgumentNullException>(() => CombatTargetResolver.Resolve(state, Player, null));
            var foreign = new CombatState("foreign", 0, new[] { Unit(Player, "red", 40), Unit(Missing, "blue", 40) });
            var foreignSource = Hit(foreign, Missing, Player).RuleEvents.Single();
            var foreignTarget = Hit(foreign, Player, Missing).RuleEvents.Single();
            Assert.Throws<ArgumentException>(() => CombatTargetResolver.Resolve(state, Player, CombatTargetSpec.EventSource(), foreignSource));
            Assert.Throws<ArgumentException>(() => CombatTargetResolver.Resolve(state, Player, CombatTargetSpec.EventTarget(), foreignTarget));
        }

        [Test] public void SelectorsReadTheSuppliedWorkingSnapshotWithoutMutatingEarlierResults()
        {
            var state = State(); var spec = CombatTargetSpec.Opponents(CombatTargetSelection.LowestHealth);
            var original = CombatTargetResolver.Resolve(state, Player, spec);
            var damaged = Hit(state, Player, Other, 25);
            CollectionAssert.AreEqual(new[] { Other }, CombatTargetResolver.Resolve(damaged.State, Player, spec));
            CollectionAssert.AreEqual(new[] { Enemy }, original);
            Assert.AreEqual(30, state.Get(Other).Health); Assert.AreEqual(5, damaged.State.Get(Other).Health);
        }

        [TestCase(EffectTargetSelection.FirstLiving, CombatTargetSelection.FirstLiving)]
        [TestCase(EffectTargetSelection.LowestHealth, CombatTargetSelection.LowestHealth)]
        [TestCase(EffectTargetSelection.HighestHealth, CombatTargetSelection.HighestHealth)]
        [TestCase(EffectTargetSelection.AllLiving, CombatTargetSelection.AllLiving)]
        public void BattleAutomaticSelectionsMatchTheSharedCombatSelectors(EffectTargetSelection battleSelection, CombatTargetSelection combatSelection)
        {
            TeamBattleEngine engine; var state = Battle(out engine);
            foreach (var owner in new[] { Player, Enemy })
            {
                CollectionAssert.AreEqual(CombatTargetResolver.Resolve(state.Combat, owner, CombatTargetSpec.Opponents(combatSelection)),
                    BattleTargetResolver.Resolve(state, owner, EffectTargetSpec.Opponents(battleSelection)));
                CollectionAssert.AreEqual(CombatTargetResolver.Resolve(state.Combat, owner, CombatTargetSpec.Allies(combatSelection)),
                    BattleTargetResolver.Resolve(state, owner, EffectTargetSpec.Allies(battleSelection)));
            }
            CollectionAssert.AreEqual(new[] { Player }, BattleTargetResolver.Resolve(state, Player, EffectTargetSpec.Self()));
        }

        [Test] public void BattleRequestsAndActorPoliciesRetainTheirOriginalValidationAndDefaults()
        {
            TeamBattleEngine engine; var state = Battle(out engine, new OpponentTargetPolicy(OpponentTargetSelection.AllLiving));
            CollectionAssert.AreEqual(new[] { Enemy, Other }, BattleTargetResolver.Resolve(state, Player, EffectTargetSpec.Opponents()));
            CollectionAssert.AreEqual(new[] { Other }, BattleTargetResolver.Resolve(state, Player, EffectTargetSpec.Opponents(), new[] { Other }));
            CollectionAssert.AreEqual(new[] { Ally }, BattleTargetResolver.Resolve(state, Player, EffectTargetSpec.Allies(EffectTargetSelection.Requested), new[] { Ally }));
            CollectionAssert.AreEqual(new[] { Player }, BattleTargetResolver.Resolve(state, Player, EffectTargetSpec.Allies()));
            Assert.Throws<ArgumentException>(() => BattleTargetResolver.Resolve(state, Player, EffectTargetSpec.Opponents(EffectTargetSelection.Requested)));
            Assert.Throws<ArgumentException>(() => BattleTargetResolver.Resolve(state, Player, EffectTargetSpec.Opponents(EffectTargetSelection.Requested), new[] { Enemy, Other }));
            Assert.Throws<ArgumentException>(() => BattleTargetResolver.Resolve(state, Player, EffectTargetSpec.Opponents(), new[] { Ally }));
            Assert.Throws<ArgumentException>(() => BattleTargetResolver.Resolve(state, Player, EffectTargetSpec.Opponents(), new[] { Other, Other }));
            Assert.Throws<ArgumentException>(() => BattleTargetResolver.Resolve(state, Player, EffectTargetSpec.Opponents(), new[] { Missing }));
            Assert.Throws<ArgumentException>(() => BattleTargetResolver.Resolve(state, Player, EffectTargetSpec.Opponents(EffectTargetSelection.AllLiving), new[] { Enemy }));
            Assert.Throws<ArgumentException>(() => BattleTargetResolver.Resolve(state, Player, EffectTargetSpec.Self(), new[] { Player }));
            var invalid = Battle(out engine, new InvalidPolicy());
            Assert.Throws<ArgumentException>(() => BattleTargetResolver.Resolve(invalid, Player, EffectTargetSpec.Opponents()));
        }

        [Test] public void BattleTargetingStillRejectsDefeatedSourcesAndEmptyResults()
        {
            TeamBattleEngine engine; var living = Battle(out engine, playerHealth: 1);
            var playerEnded = engine.Execute(living, living.Revision, Player, ScoredDuelActionKind.EndPlayerTurn);
            Assert.IsTrue(playerEnded.Accepted);
            var allyEnded = engine.Execute(playerEnded.State, playerEnded.State.Revision, Ally, ScoredDuelActionKind.EndPlayerTurn);
            Assert.IsTrue(allyEnded.Accepted);
            var defeated = engine.Execute(allyEnded.State, allyEnded.State.Revision, Enemy, ScoredDuelActionKind.EnemyAction);
            Assert.IsTrue(defeated.Accepted); var deadSource = defeated.State;
            Assert.AreEqual(0, deadSource.Combat.Get(Player).Health);
            CollectionAssert.AreEqual(new[] { Enemy }, CombatTargetResolver.Resolve(deadSource.Combat, Player, CombatTargetSpec.Opponents()));
            Assert.Throws<ArgumentException>(() => BattleTargetResolver.Resolve(deadSource, Player, EffectTargetSpec.Self()));
            Assert.Throws<ArgumentException>(() => BattleTargetResolver.Resolve(deadSource, Player, EffectTargetSpec.Opponents(EffectTargetSelection.FirstLiving)));
            var initial = Battle(out engine, new OpponentTargetPolicy(OpponentTargetSelection.AllLiving));
            var won = engine.Execute(initial, initial.Revision, Player, ScoredDuelActionKind.ScoredHand, ScoreEngine.Fixed(engine.NextScoreId(initial), 1000));
            Assert.IsTrue(won.Accepted); Assert.AreEqual(ScoredDuelPhase.Won, won.State.Phase);
            Assert.IsEmpty(CombatTargetResolver.Resolve(won.State.Combat, Player, CombatTargetSpec.Opponents()));
            Assert.Throws<ArgumentException>(() => BattleTargetResolver.Resolve(won.State, Player, EffectTargetSpec.Opponents(EffectTargetSelection.FirstLiving)));
            Assert.Throws<ArgumentException>(() => BattleTargetResolver.Resolve(won.State, Player, EffectTargetSpec.Opponents(EffectTargetSelection.AllLiving)));
        }
    }
}
