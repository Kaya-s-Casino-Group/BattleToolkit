using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using NUnit.Framework;

namespace PokerEngine.Tests
{
    [TestFixture]
    public sealed class CardZoneOperationsTests
    {
        private static CardInstanceId Id(string id) => new CardInstanceId(id);
        private static CardInstanceId[] Ids(params string[] ids) => ids.Select(Id).ToArray();
        private static CardZone Zone(string name, params string[] cards) =>
            new CardZone(new ZoneId(name), "custom.row", new ParticipantId("owner"), Ids(cards));
        private static void Cards(CardZone zone, params string[] expected) =>
            CollectionAssert.AreEqual(expected, zone.Cards.Select(c => c.Value));
        private static void Metadata(CardZone before, CardZone after)
        {
            Assert.AreEqual(before.Id, after.Id);
            Assert.AreEqual(before.Kind, after.Kind);
            Assert.AreEqual(before.ControllerId, after.ControllerId);
        }

        [Test] public void SuitAndRankSortAreStableAcrossAllStandardFacesAndKeepUnrankedCardsLast()
        {
            var definitions = StandardDeck.Cards().ToArray();
            var lookup = definitions.ToDictionary(d => Id(d.Id.Value), d => d);
            var ace = definitions.Single(d => d.Suit == Suit.Spades && d.Rank == Rank.Ace);
            lookup.Add(Id("ace-copy"), ace); lookup.Add(Id("unranked-1"), null); lookup.Add(Id("unranked-2"), null);
            var input = new[] { Id("unranked-1"), Id("ace-copy") }.Concat(definitions.Select(d => Id(d.Id.Value)))
                .Concat(new[] { Id("unranked-2") }).ToArray();
            var original = new CardZone(new ZoneId("row"), "custom.row", new ParticipantId("owner"), input);
            int resolutions = 0;
            Func<CardInstanceId, CardDefinition> resolve = id => { resolutions++; return lookup[id]; };
            var suits = new[] { Suit.Spades, Suit.Hearts, Suit.Clubs, Suit.Diamonds };
            var ranks = Enumerable.Range(2, 13).Reverse().Select(value => (Rank)value).ToArray();
            var suitOrder = suits.SelectMany(s => ranks.Select(r => Id(StandardDeck.CardId(s, r).Value))).ToArray();
            var rankOrder = ranks.SelectMany(r => suits.Select(s => Id(StandardDeck.CardId(s, r).Value))).ToArray();
            var bySuit = original.SortBySuit(resolve);
            Assert.AreEqual(input.Length, resolutions); Metadata(original, bySuit);
            CollectionAssert.AreEqual(new[] { Id("ace-copy") }.Concat(suitOrder).Concat(new[] { Id("unranked-1"), Id("unranked-2") }), bySuit.Cards);
            resolutions = 0;
            var byRank = original.SortByRank(resolve);
            Assert.AreEqual(input.Length, resolutions); Metadata(original, byRank);
            CollectionAssert.AreEqual(new[] { Id("ace-copy") }.Concat(rankOrder).Concat(new[] { Id("unranked-1"), Id("unranked-2") }), byRank.Cards);
            CollectionAssert.AreEqual(input, original.Cards);
            Assert.AreSame(byRank, byRank.SortByRank(resolve));
            Assert.AreSame(bySuit, bySuit.SortBySuit(resolve));
        }

        [Test] public void SortingRejectsFailedResolutionAtomicallyAndHandlesEmptyOrSingleZones()
        {
            var original = Zone("row", "a", "missing");
            var definition = StandardDeck.Cards().First();
            Func<CardInstanceId, CardDefinition> resolve = id => id.Value == "a" ? definition : throw new KeyNotFoundException();
            Assert.Throws<KeyNotFoundException>(() => original.SortBySuit(resolve));
            Assert.Throws<KeyNotFoundException>(() => original.SortByRank(resolve));
            Assert.Throws<ArgumentNullException>(() => original.SortBySuit(null));
            Assert.Throws<ArgumentNullException>(() => original.SortByRank(null));
            Assert.Throws<ArgumentNullException>(() => CardZoneOperations.SortBySuit(null, resolve));
            Cards(original, "a", "missing");
            var empty = Zone("empty"); Assert.AreSame(empty, empty.SortBySuit(resolve)); Assert.AreSame(empty, empty.SortByRank(resolve));
            var single = Zone("single", "a"); Assert.AreSame(single, single.SortByRank(resolve));
        }

