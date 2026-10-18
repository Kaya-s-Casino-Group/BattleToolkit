using System;
using System.Linq;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class CardTableSessionTests
    {
        private static CardInstanceId Id(string value) => new CardInstanceId(value);
        private static ZoneId Zone(string value) => new ZoneId(value);
        private static CardInstance Card(string id, bool disabled = false) => new CardInstance(Id(id),
            new CardDefinitionId("spades_a"), "origin", CardEffects.Enhanced | CardEffects.Ineffective, disabled);
        private static CardZone Row(string id, string kind, params string[] cards) => new CardZone(Zone(id), kind, null, cards.Select(Id));
        private static CardTableState Seed(bool disabled = false) => new CardTableState(new MatchId("table"),
            new[] { Card("a", disabled), Card("b"), Card("c") }, new[] {
                Row("hand", CardZoneKinds.Hand, "a", "b"), Row("deck", CardZoneKinds.Deck, "c"),
                Row("played", CardZoneKinds.Played), Row("discard", CardZoneKinds.Discard) });

        [Test] public void OwnershipRejectsDuplicatesUnknownCardsOrphansAndDuplicateZones()
        {
            var seed = Seed();
            Assert.Throws<ArgumentException>(() => new CardTableState(seed.MatchId, seed.Cards.Concat(new[] { Card("a") }), seed.Zones));
            Assert.Throws<ArgumentException>(() => new CardTableState(seed.MatchId, seed.Cards, seed.Zones.Concat(new[] { Row("other", "custom", "a") })));
            Assert.Throws<ArgumentException>(() => new CardTableState(seed.MatchId, seed.Cards, new[] { Row("hand", CardZoneKinds.Hand, "missing") }));
            Assert.Throws<ArgumentException>(() => new CardTableState(seed.MatchId, seed.Cards, Array.Empty<CardZone>()));
            Assert.Throws<ArgumentException>(() => new CardTableState(seed.MatchId, seed.Cards, seed.Zones.Concat(new[] { seed.Zones[0] })));
        }

        [Test] public void DrawPlayDiscardPreserveGlobalIdentitiesEffectsAndSourceOrder()
        {
            var seed = Seed(); var session = new CardTableSession(seed);
            var draw = session.PrepareDraw(Zone("deck"), Zone("hand"), 1, 5);
            Assert.AreSame(seed, session.State); session.Commit(draw);
            CollectionAssert.AreEqual(new[] { Id("a"), Id("b"), Id("c") }, session.State.GetZone(Zone("hand")).Cards);
            var play = session.PreparePlay(Zone("hand"), Zone("played"), new[] { Id("c"), Id("a") }, 5);
            session.Commit(play);
            CollectionAssert.AreEqual(new[] { Id("a"), Id("c") }, session.State.GetZone(Zone("played")).Cards);
            session.Commit(session.PrepareDiscard(Zone("hand"), Zone("discard"), new[] { Id("b") }));
            Assert.IsEmpty(session.State.GetZone(Zone("hand")).Cards); Assert.AreEqual(3, session.State.Revision);
            foreach (var card in seed.Cards) Assert.AreSame(card, session.State.GetCard(card.Id));
            Assert.IsFalse(session.State.GetCard(Id("a")).CanScore);
        }

        [Test] public void InvalidCommandsLeaveCompleteTableAndRevisionUntouched()
        {
            var seed = Seed(disabled: true); var session = new CardTableSession(seed);
            Assert.Throws<InvalidOperationException>(() => session.PreparePlay(Zone("hand"), Zone("played"), new[] { Id("a"), Id("b") }, 5));
            Assert.Throws<InvalidOperationException>(() => session.PrepareDiscard(Zone("hand"), Zone("discard"), new[] { Id("a") }));
            Assert.Throws<InvalidOperationException>(() => session.PrepareDraw(Zone("deck"), Zone("hand"), 1, 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => session.PrepareDraw(Zone("deck"), Zone("hand"), 2, 5));
            Assert.Throws<InvalidOperationException>(() => session.PrepareDraw(Zone("discard"), Zone("hand"), 1, 5));
            Assert.Throws<ArgumentException>(() => session.PrepareDiscard(Zone("hand"), Zone("discard"), new[] { Id("c") }));
            Assert.AreSame(seed, session.State);
        }

        [Test] public void StaleAndForeignPreparedCommandsCannotConsumeOnePileTwice()
        {
            var session = new CardTableSession(Seed()); var other = new CardTableSession(session.State);
            var first = session.PrepareDraw(Zone("deck"), Zone("hand"), 1, 5);
            var stale = session.PrepareDraw(Zone("deck"), Zone("hand"), 1, 5);
            Assert.Throws<InvalidOperationException>(() => other.Commit(first));
            session.Commit(first); var committed = session.State;
            Assert.Throws<InvalidOperationException>(() => session.Commit(stale));
            Assert.Throws<InvalidOperationException>(() => session.Commit(first)); Assert.AreSame(committed, session.State);
        }

        [Test] public void ModeUpdatesCanReplaceCardsButCannotStealFromUnaffectedZones()
        {
            var session = new CardTableSession(Seed()); var before = session.State;
            Assert.Throws<ArgumentException>(() => session.PrepareZones(new[] { Row("hand", CardZoneKinds.Hand, "a", "c") }, Array.Empty<CardInstance>()));
            Assert.Throws<ArgumentException>(() => session.PrepareZones(Array.Empty<CardZone>(), new[] { Card("new") }));
            Assert.AreSame(before, session.State);
            session.Commit(session.PrepareZones(new[] { Row("hand", CardZoneKinds.Hand, "new") }, new[] { Card("new") }));
            CollectionAssert.AreEquivalent(new[] { "c", "new" }, session.State.Cards.Select(card => card.Id.Value));
            Assert.AreSame(before.GetZone(Zone("deck")), session.State.GetZone(Zone("deck")));
        }

        [Test] public void SnapshotRoundtripRestoresOwnershipAndContinuesCommands()
        {
            var session = new CardTableSession(Seed()); session.Commit(session.PrepareDraw(Zone("deck"), Zone("hand"), 1, 5));
            string json = CardTableSnapshotCodec.Serialize(session.State); var restored = CardTableSnapshotCodec.Deserialize(json);
            Assert.AreEqual(json, CardTableSnapshotCodec.Serialize(restored));
            var resumed = new CardTableSession(restored);
            resumed.Commit(resumed.PrepareDiscard(Zone("hand"), Zone("discard"), new[] { Id("a") }));
            Assert.AreEqual(2, resumed.State.Revision); Assert.IsFalse(resumed.State.GetCard(Id("a")).CanScore);
            Assert.AreEqual("origin", resumed.State.GetCard(Id("a")).OriginId);
        }

        [Test] public void SnapshotRejectsUnknownSchemaMissingFieldsAndInvalidOwnership()
        {
            string json = CardTableSnapshotCodec.Serialize(Seed());
            Assert.Throws<SnapshotException>(() => CardTableSnapshotCodec.Deserialize(json.Replace("\"SchemaVersion\":1", "\"SchemaVersion\":99")));
            Assert.Throws<SnapshotException>(() => CardTableSnapshotCodec.Deserialize("{}"));
            Assert.Throws<SnapshotException>(() => CardTableSnapshotCodec.Deserialize(json.Replace("\"Revision\":0", "\"Revision\":-1")));
            // Replace an instance identity everywhere: the registry and zone now contain duplicate a identities.
            Assert.Throws<SnapshotException>(() => CardTableSnapshotCodec.Deserialize(json.Replace("\"b\"", "\"a\"")));
        }
    }
}
