using System;
using System.Runtime.Serialization;

namespace PokerEngine
{
    public enum RunFloorKind { Normal = 1, Boss }

    /// <summary>One ordered floor of a complete Run, with its own independent node graph.</summary>
    public sealed class RunFloorDefinition
    {
        public string Id { get; }
        public RunFloorKind Kind { get; }
        public RunNodeTree NodeTree { get; }
        public RunFloorDefinition(string id, RunNodeTree nodeTree, RunFloorKind kind = RunFloorKind.Normal)
        {
            Id = Guard.Text(id, nameof(id)); NodeTree = nodeTree ?? throw new ArgumentNullException(nameof(nodeTree));
            if (!Enum.IsDefined(typeof(RunFloorKind), kind)) throw new ArgumentException("Invalid floor kind.");
            Kind = kind;
        }
    }

    [DataContract]
    public sealed class RunFloorState
    {
        [DataMember(Order = 1, IsRequired = true)] public string FloorId { get; private set; }
        [DataMember(Order = 2, IsRequired = true)] public RunFloorKind Kind { get; private set; }
        [DataMember(Order = 3, IsRequired = true)] public RunNodeState Nodes { get; private set; }
        [DataMember(Order = 4, IsRequired = true)] public bool IsCompleted { get; private set; }
        public RunFloorState(string floorId, RunFloorKind kind, RunNodeState nodes, bool isCompleted = false)
        {
            FloorId = Guard.Text(floorId, nameof(floorId)); Kind = kind;
            Nodes = nodes ?? throw new ArgumentNullException(nameof(nodes)); IsCompleted = isCompleted;
        }
        internal static RunFloorState Initial(RunFloorDefinition floor) => new RunFloorState(floor.Id, floor.Kind,
            new RunNodeState(floor.NodeTree.Version, floor.NodeTree.Fingerprint, floor.NodeTree.EntryNodeId, new string[0], new string[0]));
    }
}