        [Test] public void InsertPreservesEnumerationOrderAndMetadataAtEveryBoundary()
        {
            var original = Zone("row", "a", "d");
            var middle = original.InsertRange(1, Ids("b", "c"));
            Cards(middle, "a", "b", "c", "d"); Metadata(original, middle);
            Cards(middle.Insert(0, Id("first")), "first", "a", "b", "c", "d");
            Cards(middle.Insert(4, Id("last")), "a", "b", "c", "d", "last");
            Cards(Zone("empty").Insert(0, Id("only")), "only");
            Cards(original, "a", "d");
        }

        [Test] public void RemoveByIdentityAndIndexRetainsSurvivorOrder()
        {
            var original = Zone("row", "a", "b", "c", "d");
            var removed = original.RemoveRange(Ids("d", "b"));
            Cards(removed, "a", "c"); Metadata(original, removed);
            Cards(removed.Remove(Id("a")), "c");
            Cards(removed.RemoveAt(1), "a");
            Cards(original.RemoveRange(original.Cards));
            Cards(original, "a", "b", "c", "d");
        }

        [Test] public void MoveUsesFinalIndexAndPreservesAllOtherRelativePositions()
        {
            var original = Zone("row", "a", "b", "c", "d");
            Cards(original.Move(0, 3), "b", "c", "d", "a");
            Cards(original.Move(3, 0), "d", "a", "b", "c");
            for (int from = 0; from < original.Cards.Count; from++)
                for (int to = 0; to < original.Cards.Count; to++)
                {
                    var moved = original.Move(from, to);
                    Assert.AreEqual(original.Cards[from], moved.Cards[to]);
                    CollectionAssert.AreEqual(original.Cards.Where((_, i) => i != from), moved.Cards.Where((_, i) => i != to));
                    CollectionAssert.AreEqual(original.Cards, moved.Move(to, from).Cards);
                    Metadata(original, moved);
                }
            Cards(original, "a", "b", "c", "d");
        }

        [Test] public void SwapIsReversibleAndLeavesUnselectedPositionsUntouched()
        {
            var original = Zone("row", "a", "b", "c", "d");
            var swapped = original.Swap(1, 3);
            Cards(swapped, "a", "d", "c", "b"); Metadata(original, swapped);
            CollectionAssert.AreEqual(original.Cards, swapped.Swap(1, 3).Cards);
            Assert.AreSame(original, original.Swap(2, 2));
            Cards(original, "a", "b", "c", "d");
        }

        [Test] public void ReorderRequiresAnExactPermutation()
        {
            var original = Zone("row", "a", "b", "c");
            Cards(original.Reorder(Ids("c", "a", "b")), "c", "a", "b");
            Assert.Throws<ArgumentException>(() => original.Reorder(Ids("a", "b")));
            Assert.Throws<ArgumentException>(() => original.Reorder(Ids("a", "b", "foreign")));
            Assert.Throws<ArgumentException>(() => original.Reorder(Ids("a", "a", "c")));
            Assert.Throws<ArgumentException>(() => original.Reorder(Ids("a", "b", "c", "d")));
            Cards(original, "a", "b", "c");
        }

        [Test] public void EmptySelectionsAreNoOpsAndEmptyZonesHaveNoElementIndex()
        {
            var source = Zone("source"); var target = Zone("target", "x");
            Assert.AreSame(source, source.InsertRange(0, Ids()));
            Assert.AreSame(source, source.RemoveRange(Ids()));
            Assert.AreSame(source, source.Reorder(Ids()));
            foreach (var result in new[] { source.TransferRangeTo(target, Ids(), 1), source.DrawTo(target, 0, 1) })
            {
                Assert.AreSame(source, result.Source); Assert.AreSame(target, result.Target);
                Assert.AreEqual(0, result.Cards.Count);
            }
            Assert.Throws<ArgumentOutOfRangeException>(() => source.RemoveAt(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => source.Move(0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => source.Swap(0, 0));
        }

        [Test] public void InvalidIndicesAreRejectedBeforeAnyChange()
        {
            var original = Zone("row", "a", "b");
            foreach (var index in new[] { -1, 3, int.MaxValue })
                Assert.Throws<ArgumentOutOfRangeException>(() => original.InsertRange(index, Ids()));
            foreach (var index in new[] { -1, 2, int.MaxValue })
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => original.RemoveAt(index));
                Assert.Throws<ArgumentOutOfRangeException>(() => original.Move(index, 0));
                Assert.Throws<ArgumentOutOfRangeException>(() => original.Move(0, index));
                Assert.Throws<ArgumentOutOfRangeException>(() => original.Swap(index, 0));
                Assert.Throws<ArgumentOutOfRangeException>(() => original.Swap(0, index));
            }
            Cards(original, "a", "b");
        }

