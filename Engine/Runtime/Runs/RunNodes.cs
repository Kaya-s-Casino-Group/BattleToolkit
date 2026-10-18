using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;

namespace PokerEngine
{
    public enum RunNodeKind { Encounter = 1, Elite, Boss, Reward, Shop }

    /// <summary>Immutable content shared by all nodes. Runtime progress belongs to RunNodeState.</summary>
    public abstract class RunNodeDefinition
    {
        public string Id { get; }
        public RunNodeKind Kind { get; }
        public IReadOnlyList<string> NextNodeIds { get; }
        private protected RunNodeDefinition(string id, RunNodeKind kind, IEnumerable<string> nextNodeIds)
        {
            Id = Guard.Text(id, nameof(id)); Kind = kind;
            var next = Guard.Copy(nextNodeIds ?? new string[0], nameof(nextNodeIds));
            if (next.Any(string.IsNullOrWhiteSpace) || next.Distinct(StringComparer.Ordinal).Count() != next.Length || next.Contains(id))
                throw new ArgumentException("Invalid node successors.");
            NextNodeIds = Guard.View(next);
        }
    }

    public sealed class RunEncounterNodeDefinition : RunNodeDefinition
    {
        public RunStageDefinition Stage { get; }
        public RunEncounterNodeDefinition(string id, RunStageDefinition stage, IEnumerable<string> nextNodeIds = null,
            RunNodeKind kind = RunNodeKind.Encounter) : base(id, kind, nextNodeIds)
        {
            if (kind != RunNodeKind.Encounter && kind != RunNodeKind.Elite && kind != RunNodeKind.Boss)
                throw new ArgumentException("Invalid encounter node kind.");
            Stage = stage ?? throw new ArgumentNullException(nameof(stage));
        }
    }

    public sealed class RunRewardNodeDefinition : RunNodeDefinition
    {
        /// <summary>Empty uses the Run's default reward pool.</summary>
        public IReadOnlyList<string> OfferPool { get; }
        public RunRewardNodeDefinition(string id, IEnumerable<string> nextNodeIds = null, IEnumerable<string> offerPool = null)
            : base(id, RunNodeKind.Reward, nextNodeIds) { OfferPool = RunNodeTree.CopyPool(offerPool); }
    }

    public sealed class RunShopNodeDefinition : RunNodeDefinition
    {
        /// <summary>Empty uses the Run's default shop pool.</summary>
        public IReadOnlyList<string> OfferPool { get; }
        public RunShopNodeDefinition(string id, IEnumerable<string> nextNodeIds = null, IEnumerable<string> offerPool = null)
            : base(id, RunNodeKind.Shop, nextNodeIds) { OfferPool = RunNodeTree.CopyPool(offerPool); }
    }

