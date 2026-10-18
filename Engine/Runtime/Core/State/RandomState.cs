using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace PokerEngine
{
    /// <summary>Opaque state of one explicitly versioned random provider; not an RNG implementation.</summary>
    [DataContract]
    public sealed class RandomState
    {
        [DataMember(Order = 1, IsRequired = true)] public string Algorithm { get; private set; }
        [DataMember(Order = 2, IsRequired = true)] public int AlgorithmVersion { get; private set; }
        [DataMember(Name = "StateBytes", Order = 3, IsRequired = true)] private byte[] stateBytes;
        public IReadOnlyList<byte> StateBytes => Guard.View(stateBytes);
        public RandomState(string algorithm, int algorithmVersion, IEnumerable<byte> stateBytes)
        {
            if (algorithmVersion <= 0) throw new ArgumentOutOfRangeException(nameof(algorithmVersion));
            Algorithm = Guard.Text(algorithm, nameof(algorithm)); AlgorithmVersion = algorithmVersion;
            this.stateBytes = Guard.Copy(stateBytes, nameof(stateBytes));
            if (this.stateBytes.Length == 0) throw new ArgumentException("Random provider state is required.");
        }
    }

}
