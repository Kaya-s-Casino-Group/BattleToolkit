using System;
using System.Collections.Generic;
using System.Linq;

namespace PokerEngine
{
    public sealed class EncounterParticipantBinding
    {
        public string SlotId { get; }
        public ParticipantId ActorId { get; }
        public EncounterParticipantBinding(string slotId, ParticipantId actorId)
        {
            SlotId = Guard.Text(slotId, nameof(slotId));
            if (!actorId.IsValid) throw new ArgumentException("Invalid encounter actor."); ActorId = actorId;
        }
    }

    /// <summary>Optional ownership location; standalone encounters have no Run binding.</summary>
    public sealed class EncounterRunBinding
    {
        public string RunId { get; }
        public string FloorId { get; }
        public string NodeId { get; }
        public EncounterRunBinding(string runId, string floorId, string nodeId)
        { RunId = Guard.Text(runId, nameof(runId)); FloorId = Guard.Text(floorId, nameof(floorId)); NodeId = Guard.Text(nodeId, nameof(nodeId)); }
    }

    /// <summary>Instance identity and actor-slot binding. Does not own cards, turns or random state.</summary>
    public sealed class EncounterContext
    {
        public string Id { get; }
        public EncounterDefinition Definition { get; }
        public IReadOnlyList<EncounterParticipantBinding> Participants { get; }
        public EncounterRunBinding Run { get; }
        public EncounterContext(string id, EncounterDefinition definition, IEnumerable<EncounterParticipantBinding> participants,
            EncounterRunBinding run = null)
        {
            Id = Guard.Text(id, nameof(id)); Definition = definition ?? throw new ArgumentNullException(nameof(definition));
            var bindings = Guard.Copy(participants, nameof(participants));
            if (bindings.Length != definition.Participants.Count || bindings.Select(b => b.SlotId).Distinct(StringComparer.Ordinal).Count() != bindings.Length ||
                bindings.Select(b => b.ActorId).Distinct().Count() != bindings.Length ||
                !new HashSet<string>(definition.Participants.Select(p => p.SlotId), StringComparer.Ordinal).SetEquals(bindings.Select(b => b.SlotId)))
                throw new ArgumentException("Encounter slots must bind exactly one distinct actor each.");
            Participants = Guard.View(bindings); Run = run;
        }
        public ParticipantId Actor(string slotId) => Participants.Single(p => p.SlotId == slotId).ActorId;
        public bool Contains(ParticipantId actor) => actor.IsValid && Participants.Any(p => p.ActorId == actor);
    }

    /// <summary>Typed mode settings accompany the shared description. TSettings must be immutable.</summary>
    public sealed class EncounterSetup<TSettings> where TSettings : class
    {
        public EncounterContext Context { get; }
        public TSettings Settings { get; }
        public EncounterSetup(EncounterContext context, TSettings settings)
        { Context = context ?? throw new ArgumentNullException(nameof(context)); Settings = settings ?? throw new ArgumentNullException(nameof(settings)); }
    }
}
