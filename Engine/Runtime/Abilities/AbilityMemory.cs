using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace PokerEngine
{
    public enum MemoryScope { Round = 1, Encounter }

    [DataContract]
    public sealed class AbilityCounter
    {
        [DataMember(Order = 1, IsRequired = true)] public string Key { get; private set; }
        [DataMember(Order = 2, IsRequired = true)] public long Value { get; private set; }
        [DataMember(Order = 3, IsRequired = true)] public MemoryScope Scope { get; private set; }
        public AbilityCounter(string key, long value, MemoryScope scope)
        {
            Key = Guard.Text(key, nameof(key));
            if (value < 0 || !Enum.IsDefined(typeof(MemoryScope), scope)) throw new ArgumentException("Invalid counter.");
            Value = value; Scope = scope;
        }
    }

    [DataContract]
    public sealed class AbilityMark
    {
        [DataMember(Order = 1, IsRequired = true)] public string Key { get; private set; }
        [DataMember(Order = 2, IsRequired = true)] public EntityRef Target { get; private set; }
        [DataMember(Order = 3, IsRequired = true)] public MemoryScope Scope { get; private set; }
        public AbilityMark(string key, EntityRef target, MemoryScope scope)
        {
            Key = Guard.Text(key, nameof(key)); Target = target ?? throw new ArgumentNullException(nameof(target));
            if (!Enum.IsDefined(typeof(MemoryScope), scope) || (target.Kind == EntityKind.Card && scope != MemoryScope.Round))
                throw new ArgumentException("Card marks cannot outlive their round.");
            Scope = scope;
        }
    }

    /// <summary>Typed persistent memory. Counter keys also support streaks; missing counters read as zero.</summary>
    [DataContract]
    public sealed class AbilityMemory
    {
        [DataMember(Order = 1, IsRequired = true)] public int Version { get; private set; }
        [DataMember(Name = "Counters", Order = 2, IsRequired = true)] private AbilityCounter[] counters;
        [DataMember(Name = "Marks", Order = 3, IsRequired = true)] private AbilityMark[] marks;
        public IReadOnlyList<AbilityCounter> Counters => Guard.View(counters);
        public IReadOnlyList<AbilityMark> Marks => Guard.View(marks);
        public static AbilityMemory Empty => new AbilityMemory(new AbilityCounter[0], new AbilityMark[0]);
        public AbilityMemory(IEnumerable<AbilityCounter> counters, IEnumerable<AbilityMark> marks)
        {
            Version = 1; this.counters = Guard.Copy(counters, nameof(counters)); this.marks = Guard.Copy(marks, nameof(marks));
            var keys = this.counters.Select(c => c.Key).Concat(this.marks.Select(m => m.Key)).ToArray();
            if (keys.Distinct(StringComparer.Ordinal).Count() != keys.Length) throw new ArgumentException("Memory keys must be unique.");
        }
        public long CounterValue(string key) => counters.FirstOrDefault(c => c.Key == key)?.Value ?? 0;
        public AbilityMemory WithCounter(string key, long value, MemoryScope scope) =>
            new AbilityMemory(counters.Where(c => c.Key != key).Concat(new[] { new AbilityCounter(key, value, scope) }), marks);
        public AbilityMemory ResetRound() => new AbilityMemory(counters.Where(c => c.Scope == MemoryScope.Encounter), marks.Where(m => m.Scope == MemoryScope.Encounter));
    }
}
