using System;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class BattleContentCombinationTests
    {
        private const string Damage = "<Effect id=\"damage\" kind=\"Damage\" amount=\"1\"/>";
        private const string Heal = "<Effect id=\"heal\" kind=\"Heal\" amount=\"1\" target=\"Self\"/>";
        private static string Pack(string modifiers, string highCard = Damage, string ordinary = Damage, string royal = null,
            string additionalRelics = "", string additionalSlots = "", bool equipped = true)
        {
            var hands = string.Concat(Enum.GetValues(typeof(HandCategory)).Cast<HandCategory>().Select(category =>
            {
                if (category == HandCategory.StraightFlush && royal != null)
                    return "<Hand category=\"StraightFlush\" variant=\"Ordinary\">" + ordinary + "</Hand>" +
                        "<Hand category=\"StraightFlush\" variant=\"Royal\">" + royal + "</Hand>";
                return "<Hand category=\"" + category + "\">" + (category == HandCategory.HighCard ? highCard : Damage) + "</Hand>";
            }));
            return "<BattleContent schemaVersion=\"1\" id=\"test\" version=\"1\">" +
                "<ScoringProfiles><Scoring id=\"score\" preset=\"Default\"/></ScoringProfiles>" +
                "<BattleRules><Rules id=\"rules\" scoringRef=\"score\"/></BattleRules>" +
                "<HandRules><HandRule id=\"hands\">" + hands + "</HandRule></HandRules>" +
                "<Relics><Relic id=\"gear\" maxCopies=\"2\">" + modifiers + "</Relic>" + additionalRelics + "</Relics>" +
                "<Players><Player id=\"hero\" handRuleRef=\"hands\"/></Players><Enemies><Enemy id=\"foe\"/></Enemies>" +
                "<Loadouts><Loadout id=\"build\"><Relic instanceId=\"one\" ref=\"gear\"/>" + additionalSlots + "</Loadout></Loadouts>" +
                "<Encounters><Encounter id=\"fight\" rulesRef=\"rules\"><Player id=\"p\" ref=\"hero\"" +
                (equipped ? " loadoutRef=\"build\"" : "") + "/><Enemy id=\"e\" ref=\"foe\"/></Encounter></Encounters></BattleContent>";
        }
        private static string AddHits(string step = "damage", int count = 1, string category = "HighCard") =>
            "<HandModifier id=\"repeat\" category=\"" + category + "\" operation=\"AddHits\" stepId=\"" + step + "\" hits=\"" + count + "\"/>";
        private static BattleContentBuildResult Compile(string xml) => BattleContentCompiler.CompileXml(xml, "combinations.xml");
        private static void Error(BattleContentBuildResult result, string code)
        {
            Assert.IsFalse(result.Success); Assert.IsNull(result.Content);
            Assert.That(result.Diagnostics.Any(item => item.Code == code && item.Severity == BattleDiagnosticSeverity.Error), Is.True,
                string.Join("; ", result.Diagnostics.Select(item => item.Code + ": " + item.Message)));
        }

        [Test] public void AdditionalHitsCannotTargetAHealingBaseStep()
        {
            var result = Compile(Pack(AddHits("heal"), highCard: Heal));
            Error(result, "InvalidAdditionalHits"); var error = result.Diagnostics.Single(item => item.Code == "InvalidAdditionalHits");
            Assert.AreEqual("gear", error.DefinitionId); Assert.AreEqual("repeat.stepId", error.FieldPath);
            Assert.AreEqual("combinations.xml", error.Location.SourceName); Assert.That(error.Location.Path, Does.Contain("Encounter[1]"));
        }

        [Test] public void ReplacingHealingWithDamageDoesNotMakeItsBaseEligibleForAdditionalHits()
        {
            string replacement = "<HandModifier id=\"replace\" category=\"HighCard\" operation=\"ReplaceEffect\" stepId=\"heal\"><Effect kind=\"Damage\" amount=\"2\"/></HandModifier>";
            Error(Compile(Pack(AddHits("heal") + replacement, highCard: Heal)), "InvalidAdditionalHits");
        }

        [Test] public void ReplacingDamageWithHealingConflictsWithAdditionalHitsAcrossRelics()
        {
            string other = "<Relic id=\"other\"><HandModifier id=\"replace\" category=\"HighCard\" operation=\"ReplaceEffect\" stepId=\"damage\"><Effect kind=\"Heal\" amount=\"2\"/></HandModifier></Relic>";
            var result = Compile(Pack(AddHits(), additionalRelics: other, additionalSlots: "<Relic instanceId=\"two\" ref=\"other\"/>"));
            Assert.IsFalse(result.Success); Assert.IsNull(result.Content);
            Assert.That(result.Diagnostics.Any(item => item.Message.Contains("non-damage")), Is.True);
        }

        [Test] public void ExactlySixtyFourExpandedStepsRemainValid()
        { var result = Compile(Pack(AddHits(count: 63))); Assert.IsTrue(result.Success, string.Join("; ", result.Diagnostics.Select(item => item.Message))); }

        [Test] public void HitsAndAppendEffectsShareTheSameStepCapacity()
        {
            string appended = "<HandModifier id=\"append\" category=\"HighCard\" operation=\"AppendEffect\"><Effect kind=\"Heal\" amount=\"1\" target=\"Self\"/></HandModifier>";
            Error(Compile(Pack(AddHits(count: 63) + appended)), "HandEffectLimit");
        }

        [Test] public void CopiesOfTheSameRelicBothContributeToTheStepLimit()
        { Error(Compile(Pack(AddHits(count: 32), additionalSlots: "<Relic instanceId=\"two\" ref=\"gear\"/>")), "HandEffectLimit"); }

        [Test] public void AllBaseStepsCountTowardTheExpandedLimit()
        { Error(Compile(Pack(AddHits(count: 63), highCard: Damage + Heal)), "HandEffectLimit"); }

        [Test] public void MissingStepInEveryBranchIsAWarningAndTheContentStillBuilds()
        {
            var result = Compile(Pack(AddHits("typo")));
            Assert.IsTrue(result.Success); var warning = result.Diagnostics.Single(item => item.Code == "UnmatchedHandStep");
            Assert.AreEqual(BattleDiagnosticSeverity.Warning, warning.Severity); Assert.AreEqual("gear", warning.DefinitionId);
            Assert.That(warning.Message, Does.Contain("typo"));
        }

        [Test] public void RoyalBranchMayOmitTheOrdinaryDamageStepWithoutWarning()
        {
            var result = Compile(Pack(AddHits(category: "StraightFlush"), royal: "<Effect id=\"eliminate\" kind=\"Eliminate\"/>"));
            Assert.IsTrue(result.Success); Assert.IsEmpty(result.Diagnostics);
        }

        [Test] public void OneInvalidBranchIsNotHiddenByAnotherValidBranch()
        {
            var result = Compile(Pack(AddHits(category: "StraightFlush"), royal: "<Effect id=\"damage\" kind=\"Heal\" amount=\"1\"/>"));
            Error(result, "InvalidAdditionalHits");
        }

        [Test] public void InactiveBuildDoesNotApplyItsModifiersToAnotherPlayerTemplate()
        {
            var result = Compile(Pack(AddHits("heal"), highCard: Heal, equipped: false));
            Assert.IsTrue(result.Success); Assert.IsEmpty(result.Diagnostics);
        }

        [TestCase("<ActionTrigger id=\"a\" timing=\"DiscardCompleted\"><Effect kind=\"ApplyStatus\" statusRef=\"missing\"/></ActionTrigger>")]
        [TestCase("<BoundaryTrigger id=\"a\" timing=\"BattleStarted\"><Conditions><OwnerHasStatus statusRef=\"missing\"/></Conditions><Effect kind=\"Heal\" amount=\"1\"/></BoundaryTrigger>")]
        [TestCase("<HandModifier id=\"a\" category=\"HighCard\" operation=\"AppendEffect\"><Effect kind=\"ApplyStatus\" statusRef=\"missing\"/></HandModifier>")]
        public void UnusedRelicDefinitionsStillValidateEveryStatusReference(string ability)
        {
            var result = Compile(Pack("", additionalRelics: "<Relic id=\"unused\">" + ability + "</Relic>"));
            Assert.IsFalse(result.Success); Assert.IsNull(result.Content);
            Assert.That(result.Diagnostics.Any(item => item.Message.Contains("Unknown status reference")), Is.True);
        }

        [Test] public void UnusedRelicDefinitionsStillValidateUnavailableActionTargets()
        {
            var result = Compile(Pack("", additionalRelics: "<Relic id=\"unused\"><FactTrigger id=\"a\" timing=\"DamageSettled\"><Effect kind=\"Heal\" amount=\"1\" target=\"EventActor\"/></FactTrigger></Relic>"));
            Assert.IsFalse(result.Success); Assert.IsNull(result.Content);
        }
    }
}
