using System;
using System.Linq;
using System.Xml;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class BattleItemPlaybackTests
    {
        private static readonly ParticipantId Player = new ParticipantId("player");
        private static string Item(BattleSessionPlayback playback, string definition) =>
            playback.Session.State.Duel.Player(Player).Relics.Relics.Single(r => r.DefinitionId == definition).InstanceId;
        private sealed class View : IBattleSessionPresentation
        {
            public bool IsPresenting { get; set; }
            public CardDuelState State;
            public bool Fail;
            public int PresentCount;
            public void Initialize(CardDuelState state) { State = state; }
            public void Synchronize(CardDuelState state) { State = state; IsPresenting = false; }
            public void Present(BattleSessionResult result, bool animate)
            {
                PresentCount++;
                if (Fail) throw new InvalidOperationException("frontend failure");
                State = result.State; IsPresenting = animate;
            }
        }
        private static BattleSessionPlayback Create(out View view)
        {
            var xml = new XmlDocument();
            xml.LoadXml(ToolkitTestFiles.Read("Engine/Content/StandardSingleBattle-2026-10-21.xml"));
            xml.SelectSingleNode("//Loadout[@id='baseline']").InnerXml =
                "<Relic ref='10002' instanceId='potion'/><Relic ref='14001' instanceId='dice'/>";
            ((XmlElement)xml.SelectSingleNode("//Encounter[@id='standard-single-battle']/Player")).SetAttribute("initialHealth", "500");
            var content = BattleContentCompiler.CompileXml(xml.OuterXml);
            Assert.IsTrue(content.Success, string.Join(";", content.Diagnostics.Select(d => d.Message)));
            var built = BattleContentFactory.Create(content.Content, new BattleSetupRequest("standard-single-battle",
                "item-playback", HmacDeckShuffler.FromSeed(new byte[32])));
            Assert.IsTrue(built.Success);
            view = new View();
            var playback = new BattleSessionPlayback(built.Session, view);
            playback.Start(false);
            return playback;
        }

        [Test] public void PreviewUsesIsolatedStateAndRandomThenCommitMatches()
        {
            var playback = Create(out var view); var before = playback.Session.State;
            string dice = Item(playback, "14001"), potion = Item(playback, "10002");
            var preview = playback.PreviewItem(Player, dice, potion);
            Assert.IsTrue(preview.Accepted);
            Assert.AreSame(before, playback.Session.State);
            Assert.AreEqual(1, view.PresentCount);
            var committed = playback.UseItem(before.Revision, Player, dice, potion, false);
            Assert.IsTrue(committed.Accepted);
            CollectionAssert.AreEqual(preview.State.Random.StateBytes, committed.State.Random.StateBytes);
            CollectionAssert.AreEqual(preview.State.Duel.Player(Player).Relics.Relics.Select(r => r.DefinitionId),
                committed.State.Duel.Player(Player).Relics.Relics.Select(r => r.DefinitionId));
        }

        [Test] public void PlaybackGateRejectsItemsWithoutMutatingAuthority()
        {
            var playback = Create(out var view); var before = playback.Session.State;
            string potion = Item(playback, "10002");
            view.IsPresenting = true;
            Assert.IsFalse(playback.CanUseItem(Player, potion));
            Assert.AreEqual(CardDuelError.InvalidPhase, playback.PreviewItem(Player, potion).Error);
            Assert.AreEqual(CardDuelError.InvalidPhase, playback.UseItem(before.Revision, Player, potion).Error);
            Assert.AreSame(before, playback.Session.State);
            Assert.AreEqual(1, view.PresentCount);
        }

        [Test] public void PresentationFailureSynchronizesCommittedConsumptionWithoutSpendingAnAction()
        {
            var playback = Create(out var view); var before = playback.Session.State;
            string potion = Item(playback, "10002");
            view.Fail = true;
            Assert.Throws<InvalidOperationException>(() => playback.UseItem(before.Revision, Player, potion));
            var after = playback.Session.State;
            Assert.AreSame(after, view.State);
            Assert.AreEqual(510, after.Duel.Combat.Get(Player).Health);
            Assert.AreEqual(before.FlowFor(Player).ActionsRemaining, after.FlowFor(Player).ActionsRemaining);
            Assert.IsFalse(after.Duel.Player(Player).Relics.Relics.Any(r => r.InstanceId == potion));
            Assert.IsFalse(playback.UseItem(after.Revision, Player, potion).Accepted);
            Assert.AreEqual(2, view.PresentCount);
        }
    }
}
