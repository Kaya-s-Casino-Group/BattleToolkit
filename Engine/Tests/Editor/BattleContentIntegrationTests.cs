using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class BattleContentIntegrationTests
    {
        private static readonly ParticipantId Player = new ParticipantId("p"), Enemy = new ParticipantId("e");
        private const string Minimal =
            "<BattleContent schemaVersion=\"1\" id=\"pack\" version=\"v1\">" +
            "<ScoringProfiles><Scoring id=\"score\" preset=\"Default\"/></ScoringProfiles>" +
            "<BattleRules><Rules id=\"rules\" scoringRef=\"score\" roundLimit=\"20\" blockExpiry=\"Retain\"/></BattleRules>" +
            "<Players><Player id=\"hero\" maxHealth=\"100\" handSize=\"8\" initialEnergy=\"3\" maxEnergy=\"3\"/></Players>" +
            "<Enemies><Enemy id=\"foe\" maxHealth=\"1000\" damage=\"7\"/></Enemies>" +
            "<Encounters><Encounter id=\"fight\" rulesRef=\"rules\"><Player id=\"p\" ref=\"hero\"/><Enemy id=\"e\" ref=\"foe\"/></Encounter></Encounters>" +
            "</BattleContent>";

        private static string Extra(string xml, string content) => xml.Replace("</BattleContent>", content + "</BattleContent>");
        private static string AllHands(string effect)
        {
            return "<HandRules><HandRule id=\"hands\">" + string.Concat(Enum.GetValues(typeof(HandCategory)).Cast<HandCategory>().Select(category =>
                category == HandCategory.StraightFlush ?
                "<Hand category=\"StraightFlush\" variant=\"Ordinary\">" + effect + "</Hand><Hand category=\"StraightFlush\" variant=\"Royal\">" + effect + "</Hand>" :
                "<Hand category=\"" + category + "\">" + effect + "</Hand>")) + "</HandRule></HandRules>";
        }
        private static string WithHand(string effect) => Extra(Minimal.Replace("id=\"hero\" maxHealth", "id=\"hero\" handRuleRef=\"hands\" maxHealth"), AllHands(effect));
        private static string WithRelic(string ability, string definitionAttributes = "charges=\"5\"", string budget = "") =>
            Extra(Minimal.Replace("id=\"p\" ref=\"hero\"", "id=\"p\" ref=\"hero\" loadoutRef=\"build\""),
                "<Relics><Relic id=\"r\" " + definitionAttributes + ">" + budget + ability + "</Relic></Relics>" +
                "<Loadouts><Loadout id=\"build\"><Relic instanceId=\"healer\" ref=\"r\" level=\"1\"/></Loadout></Loadouts>");
        private const string DiscardHealing = "<ActionTrigger id=\"after\" timing=\"DiscardCompleted\" relation=\"OwnerIsActor\"><Effect kind=\"Heal\" amount=\"3\" target=\"EventActor\"/></ActionTrigger>";

        private static string Explain(IEnumerable<BattleDiagnostic> diagnostics) => string.Join("\n", diagnostics.Select(item =>
            item.Code + ": " + item.Message + " at " + item.Location.Path));
        private static CompiledBattleContent Compile(string xml = Minimal)
        {
            var result = BattleContentCompiler.CompileXml(xml, "integration.xml");
            Assert.IsTrue(result.Success, Explain(result.Diagnostics)); Assert.IsNotNull(result.Content);
            return result.Content;
        }
        private static BattleSession Assemble(CompiledBattleContent content, string battleId = "battle")
        {
            var result = BattleContentFactory.Create(content,
                new BattleSetupRequest("fight", battleId, HmacDeckShuffler.FromSeed(new byte[32])));
            Assert.IsTrue(result.Success, Explain(result.Diagnostics)); Assert.IsNotNull(result.Session);
            return result.Session;
        }
        private static BattleSessionResult Act(BattleSession session, CardDuelAction action, int count = 0)
        {
            var cards = session.State.Zone(CardZoneKinds.Hand, Player).Cards.Take(count).ToArray();
            var result = session.Execute(session.State.Revision, Player, action, cards);
            Assert.IsTrue(result.Accepted, action + ": " + result.Error);
            return result;
        }
        private static void Reject(string xml)
        {
            var result = BattleContentCompiler.CompileXml(xml, "invalid.xml");
            Assert.IsFalse(result.Success); Assert.IsNull(result.Content);
            Assert.IsTrue(result.Diagnostics.Any(item => item.Severity == BattleDiagnosticSeverity.Error));
            Assert.IsTrue(result.Diagnostics.All(item => item.Location.SourceName == "invalid.xml"));
            Assert.IsTrue(result.Diagnostics.All(item => !string.IsNullOrWhiteSpace(item.Code) && !string.IsNullOrWhiteSpace(item.Message)));
        }

        [Test] public void ReadCompileAndAssembleRemainInThePureRulesAssembly()
        {
            var read = BattleContentXmlReader.Read(Minimal, "integration.xml");
            Assert.IsTrue(read.Success, Explain(read.Diagnostics)); Assert.IsNotNull(read.Source);
            var result = BattleContentCompiler.Compile(read.Source);
            Assert.IsTrue(result.Success, Explain(result.Diagnostics));
            Assert.AreEqual("pack", result.Content.Id); Assert.AreEqual("v1", result.Content.Version);
            Assert.IsFalse(string.IsNullOrWhiteSpace(result.Content.Fingerprint));
            Assert.IsFalse(string.IsNullOrWhiteSpace(result.Content.RulesContractVersion));
            Assert.AreEqual("PokerEngine.Composition", typeof(BattleContentCompiler).Assembly.GetName().Name);
            Assert.IsFalse(typeof(BattleContentCompiler).Assembly.GetReferencedAssemblies().Any(item =>
                item.Name.StartsWith("UnityEngine", StringComparison.Ordinal) || item.Name.StartsWith("UnityEditor", StringComparison.Ordinal)));
            Assert.IsFalse(Assemble(result.Content).State.HasStarted);
        }

        [Test] public void SourceTreeAndDiagnosticsAreImmutableSnapshots()
        {
            var read = BattleContentXmlReader.Read(Minimal);
            Assert.IsTrue(read.Success);
            Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)read.Source.Root.Attributes)["id"] = "changed");
            Assert.Throws<NotSupportedException>(() => ((IList<BattleContentNode>)read.Source.Root.Children).Clear());
            var invalid = BattleContentXmlReader.Read("<BattleContent>");
            Assert.Throws<NotSupportedException>(() => ((IList<BattleDiagnostic>)invalid.Diagnostics).Clear());
        }

        [Test] public void MalformedXmlReportsSourceLineWithoutReturningPartialSource()
        {
            var result = BattleContentXmlReader.Read("<BattleContent schemaVersion=\"1\" id=\"p\" version=\"1\">\n<Players>\n</BattleContent>", "broken.xml");
            Assert.IsFalse(result.Success); Assert.IsNull(result.Source); Assert.IsNotEmpty(result.Diagnostics);
            Assert.AreEqual("broken.xml", result.Diagnostics[0].Location.SourceName);
            Assert.Greater(result.Diagnostics[0].Location.Line, 1); Assert.Greater(result.Diagnostics[0].Location.Column, 0);
        }

        [TestCase("<!DOCTYPE BattleContent [<!ENTITY secret SYSTEM 'file:///definitely-not-a-config-file'>]><BattleContent schemaVersion='1' id='p' version='1'>&secret;</BattleContent>")]
        [TestCase("<!DOCTYPE BattleContent SYSTEM 'https://example.invalid/content.dtd'><BattleContent schemaVersion='1' id='p' version='1'/>")]
        [TestCase("<?include file='another.xml'?><BattleContent schemaVersion='1' id='p' version='1'/>")]
        [TestCase("<BattleContent xmlns='urn:foreign' schemaVersion='1' id='p' version='1'/>")]
        public void ReaderDoesNotInterpretExternalResourcesOrNamespaces(string xml)
        {
            var result = BattleContentXmlReader.Read(xml);
            Assert.IsFalse(result.Success); Assert.IsNull(result.Source); Assert.IsNotEmpty(result.Diagnostics);
        }

        [TestCase("")]
        [TestCase("<Other schemaVersion='1' id='p' version='1'/>")]
        [TestCase("<BattleContent schemaVersion='2' id='p' version='1'/>")]
        [TestCase("<BattleContent schemaVersion='1' version='1'/>")]
        [TestCase("<BattleContent schemaVersion='1' id='p' version='1'>not a field</BattleContent>")]
        public void ReaderRejectsInvalidRootContracts(string xml) => Reject(xml);

        [Test] public void ReaderBoundsDocumentSizeAndNesting()
        {
            Assert.IsFalse(BattleContentXmlReader.Read(new string(' ', BattleContentXmlReader.MaximumDocumentCharacters + 1)).Success);
            var nested = "<BattleContent schemaVersion='1' id='p' version='1'>" +
                string.Concat(Enumerable.Repeat("<Nested>", BattleContentXmlReader.MaximumElementDepth)) +
                string.Concat(Enumerable.Repeat("</Nested>", BattleContentXmlReader.MaximumElementDepth)) + "</BattleContent>";
            Assert.IsFalse(BattleContentXmlReader.Read(nested).Success);
        }

        [Test] public void ContentFingerprintIgnoresPresentationWhitespaceAndAttributeOrdering()
        {
            string reordered = Minimal.Replace("schemaVersion=\"1\" id=\"pack\" version=\"v1\"", "version=\"v1\" id=\"pack\" schemaVersion=\"1\"")
                .Replace("><", ">\n  <");
            Assert.AreEqual(Compile().Fingerprint, Compile(reordered).Fingerprint);
        }

        [Test] public void ContentFingerprintIgnoresDisplayNamesButTracksRuleAmounts()
        {
            var first = WithHand("<Effect id=\"damage/1\" kind=\"Damage\" amount=\"10\" target=\"Opponents\" selection=\"FirstLiving\"/>");
            Assert.AreEqual(Compile(first).Fingerprint, Compile(first.Replace("id=\"hero\"", "id=\"hero\" name=\"改名\"")).Fingerprint);
            Assert.AreNotEqual(Compile(first).Fingerprint, Compile(first.Replace("amount=\"10\"", "amount=\"11\"")).Fingerprint);
        }

        [Test] public void ContentFingerprintIgnoresCatalogAndDefinitionOrder()
        {
            const string scores = "<ScoringProfiles><Scoring id=\"score\" preset=\"Default\"/></ScoringProfiles>";
            const string anotherPlayer = "<Player id=\"another\" maxHealth=\"90\"/>";
            var first = Minimal.Replace("</Players>", anotherPlayer + "</Players>");
            var second = Extra(Minimal.Replace(scores, "").Replace("<Players>", "<Players>" + anotherPlayer), scores);
            Assert.AreEqual(Compile(first).Fingerprint, Compile(second).Fingerprint);
        }

        [Test] public void EffectOrderIsPartOfContentIdentity()
        {
            const string hit = "<Effect id=\"damage/1\" kind=\"Damage\" amount=\"10\" target=\"Opponents\" selection=\"FirstLiving\"/>";
            const string shield = "<Effect id=\"shield\" kind=\"GainBlock\" amount=\"3\" target=\"Self\"/>";
            Assert.AreNotEqual(Compile(WithHand(hit + shield)).Fingerprint, Compile(WithHand(shield + hit)).Fingerprint);
        }

        [Test] public void AssemblyDoesNotStartOrMutateSuppliedRandomState()
        {
            var random = HmacDeckShuffler.FromSeed(new byte[32]); var before = random.StateBytes.ToArray();
            var result = BattleContentFactory.Create(Compile(), new BattleSetupRequest("fight", "battle", random));
            Assert.IsTrue(result.Success, Explain(result.Diagnostics)); CollectionAssert.AreEqual(before, random.StateBytes);
            Assert.IsFalse(result.Session.State.HasStarted); Assert.AreEqual(0, result.Session.State.Revision);
            Assert.IsEmpty(result.Session.State.Duel.Actions); Assert.IsEmpty(result.Session.State.Zone(CardZoneKinds.Hand, Player).Cards);
            Assert.AreEqual(52, result.Session.State.Zone(CardZoneKinds.Deck, Player).Cards.Count);
        }

        [Test] public void MinimalContentPreservesEmptyDiscardRewardAndExistingRefill()
        {
            var session = Assemble(Compile()); Act(session, CardDuelAction.Start);
            var result = Act(session, CardDuelAction.Discard, 2);
            Assert.AreEqual(8, session.State.Zone(CardZoneKinds.Hand, Player).Cards.Count);
            Assert.AreEqual(2, session.State.DiscardsFor(Player)); Assert.AreEqual(2, session.State.Duel.Player(Player).Energy.Energy);
            Assert.AreEqual(0, session.State.Duel.Combat.Get(Player).Block); Assert.AreEqual(100, session.State.Duel.Combat.Get(Player).Health);
            Assert.IsTrue(result.Steps.All(step => step.Transition.RelicActivations.Count == 0 && step.Transition.ActionCompletion == null));
        }

        [Test] public void ConfiguredHandUsesStableStepsAndRealSessionSettlement()
        {
            var session = Assemble(Compile(WithHand("<Effect id=\"damage/1\" kind=\"Damage\" amount=\"10\" target=\"Opponents\" selection=\"FirstLiving\"/>")));
            Act(session, CardDuelAction.Start); var result = Act(session, CardDuelAction.Play, 1);
            Assert.AreEqual(990, session.State.Duel.Combat.Get(Enemy).Health);
            Assert.AreEqual(2, session.State.Duel.Player(Player).Energy.Energy);
            Assert.AreEqual(7, session.State.Zone(CardZoneKinds.Hand, Player).Cards.Count);
            Assert.IsEmpty(session.State.Zone(CardZoneKinds.Played, Player).Cards);
            CollectionAssert.AreEqual(new[] { CardDuelAction.Play, CardDuelAction.CollectPlayed }, result.Steps.Select(step => step.Action));
        }

        [Test] public void CustomScoreDeckAndHandLevelFeedAuthorityAmountsInTheRealBattle()
        {
            var score = "<Scoring id=\"score\">" + string.Concat(Enum.GetValues(typeof(HandCategory)).Cast<HandCategory>().Select(category =>
                "<Hand category=\"" + category + "\" points=\"7\" multiplier=\"2\" pointsPerLevel=\"3\" multiplierPerLevel=\"1\"/>")) +
                "<CardPoints>" + string.Concat(Enum.GetValues(typeof(Rank)).Cast<Rank>().Select(rank =>
                "<Rank rank=\"" + rank + "\" points=\"1\"/>")) + "<Face rank=\"Ace\" suit=\"Clubs\" points=\"9\"/></CardPoints></Scoring>";
            var xml = WithHand("<Effect id=\"damage/1\" kind=\"Damage\" amountKind=\"ScoreTotalScale\" amount=\"0.5\" target=\"Opponents\"/>" +
                "<Effect id=\"shield\" kind=\"GainBlock\" amountKind=\"ScorePointsScale\" amount=\"0.2\" target=\"Self\"/>")
                .Replace("<Scoring id=\"score\" preset=\"Default\"/>", score)
                .Replace("id=\"p\" ref=\"hero\"", "id=\"p\" ref=\"hero\" loadoutRef=\"build\"");
            xml = Extra(xml, "<Decks><Deck id=\"aces\">" + string.Concat(Enumerable.Range(0, 8).Select(index =>
                "<Card id=\"ace/" + index + "\" face=\"standard:1:14\"/>")) + "</Deck></Decks>" +
                "<Loadouts><Loadout id=\"build\" deckRef=\"aces\"><HandLevel category=\"HighCard\" level=\"2\"/></Loadout></Loadouts>");
            var session = Assemble(Compile(xml)); Act(session, CardDuelAction.Start);
            Assert.AreEqual(8, session.State.Table.Cards.Count);
            var result = Act(session, CardDuelAction.Play, 1);
            // Level two: (7 + 3 + face override 9) * (2 + 1) = 57; floor(57 * .5) = 28.
            Assert.AreEqual(57, result.Steps[0].Transition.Score.Total);
            Assert.AreEqual(972, session.State.Duel.Combat.Get(Enemy).Health);
            Assert.AreEqual(3, session.State.Duel.Combat.Get(Player).Block);
        }

        [Test] public void StatusReferencesUseTheRealControlAndExpiryPipeline()
        {
            var xml = Extra(WithHand("<Effect id=\"control\" kind=\"ApplyStatus\" statusRef=\"stun\" target=\"Opponents\" selection=\"FirstLiving\"/>"),
                "<Statuses><Status id=\"stun\" tags=\"Control\" lifetime=\"TriggerCount\" count=\"1\"><BlockActionWindow/></Status></Statuses>");
            var session = Assemble(Compile(xml)); Act(session, CardDuelAction.Start); Act(session, CardDuelAction.Play, 1);
            Assert.AreEqual(1, session.State.Duel.Combat.Statuses.Count);
            Act(session, CardDuelAction.EndTurn);
            Assert.AreEqual(100, session.State.Duel.Combat.Get(Player).Health); Assert.IsEmpty(session.State.Duel.Combat.Statuses);
            Assert.AreEqual(1, session.State.Duel.CompletedRounds);
        }

        [Test] public void XmlCompletionTriggerBindsEventActorAndConsumesSharedCharges()
        {
            var xml = WithRelic(DiscardHealing, budget: "<Budget maxUsesPerAction=\"1\"/>");
            var session = Assemble(Compile(xml)); Act(session, CardDuelAction.Start); Act(session, CardDuelAction.EndTurn);
            Assert.AreEqual(93, session.State.Duel.Combat.Get(Player).Health);
            var result = Act(session, CardDuelAction.Discard, 1);
            Assert.AreEqual(96, session.State.Duel.Combat.Get(Player).Health);
            Assert.AreEqual(4, session.State.Duel.Player(Player).Relics.Relics.Single().RemainingCharges);
            var activation = result.Steps.SelectMany(step => step.Transition.RelicActivations).Single();
            Assert.AreEqual(RelicActionTiming.DiscardCompleted, activation.ActionTiming);
            Assert.IsTrue(activation.Batch.Reactions.All(reaction => reaction.Effect.Target == Player));
        }

        [Test] public void FreshAssembliesHaveIndependentRelicRuntimeAndCardState()
        {
            var content = Compile(WithRelic(DiscardHealing)); var first = Assemble(content); var second = Assemble(content);
            Act(first, CardDuelAction.Start); Act(second, CardDuelAction.Start); var unchanged = second.State;
            Act(first, CardDuelAction.Discard, 1);
            Assert.AreSame(unchanged, second.State); Assert.AreEqual(5, second.State.Duel.Player(Player).Relics.Relics.Single().RemainingCharges);
            Assert.AreEqual(4, first.State.Duel.Player(Player).Relics.Relics.Single().RemainingCharges);
            Assert.AreEqual(3, second.State.Duel.Player(Player).Energy.Energy);
        }

        [Test] public void AllFourRelicAbilitiesAndThreeTriggerWindowsComposeInTheSameBattle()
        {
            var xml = WithRelic(
                "<Numeric id=\"flat\" stat=\"DamageDealt\" operation=\"AddFlat\" value=\"2\"/>" +
                "<Numeric id=\"rate\" stat=\"DamageDealt\" operation=\"AddRate\" value=\"0.5\"/>" +
                "<HandModifier id=\"extra-hit\" category=\"HighCard\" operation=\"AddHits\" stepId=\"damage/1\" hits=\"1\"/>" +
                "<RuleValue id=\"free\" key=\"PlayEnergyCost\" delta=\"-1\"/>" +
                "<RuleValue id=\"larger-hand\" key=\"HandLimit\" delta=\"1\"/>" +
                "<Refill id=\"play-refill\" timing=\"AfterPlay\" mode=\"FillToLimit\"/>" +
                "<BoundaryTrigger id=\"opening\" timing=\"BattleStarted\"><Effect kind=\"GainBlock\" amount=\"1\" target=\"Self\"/></BoundaryTrigger>" +
                "<FactTrigger id=\"hit\" timing=\"DamageSettled\" relation=\"OwnerIsSource\" occurrence=\"PerAction\"><Effect kind=\"GainBlock\" amount=\"2\" target=\"Self\"/></FactTrigger>" +
                "<ActionTrigger id=\"played\" timing=\"PlayCompleted\"><Effect kind=\"GainBlock\" amount=\"3\" target=\"EventActor\"/></ActionTrigger>",
                budget: "<Budget maxUsesPerAction=\"2\" maxUsesPerBoundary=\"1\"/>");
            xml = Extra(xml.Replace("id=\"hero\" maxHealth", "id=\"hero\" handRuleRef=\"hands\" maxHealth"),
                AllHands("<Effect id=\"damage/1\" kind=\"Damage\" amount=\"10\" target=\"Opponents\" selection=\"FirstLiving\"/>"));
            var session = Assemble(Compile(xml)); Act(session, CardDuelAction.Start);
            Assert.AreEqual(1, session.State.Duel.Combat.Get(Player).Block);
            Assert.AreEqual(4, session.State.Duel.Player(Player).Relics.Relics.Single().RemainingCharges);
            Assert.AreEqual(9, session.State.Zone(CardZoneKinds.Hand, Player).Cards.Count);
            var result = Act(session, CardDuelAction.Play, 1);
            // Configuration order is sequential: ((10 + 2) * 1.5) twice.
            Assert.AreEqual(964, session.State.Duel.Combat.Get(Enemy).Health);
            Assert.AreEqual(6, session.State.Duel.Combat.Get(Player).Block);
            Assert.AreEqual(3, session.State.Duel.Player(Player).Energy.Energy);
            Assert.AreEqual(9, session.State.Zone(CardZoneKinds.Hand, Player).Cards.Count);
            Assert.AreEqual(2, session.State.Duel.Player(Player).Relics.Relics.Single().RemainingCharges);
            CollectionAssert.AreEqual(new[] { "hit", "played" }, result.Steps.SelectMany(step => step.Transition.RelicActivations).Select(item => item.TriggerId));
        }

        [Test] public void ReusedLoadoutCreatesDistinctPerParticipantRelicInstances()
        {
            var xml = WithRelic(DiscardHealing).Replace("<Enemy id=\"e\" ref=\"foe\"/>",
                "<Player id=\"p2\" ref=\"hero\" loadoutRef=\"build\"/><Enemy id=\"e\" ref=\"foe\"/>");
            var session = Assemble(Compile(xml)); Act(session, CardDuelAction.Start);
            var second = new ParticipantId("p2");
            Assert.AreNotEqual(session.State.Duel.Player(Player).Relics.Relics.Single().InstanceId,
                session.State.Duel.Player(second).Relics.Relics.Single().InstanceId);
            Act(session, CardDuelAction.Discard, 1);
            Assert.AreEqual(4, session.State.Duel.Player(Player).Relics.Relics.Single().RemainingCharges);
            Assert.AreEqual(5, session.State.Duel.Player(second).Relics.Relics.Single().RemainingCharges);
        }

        [Test] public void InitialHealthBelongsToEncounterSlotsAndCannotMutateTheSharedTemplate()
        {
            var xml = Minimal.Replace("<Player id=\"p\" ref=\"hero\"/>",
                "<Player id=\"p\" ref=\"hero\" initialHealth=\"60\"/><Player id=\"p2\" ref=\"hero\"/>");
            var session = Assemble(Compile(xml));
            Assert.AreEqual(60, session.State.Duel.Combat.Get(Player).Health);
            Assert.AreEqual(100, session.State.Duel.Combat.Get(new ParticipantId("p2")).Health);
            Reject(Minimal.Replace("id=\"hero\" maxHealth", "id=\"hero\" initialHealth=\"60\" maxHealth"));
        }

        [Test] public void EffectiveLoadoutLimitsAreValidatedBeforeTheEncounterCanRun()
        {
            Reject(WithRelic("<RuleValue id=\"too-many-cards\" key=\"HandLimit\" delta=\"45\"/>"));
            Reject(WithRelic("<Refill id=\"first\" timing=\"AfterPlay\" mode=\"FillToLimit\"/>" +
                "<Refill id=\"second\" timing=\"AfterPlay\" mode=\"Skip\"/>"));
        }

        [Test] public void EnemyEffectSequenceUsesExistingEffectAndTargetRules()
        {
            var xml = Minimal.Replace("<Enemy id=\"foe\" maxHealth=\"1000\" damage=\"7\"/>",
                "<Enemy id=\"foe\" maxHealth=\"1000\"><Effects><Effect id=\"attack\" kind=\"Damage\" amount=\"12\" target=\"Opponents\" selection=\"FirstLiving\"/>" +
                "<Effect id=\"block\" kind=\"GainBlock\" amount=\"5\" target=\"Self\"/></Effects></Enemy>");
            var session = Assemble(Compile(xml)); Act(session, CardDuelAction.Start); Act(session, CardDuelAction.EndTurn);
            Assert.AreEqual(88, session.State.Duel.Combat.Get(Player).Health); Assert.AreEqual(5, session.State.Duel.Combat.Get(Enemy).Block);
        }

        [Test] public void MissingEncounterReturnsDiagnosticsInsteadOfCreatingAPartialSession()
        {
            var result = BattleContentFactory.Create(Compile(), new BattleSetupRequest("missing", "battle", HmacDeckShuffler.FromSeed(new byte[32])));
            Assert.IsFalse(result.Success); Assert.IsNull(result.Session); Assert.IsNotEmpty(result.Diagnostics);
        }

        [Test] public void ShippedXmlExampleCompilesAndRunsThroughTheActualSession()
        {
            const string path = "Engine/Runtime/Composition/Content/Examples/battle-content.xml";
            var compiled = BattleContentCompiler.CompileXml(ToolkitTestFiles.Read(path), path);
            Assert.IsTrue(compiled.Success, Explain(compiled.Diagnostics));
            var assembled = BattleContentFactory.Create(compiled.Content,
                new BattleSetupRequest("example-fight", "example-test", HmacDeckShuffler.FromSeed(new byte[32])));
            Assert.IsTrue(assembled.Success, Explain(assembled.Diagnostics));
            var session = assembled.Session;
            Assert.AreEqual(80, session.State.Duel.Combat.Get(Player).Health);
            Assert.AreEqual(4, session.State.Duel.Player(Player).Energy.MaxEnergy);
            Act(session, CardDuelAction.Start); Assert.AreEqual(8, session.State.Zone(CardZoneKinds.Hand, Player).Cards.Count);
            Act(session, CardDuelAction.Discard, 1);
            Assert.AreEqual(1, session.State.Duel.Combat.Get(Player).Block);
            Assert.AreEqual(2, session.State.Duel.Player(Player).Relics.Relics.Single().RemainingCharges);
        }

        [TestCase("scoringRef=\"score\"", "scoringRef=\"missing\"")]
        [TestCase("rulesRef=\"rules\"", "rulesRef=\"missing\"")]
        [TestCase("ref=\"hero\"", "ref=\"missing\"")]
        [TestCase("ref=\"foe\"", "ref=\"missing\"")]
        [TestCase("maxHealth=\"100\"", "maxHealth=\"0\"")]
        [TestCase("roundLimit=\"20\"", "roundLimit=\"1001\"")]
        [TestCase("handSize=\"8\"", "handSize=\"53\"")]
        [TestCase("maxEnergy=\"3\"", "maxEnergy=\"-1\"")]
        [TestCase("handSize=\"8\"", "HandSize=\"8\"")]
        [TestCase("blockExpiry=\"Retain\"", "blockExpiry=\"retian\"")]
        [TestCase("damage=\"7\"", "damage=\"-1\"")]
        [TestCase("id=\"hero\"", "id=\"hero\" unexpected=\"true\"")]
        public void InvalidReferencesFieldsAndRangesFailCompilation(string before, string after) => Reject(Minimal.Replace(before, after));

        [TestCase("<Decks><Deck id='bad'><Card id='a' face='standard:9:99'/></Deck></Decks>")]
        [TestCase("<Decks><Deck id='bad'><Card id='a' face='standard:1:2'/><Card id='a' face='standard:2:3'/></Deck></Decks>")]
        [TestCase("<Loadouts><Loadout id='bad'><HandLevel category='HighCard' level='1'/><HandLevel category='HighCard' level='2'/></Loadout></Loadouts>")]
        [TestCase("<Loadouts><Loadout id='bad'><HandLevel level='2'/></Loadout></Loadouts>")]
        public void InvalidDeckOriginsAndHandLevelKeysCannotHideInUnusedDefinitions(string content) => Reject(Extra(Minimal, content));

        [Test] public void DuplicateDefinitionsAndParticipantIdentitiesAreRejected()
        {
            Reject(Minimal.Replace("</Players>", "<Player id=\"hero\" maxHealth=\"100\"/></Players>"));
            Reject(Minimal.Replace("<Enemy id=\"e\" ref=\"foe\"", "<Enemy id=\"p\" ref=\"foe\""));
            Reject(Minimal.Replace("</BattleContent>", "<InventedRules/></BattleContent>"));
        }

        [Test] public void TriggerAndActionTargetsCannotBorrowUnavailableEventFacts()
        {
            Reject(WithRelic(DiscardHealing.Replace("target=\"EventActor\"", "target=\"EventTarget\"")));
            Reject(WithHand("<Effect id=\"heal\" kind=\"Heal\" amount=\"3\" target=\"EventActor\"/>"));
            Reject(WithRelic("<BoundaryTrigger id=\"begin\" timing=\"BattleStarted\"><Effect kind=\"Heal\" amount=\"3\" target=\"EventActor\"/></BoundaryTrigger>"));
        }

        [Test] public void MissingStatusAndIncompleteHandCoverageAreCompileErrors()
        {
            Reject(WithHand("<Effect id=\"control\" kind=\"ApplyStatus\" statusRef=\"missing\" target=\"Self\"/>"));
            var xml = Extra(Minimal.Replace("id=\"hero\" maxHealth", "id=\"hero\" handRuleRef=\"hands\" maxHealth"),
                "<HandRules><HandRule id=\"hands\"><Hand category=\"HighCard\"><Effect id=\"damage/1\" kind=\"Damage\" amount=\"10\" target=\"Opponents\"/></Hand></HandRule></HandRules>");
            Reject(xml);
        }

        [Test] public void IndependentConfigurationErrorsAreReportedTogether()
        {
            var xml = Minimal.Replace("scoringRef=\"score\"", "scoringRef=\"missing-score\"")
                .Replace("ref=\"hero\"", "ref=\"missing-hero\"");
            var result = BattleContentCompiler.CompileXml(xml, "multiple.xml");
            Assert.IsFalse(result.Success); Assert.IsNull(result.Content);
            Assert.GreaterOrEqual(result.Diagnostics.Count(item => item.Severity == BattleDiagnosticSeverity.Error), 2);
        }

        [Test] public void DefaultXmlAssemblyMatchesDirectLegacyConstructionAcrossPlayDiscardAndRounds()
        {
            var assembled = Assemble(Compile());
            var engine = new CardDuelEngine(new BattleRules("legacy", 20));
            var direct = new BattleSession(engine, engine.Create("battle", HmacDeckShuffler.FromSeed(new byte[32]),
                new[] { new BattlePlayerSetup(Player, new CombatantDefinition("hero", 100), rules: new BattlePlayerRules()) },
                new[] { new BattleEnemySetup(Enemy, new CombatantDefinition("foe", 1000), 7) }));
            SameBattle(direct.State, assembled.State);
            foreach (var action in new[] { CardDuelAction.Start, CardDuelAction.Discard, CardDuelAction.Play, CardDuelAction.EndTurn })
            {
                int count = action == CardDuelAction.Discard || action == CardDuelAction.Play ? 1 : 0;
                var expected = Act(direct, action, count); var actual = Act(assembled, action, count);
                CollectionAssert.AreEqual(expected.Steps.Select(step => step.Action), actual.Steps.Select(step => step.Action));
                CollectionAssert.AreEqual(expected.Steps.SelectMany(step => step.Transition.RuleEvents).Select(item => item.GetType()),
                    actual.Steps.SelectMany(step => step.Transition.RuleEvents).Select(item => item.GetType()));
                SameBattle(direct.State, assembled.State);
            }
        }

        private static void SameBattle(CardDuelState expected, CardDuelState actual)
        {
            Assert.AreEqual(expected.Revision, actual.Revision); Assert.AreEqual(expected.HasStarted, actual.HasStarted);
            Assert.AreEqual(expected.Duel.Revision, actual.Duel.Revision); Assert.AreEqual(expected.Duel.Phase, actual.Duel.Phase);
            Assert.AreEqual(expected.Duel.CompletedRounds, actual.Duel.CompletedRounds);
            CollectionAssert.AreEqual(expected.Random.StateBytes, actual.Random.StateBytes);
            foreach (var zone in expected.Table.Zones) CollectionAssert.AreEqual(zone.Cards, actual.Table.GetZone(zone.Id).Cards);
            foreach (var unit in expected.Duel.Combat.Combatants)
            { Assert.AreEqual(unit.Health, actual.Duel.Combat.Get(unit.Id).Health); Assert.AreEqual(unit.Block, actual.Duel.Combat.Get(unit.Id).Block); }
            Assert.AreEqual(expected.Duel.Player(Player).Energy.Energy, actual.Duel.Player(Player).Energy.Energy);
            Assert.AreEqual(expected.DiscardsFor(Player), actual.DiscardsFor(Player));
            CollectionAssert.AreEqual(expected.Duel.Scores.Select(score => score.Total), actual.Duel.Scores.Select(score => score.Total));
        }
    }
}
