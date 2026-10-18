using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace PokerEngine
{
    /// <summary>Resource bundle used by the existing Draw/Holdem ability flows; not a mandatory combatant contract.</summary>
    [DataContract]
    public sealed class ResourceState
    {
        [DataMember(Order = 1, IsRequired = true)] public Chips Chips { get; private set; }
        [DataMember(Order = 2, IsRequired = true)] public int Energy { get; private set; }
        [DataMember(Order = 3)] public int? Health { get; private set; }
        public ResourceState(Chips chips, int energy = 0, int? health = null)
        {
            if (energy < 0 || health < 0) throw new ArgumentOutOfRangeException(nameof(energy));
            Chips = chips; Energy = energy; Health = health;
        }
    }

}
