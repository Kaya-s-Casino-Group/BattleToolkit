using System;
using System.Globalization;
using System.Runtime.Serialization;

namespace PokerEngine
{
    /// <summary>Integer chip units. Negative balances and arithmetic overflow are rejected.</summary>
    [DataContract]
    public struct Chips : IEquatable<Chips>, IComparable<Chips>
    {
        [DataMember(Order = 1, IsRequired = true)]
        public long Value { get; private set; }
        public static Chips Zero => new Chips(0);
        public Chips(long value)
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            Value = value;
        }
        public int CompareTo(Chips other) => Value.CompareTo(other.Value);
        public bool Equals(Chips other) => Value == other.Value;
        public override bool Equals(object obj) => obj is Chips && Equals((Chips)obj);
        public override int GetHashCode() => Value.GetHashCode();
        public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
        public static Chips operator +(Chips a, Chips b) => new Chips(checked(a.Value + b.Value));
        public static Chips operator -(Chips a, Chips b) => new Chips(checked(a.Value - b.Value));
        public static bool operator ==(Chips a, Chips b) => a.Equals(b);
        public static bool operator !=(Chips a, Chips b) => !a.Equals(b);
        public static bool operator <(Chips a, Chips b) => a.Value < b.Value;
        public static bool operator >(Chips a, Chips b) => a.Value > b.Value;
        public static bool operator <=(Chips a, Chips b) => a.Value <= b.Value;
        public static bool operator >=(Chips a, Chips b) => a.Value >= b.Value;
    }
}
