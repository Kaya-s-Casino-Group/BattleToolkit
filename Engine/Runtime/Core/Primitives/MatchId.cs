using System;
using System.Runtime.Serialization;

namespace PokerEngine
{
    /// <summary>Ordinal, case-sensitive identity. default is invalid; use nullable IDs for absence.</summary>
    [DataContract]
    public struct MatchId : IEquatable<MatchId>
    {
        [DataMember(Order = 1, IsRequired = true)]
        public string Value { get; private set; }
        public bool IsValid => !string.IsNullOrWhiteSpace(Value);
        public MatchId(string value) { Value = Guard.Text(value, nameof(value)); }
        public bool Equals(MatchId other) => StringComparer.Ordinal.Equals(Value, other.Value);
        public override bool Equals(object obj) => obj is MatchId && Equals((MatchId)obj);
        public override int GetHashCode() => Value == null ? 0 : StringComparer.Ordinal.GetHashCode(Value);
        public override string ToString() => Value ?? "<invalid>";
        public static bool operator ==(MatchId left, MatchId right) => left.Equals(right);
        public static bool operator !=(MatchId left, MatchId right) => !left.Equals(right);
    }
}
