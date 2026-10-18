using System;
using System.Runtime.Serialization;

namespace PokerEngine
{
    /// <summary>Ordinal, case-sensitive identity. default is invalid; use nullable IDs for absence.</summary>
    [DataContract]
    public struct HandId : IEquatable<HandId>
    {
        [DataMember(Order = 1, IsRequired = true)]
        public string Value { get; private set; }
        public bool IsValid => !string.IsNullOrWhiteSpace(Value);
        public HandId(string value) { Value = Guard.Text(value, nameof(value)); }
        public bool Equals(HandId other) => StringComparer.Ordinal.Equals(Value, other.Value);
        public override bool Equals(object obj) => obj is HandId && Equals((HandId)obj);
        public override int GetHashCode() => Value == null ? 0 : StringComparer.Ordinal.GetHashCode(Value);
        public override string ToString() => Value ?? "<invalid>";
        public static bool operator ==(HandId left, HandId right) => left.Equals(right);
        public static bool operator !=(HandId left, HandId right) => !left.Equals(right);
    }
}