        [Test] public void BulkInsertAndRemoveFailAtomicallyOnDuplicatesOrMissingCards()
        {
            var original = Zone("row", "a", "b");
            Assert.Throws<ArgumentException>(() => original.InsertRange(1, Ids("new", "a")));
            Assert.Throws<ArgumentException>(() => original.InsertRange(1, Ids("new", "new")));
            Assert.Throws<ArgumentException>(() => original.RemoveRange(Ids("a", "missing")));
            Assert.Throws<ArgumentException>(() => original.RemoveRange(Ids("a", "a")));
            Assert.Throws<ArgumentException>(() => original.Insert(0, default(CardInstanceId)));
            Assert.Throws<ArgumentException>(() => original.Remove(default(CardInstanceId)));
            Cards(original, "a", "b");
        }

        [Test] public void ConstructorRejectsInvalidOrDuplicateInstanceIds()
        {
            Assert.Throws<ArgumentException>(() => Zone("row", "same", "same"));
            Assert.Throws<ArgumentException>(() => new CardZone(new ZoneId("row"), "custom", null, new[] { default(CardInstanceId) }));
            // Different instances may still share the same card definition; zones only operate on identity.
            Cards(Zone("row", "copy-1", "copy-2"), "copy-1", "copy-2");
        }

        [Test] public void NullZonesAndEnumerablesAreRejected()
        {
            var original = Zone("row", "a"); var target = Zone("target");
            Assert.Throws<ArgumentNullException>(() => CardZoneOperations.Insert(null, 0, Id("a")));
            Assert.Throws<ArgumentNullException>(() => original.InsertRange(0, null));
            Assert.Throws<ArgumentNullException>(() => original.RemoveRange(null));
            Assert.Throws<ArgumentNullException>(() => original.Reorder(null));
            Assert.Throws<ArgumentNullException>(() => original.TransferRangeTo(target, null, 0));
            Assert.Throws<ArgumentNullException>(() => original.DrawTo(null, 1, 0));
        }

        [Test] public void TransferRangeKeepsRequestedOrderAtTheDestinationAndPreservesBothMetadata()
        {
            var source = Zone("source", "a", "b", "c", "d");
            var target = new CardZone(new ZoneId("target"), CardZoneKinds.Deck, null, Ids("x", "y"));
            var result = source.TransferRangeTo(target, Ids("d", "b"), 1);
            Cards(result.Source, "a", "c"); Cards(result.Target, "x", "d", "b", "y");
            CollectionAssert.AreEqual(Ids("d", "b"), result.Cards);
            Metadata(source, result.Source); Metadata(target, result.Target);
            Cards(source, "a", "b", "c", "d"); Cards(target, "x", "y");
        }

        [Test] public void SingleAndWholeZoneTransfersSupportBothInsertionBoundaries()
        {
            var source = Zone("source", "a", "b"); var target = Zone("target", "x");
            var first = source.TransferTo(target, Id("b"), 0);
            Cards(first.Source, "a"); Cards(first.Target, "b", "x");
            var all = first.Source.TransferRangeTo(first.Target, first.Source.Cards, 2);
            Cards(all.Source); Cards(all.Target, "b", "x", "a");
            Assert.AreEqual(3, all.Target.Cards.Distinct().Count());
        }