    /// <summary>A rooted acyclic node graph. Branches may merge; all nodes must be reachable.</summary>
    public sealed class RunNodeTree
    {
        private readonly Dictionary<string, RunNodeDefinition> byId;
        public string Version { get; }
        public string EntryNodeId { get; }
        public string Fingerprint { get; }
        public IReadOnlyList<RunNodeDefinition> Nodes { get; }
        public IReadOnlyList<RunEncounterNodeDefinition> EncounterNodes { get; }
        public RunNodeTree(string version, string entryNodeId, IEnumerable<RunNodeDefinition> nodes)
        {
            Version = Guard.Text(version, nameof(version)); EntryNodeId = Guard.Text(entryNodeId, nameof(entryNodeId));
            var copy = Guard.Copy(nodes, nameof(nodes));
            if (copy.Length == 0 || copy.Length > 500 || copy.Select(n => n.Id).Distinct(StringComparer.Ordinal).Count() != copy.Length)
                throw new ArgumentException("Invalid node tree size or duplicate identities.");
            byId = copy.ToDictionary(n => n.Id, StringComparer.Ordinal);
            if (!byId.ContainsKey(EntryNodeId) || copy.Any(n => n.NextNodeIds.Any(id => !byId.ContainsKey(id))))
                throw new ArgumentException("Unresolved node tree reference.");
            var visited = new HashSet<string>(StringComparer.Ordinal); var active = new HashSet<string>(StringComparer.Ordinal);
            Visit(EntryNodeId, visited, active);
            if (visited.Count != copy.Length) throw new ArgumentException("Unreachable node in tree.");
            Nodes = Guard.View(copy);
            EncounterNodes = Guard.View(copy.OfType<RunEncounterNodeDefinition>().ToArray());
            if (EncounterNodes.Count > 100 || EncounterNodes.Select(n => n.Stage.Id).Distinct(StringComparer.Ordinal).Count() != EncounterNodes.Count)
                throw new ArgumentException("Invalid encounter stages.");
            Fingerprint = ComputeFingerprint();
        }
        private void Visit(string id, HashSet<string> visited, HashSet<string> active)
        {
            if (active.Contains(id)) throw new ArgumentException("Node trees cannot contain cycles.");
            if (!visited.Add(id)) return;
            active.Add(id);
            foreach (var next in Node(id).NextNodeIds) Visit(next, visited, active);
            active.Remove(id);
        }
        public RunNodeDefinition Node(string id) => id != null && byId.TryGetValue(id, out var node) ? node : throw new ArgumentException("Unknown run node.");
        public int StageIndex(string nodeId)
        {
            for (int i = 0; i < EncounterNodes.Count; i++) if (EncounterNodes[i].Id == nodeId) return i;
            throw new ArgumentException("Node is not an encounter.");
        }
        internal static IReadOnlyList<string> CopyPool(IEnumerable<string> pool)
        {
            var values = Guard.Copy(pool ?? new string[0], nameof(pool));
            if (values.Any(string.IsNullOrWhiteSpace) || values.Distinct(StringComparer.Ordinal).Count() != values.Length)
                throw new ArgumentException("Invalid node offer pool.");
            return Guard.View(values);
        }
        public IReadOnlyList<string> OfferPool(RunNodeDefinition node, RunDefinition run)
        {
            if (node is RunRewardNodeDefinition reward) return reward.OfferPool.Count == 0 ? run.RewardPool : reward.OfferPool;
            if (node is RunShopNodeDefinition shop) return shop.OfferPool.Count == 0 ? run.ShopPool : shop.OfferPool;
            throw new ArgumentException("Node does not provide offers.");
        }
        private string ComputeFingerprint()
        {
            var text = new StringBuilder();
            Action<string> append = value => text.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
            append(Version); append(EntryNodeId);
            // Encounter ordering is part of the stage index contract.
            foreach (var node in Nodes)
            {
                append(node.Id); append(((int)node.Kind).ToString(CultureInfo.InvariantCulture));
                append(node.NextNodeIds.Count.ToString(CultureInfo.InvariantCulture)); foreach (var id in node.NextNodeIds) append(id);
                if (node is RunEncounterNodeDefinition combat)
                {
                    append(combat.Stage.Id); append(combat.Stage.TargetScore.ToString(CultureInfo.InvariantCulture));
                    append(combat.Stage.RoundLimit.ToString(CultureInfo.InvariantCulture)); append(combat.Stage.ClearGold.ToString(CultureInfo.InvariantCulture));
                    append(combat.Stage.Energy.ToString(CultureInfo.InvariantCulture));
                }
                var pool = node is RunRewardNodeDefinition reward ? reward.OfferPool : node is RunShopNodeDefinition shop ? shop.OfferPool : new string[0];
                append(pool.Count.ToString(CultureInfo.InvariantCulture)); foreach (var id in pool) append(id);
            }
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "").ToLowerInvariant();
        }
        internal static RunNodeTree Linear(IEnumerable<RunStageDefinition> stages)
        {
            var values = Guard.Copy(stages, nameof(stages)); var nodes = new List<RunNodeDefinition>();
            for (int i = 0; i < values.Length; i++)
            {
                string id = "stage/" + i;
                nodes.Add(new RunEncounterNodeDefinition(id, values[i], new[] { id + "/reward" }));
                nodes.Add(new RunRewardNodeDefinition(id + "/reward", i + 1 < values.Length ? new[] { id + "/shop" } : null));
                if (i + 1 < values.Length) nodes.Add(new RunShopNodeDefinition(id + "/shop", new[] { "stage/" + (i + 1) }));
            }
            return new RunNodeTree("linear.1", "stage/0", nodes);
        }
    }

    [DataContract]
    public sealed class RunNodeState
    {
        [DataMember(Order = 1, IsRequired = true)] public string TreeVersion { get; private set; }
        [DataMember(Order = 2, IsRequired = true)] public string TreeFingerprint { get; private set; }
        [DataMember(Order = 3, IsRequired = true)] public string CurrentNodeId { get; private set; }
        [DataMember(Name = "Path", Order = 4, IsRequired = true)] private string[] path;
        [DataMember(Name = "CompletedNodeIds", Order = 5, IsRequired = true)] private string[] completed;
        public IReadOnlyList<string> Path => Guard.View(path);
        public IReadOnlyList<string> CompletedNodeIds => Guard.View(completed);
        public RunNodeState(string treeVersion, string treeFingerprint, string currentNodeId, IEnumerable<string> path, IEnumerable<string> completedNodeIds)
        {
            TreeVersion = Guard.Text(treeVersion, nameof(treeVersion)); TreeFingerprint = Guard.Text(treeFingerprint, nameof(treeFingerprint));
            CurrentNodeId = Guard.Text(currentNodeId, nameof(currentNodeId));
            this.path = Guard.Copy(path, nameof(path)); completed = Guard.Copy(completedNodeIds, nameof(completedNodeIds));
        }
    }
}
