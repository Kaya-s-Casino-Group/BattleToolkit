using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    public static class RunValidator
    {
        private static void Need(bool condition, string path) { if (!condition) throw new StateValidationException("InvalidRun", path); }
        private static void Unique(IEnumerable<string> ids, string path)
        { var values = ids.ToArray(); Need(values.All(v => !string.IsNullOrWhiteSpace(v)) && values.Distinct(StringComparer.Ordinal).Count() == values.Length, path); }
        public static void Validate<TState>(RunState<TState> state, RunContent content, IRunEncounterState<TState> encounter) where TState : class
        {
            if (content == null || encounter == null) throw new ArgumentNullException(nameof(content));
            try { Check(state, content, encounter); }
            catch (NullReferenceException) { throw new StateValidationException("MissingRunData", "Run"); }
            catch (ArgumentException) { throw new StateValidationException("InvalidRunContent", "Run"); }
            catch (OverflowException) { throw new StateValidationException("RunOverflow", "Run"); }
        }
        private static void Check<TState>(RunState<TState> s, RunContent content, IRunEncounterState<TState> encounter) where TState : class
        {
            Need(s != null && !string.IsNullOrWhiteSpace(s.Id) && s.Revision >= 0 && s.PlayerId.IsValid, "Header");
            var d = content.Definition;
            Need(s.ContentVersion == content.CardsAndAbilities.Version && s.RulesVersion == d.Version && s.ScoreVersion == d.Scoring.Version &&
                s.StartingDeckVersion == content.StartingDeckVersion && s.EncounterKind == encounter.Kind &&
                s.EncounterRulesVersion == encounter.RulesVersion && s.EncounterSchemaVersion == encounter.SchemaVersion, "Versions");
            Need(Enum.IsDefined(typeof(RunPhase), s.Phase) && s.Gold >= 0 && s.StageScore >= 0 && s.RoundsScored >= 0, "Progress");
            var visitedEncounters = CheckFloors(s, d);
            int lastStage = visitedEncounters.Length == 0 ? -1 : visitedEncounters.Last().StageIndex;
            Need(s.StageIndex == lastStage && (lastStage < 0 ? s.RoundsScored == 0 && s.StageScore == 0 : s.RoundsScored <= d.Stages[lastStage].RoundLimit), "StageIndex");
            Need(s.Random.Algorithm == HmacDeckShuffler.Algorithm && s.Random.AlgorithmVersion == 1 && s.Random.StateBytes.Count == 40, "Random");
            Need(s.Deck.Count >= encounter.MinimumDeckSize && s.Deck.Count <= 512, "Deck.Count"); Unique(s.Deck.Select(c => c.Id), "Deck");
            foreach (var card in s.Deck)
                Need(content.CardsAndAbilities.Contains(card.DefinitionId) && card.BonusPoints >= 0 && card.BonusMultiplier >= 0 && card.Retriggers >= 0 && card.Retriggers <= 8, "Deck.Enhancements");
            Need(s.Relics.Count <= 32 && s.Relics.Sum(j => content.Relic(j.DefinitionId).Slots) <= d.RelicCapacity, "Relics.Capacity");
            Unique(s.Relics.Select(j => j.Id), "Relics");
            foreach (var j in s.Relics)
            {
                var definition = content.Relic(j.DefinitionId);
                Need(j.Level > 0 && j.Level <= definition.MaxLevel && j.Growth >= 0 && j.CooldownRounds >= 0 &&
                    j.Charges.HasValue == definition.Charges.HasValue && !(j.Charges < 0) && (!j.Charges.HasValue || j.Charges <= definition.Charges), "Relics.State");
                Need(s.Relics.Count(x => x.DefinitionId == j.DefinitionId) <= definition.MaxCopies, "Relics.Copies");
                Need(j.Memory.Version == 1 && j.Memory.Marks.Count == 0, "Relics.Memory"); Unique(j.Memory.Counters.Select(c => c.Key), "Relics.Memory.Keys");
                Need(j.Memory.Counters.All(c => c.Scope == MemoryScope.Encounter && c.Value >= 0), "Relics.Memory.Scope");
            }
            Need(s.HandLevels.Count == Enum.GetValues(typeof(HandCategory)).Length && s.HandLevels.Select(l => l.Category).Distinct().Count() == s.HandLevels.Count &&
                s.HandLevels.All(l => Enum.IsDefined(typeof(HandCategory), l.Category) && l.Level > 0), "HandLevels");
            Unique(s.Offers.Select(o => o.Id), "Offers"); Unique(s.Offers.Select(o => o.DefinitionId), "Offers.Definitions");
            Need(s.Offers.Count <= d.OfferCount, "Offers.Count");
            var tree = d.Floors[s.FloorIndex].NodeTree;
            var pool = s.Phase == RunPhase.Reward || s.Phase == RunPhase.Shop ? tree.OfferPool(tree.Node(s.Nodes.CurrentNodeId), d) : d.ShopPool;
            Need(s.Offers.All(o => pool.Contains(o.DefinitionId)), "Offers.Pool");
            Need((s.Phase == RunPhase.Shop || s.Phase == RunPhase.Reward) || s.Offers.Count == 0, "Offers.Phase");
            Need(s.Phase != RunPhase.Reward || s.Offers.Count > 0, "Reward.Empty");
            Unique(s.Scores.Select(r => r.Id), "Scores");
            foreach (var score in s.Scores) ScoreEngine.Validate(score);
            var stageScores = s.Scores.Where(r => r.Id.StartsWith(s.Id + "/stage/" + s.StageIndex + "/", StringComparison.Ordinal)).ToArray();
            Need(stageScores.Length == s.RoundsScored && stageScores.Sum(r => r.Total) == s.StageScore, "ScoreLedger");
            Need(s.Scores.All(r => visitedEncounters.Any(n => r.Id.StartsWith(s.Id + "/stage/" + n.StageIndex + "/", StringComparison.Ordinal))), "Scores.Stage");
            foreach (var node in visitedEncounters)
            {
                var ledger = s.Scores.Where(r => r.Id.StartsWith(s.Id + "/stage/" + node.StageIndex + "/", StringComparison.Ordinal)).ToArray();
                Need(ledger.Length <= node.Node.Stage.RoundLimit, "Scores.Budget");
                if (node.Completed) Need(ledger.Sum(r => r.Total) >= node.Node.Stage.TargetScore, "Nodes.EncounterOutcome");
            }
            if (s.Phase == RunPhase.Preparing)
                Need(s.StageIndex == -1 && s.Encounter == null && s.Scores.Count == 0 && s.RoundsScored == 0, "Preparing");
            else if (visitedEncounters.Length == 0)
                Need(s.Encounter == null && s.Scores.Count == 0 && s.Phase != RunPhase.Failed, "Encounter.Unstarted");
            else
            {
                Need(s.Encounter != null, "Encounter.Missing");
                var context = RunEncounterContext.From(s, content);
                encounter.Validate(context, s.Encounter);
                var progress = encounter.ReadProgress(context, s.Encounter);
                Need(progress != null, "Encounter.Progress");
                if (progress.Score != null)
                {
                    ScoreEngine.Validate(progress.Score);
                    var recorded = s.Scores.FirstOrDefault(r => r.Id == progress.Score.Id);
                    Need(progress.IsCheckpoint && progress.Score.Id.StartsWith(context.EncounterId + "/", StringComparison.Ordinal) &&
                        recorded != null && SameScore(recorded, progress.Score), "Encounter.ScoreLedger");
                }
                if (s.Phase == RunPhase.Encounter)
                {
                    Need(s.StageScore < d.Stages[s.StageIndex].TargetScore && s.RoundsScored < d.Stages[s.StageIndex].RoundLimit && !progress.Exhausted, "Encounter.Budget");
                    Need(s.Random.Algorithm == progress.Random.Algorithm && s.Random.AlgorithmVersion == progress.Random.AlgorithmVersion &&
                        s.Random.StateBytes.SequenceEqual(progress.Random.StateBytes), "Encounter.Random");
                }
                else
                {
                    Need(progress.IsCheckpoint, "BetweenEncounters");
                    bool cleared = s.StageScore >= d.Stages[s.StageIndex].TargetScore;
                    Need(s.Phase == RunPhase.Failed ? !cleared && (s.RoundsScored == d.Stages[s.StageIndex].RoundLimit || progress.Exhausted) : cleared, "Outcome");
                }
            }
        }
        private sealed class VisitedEncounter
        {
            public RunEncounterNodeDefinition Node;
            public int StageIndex;
            public bool Completed;
        }
        private static VisitedEncounter[] CheckFloors<TState>(RunState<TState> s, RunDefinition d) where TState : class
        {
            Need(s.Floors.Count == d.Floors.Count && s.FloorIndex >= 0 && s.FloorIndex < d.Floors.Count, "Floors.CountOrIndex");
            Need(s.Phase != RunPhase.Preparing || s.FloorIndex == 0, "Floors.Preparing");
            Need(s.Phase != RunPhase.Completed || s.FloorIndex == d.Floors.Count - 1, "Floors.Final");
            var visited = new List<VisitedEncounter>();
            for (int i = 0; i < d.Floors.Count; i++)
            {
                var floor = s.Floors[i]; var definition = d.Floors[i];
                Need(floor != null && floor.FloorId == definition.Id && floor.Kind == definition.Kind, "Floors.Identity");
                bool completed = i < s.FloorIndex || (i == s.FloorIndex && s.Phase == RunPhase.Completed);
                Need(floor.IsCompleted == completed, "Floors.CompletionOrder");
                var phase = i < s.FloorIndex ? RunPhase.Completed : i > s.FloorIndex ? RunPhase.Preparing : s.Phase;
                var nodes = CheckNodes(floor.Nodes, definition.NodeTree, phase);
                foreach (var node in nodes)
                    visited.Add(new VisitedEncounter { Node = node, StageIndex = d.StageIndex(i, node.Id), Completed = floor.Nodes.CompletedNodeIds.Contains(node.Id) });
            }
            return visited.ToArray();
        }
        private static RunEncounterNodeDefinition[] CheckNodes(RunNodeState nodes, RunNodeTree tree, RunPhase phase)
        {
            Need(nodes != null && nodes.TreeVersion == tree.Version && nodes.TreeFingerprint == tree.Fingerprint, "Nodes.Tree");
            var current = tree.Node(nodes.CurrentNodeId);
            Unique(nodes.Path, "Nodes.Path"); Unique(nodes.CompletedNodeIds, "Nodes.Completed");
            if (phase == RunPhase.Preparing)
                Need(nodes.Path.Count == 0 && nodes.CompletedNodeIds.Count == 0 && current.Id == tree.EntryNodeId, "Nodes.Preparing");
            else
            {
                Need(nodes.Path.Count > 0 && nodes.Path[0] == tree.EntryNodeId && nodes.Path.Last() == current.Id, "Nodes.Current");
                for (int i = 0; i < nodes.Path.Count; i++)
                {
                    var node = tree.Node(nodes.Path[i]);
                    if (i + 1 < nodes.Path.Count) Need(node.NextNodeIds.Contains(nodes.Path[i + 1]), "Nodes.Edge");
                }
                bool finished = phase == RunPhase.NodeChoice || phase == RunPhase.Completed;
                Need(nodes.CompletedNodeIds.SequenceEqual(nodes.Path.Take(nodes.Path.Count - (finished ? 0 : 1))), "Nodes.CompletionOrder");
                if (phase == RunPhase.Completed) Need(current.NextNodeIds.Count == 0, "Nodes.Terminal");
                else if (phase == RunPhase.NodeChoice) Need(current.NextNodeIds.Count > 1, "Nodes.Choice");
                else if (phase == RunPhase.Encounter || phase == RunPhase.Failed) Need(current is RunEncounterNodeDefinition, "Nodes.Encounter");
                else if (phase == RunPhase.Reward) Need(current is RunRewardNodeDefinition, "Nodes.Reward");
                else if (phase == RunPhase.Shop) Need(current is RunShopNodeDefinition, "Nodes.Shop");
            }
            return nodes.Path.Select(tree.Node).OfType<RunEncounterNodeDefinition>().ToArray();
        }
        private static bool SameScore(ScoreResult left, ScoreResult right)
        {
            if (left.Category != right.Category || left.BasePoints != right.BasePoints || left.BaseMultiplier != right.BaseMultiplier ||
                left.Points != right.Points || left.Multiplier != right.Multiplier || left.Total != right.Total || left.Steps.Count != right.Steps.Count) return false;
            for (int i = 0; i < left.Steps.Count; i++)
            {
                var a = left.Steps[i]; var b = right.Steps[i];
                if (a.SourceId != b.SourceId || a.CardId != b.CardId || a.Operation != b.Operation || a.Amount != b.Amount ||
                    a.PointsAfter != b.PointsAfter || a.MultiplierAfter != b.MultiplierAfter) return false;
            }
            return true;
        }
    }
}
