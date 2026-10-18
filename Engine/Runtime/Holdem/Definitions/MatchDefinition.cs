using System;
using System.Runtime.Serialization;

namespace PokerEngine
{
    /// <summary>Frozen rules reference and base limits. Ruleset identity selects future rule policies.</summary>
    [DataContract]
    public sealed class MatchDefinition
    {
        [DataMember(Order = 1, IsRequired = true)] public string RulesetId { get; private set; }
        [DataMember(Order = 2, IsRequired = true)] public string RulesetVersion { get; private set; }
        [DataMember(Order = 3, IsRequired = true)] public string ContentVersion { get; private set; }
        [DataMember(Order = 4, IsRequired = true)] public int MaxSeats { get; private set; }
        [DataMember(Order = 5, IsRequired = true)] public Chips SmallBlind { get; private set; }
        [DataMember(Order = 6, IsRequired = true)] public Chips BigBlind { get; private set; }
        [DataMember(Order = 7, IsRequired = true)] public MatchMode Mode { get; private set; }
        public MatchDefinition(string rulesetId, string rulesetVersion, string contentVersion,
            int maxSeats, Chips smallBlind, Chips bigBlind, MatchMode mode = MatchMode.Solo)
        {
            if (maxSeats < 2 || smallBlind > bigBlind || bigBlind.Value <= 0) throw new ArgumentException("Invalid table limits.");
            RulesetId = Guard.Text(rulesetId, nameof(rulesetId));
            RulesetVersion = Guard.Text(rulesetVersion, nameof(rulesetVersion));
            ContentVersion = Guard.Text(contentVersion, nameof(contentVersion));
            MaxSeats = maxSeats; SmallBlind = smallBlind; BigBlind = bigBlind;
            if (!Enum.IsDefined(typeof(MatchMode), mode)) throw new ArgumentException("Invalid match mode.");
            Mode = mode;
        }
    }
}
