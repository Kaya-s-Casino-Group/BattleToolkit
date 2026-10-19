using System;
using System.Collections.Generic;
using System.Linq;
namespace PokerEngine
{
    /// <summary>Authority-owned ordered fallback roster. Each damage hit retargets only after the previous target dies.</summary>
    public sealed class CombatTargetChain
    {
        public string Id { get; }
        public IReadOnlyList<ParticipantId> Candidates { get; }
        public IAttackDamageModifier Outgoing { get; }
        public CombatTargetChain(string id, IEnumerable<ParticipantId> candidates, IAttackDamageModifier outgoing = null)
        {
            Id = Guard.Text(id, nameof(id)); var copy = Guard.Copy(candidates, nameof(candidates));
            if (copy.Length == 0 || copy.Length > 64 || copy.Any(p => !p.IsValid) || copy.Distinct().Count() != copy.Length)
                throw new ArgumentException("Invalid retarget candidates.");
            Candidates = Guard.View(copy); Outgoing = outgoing;
        }
    }
}