        [Test] public void TransferRejectsInvalidMembershipIndicesAndAliasedZonesAtomically()
        {
            var source = Zone("source", "a", "b"); var target = Zone("target", "x");
            Assert.Throws<ArgumentException>(() => source.TransferRangeTo(target, Ids("a", "missing"), 0));
            Assert.Throws<ArgumentException>(() => source.TransferRangeTo(target, Ids("a", "a"), 0));
            Assert.Throws<ArgumentException>(() => source.TransferTo(target, default(CardInstanceId), 0));
            Assert.Throws<ArgumentException>(() => source.TransferTo(source, Id("a"), 0));
            Assert.Throws<ArgumentException>(() => source.TransferTo(Zone("source", "y"), Id("a"), 0));
            // Reject pre-existing overlap even when that overlapping card is not selected.
            Assert.Throws<ArgumentException>(() => source.TransferTo(Zone("other", "b"), Id("a"), 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => source.TransferTo(target, Id("a"), -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => source.TransferRangeTo(target, Ids(), 2));
            Cards(source, "a", "b"); Cards(target, "x");
        }

        [Test] public void DrawTakesTheLastCardFirstAndDoesNotReverseTheDrawnPacketAgain()
        {
            var source = Zone("deck", "a", "b", "c", "d"); var target = Zone("hand", "x", "y");
            var result = source.DrawTo(target, 3, 1);
            Cards(result.Source, "a"); Cards(result.Target, "x", "d", "c", "b", "y");
            CollectionAssert.AreEqual(Ids("d", "c", "b"), result.Cards);
            var last = result.Source.DrawTo(result.Target, 1, 5);
            Cards(last.Source); Cards(last.Target, "x", "d", "c", "b", "y", "a");
            Cards(source, "a", "b", "c", "d"); Cards(target, "x", "y");
        }

        [Test] public void FailedDrawDoesNotConsumeAnySourceCards()
        {
            var source = Zone("deck", "a", "b"); var target = Zone("hand");
            Assert.Throws<ArgumentOutOfRangeException>(() => source.DrawTo(target, 3, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => source.DrawTo(target, -1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => source.DrawTo(target, 1, 1));
            Assert.Throws<ArgumentException>(() => source.DrawTo(source, 0, 0));
            Cards(source, "a", "b"); Cards(target);
        }

        [Test] public void InputArraysAndReturnedCollectionsCannotMutateSnapshots()
        {
            var ids = Ids("a", "b");
            var source = new CardZone(new ZoneId("source"), "custom", null, ids);
            ids[0] = Id("changed"); Cards(source, "a", "b");
            var selected = Ids("b", "a"); var result = source.TransferRangeTo(Zone("target"), selected, 0);
            selected[0] = Id("changed"); Cards(result.Target, "b", "a");
            CollectionAssert.AreEqual(Ids("b", "a"), result.Cards);
            Assert.Throws<NotSupportedException>(() => ((IList<CardInstanceId>)result.Cards)[0] = Id("changed"));
            Assert.Throws<NotSupportedException>(() => ((IList<CardInstanceId>)result.Target.Cards).Clear());
            Cards(source, "a", "b");
        }

        [Test] public void RepeatedTransfersConserveEveryCardAndKeepPreviousSnapshotsValid()
        {
            var source = Zone("source", Enumerable.Range(0, 30).Select(i => "card-" + i).ToArray());
            var target = Zone("target"); var all = source.Cards.ToArray(); var random = new Random(1049);
            for (int step = 0; step < 150; step++)
            {
                var previousSource = source; var previousTarget = target;
                var oldSource = source.Cards.ToArray(); var oldTarget = target.Cards.ToArray();
                var selected = source.Cards.Where(_ => random.Next(2) == 0).Reverse().ToArray();
                var result = source.TransferRangeTo(target, selected, random.Next(target.Cards.Count + 1));
                CollectionAssert.AreEquivalent(all, result.Source.Cards.Concat(result.Target.Cards));
                Assert.AreEqual(all.Length, result.Source.Cards.Concat(result.Target.Cards).Distinct().Count());
                CollectionAssert.AreEqual(oldSource, previousSource.Cards); CollectionAssert.AreEqual(oldTarget, previousTarget.Cards);
                source = result.Target; target = result.Source;
            }
        }

        private static string Serialize(CardZone zone)
        {
            using (var stream = new MemoryStream())
            { new DataContractJsonSerializer(typeof(CardZone)).WriteObject(stream, zone); return Encoding.UTF8.GetString(stream.ToArray()); }
        }
        private static CardZone Restore(string json)
        {
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                return (CardZone)new DataContractJsonSerializer(typeof(CardZone)).ReadObject(stream);
        }

        [Test] public void OperationsRetainTheExistingDataContractAndValidateRestoredInputs()
        {
            var source = Zone("source", "a", "b", "c").Move(0, 2);
            var restored = Restore(Serialize(source));
            Metadata(source, restored); Cards(restored, "b", "c", "a");
            Cards(restored.Remove(Id("c")), "b", "a");
            var corrupt = Restore(Serialize(Zone("source", "a", "b")).Replace("\"b\"", "\"a\""));
            Assert.Throws<ArgumentException>(() => corrupt.RemoveRange(Ids()));
            Assert.Throws<ArgumentException>(() => corrupt.TransferRangeTo(Zone("target"), Ids(), 0));
        }
    }
}
