using System;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class BattleContentEffectCompilationTests
    {
        private static BattleContentBuildResult Compile(string chapters) => BattleContentCompiler.CompileXml(
            "<BattleContent schemaVersion='1' id='effects.test' version='test.1'>" + chapters + "</BattleContent>", "effects.xml");
        private static string Hands(string category = null, string replacement = null, string variant = null)
        {
            return "<HandRules><HandRule id='standard'>" + string.Concat(Enum.GetValues(typeof(HandCategory)).Cast<HandCategory>().Select(hand =>
                "<Hand category='" + hand + "'" + (hand.ToString() == category && variant != null ? " variant='" + variant + "'" : "") + ">" +
                (hand.ToString() == category ? replacement : "<Effect id='damage/1' kind='Damage' amount='1' target='Opponents'/>") + "</Hand>")) + "</HandRule></HandRules>";
        }
        private static string Relic(string abilities) => "<Relics><Relic id='relic' charges='10'>" + abilities + "</Relic></Relics>";
        private static void Error(BattleContentBuildResult result)
        {
            Assert.IsNull(result.Content);
            Assert.IsTrue(result.Diagnostics.Any(item => item.Severity == BattleDiagnosticSeverity.Error));
            Assert.IsTrue(result.Diagnostics.All(item => item.Location.SourceName == "effects.xml"));
        }
        private static void Success(BattleContentBuildResult result)
        {
            Assert.IsNotNull(result.Content, string.Join("\n", result.Diagnostics.Select(item => item.Message)));
            Assert.IsFalse(result.Diagnostics.Any(item => item.Severity == BattleDiagnosticSeverity.Error));
        }
        private static ScoredHandEffectContext Context(HandCategory category, HandAttackInfo hand = null)
        {
            var scores = new ScoreProfile("content-test-score", Enum.GetValues(typeof(HandCategory)).Cast<HandCategory>()
                .Select(item => new HandScoreRule(item, 11, 2.5m)));
            var score = ScoreEngine.Evaluate("test-score", scores, category, 1, Array.Empty<ScoringCard>(), Array.Empty<ScoringRelic>());
            var player = new ParticipantId("player");
            var combat = new CombatState("content-test", 0, new[] {
                new CombatantDefinition("hero", 100).Create(player, "player", 40),
                new CombatantDefinition("foe", 100).Create(new ParticipantId("enemy"), "enemy")
            });
            return new ScoredHandEffectContext(combat, player, score, hand);
        }
        private static HandAttackInfo StraightFlush(int high)
        {
            var cards = Enumerable.Range(high - 4, 5).Select(rank => new EffectiveCard(new CardInstanceId("card/" + rank), (Rank)rank, Suit.Hearts)).ToArray();
            var definitions = StandardDeck.Cards().ToArray();
            return new HandAttackInfo(PokerHandClassifier.Classify(cards), cards.Select(card => new AttackCardSnapshot(
                new CardInstance(card.Id, definitions.Single(face => face.Rank == card.Rank && face.Suit == card.Suit).Id), card)));
        }

        [Test] public void StatusDefinitionsCompileAllExistingBehaviorFamilies()
        {
            var result = Compile(@"<Statuses>
              <Status id='stun' tags='Control' lifetime='TriggerCount' count='1'><BlockActionWindow/></Status>
              <Status id='immune' lifetime='ThroughNextPhase' phase='enemy'><PreventStatusApplication tags='Control'/></Status>
              <Status id='reflect'><ReflectBlockedDamage ratio='0.5' sourcePolicy='AllowDefeated'/></Status>
              <Status id='drain'><LifeSteal ratio='0.25'/></Status>
              <Status id='overheal'><OverhealDamage ratio='1' target='Opponents' selection='AllLiving'/></Status>
              <Status id='bleed' lifetime='ThroughCurrentRound'><PeriodicDamage timing='ActionCompleted' amount='3'/></Status>
              <Status id='poison' reapply='IndependentInstances'><PeriodicDamage timing='PhaseEnding' amount='4'/></Status>
            </Statuses>");
            Success(result); Assert.AreEqual(7, result.Content.Statuses.Count);
            Assert.AreEqual(StatusTags.Control, result.Content.Statuses["stun"].Tags);
            Assert.AreEqual(StatusLifetimeKind.ThroughNextPhase, result.Content.Statuses["immune"].DefaultLifetime.Kind);
            var reflected = (ReflectBlockedDamageSpec)result.Content.Statuses["reflect"].Behaviors.Single();
            Assert.AreEqual(0.5m, reflected.Ratio); Assert.AreEqual(CombatTargetGroup.EventSource, reflected.Target.Group);
            Assert.AreEqual(CombatReactionSourcePolicy.AllowDefeated, reflected.SourcePolicy);
            Assert.AreEqual(StatusReapplyPolicy.IndependentInstances, result.Content.Statuses["poison"].ReapplyPolicy);
        }

        [TestCase("<Status id='s'><BlockActionWindow/><BlockActionWindow/></Status>")]
        [TestCase("<Status id='s' lifetime='TriggerCount'><BlockActionWindow/><PreventStatusApplication/></Status>")]
        [TestCase("<Status id='s' lifetime='ThroughNextPhase' phase='unknown'><BlockActionWindow/></Status>")]
        [TestCase("<Status id='s' lifetime='UntilRemoved' count='2'><BlockActionWindow/></Status>")]
        [TestCase("<Status id='s'><PeriodicDamage timing='BattleStarted' amount='1'/></Status>")]
        [TestCase("<Status id='s'><PeriodicDamage timing='PhaseEnding' amount='1' target='EventActor'/></Status>")]
        [TestCase("<Status id='s'><LifeSteal ratio='0.1' sourcePolicy='AllowDefeated'/></Status>")]
        [TestCase("<Status id='s'><LifeSteal/></Status>")]
        [TestCase("<Status id='s'><ReflectBlockedDamage ratio='1' target='EventActor'/></Status>")]
        [TestCase("<Status id='s'><Unexpected/></Status>")]
        public void StatusCompilerRejectsUnsupportedOrAmbiguousBehavior(string status) => Error(Compile("<Statuses>" + status + "</Statuses>"));

        [Test] public void HandAmountsUseAuthoritativeScoreAndPreserveOrderedStepIds()
        {
            var result = Compile(Hands("Flush", "<Effect id='damage/1' kind='Damage' amount='0.25' amountKind='ScoreTotalScale' target='Opponents'/>" +
                "<Effect id='heal' kind='Heal' amount='0.5' amountKind='ScorePointsScale' target='Self'/>"));
            Success(result); var sequence = result.Content.HandRules["standard"].Resolve(Context(HandCategory.Flush));
            CollectionAssert.AreEqual(new[] { "damage/1", "heal" }, sequence.Steps.Select(item => item.Id));
            CollectionAssert.AreEqual(new long[] { 6, 5 }, sequence.Steps.Select(item => item.Effect.Amount));
            Assert.AreEqual(EffectTargetGroup.Self, sequence.Steps[1].Target.Group);
        }

        [Test] public void RoyalAndOrdinaryStraightFlushBranchesRequireAuthoritativeHandFacts()
        {
            string source = Hands("StraightFlush", "<Effect id='damage/1' kind='Damage' amount='9'/>", "Ordinary")
                .Replace("</HandRule>", "<Hand category='StraightFlush' variant='Royal'><Effect id='eliminate' kind='Eliminate' target='Opponents' selection='AllLiving'/></Hand></HandRule>");
            var result = Compile(source); Success(result); var rule = result.Content.HandRules["standard"];
            Assert.AreEqual(CombatEffectKind.Damage, rule.Resolve(Context(HandCategory.StraightFlush, StraightFlush(10))).Steps.Single().Effect.Kind);
            Assert.AreEqual(CombatEffectKind.Eliminate, rule.Resolve(Context(HandCategory.StraightFlush, StraightFlush(14))).Steps.Single().Effect.Kind);
            Assert.Throws<ArgumentException>(() => rule.Resolve(Context(HandCategory.StraightFlush)));
        }

        [TestCase("<Effect id='a' kind='Damage' amount='1.5'/>")]
        [TestCase("<Effect id='a' kind='Damage' amount='-1'/>")]
        [TestCase("<Effect id='a' kind='Damage'/>")]
        [TestCase("<Effect id='a' kind='ClearBlock' amount='1'/>")]
        [TestCase("<Effect id='a' kind='Eliminate' amount='1'/>")]
        [TestCase("<Effect id='a' kind='ApplyStatus' statusRef='missing'/>")]
        [TestCase("<Effect id='a' kind='Damage' amount='1' target='EventActor'/>")]
        [TestCase("<Effect id='a' kind='Damage' amount='1' requestSlot='attack'/>")]
        [TestCase("<Effect id='a' kind='Damage' amount='1'/><Effect id='a' kind='Heal' amount='1'/>")]
        public void HandCompilerRejectsMalformedEffects(string effects) => Error(Compile(Hands("OnePair", effects)));

        [Test] public void HandMappingsRejectMissingCategoryAndOverlappingRoyalMatches()
        {
            Error(Compile("<HandRules><HandRule id='incomplete'><Hand category='HighCard'><Effect id='damage/1' kind='Damage' amount='1'/></Hand></HandRule></HandRules>"));
            Error(Compile(Hands().Replace("</HandRule>", "<Hand category='StraightFlush' variant='Royal'><Effect id='eliminate' kind='Eliminate'/></Hand></HandRule>")));
        }

        [Test] public void ApplicationLifetimeOverridesDoNotMutateSharedStatusDefinition()
        {
            var result = Compile("<Statuses><Status id='stun' tags='Control' lifetime='TriggerCount' count='1'><BlockActionWindow/></Status></Statuses>" +
                Hands("FullHouse", "<Effect id='stun' kind='ApplyStatus' statusRef='stun' stacks='2' lifetime='ThroughNextPhase' phase='enemy'/>") );
            Success(result); var status = result.Content.HandRules["standard"].Resolve(Context(HandCategory.FullHouse)).Steps.Single().Effect.Status;
            Assert.AreEqual(2, status.Stacks); Assert.AreEqual(StatusLifetimeKind.ThroughNextPhase, status.Lifetime.Kind);
            Assert.AreEqual(StatusLifetimeKind.TriggerCount, result.Content.Statuses["stun"].DefaultLifetime.Kind);
        }

        [Test] public void TriggerCountOverrideRejectsMultipleConsumingBehaviors()
        {
            Error(Compile("<Statuses><Status id='mixed'><BlockActionWindow/><PreventStatusApplication/></Status></Statuses>" +
                Hands("FullHouse", "<Effect id='mixed' kind='ApplyStatus' statusRef='mixed' lifetime='TriggerCount' count='1'/>")));
        }

        [TestCase("target='Opponents' selection='Requested' requestSlot='attack'", "amount='2'")]
        [TestCase("target='Opponents'", "amount='0.5' amountKind='ScoreTotalScale'")]
        public void EnemySequencesRejectPlayerOnlyInputs(string target, string amount)
        {
            Error(Compile("<Enemies><Enemy id='enemy' maxHealth='100'><Effects><Effect id='attack' kind='Damage' " + amount + " " + target + "/></Effects></Enemy></Enemies>"));
        }

        [Test] public void EnemyFixedSequenceCompilesToExistingIndependentEffectTargets()
        {
            var result = Compile("<Enemies><Enemy id='enemy' maxHealth='100'><Effects><Effect id='attack' kind='Damage' amount='7' target='Opponents' selection='AllLiving'/>" +
                "<Effect id='shield' kind='GainBlock' amount='2' target='Self'/></Effects></Enemy></Enemies>");
            Success(result); var rule = (ConfiguredEnemyEffectRule)result.Content.Enemies["enemy"].EffectRule;
            Assert.AreEqual(2, rule.Sequence.Steps.Count); Assert.AreEqual(7, rule.Sequence.Steps[0].Effect.Amount);
            Assert.AreEqual(EffectTargetSelection.AllLiving, rule.Sequence.Steps[0].Target.Selection);
            Assert.AreEqual(EffectTargetGroup.Self, rule.Sequence.Steps[1].Target.Group);
        }

        [Test] public void RelicCompilerConnectsAllFourCapabilitiesAndThreeTriggerFamilies()
        {
            var result = Compile(Relic(@"
              <Budget maxUsesPerAction='2' maxUsesPerRound='4' maxUsesPerBoundary='1' chargeCost='2' cooldownRounds='1'/>
              <Numeric id='damage' stat='DamageDealt' operation='AddRate' value='0.2' origins='Primary' hand='OnePair'/>
              <HandModifier id='more' category='OnePair' operation='AddHits' stepId='damage/1' hits='1'/>
              <HandModifier id='append' category='Flush' operation='AppendEffect'><Effect kind='GainBlock' amount='3' target='Self'/></HandModifier>
              <HandModifier id='replace' category='HighCard' operation='ReplaceEffect' stepId='damage/1'><Effect kind='Damage' amount='6'/></HandModifier>
              <HandModifier id='target' category='Straight' operation='ChangeTarget' stepId='damage/1' target='Opponents' selection='AllLiving'/>
              <RuleValue id='energy' key='MaxEnergy' delta='1'/>
              <Refill id='draw' timing='AfterDiscard' mode='FixedCount' fixedCount='2' delta='1'/>
              <FactTrigger id='fact' timing='DamageSettled' relation='OwnerIsSource' occurrence='PerAction' origins='Primary' chain='true'>
                <Conditions><FactAtLeast fact='DamageToHealth' amount='1'/><HandIs category='OnePair'/></Conditions>
                <Effect kind='Heal' amount='2' target='EventSource'/>
              </FactTrigger>
              <BoundaryTrigger id='start' timing='PhaseStarted' phase='OwnerTeam'><Effect kind='GainBlock' amount='2' target='Self'/></BoundaryTrigger>
              <ActionTrigger id='discard' timing='DiscardCompleted' relation='AllyIsActor'>
                <Conditions><DiscardedCardsAtLeast count='2'/></Conditions><Effect kind='Heal' amount='2' target='EventActor'/>
              </ActionTrigger>"));
            Success(result); var relic = result.Content.Relics["relic"];
            Assert.AreEqual(10, relic.Definition.Charges); Assert.AreEqual(1, relic.Rules.Modifiers.Count);
            Assert.AreEqual(4, relic.Rules.HandEffects.Count); Assert.AreEqual(1, relic.Rules.RuleValues.Count); Assert.AreEqual(1, relic.Rules.RefillRules.Count);
            Assert.AreEqual(1, relic.Rules.Triggers.Count); Assert.AreEqual(1, relic.Rules.BoundaryTriggers.Count); Assert.AreEqual(1, relic.Rules.ActionTriggers.Count);
            Assert.AreEqual(2, relic.Rules.TriggerBudget.MaxUsesPerAction); Assert.AreEqual(4, relic.Rules.TriggerBudget.MaxUsesPerRound);
            Assert.AreEqual(2, relic.Rules.TriggerBudget.ChargeCost); Assert.AreEqual(1, relic.Rules.TriggerBudget.CooldownRounds);
            Assert.AreEqual(CombatTargetGroup.EventActor, relic.Rules.ActionTriggers[0].Effects[0].Target.Group);
        }

        [TestCase("<ActionTrigger id='a' timing='DiscardCompleted'><Effect kind='Heal' amount='1' target='EventTarget'/></ActionTrigger>")]
        [TestCase("<ActionTrigger id='a' timing='DiscardCompleted'><Conditions><HandIs category='OnePair'/></Conditions><Effect kind='Heal' amount='1'/></ActionTrigger>")]
        [TestCase("<ActionTrigger id='a' timing='PlayCompleted'><Effect kind='Heal' amount='1' amountKind='ScoreTotalScale'/></ActionTrigger>")]
        [TestCase("<FactTrigger id='a' timing='DamageSettled'><Effect kind='Heal' amount='1' target='EventActor'/></FactTrigger>")]
        [TestCase("<FactTrigger id='a' timing='HealingSettled'><Conditions><FactAtLeast fact='DamageToHealth' amount='1'/></Conditions><Effect kind='Heal' amount='1'/></FactTrigger>")]
        [TestCase("<FactTrigger id='a' timing='DamageSettled' origins='Triggered'><Conditions><HandIs category='OnePair'/></Conditions><Effect kind='Heal' amount='1'/></FactTrigger>")]
        [TestCase("<FactTrigger id='a' timing='DamageSettled' occurrence='PerBoundary'><Conditions><SubmittedCardsAtLeast count='1'/></Conditions><Effect kind='Heal' amount='1'/></FactTrigger>")]
        [TestCase("<BoundaryTrigger id='a' timing='BattleStarted' phase='Any'><Effect kind='Heal' amount='1'/></BoundaryTrigger>")]
        [TestCase("<BoundaryTrigger id='a' timing='PhaseEnding' phase='Any'><Effect kind='Heal' amount='1' target='EventActor'/></BoundaryTrigger>")]
        [TestCase("<Budget maxUsesPerAction='1'/>")]
        [TestCase("<Numeric id='a' stat='DamageDealt' operation='AddRate' value='0.2' origins='Triggered' hand='OnePair'/>")]
        [TestCase("<HandModifier id='a' category='OnePair' operation='ReplaceEffect' stepId='damage/1'><Effect kind='Damage' amount='2' target='Self'/></HandModifier>")]
        [TestCase("<HandModifier id='a' category='OnePair' operation='AddHits' stepId='damage/1' hits='1'><Effect kind='Heal' amount='1'/></HandModifier>")]
        [TestCase("<Refill id='a' timing='AfterDiscard' mode='Skip' fixedCount='2'/>")]
        [TestCase("<RuleValue id='a' delta='1'/>")]
        [TestCase("<Numeric id='a' operation='AddFlat' value='1'/>")]
        [TestCase("<ActionTrigger id='a'><Effect kind='Heal' amount='1'/></ActionTrigger>")]
        public void RelicCompilerRejectsUnavailableContextAndIgnoredFields(string abilities) => Error(Compile(Relic(abilities)));

        [Test] public void UnitDiedCanFilterDamageAmountWithoutRejectingExistingFacts()
        {
            Success(Compile(Relic("<FactTrigger id='kill' timing='UnitDied'><Conditions><FactAtLeast fact='DamageToHealth' amount='1'/></Conditions><Effect kind='Heal' amount='2'/></FactTrigger>")));
        }

        [Test] public void MissingStatusConditionReferenceAndDuplicateAbilityIdsAreErrors()
        {
            Error(Compile(Relic("<ActionTrigger id='a' timing='PlayCompleted'><Conditions><OwnerHasStatus statusRef='missing'/></Conditions><Effect kind='Heal' amount='1'/></ActionTrigger>")));
            Error(Compile(Relic("<RuleValue id='duplicate' key='MaxEnergy' delta='1'/><RuleValue id='duplicate' key='HandLimit' delta='1'/>")));
        }

        [Test] public void ScoreExpressionsUseCheckedArithmetic()
        {
            var amount = new EffectAmountSpec(EffectAmountKind.ScoreTotalScale, decimal.MaxValue);
            Assert.Throws<OverflowException>(() => amount.Resolve(Context(HandCategory.HighCard).Score));
            Assert.Throws<ArgumentException>(() => amount.Resolve());
        }
    }
}
