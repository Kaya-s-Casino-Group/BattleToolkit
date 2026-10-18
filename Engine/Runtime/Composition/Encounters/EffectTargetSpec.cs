using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace PokerEngine
{
    public enum EffectTargetGroup { Self = 1, Opponents, Allies }
    public enum EffectTargetSelection { Requested = 1, FirstLiving, LowestHealth, HighestHealth, AllLiving, ActorPolicy }

    /// <summary>One effect's target selection. Request slots bind caller choices without changing the effect itself.</summary>
    public sealed class EffectTargetSpec
    {
        public EffectTargetGroup Group { get; }
        public EffectTargetSelection Selection { get; }
        public string RequestSlot { get; }

        private EffectTargetSpec(EffectTargetGroup group, EffectTargetSelection selection, string requestSlot)
        {
            if (!Enum.IsDefined(typeof(EffectTargetGroup), group) || !Enum.IsDefined(typeof(EffectTargetSelection), selection))
                throw new ArgumentException("Unknown effect target selection.");
            bool usesRequest = group != EffectTargetGroup.Self &&
                (selection == EffectTargetSelection.Requested || selection == EffectTargetSelection.ActorPolicy);
            if (group == EffectTargetGroup.Self && selection != EffectTargetSelection.AllLiving || !usesRequest && requestSlot != null)
                throw new ArgumentException("Automatic target selections do not accept request slots.");
            Group = group; Selection = selection;
            RequestSlot = usesRequest ? Guard.Text(requestSlot ?? BattleTargetBindings.DefaultSlot, nameof(requestSlot)) : null;
        }

        public static EffectTargetSpec Self() => new EffectTargetSpec(EffectTargetGroup.Self, EffectTargetSelection.AllLiving, null);
        public static EffectTargetSpec Opponents(EffectTargetSelection selection = EffectTargetSelection.ActorPolicy, string requestSlot = null) =>
            new EffectTargetSpec(EffectTargetGroup.Opponents, selection, requestSlot);
        public static EffectTargetSpec Allies(EffectTargetSelection selection = EffectTargetSelection.ActorPolicy, string requestSlot = null) =>
            new EffectTargetSpec(EffectTargetGroup.Allies, selection, requestSlot);
    }

    /// <summary>Defensively copied, ordinal named target requests. Empty bindings are valid; present slots cannot be empty.</summary>
    public sealed class BattleTargetBindings
    {
        public const string DefaultSlot = "default";
        public IReadOnlyDictionary<string, IReadOnlyList<ParticipantId>> Slots { get; }

        public BattleTargetBindings(IEnumerable<KeyValuePair<string, IEnumerable<ParticipantId>>> slots)
        {
            var entries = Guard.Copy(slots, nameof(slots));
            if (entries.Length > 64) throw new ArgumentException("At most 64 target request slots are supported.");
            var copy = new Dictionary<string, IReadOnlyList<ParticipantId>>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                string key = Guard.Text(entry.Key, nameof(slots));
                var targets = Guard.Copy(entry.Value, nameof(slots));
                if (copy.ContainsKey(key) || targets.Length < 1 || targets.Length > 64 ||
                    targets.Any(target => !target.IsValid) || targets.Distinct().Count() != targets.Length)
                    throw new ArgumentException("Target slots require unique names and distinct valid target identities.");
                copy.Add(key, Guard.View(targets));
            }
            Slots = new ReadOnlyDictionary<string, IReadOnlyList<ParticipantId>>(copy);
        }

        public BattleTargetBindings(string slot, IEnumerable<ParticipantId> targets)
            : this(new[] { new KeyValuePair<string, IEnumerable<ParticipantId>>(slot, targets) }) { }
    }
}
